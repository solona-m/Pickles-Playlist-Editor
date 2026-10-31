using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils.Avfx
{
    /// <summary>
    /// Model UV to the texture coordinate the panel particle samples, per axis. A hypothesis until
    /// the probe confirms it in game.
    /// </summary>
    internal readonly record struct PanelUvMap(double ScaleU, double OffsetU, double ScaleV, double OffsetV)
    {
        public static readonly PanelUvMap Calibrated = new(1.5, 0.5, -1, 0.5);

        public (double S, double T) ToTexture(double u, double v) => (ScaleU * u + OffsetU, ScaleV * v + OffsetV);

        public (double U, double V) ToModel(double s, double t) => ((s - OffsetU) / ScaleU, (t - OffsetV) / ScaleV);
    }

    /// <summary>
    /// A Modl chunk's drawn mesh: 36-byte vertices (position as four halves, normal, tangent and
    /// colour as four bytes each, four UV pairs as halves) and u16 triangles.
    /// </summary>
    internal sealed class AvfxModel
    {
        public const int VertexBytes = 36;
        private const int UvOffset = 20;

        private readonly List<(string Name, byte[] Data)> _chunks;

        public List<byte[]> Vertices { get; }
        public List<(ushort A, ushort B, ushort C)> Triangles { get; }

        private AvfxModel(List<(string, byte[])> chunks, List<byte[]> vertices, List<(ushort, ushort, ushort)> triangles)
        {
            _chunks = chunks;
            Vertices = vertices;
            Triangles = triangles;
        }

        public static AvfxModel? Parse(byte[] modlData, out string error)
        {
            error = string.Empty;
            var chunks = new List<(string, byte[])>();
            int o = 0;
            while (o + 8 <= modlData.Length)
            {
                string name = Encoding.ASCII.GetString(modlData, o, 4).Trim('\0');
                name = new string(name.Reverse().ToArray());
                int size = BitConverter.ToInt32(modlData, o + 4);
                if (size < 0 || size > modlData.Length - o - 8) { error = "A model chunk runs past its end."; return null; }
                chunks.Add((name, modlData[(o + 8)..(o + 8 + size)]));
                o += 8 + ((size + 3) & ~3);
            }
            if (o != modlData.Length) { error = "The model has trailing bytes."; return null; }

            var draw = chunks.Where(c => c.Item1 == "VDrw").ToList();
            var index = chunks.Where(c => c.Item1 == "VIdx").ToList();
            if (draw.Count != 1 || index.Count != 1) { error = "The model needs exactly one VDrw and one VIdx."; return null; }
            if (draw[0].Item2.Length % VertexBytes != 0 || index[0].Item2.Length % 6 != 0)
            {
                error = "The model's vertex or index data has an odd size.";
                return null;
            }

            var vertices = Enumerable.Range(0, draw[0].Item2.Length / VertexBytes)
                .Select(i => draw[0].Item2[(i * VertexBytes)..((i + 1) * VertexBytes)]).ToList();
            var triangles = new List<(ushort, ushort, ushort)>();
            var ib = index[0].Item2;
            for (int i = 0; i < ib.Length; i += 6)
            {
                var t = (BitConverter.ToUInt16(ib, i), BitConverter.ToUInt16(ib, i + 2), BitConverter.ToUInt16(ib, i + 4));
                if (t.Item1 >= vertices.Count || t.Item2 >= vertices.Count || t.Item3 >= vertices.Count)
                {
                    error = "A triangle points past the vertex list.";
                    return null;
                }
                triangles.Add(t);
            }
            return new AvfxModel(chunks, vertices, triangles);
        }

        /// <summary>The chunk payload, sub-chunks in their original order, 4-byte padded.</summary>
        public byte[] ToData()
        {
            using var ms = new MemoryStream();
            foreach (var (name, original) in _chunks)
            {
                byte[] data = name switch
                {
                    "VDrw" => Vertices.SelectMany(v => v).ToArray(),
                    "VIdx" => Triangles.SelectMany(t => BitConverter.GetBytes(t.A).Concat(BitConverter.GetBytes(t.B))
                        .Concat(BitConverter.GetBytes(t.C))).ToArray(),
                    _ => original,
                };
                var tag = new byte[4];
                var ascii = Encoding.ASCII.GetBytes(name);
                for (int i = 0; i < ascii.Length && i < 4; i++) tag[ascii.Length - 1 - i] = ascii[i];
                ms.Write(tag);
                ms.Write(BitConverter.GetBytes(data.Length));
                ms.Write(data);
                ms.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
            }
            return ms.ToArray();
        }

        public static (double U, double V) Uv(byte[] vertex, int pair = 0) =>
            ((double)BitConverter.ToHalf(vertex, UvOffset + pair * 4), (double)BitConverter.ToHalf(vertex, UvOffset + pair * 4 + 2));

        private static bool PairsEqual(byte[] v) =>
            v.AsSpan(UvOffset, 4).SequenceEqual(v.AsSpan(UvOffset + 4, 4))
            && v.AsSpan(UvOffset, 4).SequenceEqual(v.AsSpan(UvOffset + 8, 4))
            && v.AsSpan(UvOffset, 4).SequenceEqual(v.AsSpan(UvOffset + 12, 4));

        private readonly record struct ClipPoint(double X, double Y, double A, double B, double C);

        /// <summary>
        /// The triangles that sample the window, with UVs remapped so they sample a crop of just
        /// that window. The region is the window inset by the margin: a triangle wholly inside is
        /// kept verbatim except its UVs, one crossing the edge is clipped, one outside is dropped.
        /// </summary>
        public static AvfxModel? ClipToWindow(AvfxModel source, PanelUvMap map, TexRect window, int margin,
            TexRect rect, int atlasWidth, int atlasHeight, out double coverage, out OverlayRefusal refusal)
        {
            coverage = 0;
            refusal = OverlayRefusal.None;
            if (source.Vertices.Any(v => !PairsEqual(v))) { refusal = OverlayRefusal.UvPairsDiffer; return null; }

            double x0 = window.X + margin, x1 = window.Right - margin, y0 = window.Y + margin, y1 = window.Bottom - margin;
            double regionArea = (x1 - x0) * (y1 - y0);
            (double X, double Y) Px(byte[] v)
            {
                var (u, t) = Uv(v);
                var (s, tt) = map.ToTexture(u, t);
                return (s * atlasWidth, tt * atlasHeight);
            }

            var vertices = new List<byte[]>();
            var lookup = new Dictionary<string, ushort>();
            var triangles = new List<(ushort, ushort, ushort)>();
            var kept = new List<(double X, double Y)[]>();

            ushort Add(byte[] vertex, double px, double py)
            {
                var (u, v) = map.ToModel((px - window.X) / window.Width, (py - window.Y) / window.Height);
                var bytes = (byte[])vertex.Clone();
                for (int pair = 0; pair < 4; pair++)
                {
                    BitConverter.GetBytes((Half)u).CopyTo(bytes, UvOffset + pair * 4);
                    BitConverter.GetBytes((Half)v).CopyTo(bytes, UvOffset + pair * 4 + 2);
                }
                string key = Convert.ToHexString(bytes);
                if (!lookup.TryGetValue(key, out ushort index))
                {
                    index = (ushort)vertices.Count;
                    vertices.Add(bytes);
                    lookup[key] = index;
                }
                return index;
            }

            foreach (var (a, b, c) in source.Triangles)
            {
                byte[] va = source.Vertices[a], vb = source.Vertices[b], vc = source.Vertices[c];
                var pa = Px(va); var pb = Px(vb); var pc = Px(vc);
                bool Inside((double X, double Y) p) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

                if (Inside(pa) && Inside(pb) && Inside(pc))
                {
                    triangles.Add((Add(va, pa.X, pa.Y), Add(vb, pb.X, pb.Y), Add(vc, pc.X, pc.Y)));
                    kept.Add(new[] { pa, pb, pc });
                    continue;
                }

                var poly = new List<ClipPoint> { new(pa.X, pa.Y, 1, 0, 0), new(pb.X, pb.Y, 0, 1, 0), new(pc.X, pc.Y, 0, 0, 1) };
                poly = ClipEdge(poly, p => p.X - x0);
                poly = ClipEdge(poly, p => x1 - p.X);
                poly = ClipEdge(poly, p => p.Y - y0);
                poly = ClipEdge(poly, p => y1 - p.Y);
                if (poly.Count < 3 || Math.Abs(Area(poly.Select(p => (p.X, p.Y)).ToList())) < 1e-6 * regionArea) continue;

                var made = poly.Select(p => Add(Blend(va, vb, vc, p), p.X, p.Y)).ToList();
                for (int i = 1; i + 1 < poly.Count; i++)
                {
                    triangles.Add((made[0], made[i], made[i + 1]));
                    kept.Add(new[] { (poly[0].X, poly[0].Y), (poly[i].X, poly[i].Y), (poly[i + 1].X, poly[i + 1].Y) });
                }
            }

            coverage = Coverage(kept, rect);
            if (triangles.Count == 0 || coverage <= 0) { refusal = OverlayRefusal.NoPanelGeometry; return null; }
            var chunks = new List<(string, byte[])> { ("VDrw", Array.Empty<byte>()), ("VIdx", Array.Empty<byte>()) };
            return new AvfxModel(chunks, vertices, triangles);
        }

        private static List<ClipPoint> ClipEdge(List<ClipPoint> poly, Func<ClipPoint, double> inside)
        {
            var output = new List<ClipPoint>();
            for (int i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var prev = poly[(i + poly.Count - 1) % poly.Count];
                double dc = inside(cur), dp = inside(prev);
                if (dc >= 0)
                {
                    if (dp < 0) output.Add(Lerp(prev, cur, dp / (dp - dc)));
                    output.Add(cur);
                }
                else if (dp >= 0)
                {
                    output.Add(Lerp(prev, cur, dp / (dp - dc)));
                }
            }
            return output;
        }

        private static ClipPoint Lerp(ClipPoint p, ClipPoint q, double t) =>
            new(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.A + (q.A - p.A) * t, p.B + (q.B - p.B) * t, p.C + (q.C - p.C) * t);

        /// <summary>A vertex at barycentric weights; a component the three agree on is copied exactly.</summary>
        private static byte[] Blend(byte[] va, byte[] vb, byte[] vc, ClipPoint p)
        {
            var result = (byte[])va.Clone();
            for (int i = 0; i < 8; i += 2)
            {
                if (va.AsSpan(i, 2).SequenceEqual(vb.AsSpan(i, 2)) && va.AsSpan(i, 2).SequenceEqual(vc.AsSpan(i, 2))) continue;
                double value = p.A * (double)BitConverter.ToHalf(va, i) + p.B * (double)BitConverter.ToHalf(vb, i)
                    + p.C * (double)BitConverter.ToHalf(vc, i);
                BitConverter.GetBytes((Half)value).CopyTo(result, i);
            }
            for (int i = 8; i < UvOffset; i++)
                result[i] = (byte)Math.Clamp(Math.Round(p.A * va[i] + p.B * vb[i] + p.C * vc[i]), 0, 255);
            return result;
        }

        private static double Area(IReadOnlyList<(double X, double Y)> poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                sum += p.X * q.Y - q.X * p.Y;
            }
            return sum / 2;
        }

        /// <summary>Share of the rect's texel centres some kept triangle covers.</summary>
        private static double Coverage(List<(double X, double Y)[]> triangles, TexRect rect)
        {
            int hit = 0;
            for (int y = rect.Y; y < rect.Bottom; y++)
                for (int x = rect.X; x < rect.Right; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    foreach (var t in triangles)
                        if (Contains(t, px, py)) { hit++; break; }
                }
            return (double)hit / (rect.Width * rect.Height);
        }

        private static bool Contains((double X, double Y)[] t, double x, double y)
        {
            double d1 = Cross(t[0], t[1], x, y), d2 = Cross(t[1], t[2], x, y), d3 = Cross(t[2], t[0], x, y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static double Cross((double X, double Y) a, (double X, double Y) b, double x, double y) =>
            (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
    }
}
