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

    /// <summary>Overlay frames for a site: one crop of the atlas window, one plan per table effect.</summary>
    internal sealed record OverlayRoute(TexRect Window, TexRect RectInWindow, byte[] Crop,
        IReadOnlyDictionary<string, OverlayPlan> Plans);

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

    /// <summary>What the laptop card offers. Interrupted and orphaned runs stay on the table card.</summary>
    internal static class LaptopCard
    {
        public static TableCardView Describe(DjTableAnimationSite site, bool stillCustomised)
        {
            var status = DjTableAnimationCore.Status(site, AnimatedSurface.Laptop);
            bool ours = status.Kind == AnimationStatusKind.Ours;
            return new(ours ? status.Frames > 1 ? TableText.YourAnimation : TableText.YourStill : TableText.Default,
                PlaysFrames: ours && status.Frames > 1,
                RestoreEnabled: stillCustomised || ours,
                TableChangeAllowed: true,
                AnimationAllowed: (status.Kind is AnimationStatusKind.None or AnimationStatusKind.Ours) && site.LaptopProblem == null,
                StillRoute: ours ? StillRoute.PasteThenRestore : StillRoute.Existing);
        }
    }

    /// <summary>
    /// Applying and removing a table animation. Every write goes through <see cref="IAnimationHost"/>
    /// in an order where each in-between state is a working mod, with the intent recorded first so
    /// an interrupted run can always be put back.
    /// </summary>
    internal static class DjTableAnimationCore
    {
        public static AnimationStatus Status(DjTableAnimationSite site) => Status(site, AnimatedSurface.Table);

        public static AnimationStatus Status(DjTableAnimationSite site, AnimatedSurface surface)
        {
            if (site.Orphaned) return new(AnimationStatusKind.Orphaned);
            if (site.Problem != null) return new(AnimationStatusKind.Unsupported, Reason: site.Problem);
            bool table = surface == AnimatedSurface.Table;
            if (!site.HasState)
                return table && site.Mode == AnimationMode.InPlace
                    ? new(AnimationStatusKind.Author, site.AuthorFrames)
                    : new(AnimationStatusKind.None);

            if (!IsLive(site)) return new(AnimationStatusKind.Interrupted);
            var state = site.State!;
            if (!table)
                return state.Laptop is { Frames.Count: > 0 } lap ? new(AnimationStatusKind.Ours, lap.Frames.Count) : new(AnimationStatusKind.None);
            if (state.Frames.Count > 0) return new(AnimationStatusKind.Ours, state.Frames.Count);
            if (site.Mode == AnimationMode.InPlace) return new(AnimationStatusKind.Author, site.AuthorFrames);
            // Only the laptop is animated; the table may have no room left beside it.
            return site.Capacity < 2
                ? new(AnimationStatusKind.Unsupported, Reason: AnimationText.Format(AnimationMessage.TooManyTextures, string.Empty))
                : new(AnimationStatusKind.None);
        }

        /// <summary>The state was applied and every effect it lists still holds what was written.</summary>
        public static bool IsLive(DjTableAnimationSite site)
        {
            var state = site.State;
            return state != null && state.Phase == AnimationState.Applied && state.Avfx.All(a =>
            {
                var entry = site.Avfx.FirstOrDefault(s => DjAnimationScan.SameRelative(s.Relative, a.Relative));
                return entry?.CurrentSha != null
                    && string.Equals(entry.CurrentSha, a.AppliedSha, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>Whether this surface has something of ours the card can remove.</summary>
        public static bool HasLayer(DjTableAnimationSite site, AnimatedSurface surface)
        {
            if (!site.HasState) return false;
            bool live = IsLive(site);
            return surface == AnimatedSurface.Table
                ? !live || site.State!.Frames.Count > 0
                : live && AnimationLayers.LaptopOf(site.State) != null;
        }

        /// <summary>Every live frame key of both surfaces, game path to file.</summary>
        public static IReadOnlyDictionary<string, string> LiveFrameKeys(AnimationState state)
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in state.Frames.Concat(state.Laptop?.Frames ?? new List<AnimationFrame>())) keys[f.GamePath] = f.Relative;
            return keys;
        }

        /// <summary>Where a caught failure's exception goes; the app points this at its log.</summary>
        public static Action<Exception>? Failed { get; set; }

        /// <summary>Where a fall back to full-atlas frames is reported; the app points this at its log.</summary>
        public static Action<string>? OverlaySkipped { get; set; }

        /// <summary>Test seam: replaces <see cref="PlanOverlay"/> in Apply and Summarise when set.</summary>
        internal static Func<DjTableAnimationSite, byte[], OverlayRoute?>? OverlayPlanner { get; set; }

        /// <summary>Test seam: replaces the laptop's <see cref="AnimationLayers.PlanLaptop"/> when set.</summary>
        internal static Func<DjTableAnimationSite, byte[], OverlayRoute?>? LaptopPlanner { get; set; }

        /// <summary>Plans every animatable effect against the atlas; null, with the reason, when any refuses.</summary>
        public static OverlayRoute? PlanOverlay(DjTableAnimationSite site, byte[] atlas, out string refusal)
        {
            refusal = string.Empty;
            var plans = new Dictionary<string, OverlayPlan>(StringComparer.OrdinalIgnoreCase);
            OverlayPlan? first = null;
            foreach (var avfx in site.Animatable)
            {
                var plan = AvfxTableOverlay.Plan(avfx.Baseline!, avfx.Inspect!.Info!, site.Table.Surface.Rect, atlas,
                    PanelUvMap.Calibrated, out var r);
                if (plan == null) { refusal = $"{r} {avfx.Relative}"; return null; }
                first ??= plan;
                if (plan.Window != first.Window || plan.RectInWindow != first.RectInWindow) { refusal = $"NoWindow {avfx.Relative}"; return null; }
                plans[DjAnimationScan.Normalise(avfx.Relative)] = plan;
            }
            if (first == null) { refusal = "NoPanelGeometry"; return null; }
            var crop = TexCrop.Crop(atlas, first.Window, out string e);
            if (crop == null) { refusal = "AtlasUnreadable crop: " + e; return null; }
            return new OverlayRoute(first.Window, first.RectInWindow, crop, plans);
        }

        /// <summary>One written frame as the card shows it (upright), or null when it cannot be read.</summary>
        public static BgraImage? ReadFrame(byte[] frameFile, AnimationState state, DjTextureSurface surface) =>
            ReadFrame(frameFile, state.FrameRect, surface);

        public static BgraImage? ReadFrame(byte[] frameFile, TexRect? frameRect, DjTextureSurface surface)
        {
            var rect = TexPaste.ReadRect(frameFile, frameRect ?? surface.Rect, out _);
            return rect == null ? null : ImageOps.Rotate(rect, surface.QuarterTurns);
        }

        public static FrameTimeline? LiveTimeline(AnimationState? state) =>
            state == null || state.Frames.Count == 0 || state.StartTicks.Count != state.Frames.Count
                ? null
                : new FrameTimeline(state.StartTicks, state.LoopTicks);

        public static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        // ---- apply -------------------------------------------------------------------------------

        public static bool Apply(IAnimationHost host, DjTableAnimationSite site, IReadOnlyList<BgraImage> frames,
            FrameTimeline timeline, FitMode fit, IProgress<int>? progress,
            out IReadOnlyList<string> warnings, out string error) =>
            Apply(host, site, AnimatedSurface.Table, frames, timeline, fit, progress, out warnings, out error, out _);

        /// <summary>
        /// Animates one surface. The laptop has no full-atlas route: a laptop the overlay refuses comes
        /// back with <paramref name="overlayRefused"/> set and nothing written.
        /// </summary>
        public static bool Apply(IAnimationHost host, DjTableAnimationSite site, AnimatedSurface surface,
            IReadOnlyList<BgraImage> frames, FrameTimeline timeline, FitMode fit, IProgress<int>? progress,
            out IReadOnlyList<string> warnings, out string error, out bool overlayRefused)
        {
            // Anything that throws before the mod is touched still comes back as a refusal.
            try
            {
                return ApplyCore(host, site, surface, frames, timeline, fit, progress, out warnings, out error, out overlayRefused);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                warnings = Array.Empty<string>();
                error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                overlayRefused = false;
                return false;
            }
        }

        /// <summary>Reads each surface's texture at most once.</summary>
        private static Func<AnimatedSurface, byte[]> AtlasReader(IAnimationHost host, DjTableAnimationSite site, byte[]? table = null)
        {
            var cache = new Dictionary<AnimatedSurface, byte[]>();
            if (table != null) cache[AnimatedSurface.Table] = table;
            return s => cache.TryGetValue(s, out var b) ? b
                : cache[s] = host.ReadFile(s == AnimatedSurface.Table ? site.Table.FullPath : site.Laptop!.FullPath);
        }

        /// <summary>Recomposing the live layers must give the live bytes; anything else is drift we must not overwrite.</summary>
        private static string? LiveCheck(DjTableAnimationSite site, Func<AnimatedSurface, byte[]> atlas)
        {
            var state = site.State!;
            AnimationLayer? table = AnimationLayers.TableOf(state), laptop = AnimationLayers.LaptopOf(state);
            foreach (var avfx in site.Animatable)
            {
                var entry = state.Find(avfx.Relative);
                if (entry?.AppliedSha == null) continue;
                var built = AnimationLayers.Compose(avfx, site, table, laptop, atlas, out string problem);
                if (built == null) return problem;
                if (!string.Equals(AvfxTableAnimation.Sha(built), entry.AppliedSha, StringComparison.OrdinalIgnoreCase))
                    return "drift " + avfx.Relative;
            }
            return null;
        }

        private static bool ApplyCore(IAnimationHost host, DjTableAnimationSite site, AnimatedSurface surface,
            IReadOnlyList<BgraImage> frames, FrameTimeline timeline, FitMode fit, IProgress<int>? progress,
            out IReadOnlyList<string> warnings, out string error, out bool overlayRefused)
        {
            var notes = new List<string>();
            warnings = notes;
            overlayRefused = false;
            if (!CheckSite(host, site, refuseProblem: true, out error)) return false;

            bool laptop = surface == AnimatedSurface.Laptop;
            int n = frames.Count;
            if (n < 1 || timeline.StartTicks.Count != n)
                throw new ArgumentException("The frames and their timeline do not match.");
            if (laptop && site.LaptopProblem != null)
            {
                OverlaySkipped?.Invoke(site.LaptopProblem);
                overlayRefused = true;
                error = site.LaptopProblem;
                return false;
            }
            if (n > (laptop ? site.LaptopCapacity : site.Capacity))
            {
                error = AnimationText.Format(AnimationMessage.TooManyTextures, string.Empty);
                return false;
            }

            var previous = site.State;
            string token = previous?.ModToken is { Length: > 0 } t ? t : host.NewToken();
            int gen = (previous?.Generation ?? 0) + 1;
            string kind = laptop ? "lap" : string.Empty;

            var planned = Enumerable.Range(0, n).Select(k => new AnimationFrame
            {
                Relative = $"{DjAnimationScan.FrameFolder}\\{token}_{gen}_{kind}{k:D2}.atex",
                GamePath = $"{AvfxTableAnimation.FramePrefix}{token}_{gen}_{kind}{k:D2}.atex",
            }).ToList();

            // Precompute everything before the first write. The other surface's live layer is carried.
            var gamePaths = planned.Select(f => f.GamePath).ToList();
            var carriedTable = laptop ? AnimationLayers.TableOf(previous) : null;
            var carriedLaptop = laptop ? null : AnimationLayers.LaptopOf(previous);
            var atlasOf = AtlasReader(host, site, laptop ? null : host.ReadFile(site.Table.FullPath));
            if ((carriedTable ?? carriedLaptop) != null && IsLive(site) && LiveCheck(site, atlasOf) is { } drift)
            {
                error = AnimationText.Format(AnimationMessage.LayerNotKept, drift);
                return false;
            }

            string Original(SiteAvfx avfx) =>
                avfx.Ours && previous?.Find(avfx.Relative) is { } entry ? entry.OriginalSha : avfx.CurrentSha!;

            byte[][] encoded;
            TexRect? frameRect;
            var builds = new List<(SiteAvfx Avfx, byte[] Bytes, string OriginalSha)>();
            if (laptop)
            {
                var route = TryLaptop(site, atlasOf, carriedTable, frames, timeline, fit, gamePaths, progress, out error, out overlayRefused);
                if (route is not { } r) return false;
                encoded = r.Encoded;
                frameRect = r.FrameRect;
                builds.AddRange(r.Builds.Select(b => (b.Avfx, b.Bytes, Original(b.Avfx))));
            }
            else
            {
                byte[] atlas = atlasOf(AnimatedSurface.Table);
                var surfaceInfo = site.Table.Surface;
                var overlay = site.Mode == AnimationMode.Append ? TryOverlay(site, atlas, frames, timeline, fit, gamePaths, progress) : null;
                frameRect = overlay?.Route.RectInWindow;
                if (overlay is { } o)
                {
                    encoded = o.Encoded;
                    builds.AddRange(o.Builds.Select(b => (b.Avfx, b.Bytes, Original(b.Avfx))));
                }
                else
                {
                    encoded = new byte[n][];
                    int done = 0;
                    string? encodeError = null;
                    Parallel.For(0, n, k =>
                    {
                        var bytes = TexPaste.PasteRect(atlas, surfaceInfo.Rect, surfaceInfo.ToTextureSpace(frames[k], fit), out string e);
                        if (bytes == null) encodeError = e;
                        else encoded[k] = bytes;
                        progress?.Report(System.Threading.Interlocked.Increment(ref done));
                    });
                    if (encodeError != null) { error = encodeError; return false; }

                    foreach (var avfx in site.Animatable)
                    {
                        var built = AvfxTableAnimation.Build(avfx.Baseline!, avfx.Inspect!.Info!, gamePaths, timeline, out string problem);
                        if (built == null) { error = problem; return false; }
                        builds.Add((avfx, built, Original(avfx)));
                    }
                }

                // A live laptop goes back on top of the new table layer, within the tested size.
                if (carriedLaptop != null)
                    for (int i = 0; i < builds.Count; i++)
                    {
                        var (avfx, tableBuilt, original) = builds[i];
                        var withLaptop = AnimationLayers.Laptop(tableBuilt, site, carriedLaptop, atlasOf(AnimatedSurface.Laptop),
                            FramePlan.MaxFrames, out var refusal, out string problem, out _);
                        if (withLaptop == null)
                        {
                            error = AnimationText.Format(AnimationMessage.LayerNotKept, $"{refusal} {avfx.Relative} {problem}".Trim());
                            return false;
                        }
                        if (!AnimationLayers.WithinTestedSize(withLaptop, AnimationLayers.LaptopInfo(tableBuilt, site)!))
                        {
                            error = AnimationText.Format(AnimationMessage.TooManyTextures, avfx.Relative);
                            return false;
                        }
                        builds[i] = (avfx, withLaptop, original);
                    }
            }
            for (int k = 0; k < n; k++) planned[k].Sha = AvfxTableAnimation.Sha(encoded[k]);

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

            return WriteLayers(host, site, previous, token, gen, planned, encoded, builds, notes, out error, applied =>
            {
                if (laptop)
                {
                    applied.StaleFrames = (previous?.Laptop?.Frames ?? new List<AnimationFrame>()).Concat(applied.StaleFrames).ToList();
                    applied.Laptop = new LaptopAnimation
                    {
                        Frames = planned, StartTicks = timeline.StartTicks.ToList(), LoopTicks = timeline.LoopTicks, FrameRect = frameRect,
                    };
                }
                else
                {
                    applied.StaleFrames = (previous?.Frames ?? new List<AnimationFrame>()).Concat(applied.StaleFrames).ToList();
                    applied.Frames = planned;
                    applied.StartTicks = timeline.StartTicks.ToList();
                    applied.LoopTicks = timeline.LoopTicks;
                    applied.FrameRect = frameRect;
                }
            });
        }

        /// <summary>
        /// Steps 3-9: pristine copies, intent, new frames and keys, effect files, live, cleanup. With no
        /// planned frames this removes a layer: only the effects change.
        /// </summary>
        private static bool WriteLayers(IAnimationHost host, DjTableAnimationSite site, AnimationState? previous, string token, int gen,
            List<AnimationFrame> planned, byte[][] encoded, List<(SiteAvfx Avfx, byte[] Bytes, string OriginalSha)> builds,
            List<string> notes, out string error, Action<AnimationState> live)
        {
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
            var plannedKeys = new HashSet<string>(frameMap.Keys, StringComparer.OrdinalIgnoreCase);
            try
            {
                // 5. Frames beside the live ones.
                for (int k = 0; k < planned.Count; k++)
                {
                    host.WriteFile(Path.Combine(site.ModRoot, planned[k].Relative), encoded[k]);
                    framesWritten.Add(planned[k]);
                }

                // 6. Add keys only, so a live animation keeps working.
                if (planned.Count > 0)
                {
                    keysAdded = true;
                    host.EditGroups(group => AddFrameKeys(group, rewritten, frameMap));
                }

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
                applied.PendingFrames = new List<AnimationFrame>();
                live(applied);
                applied.StaleFrames = applied.StaleFrames
                    .GroupBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
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

        private sealed record LaptopRoute(byte[][] Encoded, TexRect FrameRect, List<(SiteAvfx Avfx, byte[] Bytes)> Builds);

        /// <summary>
        /// The laptop layer on each effect's table-built bytes, encoded in memory. A refusal or
        /// failure sets <paramref name="refused"/>; a carried table that cannot be rebuilt, or too
        /// many frames, is an error instead.
        /// </summary>
        private static LaptopRoute? TryLaptop(DjTableAnimationSite site, Func<AnimatedSurface, byte[]> atlasOf, AnimationLayer? carriedTable,
            IReadOnlyList<BgraImage> frames, FrameTimeline timeline, FitMode fit, IReadOnlyList<string> gamePaths,
            IProgress<int>? progress, out string error, out bool refused)
        {
            string err = string.Empty;
            bool isRefused = false;
            var result = Body();
            error = err;
            refused = isRefused;
            return result;

            LaptopRoute? Body()
            {
                var tableBuilt = new List<(SiteAvfx Avfx, byte[] Bytes)>();
                foreach (var avfx in site.Animatable)
                {
                    var built = AnimationLayers.Table(avfx, site, carriedTable, () => atlasOf(AnimatedSurface.Table), out string problem);
                    if (built == null) { err = AnimationText.Format(AnimationMessage.LayerNotKept, problem); return null; }
                    tableBuilt.Add((avfx, built));
                }

                int tableFrames = carriedTable?.GamePaths.Count ?? 0;
                string Refuse(string reason)
                {
                    OverlaySkipped?.Invoke(reason);
                    isRefused = true;
                    err = reason;
                    return reason;
                }
                try
                {
                    byte[] eq3 = atlasOf(AnimatedSurface.Laptop);
                    string refusal = string.Empty;
                    var route = LaptopPlanner != null ? LaptopPlanner(site, eq3) : AnimationLayers.PlanLaptop(site, eq3, out refusal);
                    if (route == null) { Refuse(refusal.Length > 0 ? refusal : "refused"); return null; }

                    var profile = AnimationLayers.LaptopProfile(site);
                    var builds = new List<(SiteAvfx, byte[])>();
                    foreach (var (avfx, bytes) in tableBuilt)
                    {
                        var info = AnimationLayers.LaptopInfo(bytes, site);
                        if (info == null) { Refuse($"ModelUnreadable {avfx.Relative}"); return null; }
                        int capacity = AnimationLayers.LaptopCapacity(bytes, info, tableFrames);
                        if (frames.Count > capacity) { err = AnimationText.Format(AnimationMessage.TooManyTextures, avfx.Relative); return null; }
                        var plan = AvfxTableOverlay.Plan(bytes, info, site.Laptop!.Surface.Rect, eq3, profile, out var r);
                        if (plan == null) { Refuse($"{r} {avfx.Relative}"); return null; }
                        if (plan.RectInWindow != route.RectInWindow) { Refuse($"NoWindow {avfx.Relative}"); return null; }
                        var built = AvfxTableOverlay.Build(bytes, info, plan, gamePaths, timeline, out r, out string problem,
                            capacity, AvfxTableAnimation.LaptopVisible);
                        if (built == null && r is OverlayRefusal.TooManyFrames or OverlayRefusal.TooManyTextures)
                        {
                            err = AnimationText.Format(AnimationMessage.TooManyTextures, avfx.Relative);
                            return null;
                        }
                        if (built == null) { Refuse($"{r} {avfx.Relative}: {problem}"); return null; }
                        if (!AnimationLayers.WithinTestedSize(built, info))
                        {
                            err = AnimationText.Format(AnimationMessage.TooManyTextures, avfx.Relative);
                            return null;
                        }
                        builds.Add((avfx, built));
                    }

                    var surface = site.Laptop!.Surface;
                    var encoded = new byte[frames.Count][];
                    int done = 0;
                    string? encodeError = null;
                    Parallel.For(0, frames.Count, k =>
                    {
                        var bytes = TexPaste.PasteRect(route.Crop, route.RectInWindow, surface.ToTextureSpace(frames[k], fit), out string e);
                        if (bytes == null) encodeError = e;
                        else encoded[k] = bytes;
                        progress?.Report(System.Threading.Interlocked.Increment(ref done));
                    });
                    if (encodeError != null) { Refuse("encode: " + encodeError); return null; }
                    return new LaptopRoute(encoded, route.RectInWindow, builds);
                }
                catch (Exception ex)
                {
                    // Not Failed: the laptop takes the first frame as a still instead.
                    Refuse("exception: " + ex);
                    return null;
                }
        
            }
        }

        /// <summary>
        /// The laptop's animation, or, when the overlay refuses it, its first frame as a still through
        /// <paramref name="pasteStill"/>. Too many frames is an error, never a still.
        /// </summary>
        public static bool ApplyLaptopOrStill(IAnimationHost host, DjTableAnimationSite site, IReadOnlyList<BgraImage> frames,
            FrameTimeline timeline, FitMode fit, IProgress<int>? progress, Func<BgraImage, string?> pasteStill,
            out IReadOnlyList<string> warnings, out string error, out bool fellBack)
        {
            fellBack = false;
            if (Apply(host, site, AnimatedSurface.Laptop, frames, timeline, fit, progress, out warnings, out error, out bool refused))
                return true;
            if (!refused) return false;

            fellBack = true;
            var still = frames[0];
            if (HasLayer(site, AnimatedSurface.Laptop))
                return PasteThenRestore(host, site, AnimatedSurface.Laptop, () => pasteStill(still), out warnings, out error);
            string? pasteError;
            try { pasteError = pasteStill(still); }
            catch (Exception ex) { Failed?.Invoke(ex); pasteError = ex.Message; }
            warnings = Array.Empty<string>();
            error = pasteError ?? string.Empty;
            return pasteError == null;
        }

        /// <summary>
        /// The overlay route, built and encoded in memory; null (reported) when anything refuses or
        /// fails, and the caller writes full-atlas frames instead.
        /// </summary>
        private static (OverlayRoute Route, byte[][] Encoded, List<(SiteAvfx Avfx, byte[] Bytes)> Builds)? TryOverlay(
            DjTableAnimationSite site, byte[] atlas, IReadOnlyList<BgraImage> frames, FrameTimeline timeline, FitMode fit,
            IReadOnlyList<string> gamePaths, IProgress<int>? progress)
        {
            try
            {
                string refusal = string.Empty;
                var route = OverlayPlanner != null ? OverlayPlanner(site, atlas) : PlanOverlay(site, atlas, out refusal);
                if (route == null) { OverlaySkipped?.Invoke(refusal.Length > 0 ? refusal : "refused"); return null; }

                var builds = new List<(SiteAvfx, byte[])>();
                foreach (var avfx in site.Animatable)
                {
                    var built = AvfxTableOverlay.Build(avfx.Baseline!, avfx.Inspect!.Info!, route.Plans[DjAnimationScan.Normalise(avfx.Relative)],
                        gamePaths, timeline, out var r, out string problem, FramePlan.MaxFrames);
                    if (built == null) { OverlaySkipped?.Invoke($"{r} {avfx.Relative}: {problem}"); return null; }
                    builds.Add((avfx, built));
                }

                var surface = site.Table.Surface;
                var encoded = new byte[frames.Count][];
                int done = 0;
                string? encodeError = null;
                Parallel.For(0, frames.Count, k =>
                {
                    var bytes = TexPaste.PasteRect(route.Crop, route.RectInWindow, surface.ToTextureSpace(frames[k], fit), out string e);
                    if (bytes == null) encodeError = e;
                    else encoded[k] = bytes;
                    progress?.Report(System.Threading.Interlocked.Increment(ref done));
                });
                if (encodeError != null) { OverlaySkipped?.Invoke("encode: " + encodeError); return null; }
                return (route, encoded, builds);
            }
            catch (Exception ex)
            {
                // Not Failed: the apply goes on with full-atlas frames.
                OverlaySkipped?.Invoke("exception: " + ex);
                return null;
            }
        }

        private static string Rollback(IAnimationHost host, DjTableAnimationSite site, AnimationState? previous, string token, int gen,
            List<(SiteAvfx Avfx, byte[] Previous)> written, bool keysAdded, HashSet<string> plannedKeys,
            List<AnimationFrame> framesWritten, List<string> notes, Exception cause)
        {
            string? stuck = null;
            foreach (var (avfx, before) in written)
            {
                try { host.WriteFile(avfx.FullPath, before); }
                catch (Exception ex) { Failed?.Invoke(ex); stuck ??= avfx.Relative; }
            }
            if (stuck != null) return AnimationText.Format(AnimationMessage.RollbackStopped, stuck);

            if (keysAdded)
            {
                try { host.EditGroups(group => RemoveKeys(group, plannedKeys)); }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
                    return AnimationText.Format(AnimationMessage.RollbackStopped, "manifest");
                }
            }

            var leftovers = new List<AnimationFrame>();
            foreach (var frame in framesWritten)
            {
                try { host.DeleteFile(Path.Combine(site.ModRoot, frame.Relative)); }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
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
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
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
                var keep = new HashSet<string>(LiveFrameKeys(state).Keys, StringComparer.OrdinalIgnoreCase);
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
            out IReadOnlyList<string> warnings, out string error) =>
            Restore(host, site, AnimatedSurface.Table, out warnings, out error);

        /// <summary>
        /// Removes one surface's animation and keeps the other's. The laptop card never removes more
        /// than the laptop; the table card cleans up an interrupted run for both.
        /// </summary>
        public static bool Restore(IAnimationHost host, DjTableAnimationSite site, AnimatedSurface surface,
            out IReadOnlyList<string> warnings, out string error)
        {
            warnings = Array.Empty<string>();
            error = string.Empty;
            var state = site.State;
            bool live = site.HasState && IsLive(site);
            var table = AnimationLayers.TableOf(state);
            var laptop = AnimationLayers.LaptopOf(state);
            if (surface == AnimatedSurface.Laptop)
            {
                if (!live || laptop == null) return true;
                return table == null ? RestoreAll(host, site, out warnings, out error) : RemoveLayer(host, site, surface, out warnings, out error);
            }
            if (live && table == null && laptop != null) return true;
            if (live && table != null && laptop != null) return RemoveLayer(host, site, surface, out warnings, out error);
            return RestoreAll(host, site, out warnings, out error);
        }

        /// <summary>The effects with one surface's layer taken off, or the reason they cannot be rebuilt.</summary>
        private static List<(SiteAvfx Avfx, byte[] Bytes, string OriginalSha)>? WithoutLayer(IAnimationHost host,
            DjTableAnimationSite site, AnimatedSurface surface, out string error)
        {
            if (!CheckSite(host, site, refuseProblem: false, out error)) return null;
            var state = site.State!;
            var atlasOf = AtlasReader(host, site);
            if (LiveCheck(site, atlasOf) is { } drift)
            {
                error = AnimationText.Format(AnimationMessage.LayerNotKept, drift);
                return null;
            }
            var table = surface == AnimatedSurface.Table ? null : AnimationLayers.TableOf(state);
            var laptop = surface == AnimatedSurface.Laptop ? null : AnimationLayers.LaptopOf(state);
            var builds = new List<(SiteAvfx, byte[], string)>();
            foreach (var avfx in site.Animatable)
            {
                var entry = state.Find(avfx.Relative);
                if (entry == null) continue;
                var built = AnimationLayers.Compose(avfx, site, table, laptop, atlasOf, out string problem);
                if (built == null)
                {
                    error = AnimationText.Format(AnimationMessage.LayerNotKept, problem);
                    return null;
                }
                builds.Add((avfx, built, entry.OriginalSha));
            }
            return builds;
        }

        private static bool RemoveLayer(IAnimationHost host, DjTableAnimationSite site, AnimatedSurface surface,
            out IReadOnlyList<string> warnings, out string error)
        {
            var notes = new List<string>();
            warnings = notes;
            try
            {
                var builds = WithoutLayer(host, site, surface, out error);
                if (builds == null) return false;
                var state = site.State!;
                return WriteLayers(host, site, state, state.ModToken, state.Generation + 1, new List<AnimationFrame>(),
                    Array.Empty<byte[]>(), builds, notes, out error, applied =>
                    {
                        if (surface == AnimatedSurface.Laptop)
                        {
                            applied.StaleFrames = (state.Laptop?.Frames ?? new List<AnimationFrame>()).Concat(applied.StaleFrames).ToList();
                            applied.Laptop = null;
                        }
                        else
                        {
                            applied.StaleFrames = state.Frames.Concat(applied.StaleFrames).ToList();
                            applied.Frames = new List<AnimationFrame>();
                            applied.StartTicks = new List<int>();
                            applied.LoopTicks = 0;
                            applied.FrameRect = null;
                        }
                    });
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                return false;
            }
        }

        /// <summary>Puts back the shipped effects and removes every frame of both surfaces; never composes.</summary>
        public static bool RestoreAll(IAnimationHost host, DjTableAnimationSite site,
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

                var all = state.Frames.Concat(state.Laptop?.Frames ?? new List<AnimationFrame>()).Concat(state.PendingFrames).Concat(state.StaleFrames)
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
            out IReadOnlyList<string> warnings, out string error) =>
            PasteThenRestore(host, site, AnimatedSurface.Table, paste, out warnings, out error);

        public static bool PasteThenRestore(IAnimationHost host, DjTableAnimationSite site, AnimatedSurface surface, Func<string?> paste,
            out IReadOnlyList<string> warnings, out string error)
        {
            warnings = Array.Empty<string>();
            // When the other surface stays, check its layer can be rebuilt before pasting anything.
            var state = site.State;
            if (site.HasState && IsLive(site) && AnimationLayers.TableOf(state) != null && AnimationLayers.LaptopOf(state) != null)
            {
                try
                {
                    if (WithoutLayer(host, site, surface, out error) == null) return false;
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
                    error = AnimationText.Format(AnimationMessage.WriteFailed, ex.Message);
                    return false;
                }
            }
            string? pasteError;
            try { pasteError = paste(); }
            catch (Exception ex) { Failed?.Invoke(ex); pasteError = ex.Message; }
            if (pasteError != null)
            {
                error = pasteError;
                return false;
            }
            return Restore(host, site, surface, out warnings, out error);
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

        public static AnimationSummary Summarise(DjTableAnimationSite site, int frames, FrameTimeline timeline, bool thinned,
            AnimatedSurface surface = AnimatedSurface.Table)
        {
            double seconds = timeline.LoopTicks / (double)FramePlan.TicksPerSecond;
            if (surface == AnimatedSurface.Laptop)
            {
                // No full-atlas route: a laptop the overlay refuses adds nothing (its first frame is a still).
                long crop = 0;
                try
                {
                    if (site.Laptop != null && site.LaptopProblem == null)
                    {
                        byte[] eq3 = File.ReadAllBytes(site.Laptop.FullPath);
                        var route = LaptopPlanner != null ? LaptopPlanner(site, eq3) : AnimationLayers.PlanLaptop(site, eq3, out _);
                        if (route != null) crop = route.Crop.Length;
                    }
                }
                catch { }
                return new(frames, seconds, crop * frames, thinned);
            }
            long atlas = 0;
            try { atlas = new FileInfo(site.Table.FullPath).Length; } catch { }
            long perFrame = atlas;
            try
            {
                if (site.Mode == AnimationMode.Append && site.Problem == null)
                {
                    byte[] bytes = File.ReadAllBytes(site.Table.FullPath);
                    var route = OverlayPlanner != null ? OverlayPlanner(site, bytes) : PlanOverlay(site, bytes, out _);
                    if (route != null) perFrame = route.Crop.Length;
                }
            }
            catch { }
            return new(frames, timeline.LoopTicks / (double)FramePlan.TicksPerSecond, perFrame * frames, thinned);
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
