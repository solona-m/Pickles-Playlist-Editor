using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Linq;
using System.Security.Cryptography;

namespace Pickles_Playlist_Editor.Utils.Avfx
{
    /// <summary>Why a table cannot take an overlay; such a table stays on full-atlas frames.</summary>
    internal enum OverlayRefusal
    {
        None, InPlace, UvSetNotCalibrated, ModelUnreadable, UvPairsDiffer, NoPanelGeometry, AtlasUnreadable,
        GlowOutsidePanel, NoWindow, TooManyModels, TooManyTextures, DrawSetupNotCalibrated, TooManyFrames, AppendFailed,
    }

    internal sealed record OverlayPlan(int PanelModel, int NewModel, TexRect Window, TexRect ClipRegion,
        TexRect RectInWindow, byte[] ModlData, double Coverage);

    /// <summary>
    /// The Append animation with small frames: one extra model, the panel shell clipped to a
    /// power-of-two window round the picture, which every frame particle draws with a crop of
    /// just that window instead of a full copy of the atlas.
    /// </summary>
    internal static class AvfxTableOverlay
    {
        public const int MaxFrames = 128;
        public const int FilterMargin = 4;

        // The Pickles panel particle's UvSt chunk (scale 1.5 on both axes), the only one the map is calibrated for.
        private const string CalibratedUvStSha = "ECE1016B93B12B232DD6945B444A11B383CDD4E1B7CB077A0586DFE8F08A5445";

        public static int Capacity(TableAvfxInfo info, int maxFrames = MaxFrames) =>
            Math.Max(0, Math.Min(maxFrames, 255 - info.TextureCount));

        public static OverlayPlan? Plan(byte[] baseline, TableAvfxInfo info, TexRect rect, byte[] atlas,
            PanelUvMap map, out OverlayRefusal refusal)
        {
            refusal = OverlayRefusal.None;
            if (info.Mode == AnimationMode.InPlace) { refusal = OverlayRefusal.InPlace; return null; }

            var root = AvfxChunk.Parse(baseline);
            var panel = root.All("Ptcl").ElementAt(info.PanelParticle);
            var uvSt = panel.Child("UvSt")?.Data;
            if (uvSt == null || Convert.ToHexString(SHA256.HashData(uvSt)) != CalibratedUvStSha
                || panel.ReadInt("UvSN") != 1 || panel.Child("TC1")?.ReadInt("UvSN") != 0)
            {
                refusal = OverlayRefusal.UvSetNotCalibrated;
                return null;
            }
            if (panel.ReadInt("RMT") != 8 || panel.ReadInt("DsDw") != 1 || panel.ReadInt("DsDt") != 1)
            {
                refusal = OverlayRefusal.DrawSetupNotCalibrated;
                return null;
            }

            var models = root.All("Modl").ToList();
            var mdNo = panel.Child("Data")?.Child("MdNo")?.Data;
            int panelModel = mdNo is { Length: > 0 } ? mdNo[0] : -1;
            var model = panelModel >= 0 && panelModel < models.Count ? AvfxModel.Parse(models[panelModel].Data ?? Array.Empty<byte>(), out _) : null;
            if (model == null) { refusal = OverlayRefusal.ModelUnreadable; return null; }

            var header = TexHeader.TryRead(atlas, out _);
            if (header == null || BlockCodec.For(header.Format, out _) == null || !rect.FitsInside(header.Width, header.Height))
            {
                refusal = OverlayRefusal.AtlasUnreadable;
                return null;
            }
            if (!TexCrop.BlackOutside(atlas, rect)) { refusal = OverlayRefusal.GlowOutsidePanel; return null; }

            if (TexCrop.Window(header.Width, header.Height, rect) is not { } window) { refusal = OverlayRefusal.NoWindow; return null; }

            var clipped = AvfxModel.ClipToWindow(model, map, window, FilterMargin, rect, header.Width, header.Height,
                out double coverage, out refusal);
            if (clipped == null) return null;

            if (models.Count >= 255) { refusal = OverlayRefusal.TooManyModels; return null; }
            if (info.TextureCount >= 255) { refusal = OverlayRefusal.TooManyTextures; return null; }

            var region = new TexRect(window.X + FilterMargin, window.Y + FilterMargin,
                window.Width - 2 * FilterMargin, window.Height - 2 * FilterMargin);
            var inWindow = new TexRect(rect.X - window.X, rect.Y - window.Y, rect.Width, rect.Height);
            return new OverlayPlan(panelModel, models.Count, window, region, inWindow, clipped.ToData(), coverage);
        }

        /// <summary>The unchanged Append build, then the overlay model added and drawn by every frame particle.</summary>
        public static byte[]? Build(byte[] baseline, TableAvfxInfo info, OverlayPlan plan,
            System.Collections.Generic.IReadOnlyList<string> frameGamePaths, FrameTimeline timeline,
            out OverlayRefusal refusal, out string problem, int maxFrames = MaxFrames)
        {
            refusal = OverlayRefusal.None;
            problem = string.Empty;
            int n = frameGamePaths.Count;
            int capacity = Capacity(info, maxFrames);
            if (n > capacity)
            {
                refusal = 255 - info.TextureCount < maxFrames ? OverlayRefusal.TooManyTextures : OverlayRefusal.TooManyFrames;
                problem = refusal.ToString();
                return null;
            }

            var appended = AvfxTableAnimation.Build(baseline, info with { Capacity = capacity }, frameGamePaths, timeline, out problem);
            if (appended == null) { refusal = OverlayRefusal.AppendFailed; return null; }

            var root = AvfxChunk.Parse(appended);
            int lastModl = root.Children!.FindLastIndex(c => c.Name == "Modl");
            root.Children.Insert(lastModl + 1, AvfxChunk.Leaf("Modl", plan.ModlData));
            root.SetInt("MdCn", root.ReadInt("MdCn") + 1);
            foreach (var clone in root.All("Ptcl").TakeLast(n))
                clone.Child("Data")!.Child("MdNo")!.Data![0] = (byte)plan.NewModel;
            return root.ToBytes();
        }
    }
}
