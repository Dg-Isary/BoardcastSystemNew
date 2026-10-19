using System;
using System.Net;
using System.Text;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒**UDP 配对通道**（《话筒功能接口文档.md》§15.3）：给"不建虚拟网卡、没有虚拟网里 TCP 栈"的手机端
    /// 提供一条能完成 6 位动态码配对的 UDP 路径，与现有 TCP 配对（<see cref="MicServer"/>.PairLoop）**并存**。
    ///
    /// 报文（一条 UDP = 一帧）：
    ///     "cbs-pair:"(9 字节 ASCII) ‖ nonce(12) ‖ 密文(n) ‖ tag(16)
    ///   密文 = AES-256-GCM(key, nonce, 明文 = 单行 cbs-mic JSON, AAD = 空)
    ///   key  = SHA256(UTF8(数据密码))   —— 与 vnt 的 -w（数据密码）同一把，手机端扫码/设置里已有
    ///
    /// 为什么必须加密：中继模式下 vnt 层是明文（e=0），服务端能看到载荷，而 HELLO 里含**6 位动态码**，
    /// 绝不能明文上网。回复行（AUTH_OK/AUTH_FAIL）同样加密后 UDP 回发到来源地址。
    ///
    /// 本类只做"帧的封装/解封 + 密钥派生"，**不碰 socket、不碰会话状态**，因此可被自检与探针直接调用；
    /// 收发与业务处理在 <see cref="MicServer"/> 里（调用现成的 HandlePairLine，不重复实现配对逻辑）。
    /// </summary>
    public static class MicPairUdp
    {
        /// <summary>帧前缀（ASCII，9 字节）。</summary>
        public const string Prefix = "cbs-pair:";
        public const int NonceSize = 12;
        public const int TagSize = 16;
        /// <summary>一帧的最小长度（前缀 + nonce + 空密文 + tag）。</summary>
        public const int MinFrameBytes = 9 + NonceSize + TagSize;
        /// <summary>单帧上限：HELLO 约 300 字节，留足余量同时防超大报文。</summary>
        public const int MaxFrameBytes = 2048;

        /// <summary>
        /// 配对通道密钥 = SHA256(UTF8(数据密码))。手机端必须用同一把（二维码里的 w_password）。
        /// 数据密码为空时返回**空数组**，表示"没配数据密码 → UDP 配对通道不可用"（避免用空密码当密钥）。
        /// </summary>
        public static byte[] DeriveKey(string dataPassword)
        {
            if (string.IsNullOrEmpty(dataPassword)) return Array.Empty<byte>();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(dataPassword));
        }

        /// <summary>是否是本通道的帧（前缀匹配即可，密钥/长度在 Unwrap 里再校验）。</summary>
        public static bool LooksLikePairFrame(byte[] frame)
        {
            if (frame == null || frame.Length < Prefix.Length) return false;
            for (int i = 0; i < Prefix.Length; i++)
                if (frame[i] != (byte)Prefix[i]) return false;
            return true;
        }

        /// <summary>把一行明文（单行 cbs-mic JSON，不含换行）封成配对帧。</summary>
        public static byte[] Build(string line, byte[] key)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("配对密钥必须是 32 字节（SHA256(数据密码)）", nameof(key));
            var plain = Encoding.UTF8.GetBytes((line ?? "").Replace("\r", "").Replace("\n", ""));
            var nonce = new byte[NonceSize];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var gcm = new System.Security.Cryptography.AesGcm(key, TagSize))
                gcm.Encrypt(nonce, plain, cipher, tag, null);
            var frame = new byte[Prefix.Length + NonceSize + cipher.Length + TagSize];
            Encoding.ASCII.GetBytes(Prefix).CopyTo(frame, 0);
            Buffer.BlockCopy(nonce, 0, frame, Prefix.Length, NonceSize);
            Buffer.BlockCopy(cipher, 0, frame, Prefix.Length + NonceSize, cipher.Length);
            Buffer.BlockCopy(tag, 0, frame, Prefix.Length + NonceSize + cipher.Length, TagSize);
            return frame;
        }

        /// <summary>
        /// 解封配对帧。任何一步不满足都返回 null 并给出可诊断的原因（服务端只记日志、不回包，
        /// 避免给探测者任何反馈）。
        /// </summary>
        public static string TryUnwrap(byte[] frame, byte[] key, out string error)
        {
            error = null;
            if (key == null || key.Length != 32) { error = "配对密钥未配置或长度不对（需要 SHA256(数据密码)）"; return null; }
            if (frame == null || frame.Length < MinFrameBytes) { error = "帧太短"; return null; }
            if (frame.Length > MaxFrameBytes) { error = "帧过大"; return null; }
            if (!LooksLikePairFrame(frame)) { error = "前缀不是 cbs-pair:"; return null; }
            int bodyLen = frame.Length - Prefix.Length - NonceSize - TagSize;
            var nonce = new byte[NonceSize];
            var cipher = new byte[bodyLen];
            var tag = new byte[TagSize];
            Buffer.BlockCopy(frame, Prefix.Length, nonce, 0, NonceSize);
            Buffer.BlockCopy(frame, Prefix.Length + NonceSize, cipher, 0, bodyLen);
            Buffer.BlockCopy(frame, Prefix.Length + NonceSize + bodyLen, tag, 0, TagSize);
            var plain = new byte[bodyLen];
            try
            {
                using var gcm = new System.Security.Cryptography.AesGcm(key, TagSize);
                gcm.Decrypt(nonce, cipher, tag, plain, null);
            }
            catch (System.Security.Cryptography.CryptographicException) { error = "AEAD 校验失败（密码不对或被篡改）"; return null; }
            try { return Encoding.UTF8.GetString(plain).Trim(); }
            catch (Exception ex) { error = "明文不是合法 UTF-8：" + ex.Message; return null; }
        }

        /// <summary>便于日志与自检：帧的简要描述（不含密文内容）。</summary>
        public static string Describe(byte[] frame)
            => frame == null ? "(空)" : $"cbs-pair 帧 {frame.Length} 字节（前缀 {(LooksLikePairFrame(frame) ? "正确" : "不符")}）";

        /// <summary>回发地址是否可用于回包（必须是具体单播地址，不能是 0.0.0.0 这种）。</summary>
        public static bool IsUsableReplyEndpoint(IPEndPoint ep)
            => ep != null && ep.Port > 0 && ep.Address != null
               && !ep.Address.Equals(IPAddress.Any) && !ep.Address.Equals(IPAddress.IPv6Any);
    }
}
