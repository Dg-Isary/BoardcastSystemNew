using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>
    /// vnt「服务端加密」（`-W`）用的 RSA 部分。
    ///
    /// 背景（为什么必须有它）：`vnts` **无条件**创建 RSA 密钥对，并在 `HandshakeResponse` 里
    /// 回 `secret=true` + 自己的公钥；客户端此时必须再发一次 `SecretHandshakeRequest{token,key}`，
    /// 用**服务端公钥加密**，服务端解出后拿 `key` 当对称密钥，后续所有报文按加密体收发。
    /// 官方客户端的 `server_encrypt` 是**默认开启**的特性，所以不发这一步的客户端在真实 vnts 上
    /// 根本注册不上（服务端会回 `Error::NoKey`）。
    ///
    /// 逐字节依据（两边源码已交叉印证）：
    ///  · 第一次握手：`HandshakeRequest.key_finger` 填**客户端自己的**公钥指纹
    ///    （`vnt\src\handle\handshaker.rs` L59-62）；指纹不同时服务端才会回带公钥，用于重连时省流量。
    ///  · 指纹 = SPKI(public_key_der) 的 `fingerprint_base64()`，即 **SHA-256 → base64（无换行）**
    ///    （`rsa_cipher.rs` finger()）。
    ///  · **RSA 加密用 PKCS#1 v1.5**（`rsa::Pkcs1v15Encrypt`，客户端加密 / 服务端 `Pkcs1v15Encrypt` 解密，一致）。
    ///  · 明文体布局 = `[protobuf 报文][random 16][finger 16]`，其中
    ///    `finger = SHA256(body ‖ random ‖ 12字节包内字段)[16..32]`，`body` 又是 `[报文][random]`
    ///    （`protocol\body.rs` 的 `RsaSecretBody`：data/buffer 的关系见该类注释）。
    ///
    /// 密钥对保存在 `config\vnt_rsa.pem`（PKCS#8 私钥 + SPKI 公钥），只在本地使用、不外发。
    /// </summary>
    public static class VntRsa
    {
        static RSA _local;
        static string _localFinger;
        static readonly object _lock = new object();

        /// <summary>本机 RSA 密钥文件（含私钥，权限随数据目录）。</summary>
        public static string KeyPath => Path.Combine(Program.ConfigDir, "vnt_rsa.pem");

        /// <summary>
        /// 本机 RSA 密钥对：首次生成 2048 位并落盘，之后复用（与官方客户端行为一致）。
        /// 生成失败（极少见）时返回 null —— 调用方会退化为"不带指纹的第一次握手"。
        /// </summary>
        public static RSA LocalKey()
        {
            lock (_lock)
            {
                if (_local != null) return _local;
                try
                {
                    string path = KeyPath;
                    if (File.Exists(path))
                    {
                        string pem = File.ReadAllText(path);
                        var rsa = RSA.Create();
                        // 注意：本文件同时保存了私钥段与公钥段，而 ImportFromPem 遇到"多把密钥"
                        // 会直接抛 ArgumentException（实测："The input contains multiple keys,
                        // but only one key can be imported."）。所以不能整体导入，必须先取出私钥段。
                        // && 私钥段的标签有两种可能，都要认：
                        //    PKCS#1 → "RSA PRIVATE KEY"（ExportRSAPrivateKeyPem 生成的就是这种）
                        //    PKCS#8 → "PRIVATE KEY"
                        string priv = ExtractPemBlock(pem, "RSA PRIVATE KEY")
                                   ?? ExtractPemBlock(pem, "PRIVATE KEY");
                        if (priv == null)
                        {
                            LastError = "密钥文件里没有私钥段（既不是 RSA PRIVATE KEY 也不是 PRIVATE KEY）：" + path;
                            PlayLog.Write("vnt-lite", "RSA密钥文件异常", "warn", detail: LastError);
                            return null;
                        }
                        rsa.ImportFromPem(priv);
                        _local = rsa;
                        return _local;
                    }
                    var gen = RSA.Create(2048);
                    Directory.CreateDirectory(Program.ConfigDir);
                    // 私钥与公钥都写进同一个文件：ImportFromPem 会挑出 PRIVATE KEY 段
                    string text = gen.ExportRSAPrivateKeyPem()
                                  + Environment.NewLine
                                  + gen.ExportSubjectPublicKeyInfoPem();
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    PlayLog.Write("vnt-lite", "生成RSA密钥", "info",
                        detail: "已生成 2048 位 RSA 密钥对（用于 -W 服务端加密握手），保存在 " + path);
                    _local = gen;
                    return _local;
                }
                catch (Exception ex)
                {
                    LastError = "生成/读取密钥失败：" + ex.GetType().Name + "：" + ex.Message
                              + "（路径 " + KeyPath + "）";
                    PlayLog.Write("vnt-lite", "生成RSA密钥失败", "warn",
                        detail: LastError + "（本次将退回不带指纹的握手；若服务端要求 -W 会连接失败）");
                    return null;
                }
            }
        }

        /// <summary>诊断：本机密钥/指纹获取失败时的原因（探针会打印，避免异常被静默吞掉）。</summary>
        public static string LastError = "";

        /// <summary>
        /// 从一个可能含多个 PEM 段的文本里取出指定的段（含 BEGIN/END 行）。
        /// 取不到返回 null。
        /// </summary>
        static string ExtractPemBlock(string text, string label)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string begin = "-----BEGIN " + label + "-----";
            string end = "-----END " + label + "-----";
            int i = text.IndexOf(begin, StringComparison.Ordinal);
            if (i < 0) return null;
            int j = text.IndexOf(end, i, StringComparison.Ordinal);
            if (j < 0) return null;
            return text.Substring(i, j - i + end.Length);
        }

        /// <summary>本机公钥指纹（base64，无换行）。没有密钥时返回空串。</summary>
        public static string LocalFinger()
        {
            if (_localFinger != null) return _localFinger;
            var rsa = LocalKey();
            if (rsa == null) return _localFinger = "";
            try { return _localFinger = FingerOfSpki(rsa.ExportSubjectPublicKeyInfo()); }
            catch (Exception ex) { LastError = "导出公钥失败：" + ex.Message; return _localFinger = ""; }
        }

        /// <summary>把 RSA 公钥导入为可加密的对象（服务端握手响应里给的是 SPKI DER）。</summary>
        public static RSA ImportPublicKey(byte[] spkiDer)
        {
            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spkiDer, out int read);
            if (read <= 0) throw new CryptographicException("公钥 DER 解析失败");
            return rsa;
        }

        /// <summary>SPKI DER 的指纹 = SHA256(DER) 的 base64（与 Rust `fingerprint_base64()` 一致）。</summary>
        public static string FingerOfSpki(byte[] spkiDer)
            => Convert.ToBase64String(SHA256.HashData(spkiDer));

        /// <summary>带 12 字节包内字段参与的非对称加密体指纹（取 SHA256 的后 16 字节）。</summary>
        public static byte[] BodyFinger(byte[] body, byte[] nonce12)
        {
            using var sha = SHA256.Create();
            var buf = new byte[body.Length + nonce12.Length];
            Buffer.BlockCopy(body, 0, buf, 0, body.Length);
            Buffer.BlockCopy(nonce12, 0, buf, body.Length, nonce12.Length);
            var full = sha.ComputeHash(buf);
            var f = new byte[16];
            Array.Copy(full, 16, f, 0, 16);
            return f;
        }

        /// <summary>
        /// 组一个"第二次握手"（SecretHandshakeRequest）包的载荷：
        ///   `RSA_PKCS1v15_加密( 报文 ‖ random16 ‖ finger16 )`
        /// </summary>
        public static byte[] BuildSecretHandshakePayload(RSA serverPublicKey, string token, byte[] symmetricKey32, byte[] nonce12)
        {
            var msg = new SecretHandshakeRequest { Token = token ?? "", Key = symmetricKey32 ?? Array.Empty<byte>() }.ToBytes();
            var random = new byte[16];
            RandomNumberGenerator.Fill(random);

            // body = 报文 ‖ random（RsaSecretBody.body() 的定义）
            var body = new byte[msg.Length + random.Length];
            Buffer.BlockCopy(msg, 0, body, 0, msg.Length);
            Buffer.BlockCopy(random, 0, body, msg.Length, random.Length);

            var finger = BodyFinger(body, nonce12);
            var plain = new byte[body.Length + finger.Length];
            Buffer.BlockCopy(body, 0, plain, 0, body.Length);
            Buffer.BlockCopy(finger, 0, plain, body.Length, finger.Length);

            return serverPublicKey.Encrypt(plain, RSAEncryptionPadding.Pkcs1);
        }

        /// <summary>解一个非对称加密体（自检用：本机加密后再用本机私钥解开，验证布局正确）。</summary>
        public static bool TryOpenSecretBody(RSA key, byte[] cipher, byte[] nonce12, out byte[] message, out string error)
        {
            message = null; error = null;
            try
            {
                var plain = key.Decrypt(cipher, RSAEncryptionPadding.Pkcs1);
                if (plain.Length < 32) { error = "明文过短"; return false; }
                var body = new byte[plain.Length - 16];
                Buffer.BlockCopy(plain, 0, body, 0, body.Length);
                var finger = new byte[16];
                Buffer.BlockCopy(plain, plain.Length - 16, finger, 0, 16);
                var want = BodyFinger(body, nonce12);
                for (int i = 0; i < 16; i++) if (want[i] != finger[i]) { error = "finger 校验失败"; return false; }
                var msg = new byte[body.Length - 16];
                Buffer.BlockCopy(body, 0, msg, 0, msg.Length);
                message = msg;
                return true;
            }
            catch (Exception ex) { error = "解密失败：" + ex.Message; return false; }
        }
    }
}
