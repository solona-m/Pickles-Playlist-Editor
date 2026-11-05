using Pickles_Playlist_Editor.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Pickles_Playlist_Editor.Tools
{
    internal readonly record struct SourceFile(string Path, bool CloudOnly);

    internal sealed class WalkResult
    {
        public int FilesChecked, DirsSkipped;
        public string? RootError;
        public readonly Dictionary<string, List<SourceFile>> Exact = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<SourceFile>> Stripped = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folder walk for Backfill tags from folder.</summary>
    internal static class SourceFolderWalk
    {
        private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

        /// <summary>
        /// Every mp3/m4a/flac/wav under <paramref name="root"/>, indexed by import key when some song wants it. Skips hidden
        /// and system items, links (no loops), unreadable folders and depth past <see cref="SourceTagBackfill.MaxDepth"/>; cloud-only files are
        /// recorded, never opened.
        /// </summary>
        internal static WalkResult Walk(string root, ISet<string> wanted, ISet<string> wantedExact, CancellationToken ct, Action<int>? onChecked = null)
        {
            var result = new WalkResult();
            DirectoryInfo rootDir;
            try
            {
                rootDir = new DirectoryInfo(root);
                if (!rootDir.Exists) { result.RootError = $"'{root}' was not found"; return result; }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.RootError = ex.Message;
                return result;
            }
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false, // an unreadable folder throws below: the root is reported, a subfolder counted
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                RecurseSubdirectories = false,
            };
            var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
            stack.Push((rootDir, 0));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = stack.Pop();
                List<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos("*", options).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    if (depth == 0) { result.RootError = ex.Message; return result; }
                    result.DirsSkipped++;
                    continue;
                }
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry is DirectoryInfo sub)
                        {
                            if (sub.LinkTarget == null && depth < SourceTagBackfill.MaxDepth) stack.Push((sub, depth + 1));
                            continue;
                        }
                        if (!SourceTagBackfill.Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase) || Player.IsExtractedPlaybackFile(entry.FullName))
                            continue;
                        result.FilesChecked++;
                        if (result.FilesChecked % 50 == 0) onChecked?.Invoke(result.FilesChecked);
                        string key = Playlist.ImportScdStem(entry.FullName);
                        if (key.Length == 0) continue;
                        bool cloud = (entry.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
                        var file = new SourceFile(entry.FullName, cloud);
                        if (wanted.Contains(key)) Add(result.Exact, key, file);
                        foreach (string stripped in SourceTagBackfill.StemKeys(key).Skip(1))
                            if (wantedExact.Contains(stripped)) Add(result.Stripped, stripped, file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // vanished or unreadable entry
                }
            }
            onChecked?.Invoke(result.FilesChecked);
            return result;
        }

        private static void Add(Dictionary<string, List<SourceFile>> index, string key, SourceFile file)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<SourceFile>();
            if (!list.Any(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))) list.Add(file);
        }
    }
}
