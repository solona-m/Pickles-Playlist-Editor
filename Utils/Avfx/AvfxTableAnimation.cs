using Pickles_Playlist_Editor.Utils;
using Pickles_Playlist_Editor.Utils.Tex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Pickles_Playlist_Editor.Utils.Avfx
{
    internal enum InspectKind { NoPanel, Animatable, NotAnimatable }

    internal enum AnimationMode { Append, InPlace }

    internal sealed record TableAvfxInfo(
        string BaselineSha, int PanelParticle, int Emitter, int Item, AnimationMode Mode,
        IReadOnlyList<int> AnimationSet, IReadOnlyList<int> Slots,
        int ParticleCount, int TextureCount, int Capacity);

    internal sealed record InspectResult(InspectKind Kind, TableAvfxInfo? Info, AnimationMessage? Problem);

    /// <summary>
    /// Turns the DJ table's panel particle into a frame animation, the way DJ Moon Base does it:
    /// one particle and one texture per frame, each with a looping alpha curve that is visible only
    /// in its own time slot.
    ///
    /// Append (a static table): hide the panel and append a clone, a Tex entry and an emitter item
    /// per frame. InPlace (a table the author already animated): put the frames on the author's own
    /// frame particles and textures, so the effect never grows past what the pack already ships.
    /// </summary>
    internal static class AvfxTableAnimation
    {
        // Moon Base's own key values, bit for bit.
        public const uint MoonVisible = 0x3FAB28EB;  // 1.337
        public const uint LaptopVisible = 0x3F800000; // 1.0, untested in game; Moon's proven value is 1.337
        private const uint HiddenFirst = 0xBE24166F; // -0.16
        private const uint Hidden = 0xBDF7FE33;      // -0.121
        private const uint End = 0xBF8F8FFC;         // -1.122
        private const uint One = 0x3F800000;

        public const string FramePrefix = "vfx/atex/ppe_";

        private static readonly string[] TexFields = { "TC2", "TC3", "TC4", "TN", "TR", "TD", "TP" };

        public static InspectResult Inspect(byte[] baseline, IReadOnlyCollection<string> tableTextureGamePaths,
            Func<string, bool> isTableShaped)
        {
            AvfxChunk root;
            try { root = AvfxChunk.Parse(baseline); }
            catch { return new(InspectKind.NotAnimatable, null, AnimationMessage.Unparseable); }

            var textures = root.All("Tex").Select(t => t.Text()).ToList();
            var particles = root.All("Ptcl").ToList();
            var tableTex = new HashSet<int>(Enumerable.Range(0, textures.Count)
                .Where(i => tableTextureGamePaths.Any(p => string.Equals(p, textures[i], StringComparison.OrdinalIgnoreCase))));
            if (tableTex.Count == 0) return new(InspectKind.NoPanel, null, null);

            var panels = Enumerable.Range(0, particles.Count).Where(i => tableTex.Contains(Tlst(particles[i]))).ToList();
            if (panels.Count == 0) return new(InspectKind.NoPanel, null, null);
            if (panels.Count > 1) return new(InspectKind.NotAnimatable, null, AnimationMessage.DuplicatePanel);
            int p = panels[0];
            if (particles[p].Child("Col") == null) return new(InspectKind.NotAnimatable, null, AnimationMessage.Unparseable);

            int model = ModelOf(particles[p]);
            var set = new List<int>();
            for (int i = 0; i < particles.Count; i++)
            {
                if (i == p) { set.Add(i); continue; }
                int tex = Tlst(particles[i]);
                if (ModelOf(particles[i]) != model || tex < 0 || tex >= textures.Count) continue;
                if (!isTableShaped(textures[tex])) continue;
                if (!IsStepWindow(particles[i])) continue;
                set.Add(i);
            }

            var items = LastItems(root);
            string sha = Sha(baseline);
            int texCount = textures.Count;

            if (set.Count == 1)
            {
                var targeting = items.Where(it => it.Target == p).ToList();
                if (targeting.Count != 1)
                    return new(InspectKind.NotAnimatable, null, AnimationMessage.PanelItemCount);

                int capacity = Math.Min(FramePlan.MaxFrames, 255 - texCount);
                return new(InspectKind.Animatable, new TableAvfxInfo(sha, p, targeting[0].Emitter, targeting[0].Index,
                    AnimationMode.Append, set, Array.Empty<int>(), particles.Count, texCount, Math.Max(0, capacity)), null);
            }

            var refs = TextureReferenceCounts(particles);
            var slots = set.Where(s =>
            {
                int tex = Tlst(particles[s]);
                return refs.TryGetValue(tex, out int n) && n == 1 && items.Count(it => it.Target == s) == 1;
            }).ToList();
            if (slots.Count == 0)
                return new(InspectKind.NotAnimatable, null, AnimationMessage.NoUsableSlot);

            return new(InspectKind.Animatable, new TableAvfxInfo(sha, p, -1, -1, AnimationMode.InPlace, set, slots,
                particles.Count, texCount, Math.Min(FramePlan.MaxFrames, slots.Count)), null);
        }

        public static byte[]? Build(byte[] baseline, TableAvfxInfo info, IReadOnlyList<string> frameGamePaths,
            FrameTimeline timeline, out string problem, uint visible = MoonVisible)
        {
            problem = string.Empty;
            int n = frameGamePaths.Count;
            if (Sha(baseline) != info.BaselineSha) { problem = AnimationText.Format(AnimationMessage.ModChanged, string.Empty); return null; }
            if (n < 1 || n > info.Capacity || timeline.StartTicks.Count != n)
            {
                problem = AnimationText.Format(AnimationMessage.TooManyTextures, string.Empty);
                return null;
            }

            var root = AvfxChunk.Parse(baseline);
            var particles = root.All("Ptcl").ToList();

            if (info.Mode == AnimationMode.InPlace)
            {
                var textures = root.All("Tex").ToList();
                foreach (int member in info.AnimationSet)
                {
                    int k = IndexOf(info.Slots, member);
                    if (k >= 0 && k < n)
                    {
                        textures[Tlst(particles[member])].Data = AvfxChunk.TextBytes(frameGamePaths[k]);
                        SetAlpha(particles[member], AlphaKeys(k, timeline, visible), Looping(n));
                    }
                    else
                    {
                        SetAlpha(particles[member], HiddenKeys(), false);
                    }
                }
                return root.ToBytes();
            }

            // Append
            var panel = particles[info.PanelParticle];
            var template = panel.Clone();
            SetAlpha(panel, HiddenKeys(), false);

            var emitter = root.All("Emit").ElementAt(info.Emitter);
            var itemTemplate = ItemsOf(emitter.All("ItPr").Last())[info.Item];

            int lastPtcl = root.Children!.FindLastIndex(c => c.Name == "Ptcl");
            var clones = new List<AvfxChunk>();
            for (int k = 0; k < n; k++)
            {
                var clone = template.Clone();
                clone.Child("TC1")!.Child("TLst")!.Data = new[] { (byte)(info.TextureCount + k) };
                SetAlpha(clone, AlphaKeys(k, timeline, visible), Looping(n));
                clones.Add(clone);
            }
            root.Children.InsertRange(lastPtcl + 1, clones);

            int lastTex = root.Children.FindLastIndex(c => c.Name == "Tex");
            root.Children.InsertRange(lastTex + 1,
                frameGamePaths.Select(path => AvfxChunk.Leaf("Tex", AvfxChunk.TextBytes(path))));

            int lastItPr = emitter.Children!.FindLastIndex(c => c.Name == "ItPr");
            var current = emitter.Children[lastItPr];
            var added = new List<AvfxChunk>();
            for (int k = 0; k < n; k++)
            {
                var item = itemTemplate.Select(c => c.Clone()).ToList();
                item.First(c => c.Name == "TgtB").Data = BitConverter.GetBytes(info.ParticleCount + k);
                var next = AvfxChunk.Container("ItPr", current.Children!.Select(c => c.Clone()).Concat(item));
                added.Add(next);
                current = next;
            }
            emitter.Children.InsertRange(lastItPr + 1, added);
            emitter.SetInt("PrCn", emitter.ReadInt("PrCn") + n);

            root.SetInt("PrCn", root.ReadInt("PrCn") + n);
            root.SetInt("TxCn", root.ReadInt("TxCn") + n);
            return root.ToBytes();
        }

        /// <summary>Frame k's looping alpha curve, Moon Base's layout.</summary>
        internal static byte[] AlphaKeys(int frame, FrameTimeline t, uint visible = MoonVisible)
        {
            int n = t.StartTicks.Count;
            int loop = t.LoopTicks;
            if (n == 1) return Key(0, 1, One);

            int end = frame + 1 < n ? t.StartTicks[frame + 1] : loop;
            if (frame == 0)
                return Concat(Key(0, 2, visible), Key(end, 2, Hidden), Key(loop, 1, End));
            return Concat(Key(0, 2, HiddenFirst), Key(t.StartTicks[frame], 2, visible),
                Key(end, 2, Hidden), Key(loop, 1, End));
        }

        internal static byte[] HiddenKeys() => Key(0, 2, HiddenFirst);

        /// <summary>The tokens in this effect's <c>vfx/atex/ppe_&lt;token&gt;_…</c> texture names.</summary>
        public static IReadOnlySet<string> FrameTokens(byte[] avfx)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            AvfxChunk root;
            try { root = AvfxChunk.Parse(avfx); }
            catch { return tokens; }

            foreach (var tex in root.All("Tex"))
            {
                string path = tex.Text();
                if (!path.StartsWith(FramePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = path[FramePrefix.Length..];
                int underscore = rest.IndexOf('_');
                if (underscore > 0) tokens.Add(rest[..underscore]);
            }
            return tokens;
        }

        // ---- particle helpers --------------------------------------------------------------------

        internal static int Tlst(AvfxChunk particle)
        {
            var data = particle.Child("TC1")?.Child("TLst")?.Data;
            return data is { Length: > 0 } && data[0] != 0xFF ? data[0] : -1;
        }

        private static int ModelOf(AvfxChunk particle)
        {
            var data = particle.Child("Data")?.Child("MdNo")?.Data;
            return data is { Length: > 0 } ? data[0] : -1;
        }

        /// <summary>A Moon-style step window: looping, step keys, one visible window.</summary>
        private static bool IsStepWindow(AvfxChunk particle)
        {
            var alpha = particle.Child("Col")?.Child("A");
            if (alpha == null || alpha.ReadInt("BvPo") != 1) return false;
            var keys = alpha.Child("Keys")?.Data;
            if (keys == null || keys.Length < 32 || keys.Length % 16 != 0) return false;

            int count = keys.Length / 16;
            int windows = 0;
            for (int i = 0; i < count; i++)
            {
                int type = BitConverter.ToUInt16(keys, i * 16 + 2);
                float value = BitConverter.ToSingle(keys, i * 16 + 12);
                if (type != (i == count - 1 ? 1 : 2)) return false;
                if (value > 0 && i < count - 1) windows++;
            }
            return windows == 1;
        }

        private static Dictionary<int, int> TextureReferenceCounts(List<AvfxChunk> particles)
        {
            var counts = new Dictionary<int, int>();
            void Count(int tex) { if (tex >= 0) counts[tex] = counts.GetValueOrDefault(tex) + 1; }

            foreach (var particle in particles)
            {
                Count(Tlst(particle));
                foreach (string field in TexFields)
                    Count(particle.Child(field)?.ReadInt("TxNo") ?? -1);
            }
            return counts;
        }

        private static bool Looping(int frames) => frames > 1;

        private static void SetAlpha(AvfxChunk particle, byte[] keys, bool looping)
        {
            var col = particle.Child("Col") ?? throw new InvalidDataException("The panel particle has no colour curve.");
            if (col.Children == null)
            {
                // An empty colour chunk (the laptop): a container holding just the alpha curve.
                int at = particle.Children!.IndexOf(col);
                col = AvfxChunk.Container("Col", Array.Empty<AvfxChunk>());
                particle.Children[at] = col;
            }
            var alpha = col.Child("A");
            if (alpha == null)
            {
                alpha = AvfxChunk.Container("A", new[]
                {
                    AvfxChunk.Leaf("KeyC", new byte[4]), AvfxChunk.Leaf("BvPr", new byte[4]),
                    AvfxChunk.Leaf("BvPo", new byte[4]), AvfxChunk.Leaf("RanT", new byte[4]),
                    AvfxChunk.Leaf("Keys", Array.Empty<byte>()),
                });
                int rgb = col.Children!.FindIndex(c => c.Name == "RGB");
                col.Children.Insert(rgb + 1, alpha);
            }

            alpha.SetInt("KeyC", keys.Length / 16);
            alpha.SetInt("BvPr", looping ? 1 : 0);
            alpha.SetInt("BvPo", looping ? 1 : 0);
            alpha.Child("Keys")!.Data = keys;
        }

        // ---- emitter items -----------------------------------------------------------------------

        private sealed record Item(int Emitter, int Index, int Target);

        /// <summary>Every item of every emitter, read from its last (complete) ItPr.</summary>
        private static List<Item> LastItems(AvfxChunk root)
        {
            var result = new List<Item>();
            int e = 0;
            foreach (var emitter in root.All("Emit"))
            {
                var last = emitter.All("ItPr").LastOrDefault();
                if (last != null)
                {
                    var items = ItemsOf(last);
                    for (int i = 0; i < items.Count; i++)
                    {
                        var target = items[i].FirstOrDefault(c => c.Name == "TgtB")?.Data;
                        result.Add(new Item(e, i, target is { Length: >= 4 } ? BitConverter.ToInt32(target, 0) : -1));
                    }
                }
                e++;
            }
            return result;
        }

        /// <summary>An effect's particles, textures and the items in one emitter's last ItPr, for the tested-size envelope.</summary>
        internal static (int Particles, int Textures, int Items) Counts(byte[] avfx, int emitter)
        {
            var root = AvfxChunk.Parse(avfx);
            var last = root.All("Emit").ElementAtOrDefault(emitter)?.All("ItPr").LastOrDefault();
            return (root.All("Ptcl").Count(), root.All("Tex").Count(), last == null ? 0 : ItemsOf(last).Count);
        }

        private static List<List<AvfxChunk>> ItemsOf(AvfxChunk itpr)
        {
            var items = new List<List<AvfxChunk>>();
            foreach (var leaf in itpr.Children ?? new List<AvfxChunk>())
            {
                if (leaf.Name == "bEnb" || items.Count == 0) items.Add(new List<AvfxChunk>());
                items[^1].Add(leaf);
            }
            return items;
        }

        // ---- encoding ----------------------------------------------------------------------------

        private static byte[] Key(int time, int type, uint value)
        {
            var key = new byte[16];
            BitConverter.GetBytes((ushort)time).CopyTo(key, 0);
            BitConverter.GetBytes((ushort)type).CopyTo(key, 2);
            BitConverter.GetBytes(One).CopyTo(key, 4);
            BitConverter.GetBytes(One).CopyTo(key, 8);
            BitConverter.GetBytes(value).CopyTo(key, 12);
            return key;
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
            return -1;
        }

        public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}
