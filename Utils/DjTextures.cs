using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>
    /// Replacing the DJ table pictures and putting them back. The finding half is
    /// <see cref="DjTextureScan"/>.
    ///
    /// This writes over files inside somebody else's mod, so the pristine copy is the whole design:
    /// one copy per TEXTURE FILE, taken before the first edit and never overwritten while this app
    /// is the only thing changing that file. Restoring is a file copy rather than an attempt to undo
    /// a lossy edit. The copies live in the app's backup folder, never in the mod — Penumbra scans
    /// the mod directory, and a stray .atex.original there would be packed into any .pmp exported.
    ///
    /// Per file rather than per surface because one texture can hold several pictures: the laptop
    /// screen and the badge on the back of its lid are both islands of eq3.atex. Keyed per surface,
    /// the second apply saw a file it did not recognise, decided the mod had been updated, and took
    /// a fresh "pristine" copy that already had the first picture baked into it — quietly destroying
    /// the only copy of the shipped art.
    /// </summary>
    internal static class DjTextures
    {
        /// <summary>
        /// The surfaces this mod has, with <see cref="DjTextureTarget.IsCustomised"/> filled in.
        ///
        /// Customised means "this app put the picture that is on that surface there", which needs
        /// both the file to be the one we last wrote and the surface to be named in its state.
        /// A mod update or a hand edit replaces the file behind our back, and offering to restore in
        /// that case would throw the user's own work away.
        /// </summary>
        public static List<DjTextureTarget> Locate(string modRoot, string modName,
            out List<DjTextureProblem> problems)
        {
            var found = DjTextureScan.Locate(modRoot, modName, out problems);

            foreach (var group in found.GroupBy(t => t.Relative, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                var state = ReadState(first);
                if (state?.AppliedSha == null) continue;

                // One hash per FILE, not per surface: the laptop screen and the lid badge share
                // 22 MB and hashing it twice would double the wait on opening the dialog.
                string current = Sha(File.ReadAllBytes(first.FullPath));
                if (!string.Equals(state.AppliedSha, current, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var target in group)
                    target.IsCustomised = state.Applied.Contains(target.Surface.Key,
                        StringComparer.OrdinalIgnoreCase);
            }

            return found;
        }

        public static List<DjTextureModCandidate> FindCandidates(string penumbraRoot, string? preferred)
            => DjTextureScan.FindCandidates(penumbraRoot, preferred);

        // ---- reading and writing -----------------------------------------------------------------

        /// <summary>The picture currently on this surface, the right way up.</summary>
        public static BgraImage? ReadCurrent(DjTextureTarget target, out string error)
        {
            try
            {
                var rect = TexPaste.ReadRect(Current(target), target.Surface.Rect, out error);
                return rect == null ? null : ImageOps.Rotate(rect, target.Surface.QuarterTurns);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// What the surface would look like with this picture on it, without writing anything.
        ///
        /// Runs the real encode, so the preview is the compressed result rather than a flattering
        /// approximation of it — the point of showing it at all is that a picture which turns to
        /// mush at panel size is visibly mush before it reaches the game.
        /// </summary>
        public static BgraImage? Preview(DjTextureTarget target, BgraImage picture, FitMode fit,
            out string error)
        {
            try
            {
                var surface = target.Surface;
                var inTextureSpace = ToTextureSpace(surface, picture, fit);

                // RoundTripRect, not the full paste: a preview only ever shows mip 0, and building a
                // whole new 22 MB file to look at a 387x656 rectangle is the allocation this avoids.
                var rect = TexPaste.RoundTripRect(Current(target), surface.Rect, inTextureSpace, out error);
                return rect == null ? null : ImageOps.Rotate(rect, surface.QuarterTurns);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Puts the picture on the surface, handing back the rectangle it wrote.
        ///
        /// Composites onto the texture as it stands rather than onto the pristine copy, because a
        /// second surface in the same file would otherwise be wiped out by the first one's paste.
        /// Nothing is lost by it: the rectangle's colours come entirely from the picture, freshly
        /// encoded each time, and every block the rectangle does not cover is copied through
        /// untouched. Only the ring of blocks straddling the rectangle's edge is ever re-encoded.
        ///
        /// Returning the picture matters too: the caller has a card to redraw, and reading the 22 MB
        /// file back off disk to do it would be the most expensive part of an apply.
        /// </summary>
        public static bool Apply(DjTextureTarget target, BgraImage picture, FitMode fit,
            out BgraImage? written, out string error)
        {
            written = null;
            try
            {
                var state = EnsureOriginal(target);

                byte[]? pasted = TexPaste.PasteRect(Current(target), target.Surface.Rect,
                    ToTextureSpace(target.Surface, picture, fit), out error);
                if (pasted == null) return false;

                Commit(target, state, pasted, applied: true);

                var rect = TexPaste.ReadRect(pasted, target.Surface.Rect, out _);
                if (rect != null) written = ImageOps.Rotate(rect, target.Surface.QuarterTurns);

                Logger.LogInfo("Replaced the {Surface} picture in {Mod} ({File}).",
                    target.Surface.Key, target.ModName, target.Relative);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Replacing the {Surface} picture in {Mod} failed: {Error}",
                    target.Surface.Key, target.ModName, ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Puts the shipped picture back on this surface, leaving any other surface in the same
        /// file alone.
        ///
        /// When it is the last customised surface the pristine file goes back wholesale, which is
        /// exact. Otherwise only this rectangle is taken from the pristine copy and pasted over the
        /// current file, so restoring the lid badge does not also undo the laptop screen.
        /// </summary>
        public static bool Restore(DjTextureTarget target, out string error)
        {
            error = string.Empty;
            try
            {
                var state = EnsureOriginal(target);
                byte[] original = File.ReadAllBytes(OriginalPath(target));

                var remaining = state.Applied
                    .Where(k => !string.Equals(k, target.Surface.Key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                byte[] result;
                if (remaining.Count == 0)
                {
                    result = original;
                }
                else
                {
                    var rect = TexPaste.ReadRect(original, target.Surface.Rect, out error);
                    if (rect == null) return false;

                    var restored = TexPaste.PasteRect(Current(target), target.Surface.Rect, rect, out error);
                    if (restored == null) return false;
                    result = restored;
                }

                Commit(target, state, result, applied: false);

                Logger.LogInfo("Restored the shipped {Surface} picture in {Mod}.",
                    target.Surface.Key, target.ModName);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Restoring the {Surface} picture in {Mod} failed: {Error}",
                    target.Surface.Key, target.ModName, ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Tells Penumbra to drop its cached copy of the mod.
        ///
        /// Unlike the dances, nothing in the manifest changed, so there is no in-memory copy waiting
        /// to overwrite this. The reload is only about the file the game already has open — without
        /// it the table keeps showing the old picture until the zone changes.
        /// </summary>
        public static void Reload(string modName)
        {
            try
            {
                PenumbraApi.ReloadMod(modName, modName).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogWarn("Reloading {Mod} after a picture change failed: {Error}", modName, ex.Message);
            }
        }

        /// <summary>
        /// Fits the picture to the panel and turns it into the texture's own orientation.
        ///
        /// Fitted upright first — the orientation the user chose it in — and rotated afterwards. The
        /// other order would crop to the wrong aspect ratio on anything stored on its side, which
        /// both the laptop screen and the lid badge are.
        /// </summary>
        private static BgraImage ToTextureSpace(DjTextureSurface surface, BgraImage picture, FitMode fit)
        {
            var upright = ImageOps.FitTo(picture, surface.UprightWidth, surface.UprightHeight, fit);
            return ImageOps.Rotate(upright, -surface.QuarterTurns);
        }

        /// <summary>Writes the new texture and records which surfaces this app is now responsible for.</summary>
        private static void Commit(DjTextureTarget target, TextureState state, byte[] bytes, bool applied)
        {
            PenumbraMeta.AtomicWrite(target.FullPath, bytes);

            state.Applied.RemoveAll(k => string.Equals(k, target.Surface.Key, StringComparison.OrdinalIgnoreCase));
            if (applied) state.Applied.Add(target.Surface.Key);
            state.AppliedSha = state.Applied.Count > 0 ? Sha(bytes) : null;

            WriteState(target, state);
            target.IsCustomised = applied;
            Forget();
        }

        // ---- the pristine copy -------------------------------------------------------------------

        private sealed class TextureState
        {
            /// <summary>Hash of the file the pristine copy was taken from.</summary>
            public string? OriginalSha { get; set; }

            /// <summary>Hash of the file this app last wrote, or null once nothing is customised.</summary>
            public string? AppliedSha { get; set; }

            public string? Relative { get; set; }

            /// <summary>Surface keys this app currently has a picture on in this file.</summary>
            public List<string> Applied { get; set; } = new();
        }

        // The texture as it stands on disk, kept so that nudging the fit control does not re-read
        // 22 MB per preview.
        //
        // Keyed on the file's identity, and on both timestamps rather than just the write time: an
        // archive extractor that restores mtimes could otherwise replace a texture with one of the
        // same length and go unnoticed. Creation time moves when a file is replaced even when the
        // write time is restored, and Forget on every write and every mod load bounds the rest.
        private static readonly object CacheLock = new();
        private static (string Path, DateTime Written, DateTime Created, long Length) _cacheKey;
        private static byte[]? _cacheBytes;

        /// <summary>Drops the cached texture. Called on every write and when the dialog changes mod.</summary>
        public static void Forget()
        {
            lock (CacheLock)
            {
                _cacheKey = default;
                _cacheBytes = null;
            }
        }

        private static byte[] Current(DjTextureTarget target)
        {
            var identity = FileIdentity(target.FullPath);
            lock (CacheLock)
            {
                if (_cacheBytes != null && _cacheKey == identity) return _cacheBytes;
            }

            byte[] bytes = File.ReadAllBytes(target.FullPath);

            lock (CacheLock)
            {
                _cacheKey = identity;
                _cacheBytes = bytes;
            }
            return bytes;
        }

        private static (string, DateTime, DateTime, long) FileIdentity(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return (path, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Length);
            }
            catch
            {
                // Unreadable stat means "never match the cache", which is the safe answer. A ticking
                // value rather than a constant, so two failures in a row do not look like a hit.
                return (path, DateTime.MinValue, DateTime.UtcNow, -1);
            }
        }

        /// <summary>
        /// Makes sure a pristine copy of this texture exists, and returns its state.
        ///
        /// Re-baselines when the file on disk is neither the pristine copy nor this app's own last
        /// write — which is what a mod update looks like. Keeping the stale copy in that case would
        /// silently revert the user's update the next time they changed a picture.
        /// </summary>
        private static TextureState EnsureOriginal(DjTextureTarget target)
        {
            Migrate(target);

            string originalPath = OriginalPath(target);
            byte[] current = Current(target);
            string currentSha = Sha(current);
            var state = ReadState(target);

            if (state != null && File.Exists(originalPath)
                && (string.Equals(state.OriginalSha, currentSha, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state.AppliedSha, currentSha, StringComparison.OrdinalIgnoreCase)))
                return state;

            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            File.WriteAllBytes(originalPath, current);

            var fresh = new TextureState
            {
                OriginalSha = currentSha,
                AppliedSha = null,
                Relative = target.Relative,
            };
            WriteState(target, fresh);

            Logger.LogInfo("Kept a pristine copy of {File} from {Mod}.", target.Relative, target.ModName);
            return fresh;
        }

        /// <summary>
        /// Moves a backup taken by the older per-surface layout into the per-file one.
        ///
        /// Only reachable for someone who used the first build of this feature, where the table and
        /// the laptop each had their own folder. Without it their pristine copies would be orphaned
        /// and the next apply would take a fresh "original" from a file that already has their
        /// picture in it.
        /// </summary>
        private static void Migrate(DjTextureTarget target)
        {
            try
            {
                string destination = StateFolder(target);
                if (Directory.Exists(destination)) return;

                string legacy = Path.Combine(ModFolder(target), target.Surface.Key);
                if (!File.Exists(Path.Combine(legacy, "original.atex"))) return;

                Directory.CreateDirectory(destination);
                File.Copy(Path.Combine(legacy, "original.atex"),
                          Path.Combine(destination, "original.atex"), overwrite: true);

                var state = ReadStateAt(Path.Combine(legacy, "state.json")) ?? new TextureState();
                state.Relative = target.Relative;
                if (state.AppliedSha != null && state.Applied.Count == 0)
                    state.Applied.Add(target.Surface.Key);
                WriteStateAt(Path.Combine(destination, "state.json"), state, target.Relative);

                Logger.LogInfo("Moved the {Surface} backup of {Mod} to the per-file layout.",
                    target.Surface.Key, target.ModName);
            }
            catch (Exception ex)
            {
                // A failed migration means a fresh baseline, not a failed edit.
                Logger.LogWarn("Could not migrate the {Surface} backup: {Error}",
                    target.Surface.Key, ex.Message);
            }
        }

        private static string ModFolder(DjTextureTarget target) => Path.Combine(
            Playlist.BackupDir, "textures", PenumbraMeta.SnapshotFolderNameForMod(target.ModName));

        /// <summary>The mod-relative path flattened into one folder name, so two files never collide.</summary>
        private static string StateFolder(DjTextureTarget target)
        {
            string name = target.Relative.Replace('\\', '-').Replace('/', '-');
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return Path.Combine(ModFolder(target), name);
        }

        private static string OriginalPath(DjTextureTarget target) =>
            Path.Combine(StateFolder(target), "original.atex");

        private static string StatePath(DjTextureTarget target) =>
            Path.Combine(StateFolder(target), "state.json");

        private static TextureState? ReadState(DjTextureTarget target)
        {
            Migrate(target);
            return ReadStateAt(StatePath(target));
        }

        private static TextureState? ReadStateAt(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var node = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return new TextureState
                {
                    OriginalSha = node["OriginalSha"]?.ToString(),
                    AppliedSha = node["AppliedSha"]?.ToString(),
                    Relative = node["Relative"]?.ToString(),
                    Applied = (node["Applied"] as JArray)?.Select(v => v.ToString()).ToList()
                              ?? new List<string>(),
                };
            }
            catch
            {
                // An unreadable note about a backup is a reason to take a fresh one, not to fail.
                return null;
            }
        }

        private static void WriteState(DjTextureTarget target, TextureState state)
        {
            Directory.CreateDirectory(StateFolder(target));
            WriteStateAt(StatePath(target), state, target.Relative);
        }

        private static void WriteStateAt(string path, TextureState state, string relative)
        {
            var node = new JObject
            {
                ["OriginalSha"] = state.OriginalSha,
                ["AppliedSha"] = state.AppliedSha,
                ["Relative"] = state.Relative ?? relative,
                ["Applied"] = new JArray(state.Applied),
            };
            File.WriteAllText(path, node.ToString(), new UTF8Encoding(false));
        }

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}
