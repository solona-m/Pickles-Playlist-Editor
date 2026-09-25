using Pickles_Playlist_Editor.Utils.Tex;

namespace TexHarness;

/// <summary>
/// Just enough uncompressed BMP to get pictures in and out of the harness by eye.
///
/// BMP rather than PNG because the format code under test depends on nothing but System, and the
/// harness is supposed to stay that way too — an image codec dependency here would be a bigger
/// thing than the thing it is checking.
/// </summary>
internal static class Bmp
{
    public static BgraImage Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException($"{path} is not a BMP.");

        int dataOffset = BitConverter.ToInt32(bytes, 10);
        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        int bits = BitConverter.ToInt16(bytes, 28);

        if (bits != 24 && bits != 32)
            throw new InvalidDataException($"{path} is {bits}-bit; only 24 and 32 are read here.");

        bool bottomUp = height > 0;
        height = Math.Abs(height);

        int sourceStride = (width * (bits / 8) + 3) / 4 * 4;
        var image = new BgraImage(width, height);

        for (int y = 0; y < height; y++)
        {
            int row = bottomUp ? height - 1 - y : y;
            int source = dataOffset + row * sourceStride;
            for (int x = 0; x < width; x++)
            {
                int from = source + x * (bits / 8);
                int to = (y * width + x) * 4;
                image.Pixels[to] = bytes[from];
                image.Pixels[to + 1] = bytes[from + 1];
                image.Pixels[to + 2] = bytes[from + 2];
                image.Pixels[to + 3] = bits == 32 ? bytes[from + 3] : (byte)255;
            }
        }
        return image;
    }

    public static void Write(string path, BgraImage image)
    {
        int stride = (image.Width * 3 + 3) / 4 * 4;
        int size = 54 + stride * image.Height;
        var bytes = new byte[size];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(image.Width).CopyTo(bytes, 18);
        BitConverter.GetBytes(image.Height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(stride * image.Height).CopyTo(bytes, 34);

        for (int y = 0; y < image.Height; y++)
        {
            int destination = 54 + (image.Height - 1 - y) * stride;
            for (int x = 0; x < image.Width; x++)
            {
                int from = (y * image.Width + x) * 4;
                bytes[destination + x * 3] = image.Pixels[from];
                bytes[destination + x * 3 + 1] = image.Pixels[from + 1];
                bytes[destination + x * 3 + 2] = image.Pixels[from + 2];
            }
        }

        File.WriteAllBytes(path, bytes);
    }
}
