using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pickles_Playlist_Editor.Utils.Avfx
{
    /// <summary>
    /// A minimal AVFX chunk tree: just enough structure to edit particles, textures and emitter
    /// items, with every other chunk carried as raw bytes.
    ///
    /// Names are stored byte-reversed and zero-padded ("Tex" is <c>78 65 54 00</c>). A leaf's size
    /// field excludes its padding; padding bytes are kept so an untouched file round-trips exactly.
    /// </summary>
    internal sealed class AvfxChunk
    {
        // Containers are expanded only along these paths; everything else stays a leaf.
        private static readonly HashSet<string> Expand = new(StringComparer.Ordinal)
        {
            "Ptcl", "Ptcl/Col", "Ptcl/Col/*",
            "Ptcl/TC1", "Ptcl/TC2", "Ptcl/TC3", "Ptcl/TC4",
            "Ptcl/TN", "Ptcl/TR", "Ptcl/TD", "Ptcl/TP", "Ptcl/Data",
            "Emit", "Emit/ItPr",
        };

        public string Name { get; }
        public byte[] Tag { get; }
        public byte[]? Data { get; set; }
        public List<AvfxChunk>? Children { get; private set; }

        private byte[] _pad = Array.Empty<byte>();
        private byte[] _trailing = Array.Empty<byte>();

        private AvfxChunk(byte[] tag)
        {
            Tag = tag;
            Name = NameOf(tag);
        }

        public static AvfxChunk Leaf(string name, byte[] data) => new(TagOf(name)) { Data = data };

        public static AvfxChunk Container(string name, IEnumerable<AvfxChunk> children) =>
            new(TagOf(name)) { Children = children.ToList() };

        public static AvfxChunk Parse(byte[] file)
        {
            if (file.Length < 8) throw new InvalidDataException("Too small to be an AVFX file.");
            var root = new AvfxChunk(file[..4]);
            int size = BitConverter.ToInt32(file, 4);
            if (size < 0 || 8 + size > file.Length) throw new InvalidDataException("AVFX root size is out of range.");

            root.Children = ParseChildren(file, 8, 8 + size, "")
                ?? throw new InvalidDataException("AVFX root does not parse as chunks.");
            root._trailing = file[(8 + size)..];
            return root;
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            ms.Write(Tag);
            ms.Write(BitConverter.GetBytes(ContentSize()));
            WriteContent(ms);
            ms.Write(_trailing);
            return ms.ToArray();
        }

        public AvfxChunk Clone() => new(Tag)
        {
            Data = Data == null ? null : (byte[])Data.Clone(),
            Children = Children?.Select(c => c.Clone()).ToList(),
            _pad = _pad,
            _trailing = _trailing,
        };

        public IEnumerable<AvfxChunk> All(string name) =>
            Children?.Where(c => c.Name == name) ?? Enumerable.Empty<AvfxChunk>();

        public AvfxChunk? Child(string name) => Children?.FirstOrDefault(c => c.Name == name);

        public int ReadInt(string name)
        {
            var data = Child(name)?.Data;
            if (data == null || data.Length == 0) return -1;
            return data.Length >= 4 ? BitConverter.ToInt32(data, 0) : data[0];
        }

        public void SetInt(string name, int value)
        {
            var chunk = Child(name) ?? throw new InvalidDataException($"No {name} chunk.");
            chunk.Data = BitConverter.GetBytes(value);
        }

        /// <summary>A Tex chunk's path, without its NUL.</summary>
        public string Text()
        {
            var data = Data ?? Array.Empty<byte>();
            int end = Array.IndexOf(data, (byte)0);
            return Encoding.ASCII.GetString(data, 0, end < 0 ? data.Length : end);
        }

        public static byte[] TextBytes(string text) => Encoding.ASCII.GetBytes(text + "\0");

        // ---- encoding ----------------------------------------------------------------------------

        private int ContentSize()
        {
            if (Children == null) return Data?.Length ?? 0;
            int total = 0;
            foreach (var child in Children) total += 8 + Round4(child.ContentSize());
            return total;
        }

        private void WriteContent(Stream ms)
        {
            if (Children == null)
            {
                var data = Data ?? Array.Empty<byte>();
                ms.Write(data);
                int pad = Round4(data.Length) - data.Length;
                ms.Write(_pad.Length == pad ? _pad : new byte[pad]);
                return;
            }

            foreach (var child in Children)
            {
                ms.Write(child.Tag);
                ms.Write(BitConverter.GetBytes(child.ContentSize()));
                child.WriteContent(ms);
            }
        }

        private static List<AvfxChunk>? ParseChildren(byte[] file, int start, int end, string path)
        {
            var list = new List<AvfxChunk>();
            int o = start;
            while (o + 8 <= end)
            {
                var chunk = new AvfxChunk(file[o..(o + 4)]);
                int size = BitConverter.ToInt32(file, o + 4);
                int body = o + 8;
                if (size < 0 || body + size > end) return null;

                int padded = Round4(size);
                string childPath = path.Length == 0 ? chunk.Name : path + "/" + chunk.Name;
                var children = ShouldExpand(childPath) && size >= 8 && padded == size
                    ? ParseChildren(file, body, body + size, childPath)
                    : null;

                if (children != null)
                {
                    chunk.Children = children;
                }
                else
                {
                    chunk.Data = file[body..(body + size)];
                    int padEnd = Math.Min(end, body + padded);
                    chunk._pad = file[(body + size)..padEnd];
                    if (chunk._pad.Length != padded - size) return null;
                }

                list.Add(chunk);
                o = body + padded;
            }
            return o == end ? list : null;
        }

        private static bool ShouldExpand(string path)
        {
            if (Expand.Contains(path)) return true;
            int slash = path.LastIndexOf('/');
            return slash > 0 && Expand.Contains(path[..slash] + "/*");
        }

        private static int Round4(int value) => (value + 3) & ~3;

        private static string NameOf(byte[] tag)
        {
            var reversed = tag.Reverse().ToArray();
            return Encoding.ASCII.GetString(reversed).Trim('\0');
        }

        private static byte[] TagOf(string name)
        {
            var tag = new byte[4];
            var ascii = Encoding.ASCII.GetBytes(name);
            for (int i = 0; i < ascii.Length && i < 4; i++) tag[ascii.Length - 1 - i] = ascii[i];
            return tag;
        }
    }
}
