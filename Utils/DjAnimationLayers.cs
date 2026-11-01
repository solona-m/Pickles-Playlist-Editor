using Pickles_Playlist_Editor.Utils.Avfx;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pickles_Playlist_Editor.Utils
{
    internal enum AnimatedSurface { Table, Laptop }

    /// <summary>One surface's animation as it sits on an effect: its frame keys, timing and picture rect.</summary>
    internal sealed record AnimationLayer(IReadOnlyList<string> GamePaths, FrameTimeline Timeline, TexRect? FrameRect);

    /// <summary>
    /// The table and laptop share the same effect files, so every effect is rebuilt from its shipped
    /// copy: the table layer first, then the laptop layer on top of the table-built bytes.
    /// </summary>
    internal static class AnimationLayers
    {
        public static AnimationLayer? TableOf(AnimationState? state) =>
            state == null || state.Frames.Count == 0 || state.StartTicks.Count != state.Frames.Count ? null
                : new(state.Frames.Select(f => f.GamePath).ToList(), new FrameTimeline(state.StartTicks, state.LoopTicks), state.FrameRect);

        public static AnimationLayer? LaptopOf(AnimationState? state) =>
            state?.Laptop is not { Frames.Count: > 0 } lap || lap.StartTicks.Count != lap.Frames.Count ? null
                : new(lap.Frames.Select(f => f.GamePath).ToList(), new FrameTimeline(lap.StartTicks, lap.LoopTicks), lap.FrameRect);

        /// <summary>The table layer on the shipped copy; null with the reason when it cannot be rebuilt.</summary>
        public static byte[]? Table(SiteAvfx avfx, DjTableAnimationSite site, AnimationLayer? table, Func<byte[]> eq55, out string problem)
        {
            problem = string.Empty;
            if (table == null) return avfx.Baseline!;
            var info = avfx.Inspect!.Info!;
            if (table.FrameRect == null)
                return AvfxTableAnimation.Build(avfx.Baseline!, info, table.GamePaths, table.Timeline, out problem);

            var plan = AvfxTableOverlay.Plan(avfx.Baseline!, info, site.Table.Surface.Rect, eq55(), OverlayProfile.Table, out var refusal);
            if (plan == null) { problem = $"{refusal} {avfx.Relative}"; return null; }
            if (plan.RectInWindow != table.FrameRect) { problem = $"FrameRect {avfx.Relative}"; return null; }
            var built = AvfxTableOverlay.Build(avfx.Baseline!, info, plan, table.GamePaths, table.Timeline, out refusal, out problem, FramePlan.MaxFrames);
            if (built == null) problem = $"{refusal} {avfx.Relative}: {problem}";
            return built;
        }

        /// <summary>The laptop's inspect on table-built bytes (its clones go after the table's).</summary>
        public static TableAvfxInfo? LaptopInfo(byte[] tableBuilt, DjTableAnimationSite site) =>
            AvfxTableAnimation.Inspect(tableBuilt, site.LaptopGamePaths, _ => false) is { Kind: InspectKind.Animatable, Info: { Mode: AnimationMode.Append } info }
                ? info : null;

        public static OverlayProfile LaptopProfile(DjTableAnimationSite site) => OverlayProfile.Laptop(KeepOut(site.Laptop!.Surface));

        /// <summary>The laptop layer on table-built bytes, with the plan's refusal or the build's problem.</summary>
        public static byte[]? Laptop(byte[] tableBuilt, DjTableAnimationSite site, AnimationLayer laptop, byte[] eq3,
            int maxFrames, out OverlayRefusal refusal, out string problem, out OverlayPlan? plan)
        {
            problem = string.Empty;
            plan = null;
            var info = LaptopInfo(tableBuilt, site);
            if (info == null) { refusal = OverlayRefusal.ModelUnreadable; problem = "laptop inspect"; return null; }
            plan = AvfxTableOverlay.Plan(tableBuilt, info, site.Laptop!.Surface.Rect, eq3, LaptopProfile(site), out refusal);
            if (plan == null) return null;
            if (laptop.FrameRect is { } rect && plan.RectInWindow != rect) { refusal = OverlayRefusal.NoWindow; problem = "FrameRect"; return null; }
            return AvfxTableOverlay.Build(tableBuilt, info, plan, laptop.GamePaths, laptop.Timeline, out refusal, out problem,
                maxFrames, AvfxTableAnimation.LaptopVisible);
        }

        /// <summary>Both layers on the shipped copy; null with the reason when either cannot be rebuilt.</summary>
        public static byte[]? Compose(SiteAvfx avfx, DjTableAnimationSite site, AnimationLayer? table, AnimationLayer? laptop,
            Func<AnimatedSurface, byte[]> atlas, out string problem)
        {
            var built = Table(avfx, site, table, () => atlas(AnimatedSurface.Table), out problem);
            if (built == null || laptop == null) return built;
            if (site.Laptop == null) { problem = "NoLaptop " + avfx.Relative; return null; }
            var result = Laptop(built, site, laptop, atlas(AnimatedSurface.Laptop), FramePlan.MaxFrames, out var refusal, out problem, out _);
            if (result == null) problem = $"{refusal} {avfx.Relative}{(problem.Length > 0 ? ": " + problem : string.Empty)}";
            return result;
        }

        /// <summary>True when an effect holding a laptop layer stays within the size tested in game.</summary>
        public static bool WithinTestedSize(byte[] built, TableAvfxInfo laptopInfo)
        {
            var (particles, textures, items) = AvfxTableAnimation.Counts(built, laptopInfo.Emitter);
            return particles <= AvfxTableOverlay.TestedParticles && textures <= AvfxTableOverlay.TestedTextures
                && items <= AvfxTableOverlay.TestedItems;
        }

        /// <summary>How many laptop frames fit on these table-built bytes: its own cap and tested size.</summary>
        public static int LaptopCapacity(byte[] tableBuilt, TableAvfxInfo laptopInfo, int tableFrames)
        {
            var (particles, textures, items) = AvfxTableAnimation.Counts(tableBuilt, laptopInfo.Emitter);
            return Math.Max(0, Math.Min(Math.Min(FramePlan.LaptopMaxFrames, AvfxTableOverlay.TestedParticles - particles),
                Math.Min(AvfxTableOverlay.TestedTextures - textures, AvfxTableOverlay.TestedItems - items)));
        }

        /// <summary>Plans the laptop on every shipped copy and crops eq3 once; null with the reason when any refuses.</summary>
        public static OverlayRoute? PlanLaptop(DjTableAnimationSite site, byte[] eq3, out string refusal)
        {
            refusal = string.Empty;
            if (site.Laptop == null) { refusal = "NoLaptop"; return null; }
            var plans = new Dictionary<string, OverlayPlan>(StringComparer.OrdinalIgnoreCase);
            OverlayPlan? first = null;
            foreach (var avfx in site.Animatable)
            {
                if (avfx.LaptopInspect?.Info is not { } info) { refusal = $"NoPanel {avfx.Relative}"; return null; }
                var plan = AvfxTableOverlay.Plan(avfx.Baseline!, info, site.Laptop.Surface.Rect, eq3, LaptopProfile(site), out var r);
                if (plan == null) { refusal = $"{r} {avfx.Relative}"; return null; }
                first ??= plan;
                if (plan.Window != first.Window || plan.RectInWindow != first.RectInWindow) { refusal = $"NoWindow {avfx.Relative}"; return null; }
                plans[DjAnimationScan.Normalise(avfx.Relative)] = plan;
            }
            if (first == null) { refusal = "NoPanelGeometry"; return null; }
            var crop = TexCrop.Crop(eq3, first.Window, out string e);
            if (crop == null) { refusal = "AtlasUnreadable crop: " + e; return null; }
            return new OverlayRoute(first.Window, first.RectInWindow, crop, plans);
        }

        /// <summary>The other pictures in the same texture file, which a window must not freeze.</summary>
        public static IReadOnlyList<TexRect> KeepOut(DjTextureSurface surface) =>
            DjTextureScan.Surfaces.Where(s => s.Key != surface.Key && string.Equals(s.FileName, surface.FileName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Rect).ToList();
    }
}
