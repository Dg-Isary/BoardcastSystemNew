using System;
using System.Security.Cryptography;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>
    /// vnt 客户端数据加密（N1）。逐条依据 vnt v1.2.16 源码：
    ///  · 密钥：`key = SHA256(密码)`；密码长度 &lt; 8 时用 key[..16]（AES-128），否则 32 字节（AES-256）  —— cipher.rs L128-142
    ///  · 指纹哈希：`SHA256(token)`                                                                    —— finger.rs L14-19
    ///  · AEAD：AES-GCM，**AAD 为空**                                                                   —— aes_gcm_cipher.rs L94/L97
    ///  · nonce(12)：**直接取包头字段** 源IP(4)‖目的IP(4)‖外层协议(1)‖上层协议(1)‖服务包标志(1)‖源TTL(1)  —— aes_gcm_cipher.rs L86-87 + finger.rs L54-63
    ///  · 载荷布局：`密文 ‖ tag(16) ‖ random(4) ‖ finger(12)`，预留 32 字节                              —— body.rs L3-9 / aes_gcm_cipher.rs L88
    ///  · finger(12) = SHA256(nonce ‖ (密文‖tag‖random) ‖ SHA256(token))[20..32]                        —— finger.rs L44-51
    ///  · 头字节 bit7（e 位）必须置 1，否则对端/服务端直接丢弃（"not encrypt"）                          —— finger.rs L21-24
    /// </summary>
    public static class VntCipher
    {
        public const int TagLen = 16;
        public const int RandomLen = 4;
        public const int FingerLen = 12;
        /// <summary>密文之外要预留的字节数（= tag + random + finger）。</summary>
        public const int EncryptionReserved = TagLen + RandomLen + FingerLen;   // 32

        /// <summary>数据密钥：SHA256(密码)。密码短于 8 字符时上游用前 16 字节（AES-128）。</summary>
        public static byte[] DeriveKey(string password, out bool use128)
        {
            using var sha = SHA256.Create();
            var full = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password ?? ""));
            use128 = (password ?? "").Length < 8;
            if (!use128) return full;
            var k = new byte[16];
            Array.Copy(full, 0, k, 0, 16);
            return k;
        }

        /// <summary>指纹哈希 = SHA256(token)。</summary>
        public static byte[] FingerHash(string token)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token ?? ""));
        }

        /// <summary>
        /// AEAD nonce（12 字节）：直接由包头字段构成（不异或）：
        /// 源 IP(4) ‖ 目的 IP(4) ‖ 外层协议(1) ‖ 上层协议(1) ‖ 服务包标志(1) ‖ 源 TTL(1)
        /// </summary>
        public static byte[] BuildNonce(VntPacket p)
        {
            var n = new byte[12];
            WriteU32Be(n, 0, p.SourceIp);
            WriteU32Be(n, 4, p.DestIp);
            n[8] = p.Protocol;
            n[9] = p.Transport;
            n[10] = (byte)(p.GatewayFlag ? 1 : 0);
            n[11] = p.InitTtl;
            return n;
        }

        /// <summary>finger = SHA256(nonce ‖ body ‖ SHA256(token))[20..32]（取最后 12 字节）。</summary>
        public static byte[] ComputeFinger(byte[] nonce, byte[] body, byte[] fingerHash)
        {
            using var sha = SHA256.Create();
            var buf = new byte[nonce.Length + body.Length + fingerHash.Length];
            Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length);
            Buffer.BlockCopy(body, 0, buf, nonce.Length, body.Length);
            Buffer.BlockCopy(fingerHash, 0, buf, nonce.Length + body.Length, fingerHash.Length);
            var full = sha.ComputeHash(buf);
            var f = new byte[FingerLen];
            Array.Copy(full, 20, f, 0, FingerLen);
            return f;
        }

        /// <summary>
        /// 加密：返回 `密文 ‖ tag ‖ finger`（若 useFinger=false 则不含 finger，预留 20 字节）。
        ///
        /// 与服务端源码 `SecretBody` 完全一致的关键点（之前所有实现都错在这里）：
        ///   **random(4) 不是附加在密文后的明文字段，而是被加密的明文本身的一部分**。
        ///   服务端 `encrypt_ipv4` 的顺序是：`set_random()` → `encrypt_in_place_detached(nonce, body_mut())`，
        ///   其中 `body()` = `buffer[0..len-16-4]`（**含那 4 字节 random**）。所以 AES-GCM 的明文是
        ///   `data ‖ random4`，解密侧要拿到原始 `data` 必须丢弃末尾 4 字节。
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] nonce, byte[] fingerHash, bool useFinger)
        {
            plaintext ??= Array.Empty<byte>();
            var rnd = new byte[RandomLen];
            RandomNumberGenerator.Fill(rnd);

            // 明文 = data ‖ random4（与服务端 body() 的定义一致）
            var pt = new byte[plaintext.Length + RandomLen];
            Buffer.BlockCopy(plaintext, 0, pt, 0, plaintext.Length);
            Buffer.BlockCopy(rnd, 0, pt, plaintext.Length, RandomLen);

            var ct = new byte[pt.Length];
            var tag = new byte[TagLen];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Encrypt(nonce, pt, ct, tag, null);            // AAD = 空（上游 &[]）

            // body = 密文(ct) ‖ tag(16) ‖ finger(12)（若 useFinger）
            int extra = useFinger ? TagLen + FingerLen : TagLen;
            var body = new byte[ct.Length + extra];
            Buffer.BlockCopy(ct, 0, body, 0, ct.Length);
            Buffer.BlockCopy(tag, 0, body, ct.Length, TagLen);
            if (useFinger)
            {
                // en_body = 密文‖tag（不含 finger）——finger.rs 里 calculate_finger 传的就是它
                var enBody = new byte[ct.Length + TagLen];
                Buffer.BlockCopy(body, 0, enBody, 0, enBody.Length);
                var f = ComputeFinger(nonce, enBody, fingerHash);
                Buffer.BlockCopy(f, 0, body, ct.Length + TagLen, FingerLen);
            }
            return body;
        }

        /// <summary>诊断：最近一次 TryDecrypt 用到/算出的关键字节，便于对着服务端源码核对。</summary>
        public static string LastDecryptDiag = "";

        /// <summary>
        /// 解密：先验 finger（若启用），再解 AES-GCM。失败返回 false 并给出原因。
        ///
        /// 与 Encrypt 对应的要点：`random4` 是明文的一部分（`data ‖ random4`），解密后**末尾 4 字节是 random，不是数据**。
        /// </summary>
        public static bool TryDecrypt(byte[] payload, byte[] key, byte[] nonce, byte[] fingerHash, bool useFinger,
            out byte[] plaintext, out string error)
        {
            plaintext = Array.Empty<byte>();
            error = null;
            int need = useFinger ? TagLen + FingerLen : TagLen;
            if (payload == null || payload.Length < need) { error = "长度不足（至少 " + need + " 字节）"; return false; }
            int ctLen = payload.Length - need - RandomLen;            // 密文长度（= 明文数据长度，random 不计入）
            if (ctLen < 0) { error = "载荷过短"; return false; }

            if (useFinger)
            {
                // en_body = 密文‖tag（不含 finger）
                var enBody = new byte[ctLen + RandomLen + TagLen];
                Buffer.BlockCopy(payload, 0, enBody, 0, enBody.Length);
                var want = ComputeFinger(nonce, enBody, fingerHash);
                for (int i = 0; i < FingerLen; i++)
                    if (payload[ctLen + RandomLen + TagLen + i] != want[i]) { error = "finger 校验失败"; return false; }
            }

            var tag = new byte[TagLen];
            Buffer.BlockCopy(payload, ctLen + RandomLen, tag, 0, TagLen);
            var ct = new byte[ctLen + RandomLen];                       // 密文 = AES(data‖random4)
            Buffer.BlockCopy(payload, 0, ct, 0, ct.Length);
            var pt = new byte[ctLen + RandomLen];
            try
            {
                using var gcm = new AesGcm(key, TagLen);
                gcm.Decrypt(nonce, ct, tag, pt, null);
            }
            catch (Exception ex)
            {
                // 诊断：把"我们自己算的 tag"也算一份，与服务端包里的 tag 直接对比
                try
                {
                    var tagWant = new byte[TagLen];
                    using (var g = new AesGcm(key, TagLen))
                        g.Encrypt(nonce, new byte[ctLen + RandomLen], new byte[ctLen + RandomLen], tagWant, null);
                    LastDecryptDiag = $"载荷={BitConverter.ToString(payload)} nonce={BitConverter.ToString(nonce)} "
                        + $"密文长={ctLen} keySHA8={(key != null ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))[..8] : "(null)")}"
                        + $" 自算tag={Convert.ToHexString(tagWant)} 包内tag={Convert.ToHexString(tag)}";
                }
                catch { }
                error = "解密失败：" + ex.Message;
                return false;
            }
            // 去掉末尾的 random4，只留原始数据
            plaintext = new byte[ctLen];
            Buffer.BlockCopy(pt, 0, plaintext, 0, ctLen);
            return true;
        }

        static void WriteU32Be(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }
    }
}
