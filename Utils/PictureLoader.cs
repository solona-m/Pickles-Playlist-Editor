using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pickles_Playlist_Editor.Utils
{
    internal enum PictureNote { None, TableOnly, TooLarge, Capped }

    /// <summary>A picture as chosen: a still, and for an animated GIF its kept frames and timing.</summary>
    internal sealed class LoadedPicture
    {
        public required BgraImage Still { get; init; }
        public IReadOnlyList<BgraImage>? Frames { get; init; }
        public FrameTimeline? Timeline { get; init; }
        public int SourceFrameCount { get; init; } = 1;
        public PictureNote Note { get; init; }
        public bool Thinned { get; init; }
    }

    /// <summary>Reads picture files, including animated GIFs, into the format the texture code uses.</summary>
    internal static class PictureLoader
    {
        private const uint MaxEdge = 2048;
        private const int FrameEdge = 1024;

        public static async Task<LoadedPicture?> LoadAsync(string path, bool allowAnimation, int maxFrames,
            IProgress<string>? progress)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            bool animated = decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId && decoder.FrameCount > 1;
            if (!animated)
            {
                var still = await LoadStillAsync(decoder);
                return still == null ? null : new LoadedPicture { Still = still };
            }

            var (width, height) = await CanvasSizeAsync(decoder);
            if (width > MaxEdge || height > MaxEdge)
            {
                var still = await LoadStillAsync(decoder);
                return still == null ? null : new LoadedPicture
                {
                    Still = still,
                    SourceFrameCount = (int)decoder.FrameCount,
                    Note = PictureNote.TooLarge,
                };
            }

            // Pass 1: timing only, so pass 2 keeps just the frames it will use.
            int count = (int)decoder.FrameCount;
            var meta = new List<(int Left, int Top, int Width, int Height, int Delay, int Disposal)>();
            for (uint i = 0; i < count; i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                var props = frame.BitmapProperties;
                meta.Add((
                    await ReadIntAsync(props, "/imgdesc/Left", 0),
                    await ReadIntAsync(props, "/imgdesc/Top", 0),
                    await ReadIntAsync(props, "/imgdesc/Width", (int)frame.PixelWidth),
                    await ReadIntAsync(props, "/imgdesc/Height", (int)frame.PixelHeight),
                    await ReadIntAsync(props, "/grctlext/Delay", 10),
                    await ReadIntAsync(props, "/grctlext/Disposal", 0)));
            }

            var plan = FramePlan.Plan(meta.Select(m => m.Delay).ToList(), allowAnimation ? maxFrames : 1);
            var keep = new HashSet<int>(allowAnimation ? plan.KeptSourceIndices : new[] { 0 });

            // Pass 2: every frame is composited, for disposal to come out right; only kept ones are stored.
            var canvas = new GifCanvas((int)width, (int)height);
            var kept = new List<BgraImage>();
            int last = allowAnimation ? count - 1 : 0;
            for (int i = 0; i <= last; i++)
            {
                progress?.Report($"{i + 1}/{count}");
                var frame = await decoder.GetFrameAsync((uint)i);
                var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var m = meta[i];
                var data = pixels.DetachPixelData();
                int fw = (int)frame.PixelWidth, fh = (int)frame.PixelHeight;
                var rendered = canvas.Render(new GifFrame(m.Left, m.Top, fw, fh, m.Delay, m.Disposal, data));
                if (keep.Contains(i)) kept.Add(Shrink(rendered));
            }

            if (!allowAnimation)
                return new LoadedPicture { Still = kept[0], SourceFrameCount = count, Note = PictureNote.TableOnly };

            // Room for one frame, or frames so short they merged into one: a still, and say why.
            if (plan.KeptSourceIndices.Count < 2)
                return new LoadedPicture { Still = kept[0], SourceFrameCount = count, Thinned = true, Note = PictureNote.Capped };

            return new LoadedPicture
            {
                Still = kept[0],
                Frames = kept,
                Timeline = plan.Timeline,
                SourceFrameCount = count,
                Thinned = plan.Thinned,
                Note = plan.Thinned ? PictureNote.Capped : PictureNote.None,
            };
        }

        /// <summary>The GIF's logical screen, from the container rather than frame 0.</summary>
        private static async Task<(uint Width, uint Height)> CanvasSizeAsync(BitmapDecoder decoder)
        {
            int w = await ReadIntAsync(decoder.BitmapContainerProperties, "/logscrdesc/Width", 0);
            int h = await ReadIntAsync(decoder.BitmapContainerProperties, "/logscrdesc/Height", 0);
            if (w <= 0 || h <= 0)
            {
                var first = await decoder.GetFrameAsync(0);
                return (first.PixelWidth, first.PixelHeight);
            }
            return ((uint)w, (uint)h);
        }

        /// <summary>One property, with a default when the codec does not have it (PROPERTYNOTFOUND).</summary>
        private static async Task<int> ReadIntAsync(BitmapPropertiesView properties, string key, int fallback)
        {
            try
            {
                var values = await properties.GetPropertiesAsync(new[] { key });
                if (values.TryGetValue(key, out var value) && value.Value != null)
                    return Convert.ToInt32(value.Value);
            }
            catch { }
            return fallback;
        }

        private static BgraImage Shrink(BgraImage image)
        {
            if (image.Width <= FrameEdge && image.Height <= FrameEdge) return image;
            double factor = Math.Min((double)FrameEdge / image.Width, (double)FrameEdge / image.Height);
            return ImageOps.Resize(image, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
        }

        /// <summary>
        /// Reads a still into the format the texture code works in.
        ///
        /// Capped on the way in rather than after decoding: this is an x86 process, and a modern
        /// phone photograph at full size is a 100 MB buffer for a picture that ends up 656 pixels
        /// wide. The cap is generous next to the panels and costs nothing at ordinary sizes.
        ///
        /// The scaling is expressed in RAW pixels and the result reported in ORIENTED ones, which is
        /// the whole subtlety here. BitmapTransform runs before the EXIF rotation, so scaling to the
        /// oriented size squashes a portrait phone photo into its own landscape frame and then turns
        /// it — and because the two sizes multiply to the same buffer length, nothing throws. It
        /// simply comes back diagonally sheared.
        ///
        /// That arithmetic lives in <see cref="PictureLoadPlan"/> so the harness can exercise it
        /// without an image decoder; what is left here is the platform call it feeds.
        /// </summary>
        private static async Task<BgraImage?> LoadStillAsync(BitmapDecoder decoder)
        {
            var plan = PictureLoadPlan.For(
                decoder.PixelWidth, decoder.PixelHeight, decoder.OrientedPixelWidth, MaxEdge);
            if (plan.OutputWidth <= 0 || plan.OutputHeight <= 0) return null;

            var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
            if (plan.Scale)
            {
                transform.ScaledWidth = plan.ScaledWidth;
                transform.ScaledHeight = plan.ScaledHeight;
            }

            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

            var data = pixels.DetachPixelData();
            if (data.Length < plan.RequiredBytes) return null;

            return new BgraImage(plan.OutputWidth, plan.OutputHeight, data);
        }
    }
}
