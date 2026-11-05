using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// Composites animated WebP frames the way browsers do: a blend frame goes "over" the canvas with
    /// straight alpha, a no-blend frame replaces its rect, and dispose clears the rect to transparent
    /// after the frame shows. The ANIM background colour is ignored, as Chrome and Firefox ignore it.
    /// </summary>
    internal sealed class WebpCanvas
    {
        private readonly int _width, _height;
        private readonly byte[] _canvas;

        public WebpCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width;
            _height = height;
            _canvas = new byte[width * height * 4];
        }

        /// <param name="padded">Canvas-sized straight BGRA with the frame's pixels at its top-left.</param>
        public BgraImage Render(WebpFrame frame, byte[] padded)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    int s = (y * _width + x) * 4;
                    int d = ((frame.Y + y) * _width + frame.X + x) * 4;
                    if (!frame.Blend)
                    {
                        Buffer.BlockCopy(padded, s, _canvas, d, 4);
                        continue;
                    }

                    int aS = padded[s + 3];
                    if (aS == 255) { Buffer.BlockCopy(padded, s, _canvas, d, 4); continue; }
                    if (aS == 0) continue;

                    int aD = _canvas[d + 3];
                    int dstWeight = aD * (255 - aS);          // scaled by 255
                    int aOut255 = aS * 255 + dstWeight;        // aOut * 255 * 255
                    for (int c = 0; c < 3; c++)
                        _canvas[d + c] = (byte)((padded[s + c] * aS * 255 + _canvas[d + c] * dstWeight + aOut255 / 2) / aOut255);
                    _canvas[d + 3] = (byte)((aOut255 + 127) / 255);
                }
            }

            var output = FrameFlatten.OverBlack(_canvas, _width, _height);

            if (frame.DisposeToBackground)
                for (int y = 0; y < frame.Height; y++)
                    Array.Clear(_canvas, ((frame.Y + y) * _width + frame.X) * 4, frame.Width * 4);

            return output;
        }
    }

    internal static class FrameFlatten
    {
        /// <summary>Straight BGRA laid over opaque black, as GifCanvas's output is.</summary>
        public static BgraImage OverBlack(byte[] bgra, int width, int height)
        {
            var output = new BgraImage(width, height);
            var pixels = output.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = bgra[i + 3];
                pixels[i] = (byte)(bgra[i] * a / 255);
                pixels[i + 1] = (byte)(bgra[i + 1] * a / 255);
                pixels[i + 2] = (byte)(bgra[i + 2] * a / 255);
                pixels[i + 3] = 255;
            }
            return output;
        }
    }
}
