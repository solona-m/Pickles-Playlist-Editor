using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>
    /// How big to ask a decoder for a picture, and what shape to expect back.
    ///
    /// Pulled out of the loader as a pure function purely so it can be tested. The loader itself
    /// needs a real image decoder and so can only run inside the app, but the arithmetic is where
    /// the bug was: a photograph carrying a quarter turn in its EXIF tag has its axes swapped
    /// between the size you scale to and the size you get back, and because the two multiply to the
    /// same buffer length nothing throws when you confuse them — the picture simply comes back
    /// diagonally sheared.
    ///
    /// The rule this encodes: scaling is requested in RAW pixels, because the decoder's transform
    /// runs before the EXIF rotation; the buffer that comes back is ORIENTED, so its axes are the
    /// scaled ones swapped whenever the tag is a quarter turn.
    /// </summary>
    internal readonly record struct PictureLoadPlan(
        bool Scale, uint ScaledWidth, uint ScaledHeight, int OutputWidth, int OutputHeight)
    {
        /// <summary>Bytes the decoded buffer must hold for this plan to be readable.</summary>
        public long RequiredBytes => (long)OutputWidth * OutputHeight * 4;

        /// <summary>
        /// Plans a load, capping the longest edge at <paramref name="maxEdge"/>.
        ///
        /// The cap matters on a 32-bit build: a modern phone photograph at full size is a 100 MB
        /// buffer for a picture that ends up a few hundred pixels wide.
        /// </summary>
        public static PictureLoadPlan For(uint rawWidth, uint rawHeight, uint orientedWidth, uint maxEdge)
        {
            if (rawWidth == 0 || rawHeight == 0) return default;

            // A quarter turn swaps the axes, so the oriented width is the raw height. A half turn or
            // a mirror leaves them alone, and a square picture cannot be told apart either way —
            // which is harmless, because swapping equal axes changes nothing.
            bool quarterTurned = orientedWidth != rawWidth;

            uint width = rawWidth, height = rawHeight;
            bool scale = maxEdge > 0 && (width > maxEdge || height > maxEdge);

            if (scale)
            {
                double factor = Math.Min((double)maxEdge / width, (double)maxEdge / height);
                width = Math.Max(1, (uint)Math.Round(width * factor));
                height = Math.Max(1, (uint)Math.Round(height * factor));
            }

            return new PictureLoadPlan(
                scale, width, height,
                (int)(quarterTurned ? height : width),
                (int)(quarterTurned ? width : height));
        }
    }
}
