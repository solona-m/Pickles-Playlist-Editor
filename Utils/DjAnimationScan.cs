using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    internal sealed class AnimationFrame
    {
        public string Relative { get; set; } = string.Empty;
        public string GamePath { get; set; } = string.Empty;
        public string Sha { get; set; } = string.Empty;
    }

    internal sealed class AnimationAvfx
    {
        public string Relative { get; set; } = string.Empty;
        public string OriginalSha { get; set; } = string.Empty;
        public string? AppliedSha { get; set; }
        public string? PendingSha { get; set; }
    }

    /// <summary>What this app has done to a mod's table effect, kept in the backup folder.</summary>
    internal sealed class AnimationState
    {
        public string Phase { get; set; } = "Applied";
        public int Generation { get; set; }
        public string ModToken { get; set; } = string.Empty;
        public string? Mode { get; set; }
        public List<AnimationFrame> Frames { get; set; } = new();
        public List<int> StartTicks { get; set; } = new();
        public int LoopTicks { get; set; }
        public List<AnimationFrame> PendingFrames { get; set; } = new();
        public List<AnimationFrame> StaleFrames { get; set; } = new();
        public List<AnimationAvfx> Avfx { get; set; } = new();

        /// <summary>The picture rect inside each frame file; null for full-atlas frames (at the surface rect).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TexRect? FrameRect { get; set; }

        public const string Applying = "Applying";
        public const string Applied = "Applied";

        /// <summary>Kept only to carry the token, generation and leftovers; counts as no state.</summary>
        [JsonIgnore] public bool IsEmpty => Avfx.Count == 0 && Frames.Count == 0;

        public AnimationAvfx? Find(string relative) =>
            Avfx.FirstOrDefault(a => DjAnimationScan.SameRelative(a.Relative, relative));

        public byte[] ToBytes() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Formatting.Indented));

        public static AnimationState Parse(byte[] bytes) =>
            JsonConvert.DeserializeObject<AnimationState>(Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidDataException("The animation state is empty.");

        public AnimationState Copy() => Parse(ToBytes());
    }

    /// <summary>One table effect file as the scan found it.</summary>
    internal sealed record SiteAvfx(
        string Relative, string FullPath, string? CurrentSha, bool Unreadable,
        byte[]? Baseline, bool Ours, InspectResult? Inspect);

    /// <summary>Everything the table animation needs to know about one mod, read in one pass.</summary>
    internal sealed class DjTableAnimationSite
    {
        public required string ModRoot { get; init; }
        public required string StateFolder { get; init; }
        public required DjTextureTarget Table { get; init; }
        public IReadOnlyList<string> TextureGamePaths { get; init; } = Array.Empty<string>();
        public List<SiteAvfx> Avfx { get; } = new();
        public AnimationMode? Mode { get; set; }
        public int Capacity { get; set; }
        public AnimationState? State { get; init; }
        public string? StateSha { get; init; }
        public bool HasState => State is { IsEmpty: false };
        public bool Orphaned { get; set; }
        public string? Problem { get; set; }
        public int ParsedCount { get; set; }
        public List<string> Warnings { get; } = new();

        /// <summary>The effect files that get frames.</summary>
        public IEnumerable<SiteAvfx> Animatable =>
            Avfx.Where(a => a.Inspect?.Kind == InspectKind.Animatable);

        /// <summary>Size of the author's animation set, for the card.</summary>
        public int AuthorFrames => Animatable.FirstOrDefault()?.Inspect?.Info?.AnimationSet.Count ?? 0;
    }

    internal enum AnimationMessage
    {
        FileMissing, FileUnreadable, ModChanged, Collision, RollbackStopped, Orphaned,
        NoPanel, DefaultData, TooManyTextures, MismatchedAtlas, MixedLayouts,
        DuplicatePanel, PanelItemCount, NoUsableSlot, Unparseable,
        LeftAlone, KeptEditedFrame, StaleNotDeleted, ManifestLost, WriteFailed,
    }

    /// <summary>
    /// Turns a message and its argument into a sentence. The app points this at its translated
    /// strings; the default is plain English so the harness reads without it.
    /// </summary>
    internal static class AnimationText
    {
        public static Func<AnimationMessage, string, string> Format { get; set; } =
            (message, arg) => arg.Length == 0 ? message.ToString() : $"{message}: {arg}";
    }

    /// <summary>
    /// Finding the table effect files of a mod and deciding, for each, which bytes stand for the
    /// mod as shipped. Reads; never writes.
    /// </summary>
    internal static class DjAnimationScan
    {
        public const string FrameFolder = "ppe-table-animation";
        private static readonly byte[] FrameMarker = Encoding.ASCII.GetBytes(AvfxTableAnimation.FramePrefix);

        public static string StatePath(string stateFolder) => Path.Combine(stateFolder, "state.json");

        public static string PristinePath(string stateFolder, string sha) =>
            Path.Combine(stateFolder, "pristine", sha + ".avfx");

        public static DjTableAnimationSite Inspect(string modRoot, DjTextureTarget table, string stateFolder,
            Func<string, byte[]>? read = null)
        {
            read ??= File.ReadAllBytes;
            var options = PenumbraOptions.Read(modRoot);
            if (options.Count == 0) throw new InvalidDataException("The mod's option list could not be read.");

            string tableRelative = Normalise(table.Relative);
            var gamePaths = options.SelectMany(o => o.Files)
                .Where(f => SameRelative(f.Value, tableRelative))
                .Select(f => f.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            AnimationState? state = null;
            string? stateSha = null;
            string statePath = StatePath(stateFolder);
            if (File.Exists(statePath))
            {
                byte[] bytes = File.ReadAllBytes(statePath);
                stateSha = AvfxTableAnimation.Sha(bytes);
                state = AnimationState.Parse(bytes);
            }

            var site = new DjTableAnimationSite
            {
                ModRoot = modRoot,
                StateFolder = stateFolder,
                Table = table,
                TextureGamePaths = gamePaths,
                State = state,
                StateSha = stateSha,
            };

            // Every effect file an option maps, plus every one state lists.
            var mapping = new Dictionary<string, List<ModOption>>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options)
                foreach (var value in option.Files.Values.Where(v => v.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)))
                {
                    string relative = Normalise(value);
                    if (!mapping.TryGetValue(relative, out var list)) mapping[relative] = list = new();
                    if (!list.Contains(option)) list.Add(option);
                }

            var candidates = mapping.Keys.ToList();
            foreach (var listed in state?.Avfx ?? new List<AnimationAvfx>())
                if (!candidates.Any(c => SameRelative(c, listed.Relative))) candidates.Add(Normalise(listed.Relative));

            var markers = gamePaths.Select(p => Encoding.ASCII.GetBytes(p.ToLowerInvariant())).Append(FrameMarker).ToList();
            var headerCache = new Dictionary<string, TexHeader?>(StringComparer.OrdinalIgnoreCase);
            var tableHeader = Header(modRoot, tableRelative, headerCache);

            string rootPrefix = Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string relative in candidates)
            {
                string full = Path.GetFullPath(Path.Combine(modRoot, relative));
                bool listed = state?.Find(relative) != null;
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;   // a manifest path outside the mod is never read or written

                byte[] bytes;
                try { bytes = read(full); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    site.Avfx.Add(new SiteAvfx(relative, full, null, false, null, false, null));
                    if (listed) site.Warnings.Add(AnimationText.Format(AnimationMessage.FileMissing, relative));
                    continue;
                }
                catch
                {
                    site.Avfx.Add(new SiteAvfx(relative, full, null, true, null, false, null));
                    continue;
                }

                string sha = AvfxTableAnimation.Sha(bytes);
                if (!listed && !ContainsAny(bytes, markers))
                {
                    site.Avfx.Add(new SiteAvfx(relative, full, sha, false, null, false, null));
                    continue;
                }

                site.ParsedCount++;
                var entry = state?.Find(relative);
                bool ours = IsOurs(bytes, sha, entry, state);
                var baseline = Baseline(relative, bytes, state, stateFolder);
                if (baseline.Orphaned) site.Orphaned = true;

                InspectResult? result = null;
                if (baseline.Bytes != null)
                {
                    var options4 = mapping.GetValueOrDefault(relative) ?? new List<ModOption>();
                    result = AvfxTableAnimation.Inspect(baseline.Bytes, gamePaths,
                        path => IsTableShaped(path, options4, modRoot, tableHeader, headerCache));
                }
                site.Avfx.Add(new SiteAvfx(relative, full, sha, false, baseline.Bytes, ours, result));
            }

            DecideProblem(site, mapping, tableRelative);
            return site;
        }

        private static void DecideProblem(DjTableAnimationSite site, Dictionary<string, List<ModOption>> mapping,
            string tableRelative)
        {
            if (site.Orphaned) return;

            var refused = site.Avfx.FirstOrDefault(a => a.Inspect?.Kind == InspectKind.NotAnimatable);
            if (refused != null)
            {
                site.Problem = AnimationText.Format(refused.Inspect!.Problem ?? AnimationMessage.Unparseable, refused.Relative);
                return;
            }

            var animatable = site.Animatable.ToList();
            if (animatable.Count == 0)
            {
                var unreadable = site.Avfx.FirstOrDefault(a => a.Unreadable);
                site.Problem = unreadable != null
                    ? AnimationText.Format(AnimationMessage.FileUnreadable, unreadable.Relative)
                    : AnimationText.Format(AnimationMessage.NoPanel, string.Empty);
                return;
            }

            foreach (var avfx in animatable)
            {
                var info = avfx.Inspect!.Info!;
                foreach (var option in mapping.GetValueOrDefault(avfx.Relative) ?? new List<ModOption>())
                {
                    if (option.IsDefaultData)
                    {
                        site.Problem = AnimationText.Format(AnimationMessage.DefaultData, avfx.Relative);
                        return;
                    }
                    string? panelFile = option.Files
                        .Where(f => site.TextureGamePaths.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
                        .Select(f => f.Value).FirstOrDefault();
                    if (panelFile == null || !SameRelative(panelFile, tableRelative))
                    {
                        site.Problem = AnimationText.Format(AnimationMessage.MismatchedAtlas, option.Name);
                        return;
                    }
                }
                if (info.Capacity < 1)
                {
                    site.Problem = AnimationText.Format(AnimationMessage.TooManyTextures, avfx.Relative);
                    return;
                }
            }

            var modes = animatable.Select(a => a.Inspect!.Info!.Mode).Distinct().ToList();
            if (modes.Count > 1)
            {
                site.Problem = AnimationText.Format(AnimationMessage.MixedLayouts, string.Empty);
                return;
            }

            site.Mode = modes[0];
            site.Capacity = animatable.Min(a => a.Inspect!.Info!.Capacity);
        }

        /// <summary>The parsed state, or null when it is absent or unreadable. Never throws.</summary>
        public static AnimationState? TryReadState(string stateFolder)
        {
            try
            {
                string path = StatePath(stateFolder);
                return File.Exists(path) ? AnimationState.Parse(File.ReadAllBytes(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A listed file is ours while it is still the original or carries only this mod's token,
        /// however many applies were interrupted on the way.
        /// </summary>
        public static bool IsOurs(byte[] current, string currentSha, AnimationAvfx? entry, AnimationState? state)
        {
            if (entry == null || state == null) return false;
            if (string.Equals(currentSha, entry.OriginalSha, StringComparison.OrdinalIgnoreCase)) return true;
            var tokens = AvfxTableAnimation.FrameTokens(current);
            return tokens.Count == 1 && tokens.Contains(state.ModToken);
        }

        public readonly record struct BaselineResult(byte[]? Bytes, bool Orphaned);

        /// <summary>
        /// The bytes that stand for this file as shipped: the pristine copy when the file is ours,
        /// nothing (Orphaned) when it holds frames we cannot account for, otherwise the file itself.
        /// </summary>
        public static BaselineResult Baseline(string relative, byte[] current, AnimationState? state, string stateFolder)
        {
            var entry = state?.Find(relative);
            string sha = AvfxTableAnimation.Sha(current);
            if (IsOurs(current, sha, entry, state))
            {
                string pristine = PristinePath(stateFolder, entry!.OriginalSha);
                if (File.Exists(pristine))
                {
                    byte[] bytes = File.ReadAllBytes(pristine);
                    if (string.Equals(AvfxTableAnimation.Sha(bytes), entry.OriginalSha, StringComparison.OrdinalIgnoreCase))
                        return new(bytes, false);
                }
            }

            if (AvfxTableAnimation.FrameTokens(current).Count > 0) return new(null, true);
            return new(current, false);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static bool IsTableShaped(string gamePath, List<ModOption> options, string modRoot,
            TexHeader? table, Dictionary<string, TexHeader?> cache)
        {
            if (table == null || options.Count == 0) return false;
            foreach (var option in options)
            {
                string? file = option.Files.FirstOrDefault(f => string.Equals(f.Key, gamePath, StringComparison.OrdinalIgnoreCase)).Value;
                if (file == null || !file.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)) return false;
                var header = Header(modRoot, Normalise(file), cache);
                if (header == null || header.Width != table.Width || header.Height != table.Height
                    || header.Format != table.Format) return false;
            }
            return true;
        }

        private static TexHeader? Header(string modRoot, string relative, Dictionary<string, TexHeader?> cache)
        {
            if (cache.TryGetValue(relative, out var cached)) return cached;
            TexHeader? header = null;
            try
            {
                using var stream = File.OpenRead(Path.Combine(modRoot, relative));
                var head = new byte[TexHeader.Size];
                if (stream.Read(head, 0, head.Length) == head.Length)
                    header = TexHeader.TryRead(head, stream.Length, out _);
            }
            catch { }
            cache[relative] = header;
            return header;
        }

        private static bool ContainsAny(byte[] haystack, List<byte[]> needles)
        {
            foreach (var needle in needles)
                if (needle.Length > 0 && IndexOfIgnoreCase(haystack, needle) >= 0) return true;
            return false;
        }

        private static int IndexOfIgnoreCase(byte[] haystack, byte[] needle)
        {
            byte first = needle[0];
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (Lower(haystack[i]) != first) continue;
                int j = 1;
                while (j < needle.Length && Lower(haystack[i + j]) == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

        public static string Normalise(string relative) => relative.Replace('/', '\\').TrimStart('\\');

        public static bool SameRelative(string a, string b) =>
            string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
    }
}
