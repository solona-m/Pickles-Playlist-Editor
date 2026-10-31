using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Pickles_Playlist_Editor.Utils
{
    /// <summary>The file system and manifest as the animation core sees them; the harness fakes it.</summary>
    internal interface IAnimationHost
    {
        string StateFolder { get; }
        byte[] ReadFile(string full);
        void WriteFile(string full, byte[] bytes);
        void DeleteFile(string full);
        void DeleteDirectory(string full);

        /// <summary>Hands every option group to <paramref name="edit"/>; writes the ones it reports changed.</summary>
        void EditGroups(Func<JObject, bool> edit);

        string NewToken();
    }

    internal enum AnimationStatusKind { None, Author, Ours, Interrupted, Orphaned, Unsupported }

    internal sealed record AnimationStatus(AnimationStatusKind Kind, int Frames = 0, string? Reason = null);

    internal enum StillRoute { Existing, PasteThenRestore, OneFrameAnimation }

    internal enum TableText { Default, YourAnimation, YourStill, AuthorAnimation, Interrupted, Orphaned, Unsupported, ScanFailed }

    internal sealed record TableCardView(TableText StateKey, bool PlaysFrames, bool RestoreEnabled,
        bool TableChangeAllowed, bool AnimationAllowed, StillRoute? StillRoute);

    internal sealed record AnimationSummary(int Frames, double Seconds, long AddedBytes, bool CapNote);

    /// <summary>What the table card offers, as one ordered set of rules.</summary>
    internal static class TableCard
    {
        public static TableCardView Describe(AnimationStatus status, AnimationMode? mode, bool hasState, bool stillCustomised)
        {
            if (status.Kind == AnimationStatusKind.Orphaned)
                return new(TableText.Orphaned, false, false, false, false, null);

            bool unsupported = status.Kind == AnimationStatusKind.Unsupported;
            var route = mode == AnimationMode.InPlace && !unsupported ? StillRoute.OneFrameAnimation
                : hasState ? StillRoute.PasteThenRestore : StillRoute.Existing;

            var key = status.Kind switch
            {
                AnimationStatusKind.Ours => status.Frames > 1 ? TableText.YourAnimation : TableText.YourStill,
                AnimationStatusKind.Author => TableText.AuthorAnimation,
                AnimationStatusKind.Interrupted => TableText.Interrupted,
                AnimationStatusKind.Unsupported => TableText.Unsupported,
                _ => TableText.Default,
            };

            return new(key,
                PlaysFrames: status.Kind == AnimationStatusKind.Ours && status.Frames > 1,
                RestoreEnabled: stillCustomised || hasState,
                TableChangeAllowed: true,
                AnimationAllowed: !unsupported,
                StillRoute: route);
        }

        public static TableCardView ScanFailed(bool stateReadable) =>
            new(TableText.ScanFailed, false, stateReadable, false, false, null);
    }

    /// <summary>
    /// Applying and removing a table animation. Every write goes through <see cref="IAnimationHost"/>
    /// in an order where each in-between state is a working mod, with the intent recorded first so
    /// an interrupted run can always be put back.
    /// </summary>
    internal static class DjTableAnimationCore
    {
        public static AnimationStatus Status(DjTableAnimationSite site)
        {
            if (site.Orphaned) return new(AnimationStatusKind.Orphaned);
            if (site.Problem != null) return new(AnimationStatusKind.Unsupported, Reason: site.Problem);
            if (!site.HasState)
                return site.Mode == AnimationMode.InPlace
                    ? new(AnimationStatusKind.Author, site.AuthorFrames)
                    : new(AnimationStatusKind.None);

            var state = site.State!;
            bool live = state.Phase == AnimationState.Applied && state.Avfx.All(a =>
            {
                var entry = site.Avfx.FirstOrDefault(s => DjAnimationScan.SameRelative(s.Relative, a.Relative));
                return entry?.CurrentSha != null
                    && string.Equals(entry.CurrentSha, a.AppliedSha, StringComparison.OrdinalIgnoreCase);
            });
            return live ? new(AnimationStatusKind.Ours, state.Frames.Count) : new(AnimationStatusKind.Interrupted);
        }

        /// <summary>Where a caught failure's exception goes; the app points this at its log.</summary>
        public static Action<Exception>? Failed { get; set; }

        public static FrameTimeline? LiveTimeline(AnimationState? state) =>
            state == null || state.Frames.Count == 0 || state.StartTicks.Count != state.Frames.Count
                ? null
                : new FrameTimeline(state.StartTicks, state.LoopTicks);

        public static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        // ---- apply -------------------------------------------------------------------------------

        public static bool Apply(IAnimationHost host, DjTableAnimationSite site, IReadOnlyList<BgraImage> frames,
            FrameTimeline timeline, FitMode fit, IProgress<int>? progress,
            out IReadOnlyList<string> warnings, out string error)
        {
            // Anything that throws before the mod is touched still comes back as a refusal.
            try
            {
                return ApplyCore(host, site, frames, timeline, fit, progress, out warnings, out error);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                warnings = Array.Empty<string>();
                error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                return false;
            }
        }

        private static bool ApplyCore(IAnimationHost host, DjTableAnimationSite site, IReadOnlyList<BgraImage> frames,
            FrameTimeline timeline, FitMode fit, IProgress<int>? progress,
            out IReadOnlyList<string> warnings, out string error)
        {
            var notes = new List<string>();
            warnings = notes;
            if (!CheckSite(host, site, refuseProblem: true, out error)) return false;

            int n = frames.Count;
            if (n < 1 || timeline.StartTicks.Count != n)
                throw new ArgumentException("The frames and their timeline do not match.");
            if (n > site.Capacity)
            {
                error = AnimationText.Format(AnimationMessage.TooManyTextures, string.Empty);
                return false;
            }

            var previous = site.State;
            string token = previous?.ModToken is { Length: > 0 } t ? t : host.NewToken();
            int gen = (previous?.Generation ?? 0) + 1;

            var planned = Enumerable.Range(0, n).Select(k => new AnimationFrame
            {
                Relative = $"{DjAnimationScan.FrameFolder}\\{token}_{gen}_{k:D2}.atex",
                GamePath = $"{AvfxTableAnimation.FramePrefix}{token}_{gen}_{k:D2}.atex",
            }).ToList();

            // Precompute everything before the first write.
            byte[] atlas = host.ReadFile(site.Table.FullPath);
            var surface = site.Table.Surface;
            var encoded = new byte[n][];
            int done = 0;
            string? encodeError = null;
            Parallel.For(0, n, k =>
            {
                var bytes = TexPaste.PasteRect(atlas, surface.Rect, surface.ToTextureSpace(frames[k], fit), out string e);
                if (bytes == null) encodeError = e;
                else encoded[k] = bytes;
                progress?.Report(System.Threading.Interlocked.Increment(ref done));
            });
            if (encodeError != null) { error = encodeError; return false; }
            for (int k = 0; k < n; k++) planned[k].Sha = AvfxTableAnimation.Sha(encoded[k]);

            var gamePaths = planned.Select(f => f.GamePath).ToList();
            var builds = new List<(SiteAvfx Avfx, byte[] Bytes, string OriginalSha)>();
            foreach (var avfx in site.Animatable)
            {
                var built = AvfxTableAnimation.Build(avfx.Baseline!, avfx.Inspect!.Info!, gamePaths, timeline, out string problem);
                if (built == null) { error = problem; return false; }
                var entry = previous?.Find(avfx.Relative);
                string original = avfx.Ours && entry != null ? entry.OriginalSha : avfx.CurrentSha!;
                builds.Add((avfx, built, original));
            }

            // Collisions: a planned file or key that already exists is an anomaly, not something to overwrite.
            foreach (var frame in planned)
                if (File.Exists(Path.Combine(site.ModRoot, frame.Relative)))
                {
                    error = AnimationText.Format(AnimationMessage.Collision, frame.Relative);
                    return false;
                }
            var plannedKeys = new HashSet<string>(gamePaths, StringComparer.OrdinalIgnoreCase);
            string? keyHit = null;
            host.EditGroups(group => { keyHit ??= AllFiles(group).SelectMany(f => f.Properties()).Select(p => p.Name).FirstOrDefault(plannedKeys.Contains); return false; });
            if (keyHit != null) { error = AnimationText.Format(AnimationMessage.Collision, keyHit); return false; }

            // 3. Pristine copies, write-once.
            foreach (var (avfx, _, original) in builds)
            {
                string pristine = DjAnimationScan.PristinePath(host.StateFolder, original);
                if (!File.Exists(pristine)) host.WriteFile(pristine, avfx.Baseline!);
            }

            // 4. Intent.
            var applying = previous?.Copy() ?? new AnimationState();
            applying.Phase = AnimationState.Applying;
            applying.Generation = gen;
            applying.ModToken = token;
            applying.Mode = site.Mode?.ToString();
            applying.StaleFrames = applying.StaleFrames.Concat(applying.PendingFrames).ToList();
            applying.PendingFrames = planned;
            var rewritten = new HashSet<string>(builds.Select(b => b.Avfx.Relative), StringComparer.OrdinalIgnoreCase);
            applying.Avfx = applying.Avfx.Where(a => rewritten.Contains(DjAnimationScan.Normalise(a.Relative))
                || site.Avfx.Any(s => s.Ours && DjAnimationScan.SameRelative(s.Relative, a.Relative))).ToList();
            foreach (var (avfx, built, original) in builds)
            {
                var entry = applying.Find(avfx.Relative);
                if (entry == null) applying.Avfx.Add(entry = new AnimationAvfx { Relative = avfx.Relative });
                if (!string.Equals(entry.OriginalSha, original, StringComparison.OrdinalIgnoreCase)) entry.AppliedSha = null;
                entry.OriginalSha = original;
                entry.PendingSha = AvfxTableAnimation.Sha(built);
            }
            WriteState(host, applying);

            var written = new List<(SiteAvfx Avfx, byte[] Previous)>();
            var framesWritten = new List<AnimationFrame>();
            bool keysAdded = false;
            var frameMap = planned.ToDictionary(f => f.GamePath, f => f.Relative);
            try
            {
                // 5. Frames beside the live ones.
                for (int k = 0; k < n; k++)
                {
                    host.WriteFile(Path.Combine(site.ModRoot, planned[k].Relative), encoded[k]);
                    framesWritten.Add(planned[k]);
                }

                // 6. Add keys only, so a live animation keeps working.
                keysAdded = true;
                host.EditGroups(group => AddFrameKeys(group, rewritten, frameMap));

                // 7. Effect files.
                foreach (var (avfx, built, _) in builds)
                {
                    byte[] before = host.ReadFile(avfx.FullPath);
                    written.Add((avfx, before));
                    host.WriteFile(avfx.FullPath, built);
                }

                // 8. Live.
                var applied = applying.Copy();
                applied.Phase = AnimationState.Applied;
                applied.StaleFrames = (previous?.Frames ?? new List<AnimationFrame>()).Concat(applying.StaleFrames)
                    .GroupBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                applied.Frames = planned;
                applied.PendingFrames = new List<AnimationFrame>();
                applied.StartTicks = timeline.StartTicks.ToList();
                applied.LoopTicks = timeline.LoopTicks;
                foreach (var entry in applied.Avfx.Where(a => rewritten.Contains(DjAnimationScan.Normalise(a.Relative))))
                {
                    entry.AppliedSha = entry.PendingSha;
                    entry.PendingSha = null;
                }
                WriteState(host, applied);
                applying = applied;
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                error = Rollback(host, site, previous, token, gen, written, keysAdded, plannedKeys, framesWritten, notes, ex);
                return false;
            }

            // 9. Cleanup; failures only warn, the next run finishes it.
            Cleanup(host, site, applying, notes);
            error = string.Empty;
            return true;
        }

        private static string Rollback(IAnimationHost host, DjTableAnimationSite site, AnimationState? previous, string token, int gen,
            List<(SiteAvfx Avfx, byte[] Previous)> written, bool keysAdded, HashSet<string> plannedKeys,
            List<AnimationFrame> framesWritten, List<string> notes, Exception cause)
        {
            string? stuck = null;
            foreach (var (avfx, before) in written)
            {
                try { host.WriteFile(avfx.FullPath, before); }
                catch { stuck ??= avfx.Relative; }
            }
            if (stuck != null) return AnimationText.Format(AnimationMessage.RollbackStopped, stuck);

            if (keysAdded)
            {
                try { host.EditGroups(group => RemoveKeys(group, plannedKeys)); }
                catch { return AnimationText.Format(AnimationMessage.RollbackStopped, "manifest"); }
            }

            var leftovers = new List<AnimationFrame>();
            foreach (var frame in framesWritten)
            {
                try { host.DeleteFile(Path.Combine(site.ModRoot, frame.Relative)); }
                catch
                {
                    leftovers.Add(frame);
                    notes.Add(AnimationText.Format(AnimationMessage.StaleNotDeleted, frame.Relative));
                }
            }

            try
            {
                if (previous != null)
                {
                    var restored = previous.Copy();
                    restored.Generation = gen;
                    restored.StaleFrames.AddRange(leftovers);
                    WriteState(host, restored);
                }
                else if (leftovers.Count == 0)
                {
                    host.DeleteDirectory(host.StateFolder);
                }
                else
                {
                    WriteState(host, new AnimationState
                    {
                        ModToken = token,
                        Generation = gen,
                        StaleFrames = leftovers,
                    });
                }
            }
            catch
            {
                // The Applying state stays on disk, so the card offers Restore.
            }

            return AnimationText.Format(AnimationMessage.WriteFailed, cause.Message);
        }

        /// <summary>Removes old keys, then old files; whatever cannot go stays in StaleFrames.</summary>
        private static void Cleanup(IAnimationHost host, DjTableAnimationSite site, AnimationState state, List<string> notes)
        {
            if (state.StaleFrames.Count == 0) return;
            try
            {
                var keep = new HashSet<string>(state.Frames.Select(f => f.GamePath), StringComparer.OrdinalIgnoreCase);
                host.EditGroups(group => PruneFrameKeys(group, keep));
            }
            catch (Exception ex)
            {
                notes.Add(AnimationText.Format(AnimationMessage.WriteFailed, ex.Message));
                return;
            }

            var remaining = new List<AnimationFrame>();
            foreach (var frame in state.StaleFrames)
                if (!DeleteIfOurs(host, site, frame, notes)) remaining.Add(frame);

            if (remaining.Count == state.StaleFrames.Count) return;
            state.StaleFrames = remaining;
            try { WriteState(host, state); }
            catch (Exception ex) { notes.Add(AnimationText.Format(AnimationMessage.WriteFailed, ex.Message)); }
        }

        /// <summary>True when the file is gone afterwards; an edited file is kept with a warning.</summary>
        private static bool DeleteIfOurs(IAnimationHost host, DjTableAnimationSite site, AnimationFrame frame, List<string> notes)
        {
            string full = Path.Combine(site.ModRoot, frame.Relative);
            byte[] bytes;
            try { bytes = host.ReadFile(full); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
            catch
            {
                notes.Add(AnimationText.Format(AnimationMessage.StaleNotDeleted, frame.Relative));
                return false;
            }

            if (!string.Equals(AvfxTableAnimation.Sha(bytes), frame.Sha, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(AnimationText.Format(AnimationMessage.KeptEditedFrame, frame.Relative));
                return true;
            }

            try
            {
                host.DeleteFile(full);
                return true;
            }
            catch
            {
                notes.Add(AnimationText.Format(AnimationMessage.StaleNotDeleted, frame.Relative));
                return false;
            }
        }

        // ---- restore -----------------------------------------------------------------------------

        public static bool Restore(IAnimationHost host, DjTableAnimationSite site,
            out IReadOnlyList<string> warnings, out string error)
        {
            var notes = new List<string>();
            warnings = notes;
            try
            {
                if (!CheckSite(host, site, refuseProblem: false, out error)) return false;
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                return false;
            }

            var state = site.State;
            if (state == null) return true;

            try
            {
                foreach (var entry in state.Avfx)
                {
                    var avfx = site.Avfx.FirstOrDefault(a => DjAnimationScan.SameRelative(a.Relative, entry.Relative));
                    if (avfx == null || avfx.CurrentSha == null)
                    {
                        notes.Add(AnimationText.Format(AnimationMessage.FileMissing, entry.Relative));
                        continue;
                    }
                    if (!avfx.Ours)
                    {
                        notes.Add(AnimationText.Format(AnimationMessage.LeftAlone, entry.Relative));
                        continue;
                    }
                    if (string.Equals(avfx.CurrentSha, entry.OriginalSha, StringComparison.OrdinalIgnoreCase)) continue;
                    host.WriteFile(avfx.FullPath, host.ReadFile(DjAnimationScan.PristinePath(host.StateFolder, entry.OriginalSha)));
                }

                host.EditGroups(group => PruneFrameKeys(group, new HashSet<string>()));

                var all = state.Frames.Concat(state.PendingFrames).Concat(state.StaleFrames)
                    .GroupBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
                var leftovers = all.Where(frame => !DeleteIfOurs(host, site, frame, notes)).ToList();

                string folder = Path.Combine(site.ModRoot, DjAnimationScan.FrameFolder);
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    host.DeleteDirectory(folder);

                // A frame that would not delete stays listed, so the next run removes it.
                if (leftovers.Count == 0)
                    host.DeleteDirectory(host.StateFolder);
                else
                    WriteState(host, new AnimationState
                    {
                        ModToken = state.ModToken,
                        Generation = state.Generation,
                        StaleFrames = leftovers,
                    });
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// A still on a table this app animated by appending: paste first (invisible while the panel
        /// is hidden), then remove the animation, so a failed paste leaves the animation as it was.
        /// </summary>
        public static bool PasteThenRestore(IAnimationHost host, DjTableAnimationSite site, Func<string?> paste,
            out IReadOnlyList<string> warnings, out string error)
        {
            warnings = Array.Empty<string>();
            string? pasteError;
            try { pasteError = paste(); }
            catch (Exception ex) { pasteError = ex.Message; }
            if (pasteError != null)
            {
                error = pasteError;
                return false;
            }
            return Restore(host, site, out warnings, out error);
        }

        /// <summary>Refusals shared by Apply and Restore: nothing is written when any of these hit.</summary>
        private static bool CheckSite(IAnimationHost host, DjTableAnimationSite site, bool refuseProblem, out string error)
        {
            error = string.Empty;
            if (site.Orphaned)
            {
                error = AnimationText.Format(AnimationMessage.Orphaned, string.Empty);
                return false;
            }

            var unreadable = site.Avfx.FirstOrDefault(a => a.Unreadable);
            if (unreadable != null)
            {
                error = AnimationText.Format(AnimationMessage.FileUnreadable, unreadable.Relative);
                return false;
            }

            if (refuseProblem && site.Problem != null)
            {
                error = site.Problem;
                return false;
            }

            string? stateSha = null;
            try { stateSha = AvfxTableAnimation.Sha(host.ReadFile(DjAnimationScan.StatePath(host.StateFolder))); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            if (!string.Equals(stateSha, site.StateSha, StringComparison.OrdinalIgnoreCase))
            {
                error = AnimationText.Format(AnimationMessage.ModChanged, string.Empty);
                return false;
            }

            foreach (var avfx in site.Avfx)
            {
                string? sha;
                try { sha = AvfxTableAnimation.Sha(host.ReadFile(avfx.FullPath)); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { sha = null; }
                catch
                {
                    error = AnimationText.Format(AnimationMessage.FileUnreadable, avfx.Relative);
                    return false;
                }
                if (!string.Equals(sha, avfx.CurrentSha, StringComparison.OrdinalIgnoreCase))
                {
                    error = AnimationText.Format(AnimationMessage.ModChanged, avfx.Relative);
                    return false;
                }
            }
            return true;
        }

        private static void WriteState(IAnimationHost host, AnimationState state) =>
            host.WriteFile(DjAnimationScan.StatePath(host.StateFolder), state.ToBytes());

        // ---- preview -----------------------------------------------------------------------------

        public static List<BgraImage> PreviewFrames(byte[] atlas, DjTextureSurface surface,
            IReadOnlyList<BgraImage> frames, FitMode fit)
        {
            var result = new BgraImage[frames.Count];
            Parallel.For(0, frames.Count, k =>
            {
                var rect = TexPaste.RoundTripRect(atlas, surface.Rect, surface.ToTextureSpace(frames[k], fit), out _);
                result[k] = rect == null ? frames[k] : ImageOps.Rotate(rect, surface.QuarterTurns);
            });
            return result.ToList();
        }

        public static AnimationSummary Summarise(DjTableAnimationSite site, int frames, FrameTimeline timeline, bool thinned)
        {
            long atlas = 0;
            try { atlas = new FileInfo(site.Table.FullPath).Length; } catch { }
            return new(frames, timeline.LoopTicks / (double)FramePlan.TicksPerSecond, atlas * frames, thinned);
        }

        // ---- manifest ----------------------------------------------------------------------------

        /// <summary>Adds the frame keys to every option and container that maps one of these effects.</summary>
        public static bool AddFrameKeys(JObject group, ISet<string> avfxRelatives, IReadOnlyDictionary<string, string> frames)
        {
            bool changed = false;
            foreach (var files in AllFiles(group))
            {
                bool maps = files.Properties().Any(p => p.Value.Type == JTokenType.String
                    && avfxRelatives.Any(r => DjAnimationScan.SameRelative(r, p.Value.ToString())));
                if (!maps) continue;
                foreach (var (gamePath, relative) in frames)
                {
                    if (files.ContainsKey(gamePath)) continue;
                    files[gamePath] = relative;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Drops every frame key this app added, except the ones to keep.</summary>
        public static bool PruneFrameKeys(JObject group, ISet<string> keepGamePaths)
        {
            bool changed = false;
            foreach (var files in AllFiles(group))
                foreach (var property in files.Properties().ToList())
                {
                    if (!IsFrameKey(property) || keepGamePaths.Contains(property.Name)) continue;
                    property.Remove();
                    changed = true;
                }
            return changed;
        }

        private static bool RemoveKeys(JObject group, ISet<string> gamePaths)
        {
            bool changed = false;
            foreach (var files in AllFiles(group))
                foreach (var property in files.Properties().Where(p => gamePaths.Contains(p.Name)).ToList())
                {
                    property.Remove();
                    changed = true;
                }
            return changed;
        }

        /// <summary>A warning when an option that maps one of these effects lacks the expected frames.</summary>
        public static string? VerifyManifest(IEnumerable<JObject> groups, ISet<string> avfxRelatives,
            IReadOnlyDictionary<string, string> expected)
        {
            foreach (var group in groups)
                foreach (var files in AllFiles(group))
                {
                    bool maps = files.Properties().Any(p => p.Value.Type == JTokenType.String
                        && avfxRelatives.Any(r => DjAnimationScan.SameRelative(r, p.Value.ToString())));
                    if (!maps) continue;
                    foreach (var (gamePath, relative) in expected)
                        if (files[gamePath]?.ToString() is not { } value || !DjAnimationScan.SameRelative(value, relative))
                            return AnimationText.Format(AnimationMessage.ManifestLost, group["Name"]?.ToString() ?? string.Empty);
                }
            return null;
        }

        private static bool IsFrameKey(JProperty property) =>
            property.Name.StartsWith(AvfxTableAnimation.FramePrefix, StringComparison.OrdinalIgnoreCase)
            && property.Value.Type == JTokenType.String
            && DjAnimationScan.Normalise(property.Value.ToString())
                .StartsWith(DjAnimationScan.FrameFolder + "\\", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<JObject> AllFiles(JObject group)
        {
            foreach (string list in new[] { "Options", "Containers" })
                if (group[list] is JArray entries)
                    foreach (var entry in entries.OfType<JObject>())
                        if (entry["Files"] is JObject files) yield return files;
        }
    }
}
