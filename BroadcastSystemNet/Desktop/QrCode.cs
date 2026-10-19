using System;
using System.Collections.Generic;
using System.Linq;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 纯 C# 二维码编码器（不依赖任何第三方库、不用浏览器组件）。
    ///
    /// 为什么自己写：二维码要在 WPF 界面里**直接显示**（运行总览右侧的"连接二维码"、话筒卡片里的配对码），
    /// 之前是把本地网页丢给浏览器渲染 —— 要么弹浏览器、要么嵌浏览器控件（老 IE 内核，主题色/DPI 全是坑）。
    /// 这里按 ISO/IEC 18004 实现：字节模式 + 纠错等级 M + 版本 1~40 自动选版 + 8 种掩码按惩罚分择优。
    ///
    /// 正确性怎么保证（不靠"看着像"）：
    ///   · <see cref="SelfCheck"/> 会做**反向解码**：从矩阵里读回数据码字并与原文逐字节比较；
    ///   · 对每个分块做 **Reed-Solomon 伴随式校验**（全部为 0 才算纠错码写对了）；
    ///   · 校验**格式信息**的两个标准已知值（L/掩码0 = 0x77C4、M/掩码0 = 0x5412）。
    /// 自检在 `--self-test` 里跑（见 SelfTest.cs）。
    /// </summary>
    public static class QrCode
    {
        // ---------------- 标准表（ISO/IEC 18004） ----------------

        /// <summary>每块的纠错码字数，行序：L, M, Q, H；列 = 版本（0 位占位）。</summary>
        static readonly int[][] EccPerBlock =
        {
            new[]{-1,7,10,15,20,26,18,20,24,30,18,20,24,26,30,22,24,28,30,28,28,28,28,30,30,26,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
            new[]{-1,10,16,26,18,24,16,18,22,22,26,30,22,22,24,24,28,28,26,26,26,26,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28},
            new[]{-1,13,22,18,26,18,24,18,22,20,24,28,26,24,20,30,24,28,28,26,30,28,30,30,30,30,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
            new[]{-1,17,28,22,16,22,28,26,26,24,28,24,28,22,24,24,30,28,28,26,28,30,24,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30},
        };

        /// <summary>纠错分块数，行序同上。</summary>
        static readonly int[][] NumBlocks =
        {
            new[]{-1,1,1,1,1,1,2,2,2,2,4,4,4,4,4,6,6,6,6,7,8,8,9,9,10,12,12,12,13,14,15,16,17,18,19,19,20,21,22,24,25},
            new[]{-1,1,1,1,2,2,4,4,4,5,5,5,8,9,9,10,10,11,13,14,16,17,17,18,20,21,23,25,26,28,29,31,33,35,37,38,40,43,45,47,49},
            new[]{-1,1,1,2,2,4,4,6,6,8,8,8,10,12,16,12,17,16,18,21,20,23,23,25,27,29,34,34,35,38,40,43,45,48,51,53,56,59,62,65,68},
            new[]{-1,1,1,2,4,4,4,5,6,8,8,11,11,16,16,18,16,19,21,25,25,25,34,30,32,35,37,40,42,45,48,51,54,57,60,63,66,70,74,77,81},
        };

        /// <summary>纠错等级：L=0, M=1, Q=2, H=3。</summary>
        public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

        /// <summary>某个版本里"能装数据的模块数"（去掉定位/校正/格式/版本等固定图形）。</summary>
        static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        static int DataCodewords(int ver, Ecc ecl) => RawDataModules(ver) / 8 - EccPerBlock[(int)ecl][ver] * NumBlocks[(int)ecl][ver];

        // ---------------- 编码 ----------------

        /// <summary>文本 → 模块矩阵（true = 黑）。自动选最小可用版本。</summary>
        public static bool[,] Encode(string text, Ecc ecl = Ecc.M) => Encode(text, ecl, out _, out _);

        /// <summary>同上，但把"选了哪个版本""最终交错码字流"交出来（自检用来逐字节比对）。</summary>
        public static bool[,] Encode(string text, Ecc ecl, out int ver, out byte[] codewords)
            => Encode(text, ecl, out ver, out codewords, out _, out _);

        /// <summary>完整版：还把"选中的掩码"和"功能模块地图"交出来（自检用来与读回端逐格比对）。</summary>
        public static bool[,] Encode(string text, Ecc ecl, out int ver, out byte[] codewords, out int usedMask, out bool[,] funcMap)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
            ver = 1;
            while (ver <= 40)
            {
                int cap = DataCodewords(ver, ecl) * 8;
                int need = 4 + CharCountBits(ver) + bytes.Length * 8;
                if (need <= cap) break;
                ver++;
            }
            if (ver > 40) throw new ArgumentException($"内容太长，超出二维码容量（{bytes.Length} 字节）");

            var bits = new List<bool>();
            AppendBits(bits, 0x4, 4);                                   // 字节模式
            AppendBits(bits, bytes.Length, CharCountBits(ver));         // 字符数
            foreach (var b in bytes) AppendBits(bits, b, 8);
            int capacityBits = DataCodewords(ver, ecl) * 8;
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count)); // 结束符
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);              // 补齐到字节
            for (byte pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);

            var dataCw = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) dataCw[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            codewords = AddEccAndInterleave(dataCw, ver, ecl);
            return BuildMatrix(ver, ecl, codewords, out usedMask, out funcMap);
        }

        static int CharCountBits(int ver) => ver <= 9 ? 8 : 16;

        static void AppendBits(List<bool> bits, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0);
        }

        /// <summary>分块算 Reed-Solomon 纠错码，再按标准交错排列。</summary>
        static byte[] AddEccAndInterleave(byte[] data, int ver, Ecc ecl)
        {
            int numBlocks = NumBlocks[(int)ecl][ver];
            int blockEccLen = EccPerBlock[(int)ecl][ver];
            int rawCodewords = RawDataModules(ver) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            var blocks = new List<byte[]>();
            var eccs = new List<byte[]>();
            int k = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                blocks.Add(dat);
                eccs.Add(ReedSolomon(dat, blockEccLen));
            }
            var outp = new List<byte>();
            int maxData = blocks.Max(b => b.Length);
            for (int i = 0; i < maxData; i++)
                foreach (var b in blocks) if (i < b.Length) outp.Add(b[i]);
            for (int i = 0; i < blockEccLen; i++)
                foreach (var e in eccs) outp.Add(e[i]);
            return outp.ToArray();
        }

        // ---- GF(256)，本原多项式 0x11D ----
        static readonly byte[] GfExp = new byte[512];
        static readonly byte[] GfLog = new byte[256];
        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }
        static byte GfMul(byte a, byte b) => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

        /// <summary>生成多项式 ∏(x - a^i)（经典递推，i = 0..deg-1）。</summary>
        static byte[] ClassicDivisor(int deg)
        {
            var result = new byte[deg];
            result[deg - 1] = 1;
            byte root = 1;
            for (int i = 0; i < deg; i++)
            {
                for (int j = 0; j < deg; j++)
                {
                    result[j] = GfMul(result[j], root);
                    if (j + 1 < deg) result[j] ^= result[j + 1];
                }
                root = GfMul(root, 2);
            }
            return result;
        }

        /// <summary>Reed-Solomon 余数（= 该块的纠错码字）。</summary>
        static byte[] ReedSolomon(byte[] data, int eccLen)
        {
            var divisor = ClassicDivisor(eccLen);
            var rem = new byte[eccLen];
            foreach (var b in data)
            {
                byte factor = (byte)(b ^ rem[0]);
                for (int i = 0; i < eccLen - 1; i++) rem[i] = rem[i + 1];
                rem[eccLen - 1] = 0;
                for (int i = 0; i < eccLen; i++) rem[i] ^= GfMul(divisor[i], factor);
            }
            return rem;
        }

        // ---------------- 矩阵 ----------------

        static bool[,] BuildMatrix(int ver, Ecc ecl, byte[] codewords) => BuildMatrix(ver, ecl, codewords, out _, out _);

        static bool[,] BuildMatrix(int ver, Ecc ecl, byte[] codewords, out int usedMask, out bool[,] funcOut)
        {
            int size = ver * 4 + 17;
            var m = new bool[size, size];
            var isFunc = new bool[size, size];

            void SetFunc(int x, int y, bool dark)
            {
                if (x < 0 || y < 0 || x >= size || y >= size) return;
                m[y, x] = dark; isFunc[y, x] = true;
            }


            // 时序图形（**必须先画**：定位图形要覆盖它与三个角重叠的那几个模块）
            for (int i = 0; i < size; i++) { SetFunc(6, i, i % 2 == 0); SetFunc(i, 6, i % 2 == 0); }
            // 定位图形 + 分隔带：按切比雪夫距离画（dist==2 是内部白环、dist==4 是分隔带），
            // 边缘会被画布裁掉，所以每个角实际是 8×8 —— 这一点搞错会让"数据模块数"对不上（真踩过：
            // 读回 24 码字、编码 26，就是因为定位图形多画了一圈）。
            void Finder(int cx, int cy)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunc(cx + dx, cy + dy, dist != 2 && dist != 4);
                    }
            }
            Finder(3, 3);
            Finder(size - 4, 3);
            Finder(3, size - 4);

            // 校正图形
            var align = AlignPositions(ver);
            foreach (int ay in align)
                foreach (int ax in align)
                {
                    if ((ax == 6 && ay == 6) || (ax == 6 && ay == size - 7) || (ax == size - 7 && ay == 6)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            SetFunc(ax + dx, ay + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }

            // 版本信息（版本 ≥7）
            if (ver >= 7)
            {
                int rem = ver;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = ver << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = size - 11 + i % 3, b = i / 3;
                    SetFunc(a, b, bit); SetFunc(b, a, bit);
                }
            }

            // 格式信息占位（先按掩码 0 写，稍后按选中的掩码重写）
            DrawFormat(m, isFunc, size, ecl, 0);

            // 数据填充（右下往左上、每列两格、跳过第 6 列）
            int cwIndex = 0, bitIndex = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunc[y, x]) continue;
                        bool bit = false;
                        if (cwIndex < codewords.Length)
                            bit = ((codewords[cwIndex] >> (7 - bitIndex)) & 1) != 0;
                        m[y, x] = bit;
                        bitIndex++;
                        if (bitIndex == 8) { bitIndex = 0; cwIndex++; }
                    }
                }
            }

            // 选掩码：8 种都试一遍，取惩罚分最低的
            int bestMask = 0; long bestPenalty = long.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(m, isFunc, size, mask);
                DrawFormat(m, isFunc, size, ecl, mask);
                long p = Penalty(m, size);
                if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
                ApplyMask(m, isFunc, size, mask);   // 异或两次 = 还原
            }
            ApplyMask(m, isFunc, size, bestMask);
            DrawFormat(m, isFunc, size, ecl, bestMask);
            usedMask = bestMask;
            funcOut = isFunc;
            return m;
        }

        static int[] AlignPositions(int ver)
        {
            if (ver == 1) return new int[0];
            int num = ver / 7 + 2;
            int step = (ver == 32) ? 26 : (ver * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
            var res = new int[num];
            res[0] = 6;
            for (int i = num - 1, pos = ver * 4 + 10; i >= 1; i--, pos -= step) res[i] = pos;
            return res;
        }

        static void ApplyMask(bool[,] m, bool[,] isFunc, int size, int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (isFunc[y, x]) continue;
                    bool inv;
                    switch (mask)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv) m[y, x] = !m[y, x];
                }
        }

        /// <summary>格式信息（纠错等级 + 掩码），BCH(15,5) + 固定掩码 0x5412。</summary>
        public static int FormatBits(Ecc ecl, int mask)
        {
            // 2 位纠错等级编码：L=01, M=00, Q=11, H=10
            int data = ecl switch { Ecc.L => 1, Ecc.M => 0, Ecc.Q => 3, _ => 2 };
            data = (data << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return ((data << 10) | rem) ^ 0x5412;
        }

        static void DrawFormat(bool[,] m, bool[,] isFunc, int size, Ecc ecl, int mask)
        {
            int bits = FormatBits(ecl, mask);
            for (int i = 0; i <= 5; i++) SetF(m, isFunc, 8, i, ((bits >> i) & 1) != 0);
            SetF(m, isFunc, 8, 7, ((bits >> 6) & 1) != 0);
            SetF(m, isFunc, 8, 8, ((bits >> 7) & 1) != 0);
            SetF(m, isFunc, 7, 8, ((bits >> 8) & 1) != 0);
            for (int i = 9; i < 15; i++) SetF(m, isFunc, 14 - i, 8, ((bits >> i) & 1) != 0);
            for (int i = 0; i < 8; i++) SetF(m, isFunc, size - 1 - i, 8, ((bits >> i) & 1) != 0);
            for (int i = 8; i < 15; i++) SetF(m, isFunc, 8, size - 15 + i, ((bits >> i) & 1) != 0);
            SetF(m, isFunc, 8, size - 8, true);                 // 固定的黑模块
        }

        static void SetF(bool[,] m, bool[,] isFunc, int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= m.GetLength(1) || y >= m.GetLength(0)) return;
            m[y, x] = dark; isFunc[y, x] = true;
        }

        /// <summary>标准惩罚分（4 条规则），用于择优掩码。</summary>
        static long Penalty(bool[,] m, int size)
        {
            long p = 0;
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[y, x] == m[y, x - 1]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (m[y, x] == m[y - 1, x]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = m[y, x];
                    if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) p += 3;
                }
            int dark = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (m[y, x]) dark++;
            int total = size * size;
            p += Math.Abs(dark * 100 / total - 50) / 5 * 10;
            return p;
        }

        // ---------------- 反向解码（仅用于自检，证明矩阵里真的装着原文） ----------------

        /// <summary>从矩阵读回**完整的交错码字流**（反掩码后按填充顺序读，不做分块）。</summary>
        public static byte[] ReadRawCodewords(bool[,] m, int ver, Ecc ecl, out int mask)
        {
            int size = ver * 4 + 17;
            mask = ReadFormatMask(m, size);
            var isFunc = FunctionMap(ver, size);
            var mm = (bool[,])m.Clone();
            ApplyMask(mm, isFunc, size, mask);
            var raw = new List<byte>();
            int bit = 0, cur = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunc[y, x]) continue;
                        if (mm[y, x]) cur |= 1 << (7 - bit);
                        bit++;
                        if (bit == 8) { raw.Add((byte)cur); cur = 0; bit = 0; }
                    }
                }
            }
            return raw.ToArray();
        }

        /// <summary>从矩阵读回数据码字（不纠错，只做"读出来对不对"的验证）。</summary>
        public static byte[] ReadDataCodewords(bool[,] m, int ver, Ecc ecl, out int mask, out bool syndOk)
        {
            int size = ver * 4 + 17;
            var raw = new List<byte>(ReadRawCodewords(m, ver, ecl, out mask));
            _ = size;
            // 反交错（与编码端的分块长度严格对应）
            int numBlocks = NumBlocks[(int)ecl][ver];
            int blockEccLen = EccPerBlock[(int)ecl][ver];
            int rawCodewords = raw.Count;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;
            var lens = new int[numBlocks];
            for (int i = 0; i < numBlocks; i++) lens[i] = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var blocks = new List<byte[]>();
            for (int i = 0; i < numBlocks; i++) blocks.Add(new byte[lens[i]]);
            var eccs = new List<byte[]>();
            for (int i = 0; i < numBlocks; i++) eccs.Add(new byte[blockEccLen]);
            int idx = 0;
            int maxLen = lens.Max();
            for (int i = 0; i < maxLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    if (i < lens[b] && idx < raw.Count) blocks[b][i] = (byte)raw[idx++];
            for (int i = 0; i < blockEccLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    if (idx < raw.Count) eccs[b][i] = (byte)raw[idx++];

            syndOk = true;
            for (int i = 0; i < numBlocks; i++)
                if (!SyndromesZero(blocks[i], eccs[i])) syndOk = false;
            var outp = new List<byte>();
            foreach (var b in blocks) outp.AddRange(b);
            return outp.ToArray();
        }

        static bool SyndromesZero(byte[] data, byte[] ecc)
        {
            var poly = new List<byte>(data); poly.AddRange(ecc);
            for (int i = 0; i < ecc.Length; i++)
            {
                byte x = GfExp[i];
                byte y = 0;
                foreach (var c in poly) y = (byte)(GfMul(y, x) ^ c);
                if (y != 0) return false;
            }
            return true;
        }

        static int ReadFormatMask(bool[,] m, int size)
        {
            int bits = 0;
            for (int i = 0; i <= 5; i++) if (m[i, 8]) bits |= 1 << i;
            if (m[7, 8]) bits |= 1 << 6;
            if (m[8, 8]) bits |= 1 << 7;
            if (m[8, 7]) bits |= 1 << 8;
            for (int i = 9; i < 15; i++) if (m[8, 14 - i]) bits |= 1 << i;
            bits ^= 0x5412;
            // 15 位 = 5 位数据（2 位纠错等级 + 3 位掩码）+ 10 位 BCH 校验，
            // 所以掩码在**高 5 位**里：先右移 10 位再取低 3 位。（直接对 15 位取低 3 位是错的 ——
            // 那 3 位是校验位，真机自检里表现为"写入用掩码 3、读回是 1"。）
            return (bits >> 10) & 7;
        }

        static bool[,] FunctionMap(int ver, int size)
        {
            var f = new bool[size, size];
            // 三个角的定位图形 + 分隔带（与写入端**完全同一套规则**：切比雪夫距离 ≤4，越界裁掉）
            void Finder(int cx, int cy)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int xx = cx + dx, yy = cy + dy;
                        if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                        f[yy, xx] = true;
                    }
            }
            Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
            // 时序图形
            for (int i = 0; i < size; i++) { f[i, 6] = true; f[6, i] = true; }
            // 校正图形
            foreach (int ay in AlignPositions(ver))
                foreach (int ax in AlignPositions(ver))
                {
                    if ((ax == 6 && ay == 6) || (ax == 6 && ay == size - 7) || (ax == size - 7 && ay == 6)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++) f[ay + dy, ax + dx] = true;
                }
            // 版本信息（版本 ≥7）
            if (ver >= 7)
                for (int i = 0; i < 18; i++)
                {
                    int a = size - 11 + i % 3, b = i / 3;
                    f[b, a] = true; f[a, b] = true;
                }
            // 左上角的格式信息条（写入端 DrawFormat 会把这些格子标成功能模块；漏标就会多读出一个字节）
            for (int i = 0; i <= 5; i++) { f[i, 8] = true; f[8, i] = true; }
            f[7, 8] = true; f[8, 7] = true; f[8, 8] = true;
            // 右侧与下侧的格式信息条 + 固定的黑模块
            for (int i = 0; i < 8; i++) f[8, size - 1 - i] = true;
            for (int i = 8; i < 15; i++) f[size - 15 + i, 8] = true;
            f[size - 8, 8] = true;
            return f;
        }

        // ---------------- 渲染成 WPF 位图 ----------------

        /// <summary>
        /// 矩阵 → WPF 位图（1 模块 = 1 像素 + 四边各 [quietZone] 模块留白，标准要求 ≥4）。
        ///
        /// 只生成"一个模块一个像素"的小位图，显示时由 WPF 用**最近邻**放大 —— 这样任何尺寸都清晰锐利，
        /// 不会像缩放插值那样把模块边缘糊掉（糊了扫码识别率会掉）。
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapSource ToBitmap(bool[,] m, int modulePixel = 1, int quietZone = 4)
        {
            int size = m.GetLength(0);
            int dim = (size + quietZone * 2) * modulePixel;
            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(dim, dim, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            var px = new byte[dim * dim * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255; }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (!m[y, x]) continue;
                    for (int dy = 0; dy < modulePixel; dy++)
                        for (int dx = 0; dx < modulePixel; dx++)
                        {
                            int ix = ((y + quietZone) * modulePixel + dy) * dim + ((x + quietZone) * modulePixel + dx);
                            int o = ix * 4;
                            px[o] = 0; px[o + 1] = 0; px[o + 2] = 0; px[o + 3] = 255;
                        }
                }
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, dim, dim), px, dim * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>文本直接渲染成位图（界面用这一句就够）。</summary>
        public static System.Windows.Media.Imaging.BitmapSource Render(string text, Ecc ecl = Ecc.M) => ToBitmap(Encode(text, ecl));

        /// <summary>
        /// 自检：编码 → 反向解码 → 与原文比较 + 纠错码伴随式校验 + 格式信息标准值。
        /// 返回 (成功, 说明)。`--self-test` 会跑它。
        /// </summary>
        public static (bool ok, string detail) SelfCheck()
        {
            // ① 格式信息两个公认标准值（L/掩码0 = 0x77C4，M/掩码0 = 0x5412）
            if (FormatBits(Ecc.M, 0) != 0x5412) return (false, $"格式信息 M/0 = 0x{FormatBits(Ecc.M, 0):X4}，应为 0x5412");
            if (FormatBits(Ecc.L, 0) != 0x77C4) return (false, $"格式信息 L/0 = 0x{FormatBits(Ecc.L, 0):X4}，应为 0x77C4");

            // ② 编码 → 解码往返（覆盖小/中/大三种长度，含中文与 JSON 结构）
            var samples = new[]
            {
                "HELLO",
                // 设备 ID 用**明显的示例值**：这里原来写的是本机某台手机的真实设备 ID，
                // 属于使用者的私有标识（脱敏 2026-10-13）。
                "{\"t\":\"cbs-mic\",\"v\":1,\"code\":\"123456\",\"device_id\":\"11111111-2222-3333-4444-555555555555\"}",
                "中文也要能装进去：" + new string('试', 120) + "{\"server\":\"example.com:29872\",\"token\":\"abcdefgh\",\"w_password\":\"0123456789abcdef\"}"
            };
            foreach (var s in samples)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                var m = Encode(s, Ecc.M, out int ver, out byte[] expectCw, out int usedMask, out bool[,] func);
                int size = m.GetLength(0);
                var rawBack = ReadRawCodewords(m, ver, Ecc.M, out int mask);
                // 功能模块地图必须与读回端**逐格一致**（差一格就会整体错位，长度对不上只是最粗的症状）
                if (ver <= 6)   // 只有小版本才逐格比对，避免大版本刷屏
                {
                    var readerMap = FunctionMap(ver, size);
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                            if (func[y, x] != readerMap[y, x])
                                return (false, $"版本{ver} 功能模块地图不一致：({x},{y}) 写入端={(func[y, x] ? "功能" : "数据")} 读回端={(readerMap[y, x] ? "功能" : "数据")}");
                }
                if (mask != usedMask) return (false, $"版本{ver} 掩码不一致：写入用 {usedMask}，从格式信息读回 {mask}");
                // 先证明"矩阵里读回的码字流"与"编码时算出的码字流"逐字节一致（定位写入/读取错位）
                if (rawBack.Length != expectCw.Length)
                    return (false, $"版本{ver} 读回码字数 {rawBack.Length} ≠ 编码码字数 {expectCw.Length}");
                for (int i = 0; i < rawBack.Length; i++)
                    if (rawBack[i] != expectCw[i])
                        return (false, $"版本{ver}(掩码{mask}) 码字第 {i}/{rawBack.Length} 个不一致：读到 0x{rawBack[i]:X2}，编码是 0x{expectCw[i]:X2}");
                var back = ReadDataCodewords(m, ver, Ecc.M, out mask, out bool syndOk);
                if (!syndOk) return (false, $"版本{ver} 纠错码伴随式不为零（Reed-Solomon 写错了）");
                // 按"字节模式"把数据码字解回原文：4 位模式 + 字符数 + 数据 —— 注意数据码字开头不是原文首字节
                var bitList = new List<bool>();
                foreach (var cw in back) for (int b = 7; b >= 0; b--) bitList.Add(((cw >> b) & 1) != 0);
                int mode = 0; for (int i = 0; i < 4; i++) mode |= (bitList[i] ? 1 : 0) << (3 - i);
                if (mode != 4) return (false, $"版本{ver} 解出的模式不是字节模式（{mode}）");
                int cc = CharCountBits(ver);
                int len = 0; for (int i = 0; i < cc; i++) len |= (bitList[4 + i] ? 1 : 0) << (cc - 1 - i);
                if (len != bytes.Length) return (false, $"版本{ver} 解出的长度 {len} ≠ 原文 {bytes.Length}");
                for (int i = 0; i < len; i++)
                {
                    int v = 0;
                    for (int b = 0; b < 8; b++) v = (v << 1) | (bitList[4 + cc + i * 8 + b] ? 1 : 0);
                    if ((byte)v != bytes[i]) return (false, $"版本{ver} 第 {i} 字节不一致：解出 0x{v:X2}，应为 0x{bytes[i]:X2}");
                }
            }
            return (true, "编码↔解码往返一致，纠错码伴随式全零，格式信息符合标准值（L/0=0x77C4、M/0=0x5412）");
        }
    }
}
