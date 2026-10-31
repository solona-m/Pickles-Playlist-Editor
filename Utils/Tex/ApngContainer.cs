using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Pickles_Playlist_Editor.Utils.Tex
{
    /// <summary>An APNG's canvas and its raw per-frame delays in milliseconds.</summary>
    internal sealed record ApngInfo(int Width, int Height, IReadOnlyList<double> DelaysMs);

    /// <summary>
    /// Reads just enough of a PNG to know whether it is an APNG this app can animate: IHDR, acTL and
    /// every fcTL. Frame pixels are left to ffmpeg.
    /// </summary>
    internal static class ApngContainer
    {
        /// <summary>Null when the file is not an animatable APNG (today's still path then runs). Never throws.</summary>
        public static ApngInfo? Read(Stream stream)
        {
            try
            {
                var sig = new byte[8];
                if (stream.ReadAtLeast(sig, 8, throwOnEndOfStream: false) < 8 || sig[0] != 0x89 || sig[1] != 'P') return null;

                int width = 0, height = 0;
                bool sawActl = false, sawIdat = false, firstFctlBeforeIdat = false;
                var delays = new List<double>();
                var header = new byte[8];

                while (true)
                {
                    if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8) return null;
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length > int.MaxValue) return null;
                    string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

                    switch (type)
                    {
                        case "IHDR":
                        case "acTL":
                        case "fcTL":
                        {
                            var data = new byte[length];
                            if (stream.ReadAtLeast(data, (int)length, throwOnEndOfStream: false) < length) return null;
                            if (type == "IHDR" && length >= 8)
                            {
                                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
                            }
                            else if (type == "acTL")
                            {
                                if (sawIdat) return null;
                                sawActl = true;
                            }
                            else if (type == "fcTL")
                            {
                                if (length < 26) return null;
                                if (delays.Count == 0) firstFctlBeforeIdat = !sawIdat;
                                int num = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20));
                                int den = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22));
                                delays.Add(num * 1000.0 / (den == 0 ? 100 : den));
                            }
                            Skip(stream, 4);   // CRC
                            break;
                        }
                        case "IEND":
                            return sawActl && firstFctlBeforeIdat && delays.Count >= 2 && width > 0 && height > 0
                                ? new ApngInfo(width, height, delays)
                                : null;
                        default:
                            if (type == "IDAT") sawIdat = true;
                            Skip(stream, length + 4);
                            break;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static void Skip(Stream stream, long count)
        {
            if (stream.CanSeek)
            {
                if (stream.Position + count > stream.Length) throw new EndOfStreamException();
                stream.Seek(count, SeekOrigin.Current);
                return;
            }
            var buffer = new byte[8192];
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException();
                count -= read;
            }
        }
    }
}
