using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// The guild hall's emblem banners. A banner shows its guild's tabard, and only this PC can paint
    /// that into the game: before each start, Data\Patch-Y.MPQ gets, for every guild the XorWoW addon
    /// has seen on this PC (XorWoWSettings.guildEmblems in the accounts' SavedVariables), the guild's
    /// banner texture and its copies of the banner models. The realm's own patch (Patch-Z, built by
    /// scripts\client-patch\build.py) holds the plain templates and World\XorWoW\GuildBanners\
    /// GuildBanners.txt, which names them; the server shows guild n display &lt;model's&gt; + n, whose
    /// model is the template's name with 0000 = n.
    ///
    /// The tabard is the client's own guild emblem pieces (Textures\GuildEmblems): the background
    /// colour dyes the plain cloth, the border and emblem go on top. Each piece is half a tabard front
    /// (the character model mirrors it around the chest's centre line), so it is mirrored back whole.
    /// A guild seen for the first time, or a new design, shows at the next start through the launcher.
    /// </summary>
    public static class GuildBanners
    {
        public const string Archive = "Patch-Y.MPQ";
        const string ManifestName = @"World\XorWoW\GuildBanners\GuildBanners.txt";

        /// <summary>Writes Data\Patch-Y.MPQ when what it should hold changed. Never throws: the game starts anyway.</summary>
        public static void Update(string gameDir)
        {
            try
            {
                var data = Path.Combine(gameDir, "Data");
                if (!Directory.Exists(data)) return;
                var target = Path.Combine(data, Archive);
                var designs = Designs(Path.Combine(gameDir, "WTF", "Account"));
                if (designs.Count == 0)
                {
                    if (File.Exists(target)) { File.Delete(target); Log.Write("guild banners: no guild seen, " + Archive + " removed"); }
                    return;
                }
                Dictionary<string, byte[]> files;
                using (var chain = new ArchiveChain(data))
                    files = Build(chain, designs);
                if (files == null) return;
                var bytes = MpqWriter.Build(files);
                if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes)) return;
                File.WriteAllBytes(target, bytes);
                Log.Write($"guild banners: {Archive} written for guild(s) {string.Join(", ", designs.Keys.OrderBy(k => k))}");
            }
            catch (Exception ex) { Log.Write("guild banners: " + ex.Message); }
        }

        /// <summary>Guild id -> "style,color,border,border color,background", from every account's XorWoW settings.</summary>
        static Dictionary<int, int[]> Designs(string accounts)
        {
            var found = new Dictionary<int, int[]>();
            if (!Directory.Exists(accounts)) return found;
            foreach (var file in Directory.EnumerateFiles(accounts, "XorWoW.lua", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "SavedVariables", StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                var block = Regex.Match(text, @"\[""guildEmblems""\]\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
                if (!block.Success) continue;
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"\[(\d+)\]\s*=\s*""(\d+),(\d+),(\d+),(\d+),(\d+)"""))
                    found[int.Parse(m.Groups[1].Value)] = Enumerable.Range(2, 5).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
            }
            return found;
        }

        static Dictionary<string, byte[]> Build(ArchiveChain chain, Dictionary<int, int[]> designs)
        {
            var manifest = chain.Read(ManifestName);
            if (manifest == null) { Log.Write("guild banners: the realm's patch has no " + ManifestName); return null; }
            string texture = null;
            int slots = 0;
            int[] design = { 0, 24, 256, 192 };
            var models = new List<string>();
            foreach (var raw in Encoding.ASCII.GetString(manifest).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var sp = line.IndexOf(' ');
                var key = sp < 0 ? line : line.Substring(0, sp);
                var value = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                if (key == "texture") texture = value;
                else if (key == "slots") slots = int.Parse(value);
                else if (key == "design") design = value.Split(' ').Select(int.Parse).ToArray();
                else if (key == "model") models.Add(value);
            }
            var clothData = texture == null ? null : chain.Read(texture);
            if (clothData == null) { Log.Write("guild banners: no template texture " + texture); return null; }
            var cloth = Blp.Decode(clothData);

            var files = new Dictionary<string, byte[]>();
            foreach (var kv in designs.OrderBy(k => k.Key))
            {
                int guild = kv.Key;
                if (guild < 1 || guild > slots) continue;
                var slot = guild.ToString("D4");
                files[Slot(texture, slot)] = Blp.EncodeDxt5(Paint(chain, cloth, kv.Value, design));
                foreach (var model in models)
                {
                    var m2 = chain.Read(model);
                    if (m2 == null) { Log.Write("guild banners: no template " + model); continue; }
                    files[Slot(model, slot)] = RenameTexture(m2, Path.GetFileName(texture), Path.GetFileName(Slot(texture, slot)));
                    int views = BitConverter.ToInt32(m2, 0x44);
                    var stem = model.Substring(0, model.Length - 3);
                    for (int v = 0; v < views; v++)
                    {
                        var skin = chain.Read(stem + v.ToString("D2") + ".skin");
                        if (skin != null) files[Slot(stem, slot) + v.ToString("D2") + ".skin"] = skin;
                    }
                }
            }
            return files;
        }

        /// <summary>A template name with its slot (0000) set.</summary>
        static string Slot(string name, string slot)
        {
            var i = name.LastIndexOf("_0000", StringComparison.Ordinal);
            return i < 0 ? name : name.Substring(0, i + 1) + slot + name.Substring(i + 5);
        }

        /// <summary>The model with every occurrence of the texture file name replaced (same length: nothing moves).</summary>
        static byte[] RenameTexture(byte[] m2, string from, string to)
        {
            var copy = (byte[])m2.Clone();
            var a = Encoding.ASCII.GetBytes(from.ToLowerInvariant());
            var b = Encoding.ASCII.GetBytes(to);
            for (int i = 0; i + a.Length <= copy.Length; i++)
            {
                int k = 0;
                while (k < a.Length && char.ToLowerInvariant((char)copy[i + k]) == a[k]) k++;
                if (k == a.Length) Buffer.BlockCopy(b, 0, copy, i, b.Length);
            }
            return copy;
        }

        // ------------------------------------------------------------------ painting

        /// <summary>The cloth dyed in the guild's background colour, its border and emblem on top.</summary>
        static Image Paint(ArchiveChain chain, Image cloth, int[] d, int[] rect)
        {
            int style = d[0], color = d[1], border = d[2], borderColor = d[3], background = d[4];
            int w = cloth.W, h = cloth.H;
            var shade = new float[w * h];
            var lums = new List<float>();
            for (int i = 0; i < w * h; i++)
            {
                shade[i] = (cloth.Px[i * 4] + cloth.Px[i * 4 + 1] + cloth.Px[i * 4 + 2]) / 3f;
                if (cloth.Px[i * 4 + 3] > 128) lums.Add(shade[i]);
            }
            lums.Sort();
            float median = lums.Count > 0 ? Math.Max(1, lums[lums.Count / 2]) : 128;
            for (int i = 0; i < shade.Length; i++) shade[i] /= median;

            var bg = AverageColour(Piece(chain, $"Background_{background:D2}_TU_U.blp"));
            var output = new Image(w, h);
            for (int i = 0; i < w * h; i++)
            {
                for (int c = 0; c < 3; c++) output.Px[i * 4 + c] = Clamp(bg[c] * shade[i]);
                output.Px[i * 4 + 3] = cloth.Px[i * 4 + 3];
            }

            var front = new Image(128, 96);
            foreach (var kind in new[] { $"Border_{border:D2}_{borderColor:D2}", $"Emblem_{style:D2}_{color:D2}" })
            {
                var upper = Piece(chain, kind + "_TU_U.blp");
                var lower = Piece(chain, kind + "_TL_U.blp");
                if (upper == null || lower == null) continue;   // a design the client has no art for: left out, as on a tabard
                for (int y = 0; y < 96; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        var src = y < 64 ? upper : lower;
                        int sy = y < 64 ? y : y - 64;
                        if (x >= src.W || sy >= src.H) continue;
                        int s = (sy * src.W + x) * 4;
                        Over(front, 64 + x, y, src.Px, s);
                        Over(front, 63 - x, y, src.Px, s);
                    }
            }

            // the tabard front scaled onto the cloth (bilinear, premultiplied), in the cloth's folds
            int rx = rect[0], ry = rect[1], rw = rect[2], rh = rect[3];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int ox = rx + x, oy = ry + y;
                    if (ox < 0 || oy < 0 || ox >= w || oy >= h) continue;
                    var p = Sample(front, (x + 0.5f) * front.W / rw - 0.5f, (y + 0.5f) * front.H / rh - 0.5f);
                    float a = p[3];
                    if (a <= 0) continue;
                    int o = (oy * w + ox) * 4;
                    float s = Math.Min(Math.Max(shade[oy * w + ox], 0), 1.4f);
                    for (int c = 0; c < 3; c++)
                        output.Px[o + c] = Clamp(output.Px[o + c] * (1 - a) + p[c] * 255 * s);
                }
            return output;
        }

        static Image Piece(ArchiveChain chain, string name)
        {
            var data = chain.Read(@"Textures\GuildEmblems\" + name);
            return data == null ? null : Blp.Decode(data);
        }

        static float[] AverageColour(Image img)
        {
            var sum = new double[3];
            int n = 0;
            if (img != null)
                for (int i = 0; i < img.W * img.H; i++)
                    if (img.Px[i * 4 + 3] > 128) { for (int c = 0; c < 3; c++) sum[c] += img.Px[i * 4 + c]; n++; }
            return n == 0 ? new[] { 128f, 128f, 128f } : sum.Select(v => (float)(v / n)).ToArray();
        }

        static void Over(Image dst, int x, int y, byte[] src, int s)
        {
            float a = src[s + 3] / 255f;
            if (a <= 0) return;
            int d = (y * dst.W + x) * 4;
            float da = dst.Px[d + 3] / 255f, oa = a + da * (1 - a);
            for (int c = 0; c < 3; c++)
                dst.Px[d + c] = Clamp((src[s + c] * a + dst.Px[d + c] * da * (1 - a)) / oa);
            dst.Px[d + 3] = Clamp(oa * 255);
        }

        /// <summary>Premultiplied rgba (0..1) at a fractional position; outside is transparent.</summary>
        static float[] Sample(Image img, float fx, float fy)
        {
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            var r = new float[4];
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    int x = x0 + i, y = y0 + j;
                    if (x < 0 || y < 0 || x >= img.W || y >= img.H) continue;
                    float wgt = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                    int o = (y * img.W + x) * 4;
                    float a = img.Px[o + 3] / 255f;
                    for (int c = 0; c < 3; c++) r[c] += img.Px[o + c] / 255f * a * wgt;
                    r[3] += a * wgt;
                }
            return r;
        }

        static byte Clamp(float v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f));
    }

    /// <summary>RGBA, 8 bits a channel, rows top to bottom.</summary>
    sealed class Image
    {
        public readonly int W, H;
        public readonly byte[] Px;
        public Image(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }
    }

    /// <summary>BLP2 textures: the stock formats read (palette, DXT1/3/5, raw), DXT5 with mipmaps written.</summary>
    static class Blp
    {
        public static Image Decode(byte[] d)
        {
            if (Encoding.ASCII.GetString(d, 0, 4) != "BLP2") throw new InvalidDataException("not a BLP2 texture");
            int comp = d[8], alphaDepth = d[9], alphaType = d[10];
            int w = BitConverter.ToInt32(d, 12), h = BitConverter.ToInt32(d, 16);
            int offset = BitConverter.ToInt32(d, 20);
            var img = new Image(w, h);
            if (comp == 1)
            {
                for (int i = 0; i < w * h; i++)
                {
                    int p = 148 + d[offset + i] * 4;
                    img.Px[i * 4] = d[p + 2]; img.Px[i * 4 + 1] = d[p + 1]; img.Px[i * 4 + 2] = d[p];
                    int a = 255, at = offset + w * h;
                    if (alphaDepth == 8) a = d[at + i];
                    else if (alphaDepth == 1) a = (d[at + i / 8] >> (i % 8) & 1) * 255;
                    else if (alphaDepth == 4) a = (d[at + i / 2] >> (i % 2 * 4) & 15) * 17;
                    img.Px[i * 4 + 3] = (byte)a;
                }
                return img;
            }
            if (comp == 3)
            {
                for (int i = 0; i < w * h; i++)
                {
                    img.Px[i * 4] = d[offset + i * 4 + 2]; img.Px[i * 4 + 1] = d[offset + i * 4 + 1];
                    img.Px[i * 4 + 2] = d[offset + i * 4]; img.Px[i * 4 + 3] = d[offset + i * 4 + 3];
                }
                return img;
            }
            if (comp != 2) throw new InvalidDataException("BLP compression " + comp);
            bool dxt1 = alphaDepth <= 1;
            int blockSize = dxt1 ? 8 : 16, pos = offset;
            var colours = new byte[16 * 4];
            for (int by = 0; by < (h + 3) / 4; by++)
                for (int bx = 0; bx < (w + 3) / 4; bx++, pos += blockSize)
                {
                    int c = dxt1 ? pos : pos + 8;
                    DecodeColours(d, c, dxt1, colours);
                    for (int k = 0; k < 16; k++)
                    {
                        int x = bx * 4 + k % 4, y = by * 4 + k / 4;
                        if (x >= w || y >= h) continue;
                        int o = (y * w + x) * 4;
                        Buffer.BlockCopy(colours, k * 4, img.Px, o, 4);
                        if (dxt1) { if (alphaDepth == 0) img.Px[o + 3] = 255; }
                        else if (alphaType == 1) img.Px[o + 3] = (byte)((d[pos + k / 2] >> (k % 2 * 4) & 15) * 17);
                        else img.Px[o + 3] = Dxt5Alpha(d, pos, k);
                    }
                }
            return img;
        }

        static void DecodeColours(byte[] d, int p, bool dxt1, byte[] out16)
        {
            int c0 = BitConverter.ToUInt16(d, p), c1 = BitConverter.ToUInt16(d, p + 2);
            uint bits = BitConverter.ToUInt32(d, p + 4);
            var pal = new int[4, 4];
            var a = Rgb565(c0); var b = Rgb565(c1);
            bool four = c0 > c1 || !dxt1;
            for (int c = 0; c < 3; c++)
            {
                pal[0, c] = a[c]; pal[1, c] = b[c];
                pal[2, c] = four ? (2 * a[c] + b[c]) / 3 : (a[c] + b[c]) / 2;
                pal[3, c] = four ? (a[c] + 2 * b[c]) / 3 : 0;
            }
            for (int i = 0; i < 4; i++) pal[i, 3] = 255;
            if (!four) pal[3, 3] = 0;
            for (int k = 0; k < 16; k++)
            {
                int idx = (int)(bits >> (2 * k) & 3);
                for (int c = 0; c < 4; c++) out16[k * 4 + c] = (byte)pal[idx, c];
            }
        }

        static int[] Rgb565(int v)
        {
            int r = v >> 11 & 31, g = v >> 5 & 63, b = v & 31;
            return new[] { r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2 };
        }

        static byte Dxt5Alpha(byte[] d, int p, int k)
        {
            int a0 = d[p], a1 = d[p + 1];
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)d[p + 2 + i] << (8 * i);
            int idx = (int)(bits >> (3 * k) & 7);
            if (idx == 0) return (byte)a0;
            if (idx == 1) return (byte)a1;
            if (a0 > a1) return (byte)(((8 - idx) * a0 + (idx - 1) * a1) / 7);
            if (idx == 6) return 0;
            if (idx == 7) return 255;
            return (byte)(((6 - idx) * a0 + (idx - 1) * a1) / 5);
        }

        /// <summary>A power-of-two image as a DXT5 BLP2 with the whole mip chain, like the stock cloth.</summary>
        public static byte[] EncodeDxt5(Image img)
        {
            var levels = new List<byte[]>();
            var level = img;
            while (true)
            {
                levels.Add(EncodeLevel(level));
                if (level.W == 1 && level.H == 1) break;
                level = Half(level);
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("BLP2"));
                bw.Write(1);
                bw.Write((byte)2); bw.Write((byte)8); bw.Write((byte)7); bw.Write((byte)1);
                bw.Write(img.W); bw.Write(img.H);
                int pos = 148 + 1024;
                for (int i = 0; i < 16; i++) { bw.Write(i < levels.Count ? pos : 0); if (i < levels.Count) pos += levels[i].Length; }
                for (int i = 0; i < 16; i++) bw.Write(i < levels.Count ? levels[i].Length : 0);
                bw.Write(new byte[1024]);
                foreach (var l in levels) bw.Write(l);
                return ms.ToArray();
            }
        }

        static Image Half(Image s)
        {
            int w = Math.Max(1, s.W / 2), h = Math.Max(1, s.H / 2);
            var d = new Image(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // alpha-weighted colour, so transparent texels do not darken the edges
                    float a = 0, r = 0, g = 0, b = 0;
                    for (int j = 0; j < 2; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            int sx = Math.Min(s.W - 1, x * 2 + i), sy = Math.Min(s.H - 1, y * 2 + j), o = (sy * s.W + sx) * 4;
                            float pa = s.Px[o + 3] + 1;
                            r += s.Px[o] * pa; g += s.Px[o + 1] * pa; b += s.Px[o + 2] * pa; a += pa;
                        }
                    int q = (y * w + x) * 4;
                    d.Px[q] = (byte)(r / a); d.Px[q + 1] = (byte)(g / a); d.Px[q + 2] = (byte)(b / a);
                    d.Px[q + 3] = (byte)Math.Min(255, (a - 4) / 4 + 0.5f);
                }
            return d;
        }

        static byte[] EncodeLevel(Image img)
        {
            int bw = (img.W + 3) / 4, bh = (img.H + 3) / 4;
            var output = new byte[bw * bh * 16];
            var block = new byte[16 * 4];
            int pos = 0;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++, pos += 16)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        int x = Math.Min(img.W - 1, bx * 4 + k % 4), y = Math.Min(img.H - 1, by * 4 + k / 4);
                        Buffer.BlockCopy(img.Px, (y * img.W + x) * 4, block, k * 4, 4);
                    }
                    EncodeAlpha(block, output, pos);
                    EncodeColour(block, output, pos + 8);
                }
            return output;
        }

        static void EncodeAlpha(byte[] block, byte[] o, int p)
        {
            int lo = 255, hi = 0;
            for (int k = 0; k < 16; k++) { lo = Math.Min(lo, block[k * 4 + 3]); hi = Math.Max(hi, block[k * 4 + 3]); }
            o[p] = (byte)hi; o[p + 1] = (byte)lo;   // a0 > a1: eight interpolated steps
            ulong bits = 0;
            for (int k = 0; k < 16; k++)
            {
                int a = block[k * 4 + 3], idx;
                if (hi == lo) idx = 0;
                else
                {
                    int step = (int)Math.Round((hi - a) * 7.0 / (hi - lo));   // 0 = hi ... 7 = lo
                    idx = step == 0 ? 0 : step == 7 ? 1 : step + 1;
                }
                bits |= (ulong)idx << (3 * k);
            }
            for (int i = 0; i < 6; i++) o[p + 2 + i] = (byte)(bits >> (8 * i));
        }

        static void EncodeColour(byte[] block, byte[] o, int p)
        {
            // endpoints: the block's extremes along its principal colour axis
            float[] mean = new float[3];
            for (int k = 0; k < 16; k++) for (int c = 0; c < 3; c++) mean[c] += block[k * 4 + c] / 16f;
            var cov = new float[3, 3];
            for (int k = 0; k < 16; k++)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        cov[i, j] += (block[k * 4 + i] - mean[i]) * (block[k * 4 + j] - mean[j]);
            float[] axis = { 1, 1, 1 };
            for (int it = 0; it < 8; it++)
            {
                var n = new float[3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) n[i] += cov[i, j] * axis[j];
                float len = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (len < 1e-6f) { axis = new[] { 0.577f, 0.577f, 0.577f }; break; }
                for (int i = 0; i < 3; i++) axis[i] = n[i] / len;
            }
            float min = float.MaxValue, max = float.MinValue;
            for (int k = 0; k < 16; k++)
            {
                float t = 0;
                for (int c = 0; c < 3; c++) t += (block[k * 4 + c] - mean[c]) * axis[c];
                min = Math.Min(min, t); max = Math.Max(max, t);
            }
            int c0 = To565(mean, axis, max), c1 = To565(mean, axis, min);
            if (c0 < c1) { var t = c0; c0 = c1; c1 = t; }
            o[p] = (byte)c0; o[p + 1] = (byte)(c0 >> 8); o[p + 2] = (byte)c1; o[p + 3] = (byte)(c1 >> 8);
            var a = Rgb565(c0); var b = Rgb565(c1);
            var pal = new int[4][];
            pal[0] = a; pal[1] = b;
            pal[2] = new[] { (2 * a[0] + b[0]) / 3, (2 * a[1] + b[1]) / 3, (2 * a[2] + b[2]) / 3 };
            pal[3] = new[] { (a[0] + 2 * b[0]) / 3, (a[1] + 2 * b[1]) / 3, (a[2] + 2 * b[2]) / 3 };
            uint bits = 0;
            for (int k = 0; k < 16; k++)
            {
                int best = 0, bestD = int.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    int dr = block[k * 4] - pal[i][0], dg = block[k * 4 + 1] - pal[i][1], db = block[k * 4 + 2] - pal[i][2];
                    int dd = dr * dr + dg * dg + db * db;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
                if (c0 == c1) best = 0;
                bits |= (uint)best << (2 * k);
            }
            o[p + 4] = (byte)bits; o[p + 5] = (byte)(bits >> 8); o[p + 6] = (byte)(bits >> 16); o[p + 7] = (byte)(bits >> 24);
        }

        static int To565(float[] mean, float[] axis, float t)
        {
            int r = Lim(mean[0] + axis[0] * t), g = Lim(mean[1] + axis[1] * t), b = Lim(mean[2] + axis[2] * t);
            return (r * 31 + 127) / 255 << 11 | (g * 63 + 127) / 255 << 5 | (b * 31 + 127) / 255;
        }

        static int Lim(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f);
    }

    /// <summary>MPQ hashing and table encryption.</summary>
    static class MpqCrypt
    {
        public static readonly uint[] Table = BuildTable();
        public const int Offset = 0, HashA = 1, HashB = 2, Key = 3;

        static uint[] BuildTable()
        {
            var t = new uint[0x500];
            uint seed = 0x00100001;
            for (uint i = 0; i < 0x100; i++)
                for (uint idx = i, k = 0; k < 5; k++, idx += 0x100)
                {
                    seed = (seed * 125 + 3) % 0x2AAAAB; uint hi = (seed & 0xFFFF) << 16;
                    seed = (seed * 125 + 3) % 0x2AAAAB; t[idx] = hi | (seed & 0xFFFF);
                }
            return t;
        }

        public static uint Hash(string name, int kind)
        {
            uint s1 = 0x7FED7FED, s2 = 0xEEEEEEEE;
            foreach (var ch in Encoding.ASCII.GetBytes(name.ToUpperInvariant().Replace('/', '\\')))
            {
                s1 = Table[kind * 0x100 + ch] ^ (s1 + s2);
                s2 = ch + s1 + s2 + (s2 << 5) + 3;
            }
            return s1;
        }

        public static void Decrypt(uint[] words, uint key)
        {
            uint seed = 0xEEEEEEEE;
            for (int i = 0; i < words.Length; i++)
            {
                seed += Table[0x400 + (key & 0xFF)];
                uint plain = words[i] ^ (key + seed);
                key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
                seed = plain + seed + (seed << 5) + 3;
                words[i] = plain;
            }
        }

        public static void Encrypt(uint[] words, uint key)
        {
            uint seed = 0xEEEEEEEE;
            for (int i = 0; i < words.Length; i++)
            {
                seed += Table[0x400 + (key & 0xFF)];
                uint plain = words[i];
                words[i] = plain ^ (key + seed);
                key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
                seed = plain + seed + (seed << 5) + 3;
            }
        }
    }

    /// <summary>One stock-format archive, read in place (they are gigabytes): stored or zlib-compressed files, which is all the banners need.</summary>
    sealed class MpqReader : IDisposable
    {
        const uint FileImplode = 0x100, FileCompress = 0x200, FileEncrypted = 0x10000, FileSingleUnit = 0x1000000, FileExists = 0x80000000;
        readonly FileStream _fs;
        readonly long _base;
        readonly int _sectorSize;
        readonly uint[] _hashes, _blocks;

        public MpqReader(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[32];
            for (long at = 0; ; at += 0x200)
            {
                if (at + 32 > _fs.Length) throw new InvalidDataException(path + ": no MPQ header");
                _fs.Position = at;
                _fs.Read(head, 0, 32);
                if (head[0] == 'M' && head[1] == 'P' && head[2] == 'Q' && head[3] == 0x1A) { _base = at; break; }
            }
            _sectorSize = 0x200 << BitConverter.ToUInt16(head, 14);
            _hashes = Table(BitConverter.ToUInt32(head, 16), BitConverter.ToUInt32(head, 24), "(hash table)");
            _blocks = Table(BitConverter.ToUInt32(head, 20), BitConverter.ToUInt32(head, 28), "(block table)");
        }

        uint[] Table(uint pos, uint count, string key)
        {
            var raw = ReadAt(_base + pos, (int)count * 16);
            var words = new uint[count * 4];
            Buffer.BlockCopy(raw, 0, words, 0, raw.Length);
            MpqCrypt.Decrypt(words, MpqCrypt.Hash(key, MpqCrypt.Key));
            return words;
        }

        byte[] ReadAt(long pos, int len)
        {
            var buf = new byte[len];
            _fs.Position = pos;
            int got = 0;
            while (got < len) { int n = _fs.Read(buf, got, len - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
            return buf;
        }

        int Find(string name)
        {
            int n = _hashes.Length / 4;
            uint a = MpqCrypt.Hash(name, MpqCrypt.HashA), b = MpqCrypt.Hash(name, MpqCrypt.HashB);
            int start = (int)(MpqCrypt.Hash(name, MpqCrypt.Offset) % (uint)n), i = start, found = -1;
            do
            {
                uint block = _hashes[i * 4 + 3];
                if (block == 0xFFFFFFFF) break;
                if (_hashes[i * 4] == a && _hashes[i * 4 + 1] == b && block != 0xFFFFFFFE)
                {
                    if ((_hashes[i * 4 + 2] & 0xFFFF) == 0) return (int)block;   // the neutral locale wins
                    if (found < 0) found = (int)block;
                }
                i = (i + 1) % n;
            } while (i != start);
            return found;
        }

        /// <summary>The file's bytes, or null when the archive does not hold it.</summary>
        public byte[] Read(string name)
        {
            int block = Find(name);
            if (block < 0) return null;
            uint offset = _blocks[block * 4], csize = _blocks[block * 4 + 1], size = _blocks[block * 4 + 2], flags = _blocks[block * 4 + 3];
            if ((flags & FileExists) == 0) return null;
            if ((flags & (FileEncrypted | FileImplode)) != 0) throw new InvalidDataException(name + " is encrypted or imploded");
            long start = _base + offset;
            if ((flags & FileCompress) == 0) return ReadAt(start, (int)size);
            if ((flags & FileSingleUnit) != 0)
            {
                var chunk = ReadAt(start, (int)csize);
                return csize == size ? chunk : Decompress(chunk, (int)size);
            }
            int sectors = (int)((size + _sectorSize - 1) / _sectorSize);
            var tableRaw = ReadAt(start, (sectors + 1) * 4);
            var output = new byte[size];
            for (int i = 0; i < sectors; i++)
            {
                uint from = BitConverter.ToUInt32(tableRaw, i * 4), to = BitConverter.ToUInt32(tableRaw, i * 4 + 4);
                int expected = (int)Math.Min(_sectorSize, size - i * _sectorSize);
                var chunk = ReadAt(start + from, (int)(to - from));
                var plain = chunk.Length == expected ? chunk : Decompress(chunk, expected);
                Buffer.BlockCopy(plain, 0, output, i * _sectorSize, expected);
            }
            return output;
        }

        static byte[] Decompress(byte[] chunk, int size)
        {
            if (chunk[0] != 0x02) throw new InvalidDataException($"MPQ compression 0x{chunk[0]:x2} is not supported");
            // zlib: a 2-byte header, then deflate
            using (var z = new DeflateStream(new MemoryStream(chunk, 3, chunk.Length - 3), CompressionMode.Decompress))
            {
                var output = new byte[size];
                int got = 0;
                while (got < size) { int n = z.Read(output, got, size - got); if (n <= 0) break; got += n; }
                return output;
            }
        }

        public void Dispose() => _fs.Dispose();
    }

    /// <summary>The client's archives, highest priority first, the way the game reads them: the extra patches (Patch-Z, the HD packs) by name, then the stock chain. Our own Patch-Y is left out.</summary>
    sealed class ArchiveChain : IDisposable
    {
        static readonly string[] StockOrder = { "patch-{0}-3", "patch-{0}-2", "patch-{0}", "patch-3", "patch-2", "patch",
            "lichking-locale-{0}", "expansion-locale-{0}", "locale-{0}", "lichking", "expansion", "common-2", "common" };
        readonly List<string> _paths = new List<string>();
        readonly Dictionary<string, MpqReader> _open = new Dictionary<string, MpqReader>();

        public ArchiveChain(string data)
        {
            var locale = Directory.EnumerateDirectories(data).Select(Path.GetFileName)
                .FirstOrDefault(d => d.Length == 4 && Directory.EnumerateFiles(Path.Combine(data, d), "locale-*.MPQ").Any()) ?? "enUS";
            var stock = new HashSet<string>(new[] { "patch", "patch-2", "patch-3" }, StringComparer.OrdinalIgnoreCase);
            _paths.AddRange(Directory.EnumerateFiles(data, "patch-*.MPQ")
                .Where(p => !stock.Contains(Path.GetFileNameWithoutExtension(p)) && !Path.GetFileName(p).Equals(GuildBanners.Archive, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
            foreach (var n in StockOrder)
            {
                var name = string.Format(n, locale) + ".MPQ";
                var path = n.Contains("{0}") ? Path.Combine(data, locale, name) : Path.Combine(data, name);
                if (File.Exists(path)) _paths.Add(path);
            }
        }

        public byte[] Read(string name)
        {
            foreach (var p in _paths)
            {
                if (!_open.TryGetValue(p, out var r)) _open[p] = r = new MpqReader(p);
                var data = r.Read(name);
                if (data != null) return data;
            }
            return null;
        }

        public void Dispose() { foreach (var r in _open.Values) r.Dispose(); }
    }

    /// <summary>A v1 archive of stored (uncompressed) files plus a (listfile).</summary>
    static class MpqWriter
    {
        public static byte[] Build(Dictionary<string, byte[]> files)
        {
            var names = files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var all = new Dictionary<string, byte[]>(files) { ["(listfile)"] = Encoding.ASCII.GetBytes(string.Join("\r\n", names) + "\r\n") };
            names.Add("(listfile)");

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[32]);
                var blocks = new uint[names.Count * 4];
                for (int i = 0; i < names.Count; i++)
                {
                    var data = all[names[i]];
                    blocks[i * 4] = (uint)ms.Position;
                    blocks[i * 4 + 1] = blocks[i * 4 + 2] = (uint)data.Length;
                    blocks[i * 4 + 3] = 0x80000000;   // exists, stored
                    bw.Write(data);
                }
                int hashCount = 16;
                while (hashCount < names.Count * 2) hashCount *= 2;
                var hashes = Enumerable.Repeat(0xFFFFFFFFu, hashCount * 4).ToArray();
                for (int i = 0; i < names.Count; i++)
                {
                    int at = (int)(MpqCrypt.Hash(names[i], MpqCrypt.Offset) % (uint)hashCount);
                    while (hashes[at * 4 + 3] != 0xFFFFFFFF) at = (at + 1) % hashCount;
                    hashes[at * 4] = MpqCrypt.Hash(names[i], MpqCrypt.HashA);
                    hashes[at * 4 + 1] = MpqCrypt.Hash(names[i], MpqCrypt.HashB);
                    hashes[at * 4 + 2] = 0;
                    hashes[at * 4 + 3] = (uint)i;
                }
                MpqCrypt.Encrypt(hashes, MpqCrypt.Hash("(hash table)", MpqCrypt.Key));
                MpqCrypt.Encrypt(blocks, MpqCrypt.Hash("(block table)", MpqCrypt.Key));
                uint hashPos = (uint)ms.Position;
                foreach (var w in hashes) bw.Write(w);
                uint blockPos = (uint)ms.Position;
                foreach (var w in blocks) bw.Write(w);
                uint size = (uint)ms.Position;
                ms.Position = 0;
                bw.Write(Encoding.ASCII.GetBytes("MPQ\x1A"));
                bw.Write(32u); bw.Write(size); bw.Write((ushort)0); bw.Write((ushort)3);
                bw.Write(hashPos); bw.Write(blockPos); bw.Write((uint)hashCount); bw.Write((uint)names.Count);
                return ms.ToArray();
            }
        }
    }
}
