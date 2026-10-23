using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒（远程喊话）协议核心：包格式、AEAD、防重放、动态配对码、会话密钥派生。
    /// 全部是纯逻辑（不碰网络/进程/界面），一是便于自检断言，二是安卓端照《话筒功能接口文档.md》
    /// 实现时这里就是服务端的"参考实现"。
    /// 加密用随项目已有的 BouncyCastle（X25519 + HKDF），AEAD 用 .NET 自带的 AES-GCM —— 零新依赖。
    /// </summary>
    public static class MicProtocol
    {
        /// <summary>
        /// 协议版本。**4.0.0 起为 2**：包头里的 seq 从 4 字节扩到 8 字节（头 16 → 20 字节），
        /// 目的是让 AEAD 的 nonce 用满 12 字节（原先后 4 字节恒为 0，数据通道的可变空间只剩 32 位）。
        /// 这是**不兼容改动**：版本不符会被 TryParseHeader 直接拒绝并给出"协议版本不符"，
        /// 而不是留下一个令人困惑的 AEAD 失败。两端必须同时更新（用户已确认不接受老版本兼容）。
        /// </summary>
        public const byte Version = 2;
        public const byte TypeAudio = 1;
        /// <summary>Ogg 页（安卓端直接发 Ogg-Opus 页时用；PC 端原样转给 ffplay，零解码代码）。</summary>
        public const byte TypeOgg = 3;      // 音频帧
        public const byte TypeSilence = 2;    // 静音帧（保持会话）
        /// <summary>
        /// 接口请求/响应（cbs-api）：载荷是 UTF-8 JSON，走与音频同一条已配对加密通道。
        /// 用**独立类型**而不是复用音频类型，是为了让"音频"与"接口"两条逻辑彻底分开：
        /// 音频走 ffplay 管道，接口走本机 HTTP 转发，互不影响，也不会互相误判。
        /// </summary>
        public const byte TypeApi = 4;
        public const int HeaderSize = 20;     // 包头长度（也是 AEAD 的 AAD）：magic(2)+ver(1)+type(1)+sid(4)+seq(8)+frame_ms(2)+len(2)
        public const int TagSize = 16;        // AEAD 认证标签
        /// <summary>
        /// 单包载荷上限。
        /// 起初是 1024（只装 Opus 帧，20ms@32kbps 约 80 字节）；加入 **cbs-api**（接口请求/响应，TypeApi）后
        /// 明显不够——任务列表/调休这类 JSON 动辄几 KB。
        ///
        /// 取值理由：**不做分片**（分片会让两端都多一套拼接/超时逻辑），直接把上限放到"UDP 单包安全区"内。
        /// 60000 字节 + 16 包头 + 16 tag ≈ 60032，小于 UDP 报文的 65507 上限；在 vnet/局域网里实测可发，
        /// 公网路径若被分片而丢掉某个分片，表现为该次请求超时重试（AEAD 会拦住残缺数据，不会解出脏数据）。
        /// 两端必须一致（安卓端 MicProtocol.MAX_PAYLOAD）。
        /// </summary>
        public const int MaxPayload = 60000;
        public const int ReplayWindowSize = 64;
        public const int CodePeriodSeconds = 60;   // 动态码轮换周期

        // ---------------- 包头 ----------------

        /// <summary>构建 20 字节包头：magic(2) version(1) type(1) sid(4) seq(8) frame_ms(2) payload_len(2)，全部小端。</summary>
        public static byte[] BuildHeader(byte type, uint sid, ulong seq, ushort frameMs, ushort payloadLen)
        {
            var h = new byte[HeaderSize];
            h[0] = 0x4D; h[1] = 0x43;                       // 'M' 'C'
            h[2] = Version;
            h[3] = type;
            BitConverter.GetBytes(sid).CopyTo(h, 4);
            BitConverter.GetBytes(seq).CopyTo(h, 8);        // 8 字节序号（4.0.0 起）
            BitConverter.GetBytes(frameMs).CopyTo(h, 16);
            BitConverter.GetBytes(payloadLen).CopyTo(h, 18);
            return h;
        }

        /// <summary>解析并校验包头；返回 false 时 error 说明原因（服务端只记日志，不回包）。</summary>
        public static bool TryParseHeader(ReadOnlySpan<byte> buf, out byte type, out uint sid, out ulong seq,
            out ushort frameMs, out ushort payloadLen, out string error)
        {
            type = 0; sid = 0; seq = 0; frameMs = 0; payloadLen = 0; error = null;
            if (buf.Length < HeaderSize) { error = "包太短"; return false; }
            if (buf[0] != 0x4D || buf[1] != 0x43) { error = "magic 不符"; return false; }
            if (buf[2] != Version) { error = "协议版本不符"; return false; }
            type = buf[3];
            if (type != TypeAudio && type != TypeSilence && type != TypeOgg && type != TypeApi) { error = "类型非法"; return false; }
            sid = BitConverter.ToUInt32(buf.Slice(4, 4));
            seq = BitConverter.ToUInt64(buf.Slice(8, 8));       // 8 字节序号（4.0.0 起）
            frameMs = BitConverter.ToUInt16(buf.Slice(16, 2));
            payloadLen = BitConverter.ToUInt16(buf.Slice(18, 2));
            if (payloadLen > MaxPayload) { error = "载荷过长"; return false; }
            if (type == TypeSilence && payloadLen != 0) { error = "静音帧不该带载荷"; return false; }
            if (frameMs != 0 && frameMs != 10 && frameMs != 20 && frameMs != 40 && frameMs != 60)
            { error = "帧长非法"; return false; }
            return true;
        }

        /// <summary>
        /// AEAD 的 nonce：sid(4B 小端) ‖ seq(8B 小端) —— **12 字节全部有效**。
        /// 【前提说明】原注释只写"同一 sid 内 seq 递增，故永不重复"，缺了关键前提，曾误导维护者。
        /// nonce 不重复的前提是两条同时成立：
        ///   ① 同一 sid 内 seq 严格递增；
        ///   ② 同一把密钥不会被"两段独立的 seq 空间"使用。
        /// 会话通道（sid&gt;0）的密钥含 nc‖ns‖动态码、**每会话唯一** → ② 天然成立。
        /// 数据通道（sid=0）用的是**由数据密码派生的静态密钥**（所有手机共用一把），所以 ② 必须靠
        /// "seq 空间跨重启、跨设备都不重叠"来保证：
        ///   · 手机端：**每安装持久化的 64 位单调计数器**（首次运行取随机起点，写 SharedPreferences）；
        ///   · 电脑端回包：**每次启动随机取的 62 位基数**（见 MicServer._openApiSeq）。
        /// </summary>
        public static byte[] Nonce(uint sid, ulong seq)
        {
            var n = new byte[12];
            BitConverter.GetBytes(sid).CopyTo(n, 0);
            BitConverter.GetBytes(seq).CopyTo(n, 4);
            return n;
        }

        // ---------------- 组包 / 解包 ----------------

        /// <summary>加密并组包：返回完整 UDP 报文（包头 + 密文 + tag）。</summary>
        public static byte[] BuildPacket(byte type, uint sid, ulong seq, ushort frameMs, byte[] payload, byte[] key)
        {
            byte[] body = payload ?? Array.Empty<byte>();
            var header = BuildHeader(type, sid, seq, frameMs, (ushort)body.Length);
            var cipher = new byte[body.Length];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(key, TagSize))
                gcm.Encrypt(Nonce(sid, seq), body, cipher, tag, header);
            var packet = new byte[HeaderSize + cipher.Length + TagSize];
            header.CopyTo(packet, 0);
            cipher.CopyTo(packet, HeaderSize);
            tag.CopyTo(packet, HeaderSize + cipher.Length);
            return packet;
        }

        /// <summary>校验并解密（AEAD 失败即视为伪造/被篡改，直接拒绝）。</summary>
        public static bool TryOpenPacket(byte[] packet, byte[] key, out byte type, out uint sid, out ulong seq,
            out ushort frameMs, out byte[] payload, out string error)
        {
            payload = null;
            if (packet == null)
            {
                // 短路写法会让 out 参数未赋值（编译期就报 CS0177），这里显式赋值
                type = 0; sid = 0; seq = 0; frameMs = 0; error = "空包"; return false;
            }
            if (!TryParseHeader(packet, out type, out sid, out seq, out frameMs, out ushort len, out error))
                return false;
            if (packet.Length != HeaderSize + len + TagSize) { error = "长度与头不符"; return false; }
            var cipher = new byte[len];
            Buffer.BlockCopy(packet, HeaderSize, cipher, 0, len);
            var tag = new byte[TagSize];
            Buffer.BlockCopy(packet, HeaderSize + len, tag, 0, TagSize);
            var header = new byte[HeaderSize];
            Buffer.BlockCopy(packet, 0, header, 0, HeaderSize);
            var plain = new byte[len];
            try
            {
                using var gcm = new AesGcm(key, TagSize);
                gcm.Decrypt(Nonce(sid, seq), cipher, tag, plain, header);
            }
            catch (CryptographicException) { error = "AEAD 校验失败（伪造或篡改）"; return false; }
            payload = plain;
            return true;
        }

        // ---------------- 防重放滑动窗口 ----------------

        /// <summary>
        /// 序号滑动窗口（窗口 64）。已见过/过旧的包一律拒绝；乱序但在窗口内的包接受。
        /// 用 ulong 位图记录，窗口右移时同步左移位图。
        /// </summary>
        public sealed class ReplayWindow
        {
            ulong _bits;
            ulong _maxSeq;
            bool _hasAny;

            public bool Accept(ulong seq)
            {
                if (!_hasAny) { _hasAny = true; _maxSeq = seq; _bits = 1UL; return true; }
                if (seq > _maxSeq)
                {
                    ulong delta = seq - _maxSeq;
                    int shift = (int)Math.Min((ulong)ReplayWindowSize, delta);
                    _bits = shift >= ReplayWindowSize ? 1UL : (_bits << shift) | 1UL;
                    _maxSeq = seq;
                    return true;
                }
                ulong back = _maxSeq - seq;
                if (back >= (ulong)ReplayWindowSize) return false;   // 太旧：视为重放
                ulong mask = 1UL << (int)back;
                if ((_bits & mask) != 0) return false;               // 已经收到过
                _bits |= mask;
                return true;
            }
        }

        // ---------------- 动态配对码（60 秒轮换） ----------------

        public static long WindowIndex(DateTime now) => now.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond / CodePeriodSeconds;

        /// <summary>某个时间窗的6位动态码：HMAC-SHA256(seed, 窗口号) → 取模 1000000。纯函数，重启后仍一致。</summary>
        public static string CodeForWindow(string seed, long window)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(seed ?? ""));
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes("cbs-mic-code:" + window.ToString()));
            int v = ((mac[0] << 24) | (mac[1] << 16) | (mac[2] << 8) | mac[3]) & 0x7FFFFFFF;
            return (v % 1000000).ToString("D6");
        }

        /// <summary>当前窗口的动态码。</summary>
        public static string CurrentCode(string seed, DateTime now) => CodeForWindow(seed, WindowIndex(now));

        /// <summary>
        /// **统一取码入口**：内部一律用 UTC。
        ///
        /// 为什么要有它：以前各处调用点自己传时间 —— 电脑端界面传 DateTime.UtcNow，
        /// /api/mic/code 传 DateTime.Now（靠 WindowIndex 里的 ToUniversalTime 兜底）。
        /// 虽然结果应该一样，但只要有一处传错（比如某个环境 DateTime.Now 已经带 Kind=Utc，
        /// 或者机器时区被改过），两端就会算出**不同的码**，而界面和手机各自显示一个，
        /// 表现就是"手机端和电脑端验证码不一样"。现在只留这一个入口，杜绝分叉。
        /// </summary>
        public static string CurrentCodeUtc(string seed) => CodeForWindow(seed, WindowIndex(DateTime.UtcNow));

        /// <summary>当前窗口还剩多少秒（与 CurrentCodeUtc 严格同一时钟，避免"码换了但秒数没换"）。</summary>
        public static int SecondsLeftUtc()
        {
            long sec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            int left = (int)(CodePeriodSeconds - sec % CodePeriodSeconds);
            return left <= 0 ? CodePeriodSeconds : left;
        }

        /// <summary>
        /// 种子指纹（SHA256 前 8 位十六进制）。只用于**核对两端是否同一把种子**，
        /// 反推不出种子本身（种子是 32 位随机 GUID，指纹泄露也无害）。
        /// 排查"手机和电脑验证码不一样"时，手机端 /api/mic/code 返回的 seed_fp
        /// 与电脑端日志里的值一比即可定位。
        /// </summary>
        public static string SeedFingerprint(string seed)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""));
                return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
            }
            catch { return "?"; }
        }

        /// <summary>
        /// 校验用户输入的动态码：接受"当前窗口"与"上一个窗口"（给跨窗输入留 60 秒宽限）。
        /// 是否已被用过（一次性）由调用方用 UsedCodes 自己记；常量时间比较，避免时序侧信道。
        /// </summary>
        public static bool VerifyCode(string seed, string input, DateTime now, out long window)
        {
            window = 0;
            string s = (input ?? "").Trim();
            if (s.Length == 0) return false;
            long w = WindowIndex(now);
            for (long cand = w; cand >= w - 1; cand--)
            {
                if (FixedTimeEquals(CodeForWindow(seed, cand), s)) { window = cand; return true; }
            }
            return false;
        }

        /// <summary>常量时间字符串比较（长度不同也不提前返回）。</summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            a ??= ""; b ??= "";
            int diff = a.Length ^ b.Length;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                char ca = i < a.Length ? a[i] : '\0';
                char cb = i < b.Length ? b[i] : '\0';
                diff |= ca ^ cb;
            }
            return diff == 0;
        }

        // ---------------- 会话密钥派生（X25519 + HKDF-SHA256） ----------------

        public sealed class KeyPair
        {
            public byte[] PrivateKey;   // 32 字节，仅内存
            public byte[] PublicKey;    // 32 字节，随 HELLO/AUTH_OK 传给对方
        }

        /// <summary>生成一次性 X25519 临时密钥对（每会话新生成，用完即弃）。</summary>
        public static KeyPair NewKeyPair()
        {
            var gen = new X25519KeyPairGenerator();
            gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
            var kp = gen.GenerateKeyPair();
            var priv = (X25519PrivateKeyParameters)kp.Private;
            var pub = (X25519PublicKeyParameters)kp.Public;
            return new KeyPair { PrivateKey = priv.GetEncoded(), PublicKey = pub.GetEncoded() };
        }

        /// <summary>
        /// 会话密钥 = HKDF-SHA256(ikm = X25519 共享密钥, salt = code‖nc‖ns, info = "cbs-mic-v1", 32 字节)。
        /// 动态码进入 salt：窃听握手的人也推不出密钥。
        /// </summary>
        public static byte[] DeriveSessionKey(byte[] myPrivate, byte[] peerPublic, string code, byte[] nc, byte[] ns)
        {
            var priv = new X25519PrivateKeyParameters(myPrivate, 0);
            var pub = new X25519PublicKeyParameters(peerPublic, 0);
            var agree = new X25519Agreement();
            agree.Init(priv);
            var shared = new byte[agree.AgreementSize];
            agree.CalculateAgreement(pub, shared, 0);

            var salt = new byte[(nc?.Length ?? 0) + (ns?.Length ?? 0) + Encoding.UTF8.GetByteCount(code ?? "")];
            int off = 0;
            if (nc != null) { nc.CopyTo(salt, off); off += nc.Length; }
            if (ns != null) { ns.CopyTo(salt, off); off += ns.Length; }
            var codeBytes = Encoding.UTF8.GetBytes(code ?? "");
            codeBytes.CopyTo(salt, off);

            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(shared, salt, Encoding.UTF8.GetBytes("cbs-mic-v1")));
            var key = new byte[32];
            hkdf.GenerateBytes(key, 0, key.Length);
            Array.Clear(shared, 0, shared.Length);
            return key;
        }

        /// <summary>随机 16 字节 nonce（握手用），base64 传输。</summary>
        public static byte[] NewNonce()
        {
            var n = new byte[16];
            RandomNumberGenerator.Fill(n);
            return n;
        }
    }
}
