using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒配对协议层（《话筒功能接口文档.md》§3）：HELLO 解析与校验、AUTH_OK/AUTH_FAIL 构造、
    /// 鉴权编排（配对限速 → 动态码一次性消费 → 忙闲判断 → X25519+HKDF 会话密钥）。
    /// 只做协议与判定，不碰 socket，便于自检；socket 收发放到 MicServer。
    /// </summary>
    public static class MicPairing
    {
        public const string Type = "cbs-mic";
        public const int Version = 1;
        public const int MaxDeviceIdLength = 64;
        public const int MaxHelloBytes = 2048;      // 单行 JSON 上限，防超大报文

        /// <summary>失败原因（与接口文档 §6 的字符串完全一致，安卓端按这些值做提示）。</summary>
        public static class Reason
        {
            public const string BadCode = "bad_code";
            public const string TooMany = "too_many";
            public const string MicOff = "mic_off";
            public const string Busy = "busy";
            public const string Expired = "expired";
            public const string Version = "version";
            public const string BadRequest = "bad_request";
            /// <summary>等待电脑端管理员在弹窗里确认（不是失败，手机端应继续等）。</summary>
            public const string Pending = "pending";
            /// <summary>管理员在电脑端拒绝了本次配对。</summary>
            public const string Denied = "denied";
        }

        public sealed class Hello
        {
            public int V = Version;
            public string Code = "";
            public string DeviceId = "";
            public byte[] PublicKey;     // 客户端 X25519 公钥（32 字节）
            public byte[] ClientNonce;   // 客户端随机数（16 字节）
        }

        /// <summary>解析客户端的一行 HELLO JSON。</summary>
        public static bool TryParseHello(string line, out Hello hello, out string error)
        {
            hello = null; error = null;
            if (string.IsNullOrWhiteSpace(line)) { error = "空报文"; return false; }
            if (line.Length > MaxHelloBytes) { error = "报文过大"; return false; }
            JsonNode node;
            try { node = JsonNode.Parse(line); }
            catch { error = "JSON 解析失败"; return false; }
            if (node is not JsonObject o) { error = "不是 JSON 对象"; return false; }
            if (o["t"]?.ToString() != Type) { error = "类型标记不符"; return false; }
            int v = 0; try { v = o["v"]?.GetValue<int>() ?? 0; } catch { }
            if (v != Version) { error = "版本不符"; return false; }

            string code = (o["code"]?.ToString() ?? "").Trim();
            if (code.Length != 6 || !IsAllDigits(code)) { error = "动态码格式不对"; return false; }

            string device = (o["device_id"]?.ToString() ?? "").Trim();
            if (device.Length == 0 || device.Length > MaxDeviceIdLength) { error = "设备标识不合法"; return false; }

            byte[] pk, nc;
            try { pk = Convert.FromBase64String(o["pk"]?.ToString() ?? ""); } catch { error = "公钥不是合法 base64"; return false; }
            try { nc = Convert.FromBase64String(o["nc"]?.ToString() ?? ""); } catch { error = "随机数不是合法 base64"; return false; }
            if (pk.Length != 32) { error = "公钥长度必须是 32 字节"; return false; }
            if (nc.Length != 16) { error = "随机数长度必须是 16 字节"; return false; }

            hello = new Hello { V = v, Code = code, DeviceId = device, PublicKey = pk, ClientNonce = nc };
            return true;
        }

        static bool IsAllDigits(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        // ---------------- 应答构造 ----------------

        /// <summary>
        /// 配对报文一律用**不转义**的 JSON 编码器。
        ///
        /// 【真机踩过的坑】System.Text.Json 默认编码器会把 `+` 转义成 `\u002B`（HTML 安全转义），
        /// 而 base64 里经常出现 `+` —— 手机端正则提取到的是字面 `\u002B`，base64 解码失败 →
        /// 公钥变成 0 字节 → 配对失败（或 X25519 退化异常）。约 76% 的概率命中，表现就是"时好时坏"。
        /// </summary>
        static readonly System.Text.Json.JsonSerializerOptions ReplyJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string BuildAuthOk(uint sid, byte[] serverPublicKey, byte[] serverNonce, string deviceId = null)
        {
            var o = new JsonObject
            {
                ["t"] = Type,
                ["v"] = Version,
                ["ok"] = true,
                ["sid"] = sid,
                ["ps"] = Convert.ToBase64String(serverPublicKey ?? Array.Empty<byte>()),
                ["ns"] = Convert.ToBase64String(serverNonce ?? Array.Empty<byte>())
            // 设备凭证（类 cookie）：配对成功后下发给手机，写操作（排程/调休）必须带上它
            , ["cred"] = string.IsNullOrWhiteSpace(deviceId) ? "" : VntManager.DeviceCredential(deviceId),
                // **本机设备 ID**：手机端用它"认人"（名字会改、IP 会变，设备 ID 不会）——
                // 手机在虚拟网里可能找到多台设备，靠这个字段确认"回我的是不是那台电脑"。
                ["pdid"] = VntManager.DeviceId ?? "",
                // 设备凭证（类 cookie）：连进来就自动下发，手机之后每个请求都带上它 —— 界面不显示、不可复制
                ["cred"] = VntManager.DeviceCredential(deviceId)
            };
            return o.ToJsonString(ReplyJsonOptions);
        }

        public static string BuildAuthFail(string reason)
        {
            var o = new JsonObject { ["t"] = Type, ["v"] = Version, ["ok"] = false, ["reason"] = reason ?? Reason.BadRequest };
            return o.ToJsonString(ReplyJsonOptions);
        }

        /// <summary>
        /// "等待电脑端管理员确认"应答（确认弹窗模式）：手机端收到它应继续等待同一个 socket 上的最终应答，
        /// 而不是像失败那样立刻放弃。这样电脑端可以异步弹窗（不阻塞配对线程），手机端重试也不会排长队。
        /// </summary>
        public static string BuildAuthPending(int waitSeconds)
        {
            var o = new JsonObject
            {
                ["t"] = Type,
                ["v"] = Version,
                ["ok"] = false,
                ["pending"] = true,
                ["reason"] = Reason.Pending,
                ["wait"] = waitSeconds
            };
            return o.ToJsonString(ReplyJsonOptions);
        }

        /// <summary>恢复成功应答（不需要再交换密钥，故不含 ps/ns）。</summary>
        public static string BuildResumeOk(uint sid)
        {
            var o = new JsonObject { ["t"] = Type, ["v"] = Version, ["ok"] = true, ["sid"] = sid, ["resumed"] = true };
            return o.ToJsonString(ReplyJsonOptions);
        }

        /// <summary>解析 RESUME 报文：{"t":"cbs-mic","v":1,"resume":true,"sid":123,"token":"base64"}。</summary>
        public static bool TryParseResume(string line, out uint sid, out string token, out string error)
        {
            sid = 0; token = ""; error = null;
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxHelloBytes) return false;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o) return false;
                if (o["t"]?.ToString() != Type) return false;
                if ((o["v"]?.GetValue<int>() ?? 0) != Version) return false;
                bool isResume = false;
                try { isResume = o["resume"]?.GetValue<bool>() ?? false; } catch { }
                if (!isResume) return false;
                sid = o["sid"]?.GetValue<uint>() ?? 0;
                token = (o["token"]?.ToString() ?? "").Trim();
                if (sid == 0 || token.Length == 0) { error = "恢复报文缺少 sid/token"; return false; }
                return true;
            }
            catch { return false; }
        }

        // ---------------- 鉴权编排 ----------------

        public sealed class AuthResult
        {
            public bool Ok;
            public string Reason;                 // 失败原因（Reason.*）
            public uint Sid;
            public byte[] SessionKey;             // 成功时的 32 字节会话密钥
            public byte[] ServerPublicKey;
            public byte[] ServerNonce;
            public string DeviceId = "";
            public string Reply;                  // 直接回给客户端的 JSON 行
        }

        /// <summary>
        /// 处理一次 HELLO：这是个纯函数（只依赖传入的状态对象与 now），调用方负责 socket 收发与日志。
        /// 判定顺序刻意这样排：先限速锁定（不泄露动态码是否正确）→ 话筒开关 → 动态码一次性 → 忙闲。
        /// </summary>
        public static AuthResult Authenticate(string helloLine, string sourceIp, DateTime now,
            bool micEnabled, int maxSessions, int activeSessions,
            MicSecurity.PairingGuard guard, MicSecurity.CodeStore codes, string codeSeed)
        {
            var res = new AuthResult { Ok = false };
            if (guard == null) guard = new MicSecurity.PairingGuard();

            if (!TryParseHello(helloLine, out var hello, out string parseErr))
            {
                res.Reason = parseErr != null && parseErr.Contains("版本") ? Reason.Version : Reason.BadRequest;
                res.Reply = BuildAuthFail(res.Reason);
                return res;
            }

            // ① 限速锁定：被锁的 IP 直接拒绝，且**不**去消耗动态码（避免被拿来试探）
            if (guard.IsLocked(sourceIp, now))
            {
                res.Reason = Reason.TooMany;
                res.Reply = BuildAuthFail(res.Reason);
                return res;
            }

            // ② 话筒总开关：没开麦一律拒绝（这是最高优先级的产品要求）
            if (!micEnabled)
            {
                res.Reason = Reason.MicOff;
                res.Reply = BuildAuthFail(res.Reason);
                return res;
            }

            // ③ 忙闲：默认只允许一路
            if (maxSessions > 0 && activeSessions >= maxSessions)
            {
                res.Reason = Reason.Busy;
                res.Reply = BuildAuthFail(res.Reason);
                return res;
            }

            // ④ 动态码：一次性消费（失败计入限速）；**信任设备免动态码**（同一设备配对过一次后，下次免输码）
            bool trusted = VntManager.IsTrustedDevice(hello.DeviceId);
            if (!trusted && (codes == null || !codes.TryConsume(hello.Code, now, out _)))
            {
                bool locked = guard.NoteFailure(sourceIp, now);
                res.Reason = locked ? Reason.TooMany : Reason.BadCode;
                res.Reply = BuildAuthFail(res.Reason);
                return res;
            }
            guard.NoteSuccess(sourceIp);

            // ⑤ 会话密钥：服务端临时密钥对 + X25519 + HKDF（动态码进 salt）
            var kp = MicProtocol.NewKeyPair();
            var ns = MicProtocol.NewNonce();
            var key = MicProtocol.DeriveSessionKey(kp.PrivateKey, hello.PublicKey, hello.Code, hello.ClientNonce, ns);

            res.Ok = true;
            res.Sid = 0;                      // sid 由 Registry 分配，见 MicServer
            res.SessionKey = key;
            res.ServerPublicKey = kp.PublicKey;
            res.ServerNonce = ns;
            res.DeviceId = hello.DeviceId;
            res.Reply = BuildAuthOk(res.Sid, kp.PublicKey, ns);
            return res;
        }

        /// <summary>把 sid 填进已经构造好的 AUTH_OK（sid 由 Registry 分配后才拿到）。</summary>
        // ================= 管理员"免码自动配对"（用户要求：管理员设备的音频包直接跳过配对、无限时） =================
        // 协议见《话筒功能接口文档.md》§16.3：
        //   · 手机（管理员设备）发 {"t":"cbs-mic","v":1,"admin":true,"device_id":"…"}（**不含 6 位动态码**）
        //   · 电脑端按 IsAdminDevice(device_id) 判定；会话密钥两端各自派生，**不经过网络**
        //   · 会话 MaxMinutes = 0 → 无限时

        /// <summary>是否是"管理员免码"报文（cbs-mic v1 且 admin=true 且带非空 device_id）。</summary>
        public static bool IsAdminHello(string line, out string deviceId)
        {
            deviceId = "";
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxHelloBytes) return false;
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                if (node is not JsonObject o) return false;
                if (o["t"]?.ToString() != Type) return false;
                int v = 0; try { v = o["v"]?.GetValue<int>() ?? 0; } catch { }
                if (v != Version) return false;
                bool admin = false; try { admin = o["admin"]?.GetValue<bool>() ?? false; } catch { }
                if (!admin) return false;
                deviceId = (o["device_id"]?.ToString() ?? "").Trim();
                return deviceId.Length > 0 && deviceId.Length <= MaxDeviceIdLength;
            }
            catch { return false; }
        }

        /// <summary>
        /// 管理员免码配对的会话密钥（两端各自派生、不经过网络）：
        /// HKDF-SHA256(ikm = SHA256(UTF8(数据密码)), salt = "cbs-mic-admin", info = "cbs-mic-admin-v1", 32 字节)。
        /// 没配数据密码时返回空数组（该通道不可用），避免用空密码当密钥。
        /// </summary>
        public static byte[] AdminMicKey(string dataPassword, byte[] sessionSalt = null)
        {
            if (string.IsNullOrEmpty(dataPassword)) return Array.Empty<byte>();
            var ikm = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(dataPassword));
            // 【nonce 复用根治】salt 默认是常量 "cbs-mic-admin"；若手机声明支持每会话盐（hello 带 "s":1），
            // 电脑端会为本次会话生成 16 字节随机盐并随答复下发，这里把它拼进 salt →
            // 密钥**每会话唯一** → 即使 sid/seq 重复也不会出现同一 (key, nonce) 被用两次。
            // 老手机不带 "s":1 → sessionSalt 为 null → 行为与历史完全一致（保持兼容）。
            var saltList = new List<byte>(Encoding.UTF8.GetBytes("cbs-mic-admin"));
            if (sessionSalt != null && sessionSalt.Length > 0) saltList.AddRange(sessionSalt);
            var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest());
            hkdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(
                ikm, saltList.ToArray(), Encoding.UTF8.GetBytes("cbs-mic-admin-v1")));
            var key = new byte[32];
            hkdf.GenerateBytes(key, 0, key.Length);
            return key;
        }

        /// <summary>管理员免码配对成功：带 sid / admin 标记 / 设备凭证（写操作要用）。</summary>
        public static string BuildAdminOk(uint sid, string deviceId, byte[] sessionSalt = null)
        {
            var o = new JsonObject
            {
                ["t"] = Type, ["v"] = Version, ["ok"] = true, ["admin"] = true, ["sid"] = sid,
                ["cred"] = VntManager.DeviceCredential(deviceId)
            };
            // 【nonce 复用根治】把本次会话的随机盐随答复下发（手机据此派生同一密钥）；老手机忽略该字段。
            if (sessionSalt != null && sessionSalt.Length > 0) o["ns"] = Convert.ToBase64String(sessionSalt);
            return o.ToJsonString();
        }
        public static string RebuildAuthOk(uint sid, byte[] serverPublicKey, byte[] serverNonce, string deviceId = null) => BuildAuthOk(sid, serverPublicKey, serverNonce, deviceId);

        /// <summary>
        /// 这条 OK 答复是不是可用的成功答复。两种都算：
        ///   1) 普通/信任配对：带会话密钥素材 ps+ns（手机端用 X25519+动态码派生密钥）；
        ///   2) 管理员免码：按设计不带 ps/ns（密钥由数据密码两端各自派生），只有 admin:true/sid/cred。
        /// 踩坑记录：以前这里只认 ps+ns，于是管理员免码答复刚建好会话就被销毁
        ///（真机日志：配对答复缺少密钥素材 ... 已销毁该会话），手机端表现为配对被拒：请求格式不对。
        /// </summary>
        public static bool HasKeyMaterial(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply) || !reply.Contains("\"ok\":true")) return false;
            if (reply.Contains("\"ps\":\"") && reply.Contains("\"ns\":\"")) return true;   // 普通 / 信任配对
            if (reply.Contains("\"admin\":true")) return true;                             // 管理员免码配对
            return false;
        }

        /// <summary>客户端侧派生同一把密钥（自检与安卓参考实现都用它，证明两端一致）。</summary>
        public static byte[] ClientDeriveKey(byte[] clientPrivate, byte[] serverPublicKey, string code, byte[] clientNonce, byte[] serverNonce)
            => MicProtocol.DeriveSessionKey(clientPrivate, serverPublicKey, code, clientNonce, serverNonce);
    }
}
