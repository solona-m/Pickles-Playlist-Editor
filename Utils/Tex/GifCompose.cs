using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>One decoded GIF frame: its rectangle on the logical screen and its straight BGRA pixels.</summary>
    internal sealed record GifFrame(int Left, int Top, int Width, int Height, int DelayCs, int Disposal, byte[] Bgra);

    /// <summary>
    /// Composites GIF frames the way a browser does: each frame is drawn over what the previous
    /// frames left, transparent pixels let it show through, and the disposal mode decides what the
    /// next frame starts from (0/1 keep, 2 clear the rect, 3 restore what was there before).
    /// </summary>
    internal sealed class GifCanvas
    {
        private readonly int _width, _height;
        private readonly byte[] _canvas;

        public GifCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width;
            _height = height;
            _canvas = new byte[width * height * 4];
        }

        /// <summary>The composited frame over opaque black; the canvas is then disposed for the next frame.</summary>
        public BgraImage Render(GifFrame frame)
        {
            byte[]? saved = frame.Disposal == 3 ? (byte[])_canvas.Clone() : null;

            int x0 = Math.Max(0, frame.Left), y0 = Math.Max(0, frame.Top);
            int x1 = Math.Min(_width, frame.Left + frame.Width), y1 = Math.Min(_height, frame.Top + frame.Height);

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int src = ((y - frame.Top) * frame.Width + (x - frame.Left)) * 4;
                    if (src + 3 >= frame.Bgra.Length || frame.Bgra[src + 3] == 0) continue;
                    int dst = (y * _width + x) * 4;
                    Buffer.BlockCopy(frame.Bgra, src, _canvas, dst, 4);
                }
            }

            var output = new BgraImage(_width, _height);
            var pixels = output.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = _canvas[i + 3];
                pixels[i] = (byte)(_canvas[i] * a / 255);
                pixels[i + 1] = (byte)(_canvas[i + 1] * a / 255);
                pixels[i + 2] = (byte)(_canvas[i + 2] * a / 255);
                pixels[i + 3] = 255;
            }

            if (frame.Disposal == 2 && x1 > x0)
            {
                for (int y = y0; y < y1; y++)
                    Array.Clear(_canvas, (y * _width + x0) * 4, (x1 - x0) * 4);
            }
            else if (saved != null)
            {
                Buffer.BlockCopy(saved, 0, _canvas, 0, saved.Length);
            }

            return output;
        }
    }
}
