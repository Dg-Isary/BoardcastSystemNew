using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BroadcastSystemNet
{
    /// <summary>
    /// vnt 虚拟局域网集成：配置存储、vnt-cli 命令行构建、状态解析、二维码载荷生成。
    /// 配置独立存放在 config\vnt.jsonl（与 warn.jsonl / tiaoxiu.jsonl 同一风格）：
    ///   第 1 行 { "_type":"vnt", server, token, device_id, protocol, encrypt, cli_path, enabled }
    ///   第 2 行 { "_type":"mic", enabled, port, ctl_port, max_minutes, out_db, ... }
    /// 程序不内置任何服务器/token 默认值（用户要求：全部在菜单里填）。
    /// </summary>
    public static class VntManager
    {
        public const string VntFile = "vnt.jsonl";
        public const int DefaultMicPort = 29880;
        public const int DefaultCtlPort = 29881;
        public const string QrType = "cbs-mic";
        public const int QrVersion = 1;

        // ---------------- 配置读写 ----------------

        /// <summary>
        /// 进程内缓存（"最后写入的值就是真值"）。
        /// 为什么必须有：SaveRecord 是"读整个文件 → 改一条 → 写回"，同一进程里连续两次 Set 会
        /// 后一次读到**旧文件**、把前一次的改动整条抹掉（实测：先 Set(mic.code_seed) 再 Set(mic.enabled)，
        /// 落盘后 mic 记录里只剩 enabled，"开麦"随即读回 false → 自检/配对报 mic_off）。
        /// 有缓存后：Set 立即更新缓存，Get 优先读缓存，落盘失败（权限/被杀软拦截）也不影响本次运行。
        /// </summary>
        static readonly Dictionary<string, Dictionary<string, JsonNode>> _cache = new Dictionary<string, Dictionary<string, JsonNode>>();
        static readonly object _cacheLock = new object();

        static JsonObject CachedRecord(string type)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(type, out var rec)) return new JsonObject(rec.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value?.DeepClone())));
                return null;
            }
        }

        static void CacheSet(string type, string key, JsonNode value)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(type, out var rec)) { rec = new Dictionary<string, JsonNode>(); _cache[type] = rec; }
                rec[key] = value?.DeepClone();
            }
        }

        public static JsonObject ReadRecord(string type)
        {
            try
            {
                foreach (var n in Program.ReadJsonl(VntFile))
                    if (n is JsonObject o && o["_type"]?.ToString() == type) return o;
            }
            catch { }
            return null;
        }

        public static void SaveRecord(string type, Action<JsonObject> mutate)
        {
            // 先在内存里改（后续 Get 立刻可见，不受落盘成败影响），再尽力写盘
            if (mutate != null)
            {
                var mem = CachedRecord(type) ?? new JsonObject { ["_type"] = type };
                try { mutate(mem); } catch (Exception ex) { LogHelper.Log("SaveVntRecord mutate Failed: " + type, ex); }
                lock (_cacheLock)
                {
                    var rec = new Dictionary<string, JsonNode>();
                    foreach (var kv in mem) rec[kv.Key] = kv.Value?.DeepClone();
                    _cache[type] = rec;
                }
            }
            try
            {
                var all = Program.ReadJsonl(VntFile);
                JsonObject rec = null;
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == type) { rec = o; break; }
                if (rec == null) { rec = new JsonObject { ["_type"] = type }; all.Add(rec); }
                // 落盘时用内存里的最新值覆盖，避免"读到的旧文件"把本次改动抹掉
                var latest = CachedRecord(type);
                if (latest != null) foreach (var kv in latest) rec[kv.Key] = kv.Value?.DeepClone();
                Program.WriteJsonl(VntFile, all);
            }
            catch (Exception ex) { LogHelper.Log("SaveVntRecord Failed: " + type, ex); }
        }

        public static string GetString(string type, string key, string fallback = "")
        {
            var c = CachedRecord(type);
            if (c != null && c[key] != null) return c[key].ToString();
            var d = ReadRecord(type)?[key];
            if (d == null) return fallback;
            // 文件里有就顺手填进缓存，之后无论文件是否可写都以内存为准
            CacheSet(type, key, d);
            return d.ToString();
        }

        public static int GetInt(string type, string key, int fallback)
        {
            var c = CachedRecord(type);
            if (c != null && c[key] != null) { try { return c[key].GetValue<int>(); } catch { return fallback; } }
            try
            {
                var n = ReadRecord(type)?[key];
                if (n == null) return fallback;
                CacheSet(type, key, n);
                return n.GetValue<int>();
            }
            catch { return fallback; }
        }

        public static bool GetBool(string type, string key, bool fallback)
        {
            var c = CachedRecord(type);
            if (c != null && c[key] != null) { try { return c[key].GetValue<bool>(); } catch { return fallback; } }
            try
            {
                var n = ReadRecord(type)?[key];
                if (n == null) return fallback;
                CacheSet(type, key, n);
                return n.GetValue<bool>();
            }
            catch { return fallback; }
        }

        public static void Set(string type, string key, JsonNode value) => SaveRecord(type, o => o[key] = value);

        // vnt 设置
        public static string Server { get => GetString("vnt", "server"); set => Set("vnt", "server", JsonValue.Create(value ?? "")); }
        public static string Token { get => GetString("vnt", "token"); set => Set("vnt", "token", JsonValue.Create(value ?? "")); }
        public static string DeviceId
        {
            get
            {
                // 自动生成 UUID 并写入配置：device_id 固定才能让虚拟 IP 固定（重启/掉线重连不变），
                // 手机端就不用重新扫码。用户想换也行，在菜单里改。
                string s = GetString("vnt", "device_id");
                if (string.IsNullOrWhiteSpace(s))
                {
                    s = Guid.NewGuid().ToString();
                    Set("vnt", "device_id", JsonValue.Create(s));
                }
                return s;
            }
            set => Set("vnt", "device_id", JsonValue.Create(value ?? ""));
        }
        /// <summary>加密方式（对应 vnt-cli --model）：aes_gcm/chacha20_poly1305/chacha20/aes_cbc/aes_ecb/sm4_cbc/xor；留空=用 vnt 默认。</summary>
        public static string Encrypt { get => GetString("vnt", "encrypt"); set => Set("vnt", "encrypt", JsonValue.Create(value ?? "")); }
        /// <summary>数据加密密码（对应 vnt-cli -w）：用它生成的密钥加密客户端数据，服务端无法解密；两端必须相同。</summary>
        public static string DataPassword { get => GetString("vnt", "data_password"); set => Set("vnt", "data_password", JsonValue.Create(value ?? "")); }
        /// <summary>是否启用数据指纹校验（对应 vnt-cli --finger）：服务端开了指纹校验，客户端也必须开。</summary>
        public static bool Fingerprint { get => GetBool("vnt", "fingerprint", true); set => Set("vnt", "fingerprint", JsonValue.Create(value)); }   // 默认开（用户要求）
        /// <summary>
        /// 服务端加密（对应官方客户端 vnt-cli 的 `-W` / server_encrypt）策略，三态字符串：
        ///
        ///   "auto"（默认，推荐）——**自动适配**：第一次握手永远是明文；服务端在握手响应里带
        ///         RSA 公钥（vnts 的 `handshake()` 只要 rsa_cipher 可用就会带，见 server.rs L465-473）
        ///         就自动补发 RSA 密钥握手并切到加密通道；服务端没这个能力就一直明文。
        ///         于是"服务器带 -W"和"不带 -W"都能连，用户不用管这个开关。
        ///   "on"  ——强制开启：服务端没有公钥时**直接报错**，不静默降级为明文。
        ///   "off" ——强制关闭：永不使用 -W（服务端仍可用明文通信；「中继优先」必须用它）。
        ///
        /// 注意加密的是**控制面**（Service/Control）：vnts 只原样转发明文数据包，
        /// 所以「中继优先」时本项必须关，否则中继收不到包（话筒音频本身每包 AEAD，不受影响）。
        /// </summary>
        public static string ServerEncryptMode
        {
            get
            {
                string v = GetString("vnt", "server_encrypt_mode");
                if (v == "auto" || v == "on" || v == "off") return v;
                // 兼容旧配置：以前只有一个 bool server_encrypt（默认 false = 明文）
                return GetBool("vnt", "server_encrypt", false) ? "on" : "auto";
            }
            set => Set("vnt", "server_encrypt_mode",
                JsonValue.Create(value == "on" || value == "off" ? value : "auto"));
        }

        /// <summary>兼容旧代码的布尔视图：true = 允许（自动/强制开），false = 强制关。</summary>
        public static bool ServerEncrypt
        {
            get => ServerEncryptMode != "off";
            set => ServerEncryptMode = value ? "on" : "off";
        }
        /// <summary>生成随机数据加密密码（a-z, A-Z, 0-9，16 位）。</summary>
        public static string NewDataPassword()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new System.Text.StringBuilder(16);
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var buf = new byte[16];
            rng.GetBytes(buf);
            for (int i = 0; i < 16; i++) sb.Append(chars[buf[i] % chars.Length]);
            string s = sb.ToString();
            DataPassword = s;
            return s;
        }
        /// <summary>重新生成设备ID（换一个虚拟 IP 用）。</summary>
        public static string NewDeviceId()
        {
            string s = Guid.NewGuid().ToString();
            DeviceId = s;
            return s;
        }
        /// <summary>生成一个随机组网令牌（a-z, A-Z, 0-9，6 位；vnt 官方建议用唯一值当 token，避免撞进别人的虚拟网）。</summary>
        public static string NewToken()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new System.Text.StringBuilder(6);
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var buf = new byte[6];
            rng.GetBytes(buf);
            for (int i = 0; i < 6; i++) sb.Append(chars[buf[i] % chars.Length]);
            string s = sb.ToString();
            Token = s;
            return s;
        }
        public static string Protocol { get => GetString("vnt", "protocol", "udp"); set => Set("vnt", "protocol", JsonValue.Create(value ?? "udp")); }
        public static string CliPath { get => GetString("vnt", "cli_path"); set => Set("vnt", "cli_path", JsonValue.Create(value ?? "")); }
        public static bool VntEnabled { get => GetBool("vnt", "enabled", false); set => Set("vnt", "enabled", JsonValue.Create(value)); }
        /// <summary>程序启动时自动拉起 vnt-lite 组网（默认开；「vnt 设置」里可关）。</summary>
        public static bool AutoStart { get => GetBool("vnt", "auto_start", true); set => Set("vnt", "auto_start", JsonValue.Create(value)); }
        /// <summary>vnt 是否已经配好（有服务器和 token 才算）。</summary>
        public static bool VntConfigured => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Token);

        // 话筒设置
        /// <summary>
        /// 音频"迟到帧"丢弃阈值（毫秒）。0 = 不丢（只统计）。
        ///
        /// 为什么需要它：网络卡顿后，几秒前的音频再补播没有意义（听起来是"延迟的旧话"），
        /// 但阈值太小会误杀正常抖动（我上次写死 300ms 就把整条音频丢光了）。
        /// 默认 **1500ms**（75 帧）：只有真正严重迟到的帧才丢，并且每次丢弃都会记日志（含落后毫秒数）便于校准。
        /// </summary>
        public static int MicLateDropMs { get => GetInt("mic", "late_drop_ms", 0); set => Set("mic", "late_drop_ms", JsonValue.Create(value)); }

        public static bool MicEnabled { get => GetBool("mic", "enabled", false); set => Set("mic", "enabled", JsonValue.Create(value)); }
        public static int MicPort { get => GetInt("mic", "port", DefaultMicPort); set => Set("mic", "port", JsonValue.Create(value)); }
        public static int MicCtlPort { get => GetInt("mic", "ctl_port", DefaultCtlPort); set => Set("mic", "ctl_port", JsonValue.Create(value)); }

        /// <summary>
        /// 同时允许几路话筒（默认 8 = 实际上不限制）。
        ///
        /// 【为什么不再是 1】用户明确要求：**不要让话筒独占、不要管理员手动断开** —— 谁配对上来都能用。
        /// 以前 maxSessions=1，第二台手机一配对就拿到 `busy`，还得管理员去点「断开当前话筒」，很不合理。
        /// </summary>
        public static int MicMaxSessions
        {
            get => Math.Max(1, GetInt("mic", "max_sessions", 8));
            set => Set("mic", "max_sessions", JsonValue.Create(Math.Max(1, value)));
        }

        /// <summary>话筒的信任设备列表（配对成功后把设备 ID 记下来，下次同一设备配对免动态码）。
        /// 存储在 mic 命名空间（话筒相关配置），与 vnt 组网无关。</summary>
        public static string TrustedDevices
        {
            get => GetString("mic", "trusted_devices", "");
            set => Set("mic", "trusted_devices", JsonValue.Create(value ?? ""));
        }

        /// <summary>把设备 ID 加入信任列表（已存在则不动）。</summary>
        /// <summary>
        /// **已踢出**的设备（黑名单）：被管理员踢出后，下一次连接**不再自动信任**，必须重新输一次 6 位动态码。
        /// （用户反馈：踢出后下次连接又自动进了信任列表 —— 因为配对成功会重新写信任，这里用黑名单挡住自动路径。）
        /// </summary>
        public static string UntrustedDevices
        {
            get => GetString("vnt", "untrusted_devices", "");
            set => Set("vnt", "untrusted_devices", JsonValue.Create(value ?? ""));
        }

        public static bool IsUntrustedDevice(string deviceId)
            => !string.IsNullOrWhiteSpace(deviceId) &&
               (UntrustedDevices ?? "").Split(',').Any(s => string.Equals(s.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase));

        public static void MarkUntrusted(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = (UntrustedDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (!list.Any(s => string.Equals(s, deviceId.Trim(), StringComparison.OrdinalIgnoreCase))) list.Add(deviceId.Trim());
            UntrustedDevices = string.Join(",", list);
        }

        public static void ClearUntrusted(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = (UntrustedDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0 && !string.Equals(s, deviceId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            UntrustedDevices = string.Join(",", list);
        }
        public static void AddTrustedDevice(string deviceId)
        {

            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = (TrustedDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            if (list.Contains(deviceId)) return;
            list.Add(deviceId);
            TrustedDevices = string.Join(",", list);
        }

        /// <summary>设备 ID 是否已被信任。</summary>
        public static bool IsTrustedDevice(string deviceId)
        {

            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            var list = (TrustedDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
            return list.Contains(deviceId);
        }

        /// <summary>信任设备列表（去掉空项）。界面按这个逐条列出，支持单个"踢出"。</summary>
        /// <summary>信任设备的机型名（如 PKX110）：只在"设备已被信任"时记录，供话筒窗显示、便于认设备。</summary>
        public static string TrustedDeviceName(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return "";
            foreach (var s in (GetString("mic", "trusted_names", "") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int p = s.IndexOf('|');
                if (p > 0 && string.Equals(s.Substring(0, p).Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase))
                    return s.Substring(p + 1).Trim();
            }
            return "";
        }

        public static void SetTrustedDeviceName(string deviceId, string name)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name)) return;
            if (string.Equals(TrustedDeviceName(deviceId), name.Trim(), StringComparison.Ordinal)) return;   // 没变就不写配置
            var parts = new List<string>();
            foreach (var s in (GetString("mic", "trusted_names", "") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int p = s.IndexOf('|');
                if (p > 0 && !string.Equals(s.Substring(0, p).Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase)) parts.Add(s.Trim());
            }
            parts.Add(deviceId.Trim() + "|" + name.Trim());
            Set("mic", "trusted_names", JsonValue.Create(string.Join(",", parts)));
        }

        public static List<string> TrustedDeviceList()
        {
            var list = new List<string>();
            foreach (var s in (TrustedDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        /// <summary>把某个设备从信任列表里**踢出**（下次它就必须重新输 6 位动态码）。</summary>
        public static void RemoveTrustedDevice(string deviceId)
        {
            // 踢出 = 只从信任名单移除（用户确认的语义）：下次连接必须重新输一次 6 位动态码，进来后是**临时权限**
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = TrustedDeviceList();
            list.RemoveAll(s => string.Equals(s, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            TrustedDevices = string.Join(",", list);
        }

        /// <summary>
        /// **管理员设备**列表（设备 ID）：只有管理员设备才允许用手机端「排程 / 调休」的**编辑**功能；
        /// 非管理员只能查看（播控全功能不受影响）。存在 vnt 命名空间，与组网配置一起管。
        /// </summary>
        public static string AdminDevices
        {
            get => GetString("vnt", "admin_devices", "");
            set => Set("vnt", "admin_devices", JsonValue.Create(value ?? ""));
        }

        public static List<string> AdminDeviceList()
        {
            var list = new List<string>();
            foreach (var s in (AdminDevices ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        public static void AddAdminDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = AdminDeviceList();
            if (list.Contains(deviceId)) return;
            list.Add(deviceId);
            AdminDevices = string.Join(",", list);
        }

        public static void RemoveAdminDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var list = AdminDeviceList();
            list.RemoveAll(s => string.Equals(s, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            AdminDevices = string.Join(",", list);
        }

        /// <summary>
        /// 本机签发"设备凭证"用的密钥（首次使用时随机生成并保存）。**界面不显示、不可复制**。
        /// </summary>
        /// <summary>
        /// 最近**发过接口请求**的设备（设备ID → 最后活动时间/名称）。**只在内存里，不写配置**（符合"不赋权不写配置"）。
        /// 作用：管理员赋权**不需要对方先做话筒配对** —— 只要它用过 App（播控/排程/调休都会带设备ID），这里就有记录。
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, string Name)> SeenDevices
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime, string)>();

        public static void NoteSeenDevice(string deviceId, string name = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            var id = deviceId.Trim();
            string nm = name != null && name.Trim().Length > 0 ? name.Trim() : (SeenDevices.TryGetValue(id, out var old) ? old.Name : "");
            SeenDevices[id] = (DateTime.UtcNow, nm ?? "");
            if (SeenDevices.Count > 64)
                foreach (var kv in SeenDevices.OrderBy(k => k.Value.At).Take(SeenDevices.Count - 64).ToList()) SeenDevices.TryRemove(kv.Key, out _);
        }

        /// <summary>最近见过的设备（按最后活动时间倒序）。</summary>
        public static List<(string Id, string Name, DateTime At)> SeenDeviceList()
            => SeenDevices.Select(kv => (kv.Key, kv.Value.Name ?? "", kv.Value.At)).OrderByDescending(x => x.At).ToList();
        public static string CredSecret
        {
            get
            {
                string s = GetString("vnt", "cred_secret", "");
                if (string.IsNullOrWhiteSpace(s))
                {
                    var b = new byte[32];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(b);
                    s = Convert.ToBase64String(b);
                    Set("vnt", "cred_secret", JsonValue.Create(s));
                }
                return s;
            }
        }

        /// <summary>
        /// 设备凭证（作用类似 cookie）：HMAC-SHA256(本机密钥, 设备ID) 的前 16 字节。
        ///
        /// 用户确认的语义：**连进来就自动生成、自动通信完成，不允许复制和查看**；
        /// 手机配对成功后从答复里拿到它，之后每个请求都带上；电脑端据此确认"你是哪台设备"。
        /// 它**本身不落配置**（可由密钥随时重算），只有"赋权"才写配置。
        /// </summary>
        public static string DeviceCredential(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return "";
            try
            {
                using var h = new System.Security.Cryptography.HMACSHA256(Convert.FromBase64String(CredSecret));
                var mac = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(deviceId.Trim().ToLowerInvariant()));
                return Convert.ToBase64String(mac, 0, 16);
            }
            catch { return ""; }
        }

        /// <summary>校验设备凭证（手机请求里带的 cred）。</summary>
        public static bool VerifyCredential(string deviceId, string cred)
            => !string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(cred)
               && string.Equals(DeviceCredential(deviceId), cred.Trim(), StringComparison.Ordinal);

        /// <summary>角色两档：admin（管理员）/ view（普通，默认）。</summary>
        public static string DeviceRole(string deviceId)
            => IsAdminDevice(deviceId) ? "admin" : "view";

        /// <summary>设置角色：admin = 写进管理员名单（落配置）；view = 从名单移除。</summary>
        public static void SetDeviceRole(string deviceId, string role)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)) AddAdminDevice(deviceId);
            else RemoveAdminDevice(deviceId);
        }
        public static bool IsAdminDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return AdminDeviceList().Any(s => string.Equals(s, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>清除信任设备列表。</summary>
        public static void ClearTrustedDevices() => TrustedDevices = "";

        /// <summary>单次喊话最长分钟数（到点自动断）。</summary>
        /// <summary>
        /// 日志保留天数（用户要求）：默认 30 天；**0 = 永久保留**。
        /// 只影响 `logs` 目录里的日志文件，业务数据（排程/调休/音乐/配置）一律不动。
        /// </summary>
        public static int LogKeepDays
        {
            get { int d = GetInt("log", "keep_days", 30); return d < 0 ? 30 : d; }
            set => Set("log", "keep_days", JsonValue.Create(value < 0 ? 0 : value));
        }

        public static int MicMaxMinutes   // 临时权限默认上限 60 分钟（用户确认）
        { get => GetInt("mic", "max_minutes", 60); set => Set("mic", "max_minutes", JsonValue.Create(value)); }
        /// <summary>
        /// 传输模式：false = P2P 优先（默认，先打洞直连、延迟最低）；true = 中继优先（网络对 P2P 不友好时手动切换）。
        /// 注意：服务端（vnts）的中继只转发 WireGuard 报文，所以"中继"需要我们先实现 WireGuard 封装（进行中）；
        /// 未实现前勾选"中继优先"要**明确提示不可用**，不能静默失败。
        /// </summary>
        public static bool RelayFirst { get => GetBool("vnt", "relay_first", false); set => Set("vnt", "relay_first", JsonValue.Create(value)); }
        /// <summary>输出峰值上限 dBFS（负值，防止啸叫/过载）。</summary>
        public static double MicPeakDb { get => GetInt("mic", "peak_db", -3); set => Set("mic", "peak_db", JsonValue.Create(value)); }
        /// <summary>话筒期间把音乐压低到百分之几。</summary>
        public static int MicDuckPercent { get => GetInt("mic", "duck_percent", 20); set => Set("mic", "duck_percent", JsonValue.Create(value)); }
        /// <summary>网络抖动后的"免动态码恢复"宽限期（秒）：挂起超过它就必须重新输动态码。</summary>
        public static int MicResumeGraceSeconds { get => GetInt("mic", "resume_grace_s", 60); set => Set("mic", "resume_grace_s", JsonValue.Create(value)); }

        /// <summary>动态码种子（首次生成后固定，用于 60 秒轮换）。</summary>
        public static string MicCodeSeed
        {
            get
            {
                string s = GetString("mic", "code_seed");
                if (string.IsNullOrWhiteSpace(s))
                {
                    s = Guid.NewGuid().ToString("N");
                    Set("mic", "code_seed", JsonValue.Create(s));
                }
                return s;
            }
        }

        // ---------------- vnt-cli 命令行 ----------------


        /// <summary>日志脱敏：token 只留首尾各 2 位。</summary>
        public static string MaskToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "(未设置)";
            string t = token.Trim();
            if (t.Length <= 4) return new string('*', t.Length);
            return t.Substring(0, 2) + new string('*', Math.Max(3, t.Length - 4)) + t.Substring(t.Length - 2);
        }

        // ---------------- 状态解析（vnt-cli --info / --list） ----------------

        public sealed class VntInfo
        {
            public string VirtualIp = "";
            public string Gateway = "";
            public string Netmask = "";
            public string Status = "";
            public string NatType = "";
            public string Relay = "";
            public string PublicIp = "";
            public string LocalIp = "";
            public bool Connected => string.Equals(Status, "Connected", StringComparison.OrdinalIgnoreCase);
        }

        public sealed class VntPeer
        {
            public string Name = "";
            public string VirtualIp = "";
            public string Kind = "";     // p2p / relay
            public int Rt = -1;          // 延迟 ms
            public string Status = "";
        }

        /// <summary>解析 `vnt-cli --info` 的键值行（纯函数）。</summary>
        public static VntInfo ParseInfo(string text)
        {
            var info = new VntInfo();
            if (string.IsNullOrWhiteSpace(text)) return info;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                int i = line.IndexOf(':');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim().ToLowerInvariant();
                string v = line.Substring(i + 1).Trim();
                switch (k)
                {
                    case "virtual ip": info.VirtualIp = v; break;
                    case "virtual gateway": info.Gateway = v; break;
                    case "virtual netmask": info.Netmask = v; break;
                    case "connection status": info.Status = v; break;
                    case "nat type": info.NatType = v; break;
                    case "relay server": info.Relay = v; break;
                    case "public ips": info.PublicIp = v; break;
                    case "local ip": info.LocalIp = v; break;
                }
            }
            return info;
        }

        static readonly Regex PeerLine = new Regex(
            @"^(?<name>.+?)\s{2,}(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<kind>p2p|relay)\s+(?<rt>\d+)\s+(?<status>\w+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>解析 `vnt-cli --list` 的设备表（表头行会被忽略）。</summary>
        public static List<VntPeer> ParseList(string text)
        {
            var list = new List<VntPeer>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                var m = PeerLine.Match(line);
                if (!m.Success) continue;                 // 表头/杂项行
                int rt = -1;
                int.TryParse(m.Groups["rt"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rt);
                list.Add(new VntPeer
                {
                    Name = m.Groups["name"].Value.Trim(),
                    VirtualIp = m.Groups["ip"].Value.Trim(),
                    Kind = m.Groups["kind"].Value.ToLowerInvariant(),
                    Rt = rt,
                    Status = m.Groups["status"].Value.Trim()
                });
            }
            return list;
        }

        // ---------------- 二维码载荷（接口文档 §2） ----------------

        /// <summary>
        /// 生成二维码内容：一个码同时完成 vnt 组网与话筒连接配置（UTF-8 JSON，明文，安卓端直接解析）。
        /// micHost 传**手机能直接发到的地址**（局域网 IPv4；没有网卡的 vnt-lite 模式下必须这样，
        /// 因为手机没有 TUN，发不到 10.x 虚拟地址）；vntIp 传**程序内置 vnt-lite** 在虚拟网里的地址
        /// （手机发到它 → 由 vnt-lite 直接交给我们的话筒服务）；vntIpAlt 传**你自己那份 vnt** 的虚拟地址
        /// （手机发到它 → 经它的虚拟网卡落到操作系统 → 同样能到我们监听的话筒端口）。
        /// 一台电脑上同时跑着两套 vnt 客户端时，这两个 10.x 地址**都对**，只是属于不同客户端，
        /// 所以两个都写进二维码，手机端依次尝试，不用人工判断该用哪个。
        /// </summary>
        public static string BuildQrPayload(string server, string token, string deviceId, string protocol, string encrypt,
            string micHost, int micPort, int ctlPort, string hostName, int httpPort = 0,
            bool fingerprint = false, string wPassword = null, string vntIp = null)
        {
            var vnt = new JsonObject
            {
                ["server"] = server ?? "",
                ["token"] = token ?? "",
                ["protocol"] = string.IsNullOrWhiteSpace(protocol) ? "udp" : protocol
            };
            if (!string.IsNullOrWhiteSpace(deviceId)) vnt["device_id"] = deviceId;
            if (!string.IsNullOrWhiteSpace(encrypt)) vnt["model"] = encrypt;          // 加密方式（vnt-cli --model）
            if (fingerprint) vnt["finger"] = true;                                   // 数据指纹校验（vnt-cli --finger）
            if (!string.IsNullOrWhiteSpace(wPassword)) vnt["w_password"] = wPassword; // 数据加密密码（vnt-cli -w）

            var mic = new JsonObject
            {
                ["host"] = micHost ?? "",
                ["port"] = micPort,
                ["ctl_port"] = ctlPort,
                ["name"] = hostName ?? "",
                ["code_hint"] = "在电脑端菜单点『开麦』后显示的 6 位数字"
            };
            // 诊断用：把"话筒到底绑在哪个地址"也告诉手机，排查"手机发不到电脑"时一眼看出问题
            if (!string.IsNullOrWhiteSpace(MicServer.BoundAddress)) mic["bind"] = MicServer.BoundAddress;
            var lan = VntLite.VntLiteClient.DetectLocalIpv4();
            if (!string.IsNullOrWhiteSpace(lan) && lan != "0.0.0.0") mic["lan_ip"] = lan;
            // 手机端要完整继承 index.html 的功能：告诉它网页/接口在哪个口（不要写死 5000）
            if (httpPort > 0) mic["http_port"] = httpPort;
            // 本机在虚拟网里的地址：手机端据此把配对/音频包**经 vnt-lite 中继**发过来（手机没有 TUN）
            if (!string.IsNullOrWhiteSpace(vntIp)) mic["vnt_ip"] = vntIp;

            var root = new JsonObject
            {
                ["t"] = QrType,
                ["v"] = QrVersion,
                ["vnt"] = vnt,
                ["mic"] = mic
            };
            return root.ToJsonString();
        }

        /// <summary>
        /// 组网连接的"一眼看懂"状态（纯函数，便于自检）：
        /// 返回 (文案, 颜色键)，颜色键 green=已连接 / amber=未运行或连接中 / red=未配置或掉线。
        /// </summary>
        public static (string label, string colorKey) ConnectionState(bool configured, bool running, string infoStatus, string virtualIp)
        {
            // 「vnt 由你自己管理」模式：不启动 vnt-cli，只看本机有没有虚拟网卡地址
            if (!ManagedByApp)
            {
                string ip = "";
                try { ip = MicServer.DetectLocalVntIp(); } catch { }
                if (!string.IsNullOrWhiteSpace(ip)) return ($"由你自己的 vnt 管理 · 已检测到虚拟 IP {ip}", "green");
                if (!string.IsNullOrWhiteSpace(virtualIp)) return ($"由你自己的 vnt 管理 · 虚拟 IP {virtualIp}", "green");
                return ($"由你自己的 vnt 管理 · 还没检测到虚拟网卡（网段前缀 {VnetPrefix}）", "amber");
            }
            if (!configured) return ("未配置（请在下方填写服务器与 token）", "red");
            if (!running) return ("未启动（点「启动 vnt」建网）", "amber");
            bool connected = string.Equals((infoStatus ?? "").Trim(), "Connected", StringComparison.OrdinalIgnoreCase);
            bool hasIp = LooksLikeVntAddress(virtualIp);
            if (connected && hasIp) return ($"已连接 · 虚拟 IP {virtualIp}", "green");
            // 【误报修复】已经拿到过虚拟 IP = 曾经注册成功。之后短暂的"握手中/Connecting"，
            // 只是服务端静默触发的**重新握手**（日志里"超过 40 秒没收到服务端报文，重新握手"），
            // 此时数据面（P2P/中继）仍然是通的 → 不能报"未连接"，否则界面每 40 秒红一次（用户反馈）。
            if (hasIp && !connected) return ($"已连接 · 虚拟 IP {virtualIp}（正在重新握手…）", "green");
            if (connected) return ("已连接，但还没拿到虚拟 IP", "amber");
            if (string.IsNullOrWhiteSpace(infoStatus)) return ("已启动，正在读取状态…", "amber");
            return ($"未连接（{infoStatus}）", "red");
        }

        /// <summary>
        /// 从当前配置生成二维码载荷（**唯一入口**：避免调用处漏传字段，比如忘了带数据密码/指纹，
        /// 那会导致手机端入网成功却收不到数据，这种问题很难查）。
        /// </summary>
        public static string BuildQrPayloadFromConfig(string micHost, int micPort, int ctlPort, string hostName, int httpPort)
            => BuildQrPayload(Server, Token, DeviceId, Protocol, Encrypt, micHost, micPort, ctlPort, hostName, httpPort,
                Fingerprint, DataPassword, LocalVirtualIp);

        /// <summary>
        /// 本机在虚拟网里的地址（**程序内置 vnt-lite** 的地址；没入网返回空串）。
        /// 注意：如果这台电脑上还跑着你自己装的 vnt，那个客户端会拿到**另一个** 10.x 地址
        /// （见 <see cref="OsVnetIp"/>）—— 一台电脑两套客户端 → 服务端分配两个地址，这是正常的。
        /// </summary>
        public static string LocalVirtualIp
        {
            get
            {
                try
                {
                    var c = VntProcess.Client;
                    if (c != null && c.VirtualIp != 0) return VntLite.VntPacket.UintToIp(c.VirtualIp);
                }
                catch { }
                try { return MicServer.DetectLocalVntIp(); } catch { return ""; }
            }
        }

        /// <summary>操作系统虚拟网卡上的地址（= 你自己那份 vnt 客户端的地址，通常和 vnt-lite 的不是同一个）。</summary>
        public static string OsVnetIp
        {
            get { try { return MicServer.DetectLocalVntIp(); } catch { return ""; } }
        }

        /// <summary>
        /// vnt 由谁管理：
        ///   true （默认）= 本程序负责启动/停止 vnt-cli（需要管理员权限创建虚拟网卡）；
        ///   false = 你自己已经装好并运行着 vnt（例如官方桌面客户端/服务），本程序**只借用现有虚拟网卡**，
        ///           不再启动任何 vnt 进程、也就不需要管理员权限。
        /// </summary>
        public static bool ManagedByApp { get => GetBool("vnt", "managed_by_app", true); set => Set("vnt", "managed_by_app", JsonValue.Create(value)); }
        /// <summary>虚拟网地址前缀（CIDR 表示，如 `10.26.0.0/24`；用于自动识别本机的 vnt 虚拟网卡）。</summary>
        public static string VnetPrefix { get => GetString("vnt", "vnet_prefix", ""); set => Set("vnt", "vnet_prefix", JsonValue.Create((value ?? "").Trim())); }   // 空就存空：留空 = 服务器自动分配（以前这里会回填 10.26.0.0/24，导致永远清不掉）
        /// <summary>
        /// **实际生效**的虚拟网段（CIDR）。
        ///
        /// 用户要求：虚拟 IP 留空 = 由服务器分配。但网段过滤/来源校验还需要一个网段，
        /// 所以留空时用"服务器刚分配给我们的虚拟 IP"推一个 /24 出来（例如分到 10.8.0.2 → 10.8.0.0/24）。
        /// 这样"不填也能连"，而且过滤仍然生效（只接受同网段来源）。
        /// </summary>
        /// <summary>判断 ip 是否落在 cidr（如 10.26.0.0/24）里；格式不对返回 false。</summary>
        static bool CidrContains(string cidr, string ip)
        {
            try
            {
                var sp = cidr.Split('/');
                if (sp.Length != 2) return false;
                int bits = int.Parse(sp[1].Trim());
                if (bits < 0 || bits > 32) return false;
                uint net = IpToUint(sp[0].Trim()), addr = IpToUint(ip.Trim());
                if (net == 0 || addr == 0) return false;
                uint mask = bits == 0 ? 0u : (0xFFFFFFFFu << (32 - bits));
                return (net & mask) == (addr & mask);
            }
            catch { return false; }
        }

        static uint IpToUint(string ip)
        {
            var p = (ip ?? "").Split('.');
            if (p.Length != 4) return 0;
            uint v = 0;
            foreach (var s in p) { if (!byte.TryParse(s, out byte b)) return 0; v = (v << 8) | b; }
            return v;
        }

        /// <summary>
        /// 用户在设置里填的**固定虚拟 IP**（纯 IP 字符串；留空 = 由服务器分配）。
        /// 与官方 App 一致：填了就固定，不填就自动获取。
        /// </summary>
        public static string RequestedVirtualIp
        {
            get
            {
                string raw = (VnetPrefix ?? "").Trim();
                if (raw.Contains("/")) return "";                 // 历史 CIDR 不算
                if (raw.Length == 0 || raw == "0.0.0.0") return "";
                return IpToUint(raw) != 0 ? raw : "";
            }
        }

        public static string EffectiveVnetPrefix
        {
            get
            {
                // 【与官方一致】网段是**服务端**的事（原版 vnt：--ip 只填一个 IP，默认由服务端分配；
                // 客户端从注册响应拿 virtual_netmask）。所以这里优先用"服务端下发的 IP + 掩码"算网段，
                // 只有拿不到时才退回本地推断 —— 不再由本程序"猜"一个 /24 当权威（用户指出：原版没有 CIDR 概念）。
                string assignedCidr = "";
                try
                {
                    var vc = VntProcess.Client;
                    if (vc != null && vc.VirtualIp != 0 && vc.VirtualNetmask != 0)
                    {
                        uint net = vc.VirtualIp & vc.VirtualNetmask;
                        int bits = 0; uint m = vc.VirtualNetmask;
                        while (m != 0) { bits += (int)(m & 1); m >>= 1; }
                        assignedCidr = VntLite.VntPacket.UintToIp(net) + "/" + bits;
                    }
                }
                catch { }
                if (assignedCidr.Length > 0 && (VnetPrefix ?? "").Trim().Length == 0) return assignedCidr;

                string ip = "";
                try { ip = LocalVirtualIp; } catch { }
                if (string.IsNullOrWhiteSpace(ip)) { try { ip = MicServer.DetectLocalVntIp(); } catch { } }

                string raw = (VnetPrefix ?? "").Trim();
                // 语义已改为**纯 IP**（用户要求，与官方 App 一致）：填了=固定用这个 IP，留空=服务器分配。
                // 历史配置里的 CIDR 写法（含 "/"）一律当过期值丢掉。
                if (raw.Contains("/")) raw = "";   // 历史 CIDR 只"忽略"，**绝不回写配置**（回写会和界面输入互相打架，真机踩过）
                // 【自动纠偏】存量配置里可能还留着旧默认值（例如 10.26.0.0/24），而服务器实际分配的是别的网段
                // （例如 10.8.0.x）。这时若继续按旧网段过滤，来源全被拒 → 表现成"明明拿到 IP 却连不上"。
                // 规则：填了的值"必须包含当前虚拟 IP"才算数，否则当作过期值丢掉，改由服务器分配决定。
                if (raw.Length > 0 && raw.Contains("/") && !string.IsNullOrWhiteSpace(ip) && !CidrContains(raw, ip))
                {
                    try { PlayLog.Write("vnt", "网段前缀自动纠偏", "warn", detail: $"配置里的网段 {raw} 不包含当前虚拟 IP {ip}，已忽略（改用服务器分配的网段）"); } catch { }
                    try { VnetPrefix = ""; } catch { }
                    raw = "";
                }
                if (raw.Length > 0) return raw;
                var parts = (ip ?? "").Split('.');
                if (parts.Length != 4) return "";
                return parts[0] + "." + parts[1] + "." + parts[2] + ".0/24";
            }
        }

        /// <summary>本机所有 IPv4 里是否包含某个 vnt 虚拟网地址（用于确认"我们确实在虚拟网里"）。</summary>
        public static bool LooksLikeVntAddress(string ip)
        {
            // 【重要修复】以前这里按 `vnet_prefix` 的 CIDR 前缀比对，而且默认值写死成 `10.26.0.0/24`。
            // 用户把虚拟 IP 改成"由服务端分配"后，vnet_prefix 是空串 → 这里必然 false →
            // 界面永远显示"已连接，但还没拿到虚拟 IP"（用户反复反馈的误报，根因就在这里）。
            // 现在只判断：是不是一个合法的、非 0、非回环的 IPv4（虚拟 IP 就是纯 IP，没有 CIDR 概念）。
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (!System.Net.IPAddress.TryParse(ip.Trim(), out var addr)) return false;
            var b = addr.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0) return false;   // 0.0.0.0 不算
            if (b[0] == 127) return false;                                        // 回环不算
            return true;
        }    }
}
