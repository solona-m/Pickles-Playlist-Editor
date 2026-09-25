using System;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>How a picture that is the wrong shape is made to fit the panel it is going on.</summary>
    internal enum FitMode
    {
        /// <summary>Cover the panel and crop the overflow. The usual choice: no empty bars.</summary>
        Fill,

        /// <summary>Show all of the picture and pad the rest with black.</summary>
        Fit,

        /// <summary>Ignore the aspect ratio and squash the picture to the panel.</summary>
        Stretch,
    }

    /// <summary>
    /// Scaling, rotating and cropping on a plain <see cref="BgraImage"/>.
    ///
    /// Deliberately not WIC or System.Drawing: this runs on the same pixels the block codec does, and
    /// keeping it managed and dependency-free is what lets the whole texture path be exercised from
    /// the offline harness rather than only from inside a running WinUI app.
    /// </summary>
    internal static class ImageOps
    {
        /// <summary>
        /// Resamples to an exact size: an area average when shrinking, bilinear when growing.
        ///
        /// Both branches earn their keep. The panels are small next to a modern photo, so almost every
        /// apply is a heavy downscale where a single bilinear tap would alias a busy picture into
        /// noise; but the mip chain then asks for a one-texel-wide version of the same rect, where the
        /// area window shrinks below a pixel and only the bilinear branch stays smooth.
        /// </summary>
        public static BgraImage Resize(BgraImage source, int width, int height)
        {
            if (source.Width == width && source.Height == height) return source.Clone();

            var horizontal = ResizeAxis(ToFloat(source), source.Width, source.Height, width, horizontalPass: true);
            var both = ResizeAxis(horizontal, width, source.Height, height, horizontalPass: false);
            return FromFloat(both, width, height);
        }

        /// <summary>Rotates clockwise by <paramref name="turns"/> quarter turns; negative is anticlockwise.</summary>
        public static BgraImage Rotate(BgraImage source, int turns)
        {
            int t = ((turns % 4) + 4) % 4;
            if (t == 0) return source.Clone();

            bool swapsAxes = t != 2;
            var result = new BgraImage(swapsAxes ? source.Height : source.Width,
                                       swapsAxes ? source.Width : source.Height);

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    int dx = t switch
                    {
                        1 => source.Height - 1 - y,
                        2 => source.Width - 1 - x,
                        _ => y,
                    };
                    int dy = t switch
                    {
                        1 => x,
                        2 => source.Height - 1 - y,
                        _ => source.Width - 1 - x,
                    };

                    Buffer.BlockCopy(source.Pixels, (y * source.Width + x) * 4,
                                     result.Pixels, (dy * result.Width + dx) * 4, 4);
                }
            }
            return result;
        }

        /// <summary>The given window of the source, clamped to stay inside it.</summary>
        public static BgraImage Crop(BgraImage source, int x, int y, int width, int height)
        {
            var result = new BgraImage(width, height);
            int left = Math.Clamp(x, 0, Math.Max(0, source.Width - 1));
            int copy = Math.Min(width, source.Width - left) * 4;

            for (int row = 0; row < height; row++)
            {
                int sourceY = Math.Clamp(y + row, 0, source.Height - 1);
                Buffer.BlockCopy(source.Pixels, (sourceY * source.Width + left) * 4,
                                 result.Pixels, row * result.Stride, copy);
            }
            return result;
        }

        /// <summary>Scales, then crops or pads, to exactly width x height.</summary>
        public static BgraImage FitTo(BgraImage source, int width, int height, FitMode mode)
        {
            if (mode == FitMode.Stretch) return Resize(source, width, height);

            double scale = mode == FitMode.Fill
                ? Math.Max((double)width / source.Width, (double)height / source.Height)
                : Math.Min((double)width / source.Width, (double)height / source.Height);

            int scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            var scaled = Resize(source, scaledWidth, scaledHeight);

            if (mode == FitMode.Fill)
                return Crop(scaled, (scaledWidth - width) / 2, (scaledHeight - height) / 2, width, height);

            // Fit: centred on opaque black, which is what the unlit parts of these panels already are.
            var padded = new BgraImage(width, height);
            for (int i = 3; i < padded.Pixels.Length; i += 4) padded.Pixels[i] = 255;

            int left = (width - scaledWidth) / 2;
            int top = (height - scaledHeight) / 2;
            for (int row = 0; row < scaledHeight; row++)
            {
                int destinationY = top + row;
                if (destinationY < 0 || destinationY >= height) continue;
                Buffer.BlockCopy(scaled.Pixels, row * scaled.Stride,
                                 padded.Pixels, (destinationY * width + left) * 4, scaledWidth * 4);
            }
            return padded;
        }

        // ---- resampling ------------------------------------------------------------------------

        private static float[] ToFloat(BgraImage source)
        {
            var values = new float[source.Width * source.Height * 4];
            for (int i = 0; i < values.Length; i++) values[i] = source.Pixels[i];
            return values;
        }

        private static BgraImage FromFloat(float[] values, int width, int height)
        {
            var result = new BgraImage(width, height);
            for (int i = 0; i < result.Pixels.Length; i++)
                result.Pixels[i] = (byte)Math.Clamp((int)Math.Round(values[i]), 0, 255);
            return result;
        }

        /// <summary>
        /// Resamples one axis of a float BGRA buffer, leaving the other alone.
        ///
        /// Separable rather than one 2D pass purely for cost: the laptop panel is resampled once per
        /// mip level, and a 2D kernel over a 4096-wide source would redo that work nine times.
        /// </summary>
        private static float[] ResizeAxis(float[] source, int width, int height, int target, bool horizontalPass)
        {
            int sourceLength = horizontalPass ? width : height;
            int otherLength = horizontalPass ? height : width;
            int outputWidth = horizontalPass ? target : width;

            var result = new float[(horizontalPass ? target * height : width * target) * 4];
            double ratio = (double)sourceLength / target;

            for (int index = 0; index < target; index++)
            {
                double start = index * ratio;
                double end = start + ratio;
                int first = (int)Math.Floor(start);
                int last = (int)Math.Ceiling(end) - 1;

                double centre = (index + 0.5) * ratio - 0.5;
                int low = (int)Math.Floor(centre);
                double fraction = centre - low;

                for (int other = 0; other < otherLength; other++)
                {
                    float b, g, r, a;

                    if (ratio >= 1.0)
                    {
                        double total = 0;
                        b = g = r = a = 0;
                        for (int s = first; s <= last; s++)
                        {
                            double weight = Math.Min(end, s + 1) - Math.Max(start, s);
                            if (weight <= 0) continue;

                            int offset = SampleOffset(Math.Clamp(s, 0, sourceLength - 1), other, width, horizontalPass);
                            b += (float)(source[offset] * weight);
                            g += (float)(source[offset + 1] * weight);
                            r += (float)(source[offset + 2] * weight);
                            a += (float)(source[offset + 3] * weight);
                            total += weight;
                        }
                        if (total > 0)
                        {
                            b /= (float)total; g /= (float)total;
                            r /= (float)total; a /= (float)total;
                        }
                    }
                    else
                    {
                        int lowOffset = SampleOffset(Math.Clamp(low, 0, sourceLength - 1), other, width, horizontalPass);
                        int highOffset = SampleOffset(Math.Clamp(low + 1, 0, sourceLength - 1), other, width, horizontalPass);
                        b = Lerp(source[lowOffset], source[highOffset], fraction);
                        g = Lerp(source[lowOffset + 1], source[highOffset + 1], fraction);
                        r = Lerp(source[lowOffset + 2], source[highOffset + 2], fraction);
                        a = Lerp(source[lowOffset + 3], source[highOffset + 3], fraction);
                    }

                    int destination = horizontalPass
                        ? (other * outputWidth + index) * 4
                        : (index * outputWidth + other) * 4;
                    result[destination] = b;
                    result[destination + 1] = g;
                    result[destination + 2] = r;
                    result[destination + 3] = a;
                }
            }
            return result;
        }

        private static int SampleOffset(int along, int across, int width, bool horizontalPass) =>
            horizontalPass ? (across * width + along) * 4 : (along * width + across) * 4;

        private static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);
    }
}
