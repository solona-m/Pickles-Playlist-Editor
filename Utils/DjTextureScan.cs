using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// One picture on the DJ table that a user can replace.
    ///
    /// <see cref="Rect"/> is the island of the texture atlas that surface occupies, measured off the
    /// shipped art rather than guessed: everything outside it is other parts of the table, and a
    /// paste that overran it would smear the picture across the mixer.
    /// </summary>
    internal sealed record DjTextureSurface(
        string Key,
        string FileName,
        int TextureWidth,
        int TextureHeight,
        TexRect Rect,

        /// <summary>Clockwise quarter turns from the texture's orientation to the upright one.</summary>
        int QuarterTurns)
    {
        /// <summary>Width of the picture as a person sees it, which is not the texture's.</summary>
        public int UprightWidth => QuarterTurns % 2 == 0 ? Rect.Width : Rect.Height;

        public int UprightHeight => QuarterTurns % 2 == 0 ? Rect.Height : Rect.Width;
    }

    /// <summary>A surface located in a real mod, with the file backing it.</summary>
    internal sealed class DjTextureTarget
    {
        public required DjTextureSurface Surface { get; init; }
        public required string ModName { get; init; }

        /// <summary>Full path of the .atex this surface lives in.</summary>
        public required string FullPath { get; init; }

        /// <summary>Mod-relative path, backslashed, as the manifest spells it.</summary>
        public required string Relative { get; init; }

        /// <summary>
        /// True once this app has pasted a picture in and nothing has replaced it since.
        ///
        /// Left false by the scan and filled in by <see cref="DjTextures"/>, which is the half that
        /// knows where the pristine copies live.
        /// </summary>
        public bool IsCustomised { get; set; }

        public override string ToString() => $"{Surface.Key} -> {Relative}";
    }

    /// <summary>Why a surface could not be found in the chosen mod.</summary>
    internal sealed record DjTextureProblem(DjTextureSurface Surface, string Reason);

    /// <summary>A mod in the Penumbra folder that holds at least one of these pictures.</summary>
    internal sealed record DjTextureModCandidate(string Name, string Folder, int SurfaceCount)
    {
        public override string ToString() => $"{Name} ({SurfaceCount} pictures)";
    }

    /// <summary>
    /// Finding the DJ table pictures in a mod. Reads; never writes.
    ///
    /// Split from <see cref="DjTextures"/> the way <see cref="DanceMod"/> is split from
    /// <see cref="DanceModWrites"/>, and for a reason this feature learned the hard way. Everything
    /// here depends on nothing but <see cref="PenumbraOptions"/> and the format code, so the offline
    /// harness can run the REAL detection over a real Penumbra folder. The bug that made it worth
    /// doing — a header check that rejected every texture in existence — passed every harness check
    /// there was, because the harness was exercising a different code path from the dialog.
    /// </summary>
    internal static class DjTextureScan
    {
        /// <summary>
        /// The glowing panel on the front of the table, the laptop screen, and the badge on the back
        /// of the laptop lid.
        ///
        /// The laptop is in eq3, not eq2 — the four 4096 atlases share a UV layout, so the same
        /// rectangle is the mixer in one file and the screen in another, and only the content says
        /// which. Every rectangle was measured off the shipped textures rather than guessed, and all
        /// three are stored on their side except the table, which <see cref="DjTextureSurface.QuarterTurns"/>
        /// undoes.
        ///
        /// The lid badge and the screen are two islands of the SAME file, which is why the backups
        /// are keyed per texture file rather than per surface.
        /// </summary>
        public static readonly DjTextureSurface[] Surfaces =
        {
            // The table panel is stored UPSIDE DOWN. Nothing in the Pickles art says so — a wavy
            // line of music notes looks much the same either way up — but DJ Solona ships a neon
            // "DJ Pickles" wordmark in the same island, and it only reads correctly turned through
            // 180 degrees. A picture pasted without this came out inverted on the booth in game.
            new("table", "eq55.atex", 1024, 1024, new TexRect(165, 398, 440, 289), 2),
            new("laptop", "eq3.atex", 4096, 4096, new TexRect(2993, 2759, 387, 656), 1),
            new("lidlogo", "eq3.atex", 4096, 4096, new TexRect(3109, 3660, 196, 196), 1),
        };

        /// <summary>
        /// Every mod in the Penumbra folder that actually holds one of these pictures, best first.
        ///
        /// A far surer test than the one the dances picker has to make do with: a mod either ships a
        /// texture of the right name, size and format or it does not, so there is no scoring to get
        /// wrong. The mod already chosen for the dances is preferred on a tie, because a DJ pack
        /// normally keeps its animations and its table art together.
        /// </summary>
        public static List<DjTextureModCandidate> FindCandidates(string penumbraRoot, string? preferredFolder)
        {
            var candidates = new List<DjTextureModCandidate>();
            if (string.IsNullOrWhiteSpace(penumbraRoot) || !Directory.Exists(penumbraRoot))
                return candidates;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(penumbraRoot).ToList(); }
            catch { return candidates; }

            foreach (string directory in directories)
            {
                string folder = Path.GetFileName(directory);
                int count;
                try { count = Locate(directory, folder, out _).Count; }
                catch { continue; }   // a stranger's mod that will not read is one to walk past
                if (count == 0) continue;

                candidates.Add(new DjTextureModCandidate(
                    PenumbraOptions.DisplayName(directory), folder, count));
            }

            return candidates
                .OrderByDescending(c => c.SurfaceCount)
                .ThenByDescending(c => string.Equals(c.Folder, preferredFolder, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Locates every surface in the mod, and says why for any it cannot.
        ///
        /// Goes through the mod's own option list rather than globbing the folder: the manifest is
        /// what tells us a file is really the texture the game asks for by that name, and a DJ pack
        /// commonly keeps several unused copies lying around.
        /// </summary>
        public static List<DjTextureTarget> Locate(string modRoot, string modName,
            out List<DjTextureProblem> problems)
        {
            var found = new List<DjTextureTarget>();
            problems = new List<DjTextureProblem>();

            // Two indexes, because the two names can disagree. DJ Solona ships this very texture as
            // common\4\eq55.atex but asks the game for it as vfx/atex/eq56.atex, so a lookup on the
            // game path alone misses a table that is plainly there. The game path is still tried
            // first: it is what the effect actually references, and the file on disk is only storage.
            var byGamePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byDiskName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var option in PenumbraOptions.Read(modRoot))
            {
                foreach (var (gamePath, relative) in option.Files)
                {
                    string gameName = gamePath[(gamePath.LastIndexOf('/') + 1)..];
                    if (!byGamePath.ContainsKey(gameName)) byGamePath[gameName] = relative;

                    string diskName = relative[(relative.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
                    if (!byDiskName.ContainsKey(diskName)) byDiskName[diskName] = relative;
                }
            }

            foreach (var surface in Surfaces)
            {
                if (!byGamePath.TryGetValue(surface.FileName, out string? relative)
                    && !byDiskName.TryGetValue(surface.FileName, out relative))
                {
                    problems.Add(new DjTextureProblem(surface, $"This mod has no {surface.FileName}."));
                    continue;
                }

                string full = Path.Combine(modRoot, relative.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    problems.Add(new DjTextureProblem(surface,
                        $"The mod lists {relative} but that file is not there."));
                    continue;
                }

                string? reason = Verify(full, surface);
                if (reason != null)
                {
                    problems.Add(new DjTextureProblem(surface, reason));
                    continue;
                }

                found.Add(new DjTextureTarget
                {
                    Surface = surface,
                    ModName = modName,
                    FullPath = full,
                    Relative = relative,
                });
            }

            return found;
        }

        /// <summary>Whether this file is the texture the surface expects, or a sentence saying it is not.</summary>
        public static string? Verify(string path, DjTextureSurface surface)
        {
            byte[] head;
            long length;
            try
            {
                using var stream = File.OpenRead(path);
                length = stream.Length;
                head = new byte[TexHeader.Size];
                if (stream.Read(head, 0, head.Length) < head.Length)
                    return $"{surface.FileName} is too small to be a texture.";
            }
            catch (Exception ex)
            {
                return $"{surface.FileName} could not be opened: {ex.Message}";
            }

            // Only the header is read: this runs over every mod in the Penumbra folder, and pulling
            // 22 MB off disk per candidate to answer "is this the right size" would make opening the
            // dialog a visible wait. The real length goes in separately so the offsets still check —
            // passing the 80-byte buffer as the file is what made every texture look truncated.
            var header = TexHeader.TryRead(head, length, out string error);
            if (header == null) return error;

            if (header.Width != surface.TextureWidth || header.Height != surface.TextureHeight)
                return $"{surface.FileName} is {header.Width}x{header.Height}, not the " +
                       $"{surface.TextureWidth}x{surface.TextureHeight} this knows how to edit.";

            // Both block formats these packs actually use. Solona ships its table as BC7 with no mip
            // chain while Pickles ships BC3 with nine, so neither can be assumed.
            if (BlockCodec.For(header.Format, out string unsupported) == null) return unsupported;

            return null;
        }
    }
}
