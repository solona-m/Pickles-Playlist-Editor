using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// A straight-alpha BGRA8 bitmap: rows top to bottom, four bytes per pixel, no padding.
    ///
    /// BGRA rather than RGBA because both ends of this feature already speak it — WinUI's
    /// <c>WriteableBitmap</c> and <c>BitmapDecoder</c> on one side, and the block codec on the
    /// other — so the whole path moves pixels without ever reordering a channel.
    /// </summary>
    internal sealed class BgraImage
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        public int Stride => Width * 4;

        public BgraImage(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "An image needs a positive size.");

            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        public BgraImage(int width, int height, byte[] pixels)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "An image needs a positive size.");
            if (pixels.Length < width * height * 4)
                throw new ArgumentException("Pixel buffer is too small for the given size.", nameof(pixels));

            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone());
    }
}
