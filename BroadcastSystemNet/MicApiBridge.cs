using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BroadcastSystemNet
{
    /// <summary>
    /// cbs-api：手机端通过**已配对的加密通道**（`MicProtocol.TypeApi`）调用电脑端接口。
    ///
    /// 为什么这么做：手机不建虚拟网卡，本来就到不了电脑的 5000 端口（TCP/HTTP 走不过 vnt-lite 的 UDP 通道）。
    /// 与其在两个端各写一套业务逻辑，不如**复用现成的 HTTP 接口**：
    ///   手机 →（加密包）→ 电脑 →（本机回环 HTTP）→ 现成接口 → 原样把响应塞回加密包 → 手机
    /// 这样"播控/排程/调休"在手机与网页上行为完全一致，且以后电脑端加接口，手机端不用改代码。
    ///
    /// 安全模型（重要）：
    ///   · **不绕过网页口令**。设置里配了口令时，本类会先用 `/api/login` 换 Bearer 令牌，再带令牌调用；
    ///     没配口令时才免鉴权（此时接口本身对局域网也是开放的，属既有设计）。
    ///   · 通道本身已有：配对会话密钥 + 每包 AEAD + 防重放 + 限速 + 会话白名单。
    ///   · **只允许转发到本机回环**（`127.0.0.1:{ListenPort}`），手机不能借它访问别的地址（不做开放代理）。
    ///   · 只接受白名单前缀（`/api/`、`/res/`）与 GET/POST；路径里禁止出现 `..` 与绝对 URL。
    /// </summary>
    public static class MicApiBridge
    {
        /// <summary>请求/响应 JSON 的协议版本（两端必须一致）。</summary>
        public const int ApiVersion = 1;
        /// <summary>单条请求/响应的上限（字节，UTF-8 JSON），与 MicProtocol.MaxPayload 对齐后再留一点余量。</summary>
        public const int MaxJsonBytes = MicProtocol.MaxPayload - 64;
        /// <summary>转发到本机 HTTP 的超时。</summary>
        public static int TimeoutMs = 4000;   // 4 秒：手机侧超时 8 秒，留一半余量（以前 8 秒会把接收线程堵到超时）

        static readonly HttpClient _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
        };

        /// <summary>登录得到的令牌（按"口令"缓存；口令变了会重新登录）。</summary>
        static string _token = "";
        static string _tokenForPwd = "";
        static DateTime _tokenAt = DateTime.MinValue;
        static readonly object _tokenLock = new object();

        public static long Requests, Ok, Failed, Denied, TooLarge;
        /// <summary>被安检门拦下的请求数（防注入/防刷的可见计数）。</summary>
        public static long GateDenied;
        /// <summary>因"不是管理员设备"被拒的排程/调休写入数。</summary>
        public static long AdminDenied;
        /// <summary>凭证校验失败数（有人冒用身份时能看到）。</summary>
        public static long CredDenied;

        /// <summary>这个路径+方法算不算"改配置"（需要管理员设备）。播控/读接口都不算。</summary>
        public static bool IsWritePath(string path, string method)
        {
            if (path == null) return false;
            // 【管理员专属 · 不分方法】取 6 位配对码 / 列话筒设备 / 踢出 —— 这三个即使是 GET 也必须管理员。
            // 踩坑记录：以前这段写在 `if (method == "GET") return false;` **后面** → 对 GET 而言是死代码，
            // 于是**任何非管理员设备都能取到配对码**（用户实测发现，属真漏洞）。顺序必须是"先判管理员路径"。
            if (path.StartsWith("/api/mic/code") || path.StartsWith("/api/mic/devices") || path.StartsWith("/api/mic/kick"))
                return true;
            if (method == "GET") return false;                       // 其余 GET 视为只读
            // 排程：POST/PUT/DELETE /api/tasks*；调休：POST /api/tiaoxiu*（含 sync/import）
            if (path.StartsWith("/api/tasks")) return true;
            if (path.StartsWith("/api/tiaoxiu")) return true;
            return false;
        }

        /// <summary>
        /// 处理一条 TypeApi 载荷，返回要回给手机的 JSON 字符串。
        /// 任何情况都返回**结构化 JSON**（不抛异常、不返回 null），这样手机端总能给出可读提示。
        /// [sourceKey] = 来源标识（虚拟 IP），用于安检门限速。
        /// </summary>
        public static string Handle(string json, string sourceKey = null)
        {
            Requests++;
            // ---- 重传去重（可靠性）：手机在超时后会**重发同一个请求**（id 不变）----
            // UDP 会丢包，没有重传就没有可靠性；但 POST/PUT/DELETE 重发不能重复执行（否则重复建任务/删两次）。
            // 所以这里按 id 缓存答复：同一个 id 再来，直接把上次的答复重发，**不重新执行**。
            string reqId = JsonId(json);
            if (reqId.Length > 0)
            {
                lock (_replyLock)
                {
                    if (_replies.TryGetValue(reqId, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 60)
                    {
                        Retried++;
                        return cached.Reply;
                    }
                }
            }
            string result = HandleInner(json, sourceKey);
            if (reqId.Length > 0)
            {
                lock (_replyLock)
                {
                    if (_replies.Count > 64) _replies.Clear();
                    _replies[reqId] = (result, DateTime.UtcNow);
                }
            }
            return result;
        }

        /// <summary>从请求里取 id（取不到就返回空串，表示不做去重）。</summary>
        static string JsonId(string json)
        {
            try
            {
                var o = JsonNode.Parse(json) as JsonObject;
                string id = o?["id"]?.ToString() ?? "";
                return id.Length <= 48 ? id : "";
            }
            catch { return ""; }
        }

        static readonly Dictionary<string, (string Reply, DateTime At)> _replies = new Dictionary<string, (string, DateTime)>();
        static readonly object _replyLock = new object();
        /// <summary>被去重（手机重传）的请求数：这个数大说明网络在丢包，但业务没有被重复执行。</summary>
        public static long Retried;
        /// <summary>手机因为超时/分片不全而重传的次数（由手机端上报，见日志导出）。</summary>
        public static long RetriesReported;

        static string HandleInner(string json, string sourceKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return Error(-1, "空请求");
                if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
                {
                    TooLarge++;
                    return Error(-1, "请求过大（上限 " + MaxJsonBytes + " 字节）");
                }
                var o = JsonNode.Parse(json) as JsonObject;
                if (o == null) return Error(-1, "请求不是 JSON 对象");
                int v = 0; try { v = o["v"]?.GetValue<int>() ?? 0; } catch { }
                if (v != ApiVersion) return Error(-1, "协议版本不匹配（服务端 " + ApiVersion + "）");

                string id = o["id"]?.ToString() ?? "";
                string method = (o["m"]?.ToString() ?? "GET").Trim().ToUpperInvariant();
                string path = (o["p"]?.ToString() ?? "").Trim();
                string body = o["b"] != null ? o["b"].ToJsonString() : null;

                string deny = Validate(method, path);
                if (deny != null)
                {
                    Denied++;
                    PlayLog.Write("cbs-api", "拒绝请求", "warn", detail: deny + "（" + method + " " + path + "）");
                    return Error(-1, deny, id);
                }

                // ---- 安检门：跨网来的数据先过这一关（防注入/防刷），过了才允许转发 ----
                string gateErr = Gate(json, sourceKey, out string gMethod, out string gPath, out string gBody);
                if (gateErr != null)
                {
                    GateDenied++;
                    PlayLog.Write("cbs-api", "安检门拦截", "warn", detail: gateErr + "（来源 " + (sourceKey ?? "-") + "）");
                    return Error(-1, "安检门拦截：" + gateErr, id);
                }
                if (gMethod != null) method = gMethod;
                if (!string.IsNullOrEmpty(gPath)) path = gPath;
                if (gBody != null) body = gBody;

                var (status, text, err) = Forward(method, path, body).GetAwaiter().GetResult();
                if (err != null)
                {
                    Failed++;
                    return Error(-1, err, id);
                }
                if (Encoding.UTF8.GetByteCount(text ?? "") > MaxJsonBytes)
                {
                    // 不截断（截断的 JSON 无法解析，反而更难查）：明确告诉手机"结果太大，请在电脑上看"
                    TooLarge++;
                    Failed++;
                    PlayLog.Write("cbs-api", "响应过大", "warn", detail: method + " " + path + " -> " + Encoding.UTF8.GetByteCount(text ?? "") + " 字节");
                    return Error(-1, "结果过大（" + Encoding.UTF8.GetByteCount(text ?? "") + " 字节 > 上限 " + MaxJsonBytes + "），请在电脑端查看", id);
                }
                Ok++;
                return new JsonObject
                {
                    ["v"] = ApiVersion,
                    ["id"] = id,
                    ["s"] = status,
                    ["d"] = text ?? ""
                }.ToJsonString();
            }
            catch (Exception ex)
            {
                Failed++;
                return Error(-1, "服务端处理异常：" + ex.Message);
            }
        }

        /// <summary>等价的公开入口：只做安检门校验（自检用），不真的转发。</summary>
        public static string GateOnly(string envelopeJson, string sourceKey,
            out string method, out string path, out string body)
            => Gate(envelopeJson, sourceKey, out method, out path, out body);

        /// <summary>
        /// 安检门：**所有从手机（远端）过来的接口请求都必须先过这里**，过了才允许转发到本机 HTTP 接口。
        ///
        /// 为什么需要它：这条数据跨公网/虚拟网过来（可被注入、重放、刷），而它最终会变成一条
        /// **本机 HTTP 请求**。所以这里做四件事（数据结构仍沿用 `v/id/m/p/b`，协议不变）：
        ///   ① 信封结构与体积校验；
        ///   ② 路径安检：只允许 `/api/`、`/res/`，拒绝百分号编码/控制字符/空白/反斜杠/`..`/`//`/查询串
        ///      —— 防止把 CRLF 塞进请求行、或绕过前缀白名单（`%2e%2e`、`/api/./`）；
        ///   ③ 方法与载荷安检：方法白名单 + 按端点校验字段类型/取值/长度；
        ///   ④ 按来源限速（令牌桶），防刷。
        /// 通过后载荷原样交给电脑端现有接口，业务代码不改。
        /// </summary>
        public static string Gate(string envelopeJson, string sourceKey,
            out string method, out string path, out string body)
        {
            method = null; path = null; body = null;
            if (string.IsNullOrWhiteSpace(envelopeJson)) return "请求为空";
            if (Encoding.UTF8.GetByteCount(envelopeJson) > GateMaxBytes) return "请求过大（>" + GateMaxBytes + " 字节）";
            if (!RateAllow(sourceKey ?? "")) return "请求过于频繁（安检门限速）";

            JsonNode node;
            try { node = JsonNode.Parse(envelopeJson); } catch { return "不是合法 JSON"; }
            var o = node as JsonObject;
            if (o == null) return "信封必须是 JSON 对象";

            int ver = 0; try { ver = o["v"]?.GetValue<int>() ?? 0; } catch { }
            if (ver != ApiVersion) return "协议版本不符（v=" + ver + "）";

            string id = o["id"]?.ToString() ?? "";
            if (id.Length == 0 || id.Length > 48) return "id 长度不合法";
            foreach (char ch in id)
                if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return "id 含非法字符";

            method = (o["m"]?.ToString() ?? "GET").Trim().ToUpperInvariant();
            if (method != "GET" && method != "POST" && method != "PUT" && method != "DELETE") return "方法不允许：" + method;

            path = (o["p"]?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                // 【决定性日志】路径为空时把**原样信封**记下来（截断），这样一次日志就能定位是"没带 p"还是"拼装错了"
                try
                {
                    string raw = o.ToJsonString();
                    if (raw.Length > 300) raw = raw.Substring(0, 300) + "…";
                    PlayLog.Write("数据通道", "请求被拒（原样）", "warn",
                        detail: $"路径为空：m={method} 长度={raw.Length} 内容={raw.Replace("\r", "").Replace("\n", " ")}");
                }
                catch { }
            }
            string perr = ValidatePath(path, method);
            if (perr != null) return perr;

            // **管理员校验**（用户要求）：只有"管理员设备"才允许改排程/调休；其它设备只能看。
            // 播控（/api/player/cmd）不受影响。设备 ID 由手机端放在信封的 `did` 字段里。
            string did = o["did"]?.ToString() ?? "";
            // 记下"这台设备用过 App"（内存表，不写配置）：管理员赋权不必等它先做话筒配对
            try { VntManager.NoteSeenDevice(did, o["dname"]?.ToString()); } catch { }
            try { if (VntManager.IsTrustedDevice(did)) VntManager.SetTrustedDeviceName(did, o["dname"]?.ToString()); } catch { }   // 已信任设备顺手记机型名
            // 【诊断】/api/mic/* 的调用都先记一条（放在凭证校验**之前**）：
            // 这样"请求没到"、"凭证被拒"、"不是管理员"三种情况在日志里能一眼分开。
            if (path.StartsWith("/api/mic/code") || path.StartsWith("/api/mic/devices") || path.StartsWith("/api/mic/kick"))
            {
                string credPeek = o["cred"]?.ToString() ?? "";
                bool credOk = credPeek.Length == 0 || VntManager.VerifyCredential(did, credPeek);
                PlayLog.Write("远程", "管理员接口调用", "info",
                    detail: method + " " + path + "　设备=" + (did.Length > 12 ? did.Substring(0, 12) + "…" : did)
                            + "　凭证=" + (credPeek.Length == 0 ? "未带" : (credOk ? "有效" : "无效"))
                            + "　管理员=" + (VntManager.IsAdminDevice(did) ? "是" : "否"));
            }            // 设备凭证校验（类 cookie）：带了就必须对得上；对不上直接拒（防冒用别人身份）
            string cred = o["cred"]?.ToString() ?? "";
            // ---- 设备凭证校验（类 cookie）----
            // 【分级】**写操作 + 管理员接口强制校验**（防冒用别人的设备 ID）；**只读请求**只记不拦。
            // 背景：调试"管理员面板不显示"时曾把这里整体改成"只记不拦" —— 那会让任何知道设备 ID 的人
            // （前提：已在 vnt 网络内）冒充该设备发起写操作，属于**调 bug 时引入的降级**，这里按分级修回。
            bool privileged = IsWritePath(path, method)
                              || path.StartsWith("/api/mic/code")
                              || path.StartsWith("/api/mic/devices")
                              || path.StartsWith("/api/mic/kick");
            string devShort = did.Length > 12 ? did.Substring(0, 12) + "…" : did;
            if (privileged)
            {
                if (cred.Length == 0)
                {
                    CredDenied++;
                    PlayLog.Write("cbs-api", "凭证缺失（已拒绝）", "warn",
                        detail: method + " " + path + "　设备=" + devShort + " 未带设备凭证，已拒绝（请手机重新配对一次）");
                    return "该操作需要设备凭证：请在手机上重新配对一次（输 6 位动态码）";
                }
                if (!VntManager.VerifyCredential(did, cred))
                {
                    CredDenied++;
                    PlayLog.Write("cbs-api", "凭证无效（已拒绝）", "warn",
                        detail: method + " " + path + "　设备=" + devShort + " 凭证校验未通过，已拒绝（请手机重新配对一次）");
                    return "设备凭证无效或已过期：请在手机上重新配对一次";
                }
            }
            else if (cred.Length > 0 && !VntManager.VerifyCredential(did, cred))
            {
                CredDenied++;
                PlayLog.Write("cbs-api", "凭证无效（只读放行）", "warn",
                    detail: "设备 " + devShort + " 的凭证未通过，只读请求仍放行（建议手机重新配对以刷新凭证）");
            }
            if (IsWritePath(path, method) && !VntManager.IsAdminDevice(did))
            {
                AdminDenied++;
                return did.Length == 0
                    ? "改排程/调休需要管理员权限：手机端没有带上设备 ID（请升级手机 App）"
                    : "改排程/调休需要管理员权限：本机（" + did.Substring(0, Math.Min(8, did.Length)) + "…）不在电脑端「vnt 设置 → 管理员设备」里（播控不受影响）";
            }

            var bNode = o["b"];
            if (bNode != null)
            {
                body = bNode.ToJsonString();
                if (Encoding.UTF8.GetByteCount(body) > GateMaxBodyBytes) return "载荷过大（>" + GateMaxBodyBytes + " 字节）";
                string berr = ValidateBody(method, path, bNode);
                if (berr != null) return berr;
            }
            return null;
        }

        /// <summary>安检门信封上限（比协议上限更严：正常界面请求都是几百字节）。</summary>
        public const int GateMaxBytes = 32 * 1024;
        /// <summary>载荷上限。</summary>
        public const int GateMaxBodyBytes = 24 * 1024;

        /// <summary>路径安检：前缀白名单 + 字符集 + 结构，专挡路径穿越与请求行注入。</summary>
        static string ValidatePath(string path, string method)
        {
            if (string.IsNullOrWhiteSpace(path)) return "路径为空";
            if (path.Length > 200) return "路径过长";
            if (!path.StartsWith("/")) return "路径必须以 / 开头";
            if (path.StartsWith("//") || path.Contains("://")) return "不允许绝对地址";
            if (path.Contains('%')) return "路径不允许百分号编码";
            if (path.Contains('\\')) return "路径不允许反斜杠";
            if (path.Contains("..")) return "路径不允许 ..";
            if (path.IndexOf("//", StringComparison.Ordinal) >= 0) return "路径不允许连续斜杠";
            if (path.Contains('?') || path.Contains('#')) return "路径不允许查询串/片段";
            foreach (char ch in path) if (ch < 0x21 || ch > 0x7E) return "路径含控制字符或非 ASCII";
            if (!path.StartsWith("/api/") && !path.StartsWith("/res/")) return "只允许 /api/ 与 /res/ 前缀";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 6) return "路径层级过深";
            foreach (var seg in parts)
                foreach (char ch in seg)
                    if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return "路径段含非法字符：" + seg;
            if (method == "PUT" || method == "DELETE")
            {
                bool okTask = parts.Length == 3 && parts[0] == "api" && parts[1] == "tasks" && parts[2].All(char.IsDigit);
                if (!okTask) return method + " 只允许用于 /api/tasks/{id}";
            }
            return null;
        }

        /// <summary>按端点校验载荷：类型/取值/长度都卡住，防止脏数据写进电脑端配置文件。
        /// 【唯一的一份"写入校验规则"】手机端写配置走它，<see cref="ConfigTransfer"/> 的按项导入与整包还原也走它。
        /// 新增校验请改这里，不要在导入器里另写一套（否则会出现"窗口里校验了、拖入没校验"的漏洞）。</summary>
        public static string ValidateBody(string method, string path, JsonNode body)
        {
            if (path == "/api/player/cmd")
            {
                var o = body as JsonObject;
                if (o == null) return "播控指令必须是 JSON 对象";
                string cmd = o["cmd"]?.ToString() ?? "";
                var allowed = new[] { "play", "pause", "stop", "next", "prev", "play_idx", "seek", "mode", "volume", "add", "remove" };
                if (Array.IndexOf(allowed, cmd) < 0) return "不支持的播控指令：" + cmd;
                if (cmd == "seek" || cmd == "volume" || cmd == "play_idx" || cmd == "remove")
                {
                    if (o["val"] == null || o["val"] is not JsonValue) return cmd + " 的 val 必须是数值";
                    double v; try { v = o["val"].GetValue<double>(); } catch { return cmd + " 的 val 不是数字"; }
                    if (double.IsNaN(v) || double.IsInfinity(v) || v < -1 || v > 1_000_000) return cmd + " 的 val 超出范围";
                }
                return null;
            }
            bool isTask = (path == "/api/tasks" && method == "POST") || (path.StartsWith("/api/tasks/") && method == "PUT");
            if (isTask)
            {
                var t = body as JsonObject;
                if (t == null) return "任务必须是 JSON 对象";
                if (t["start_time"] is JsonValue st)
                {
                    string s = st.ToString();
                    if (s.Length < 5 || s.Length > 8 || s[2] != ':') return "start_time 必须是 HH:mm(:ss)";
                }
                if (t["days"] is JsonArray days)
                {
                    if (days.Count > 7) return "days 最多 7 项";
                    foreach (var d in days)
                    {
                        if (d is not JsonValue) return "days 必须是数字";
                        int dv; try { dv = d.GetValue<int>(); } catch { return "days 必须是整数"; }
                        if (dv < 1 || dv > 7) return "days 取值必须是 1..7";
                    }
                }
                if (t["music_ids"] is JsonArray ids)
                {
                    if (ids.Count > 500) return "music_ids 过多";
                    foreach (var m in ids)
                    {
                        string s = m?.ToString() ?? "";
                        if (s.Length == 0 || s.Length > 32) return "music_ids 元素长度不合法";
                    }
                }
                string[] bounded = { "volume", "duration_m", "duration_s", "folder_play_count", "tiaoxiu_marker" };
                int[] maxv = { 100, 1440, 59, 1000, 10 };
                for (int i = 0; i < bounded.Length; i++)
                {
                    if (t[bounded[i]] == null) continue;
                    if (t[bounded[i]] is not JsonValue) return bounded[i] + " 必须是数字";
                    int v; try { v = t[bounded[i]].GetValue<int>(); } catch { return bounded[i] + " 必须是整数"; }
                    if (v < 0 || v > maxv[i]) return bounded[i] + " 超出范围（0.." + maxv[i] + "）";
                }
                foreach (var key in new[] { "sequence_mode", "folder_mode" })
                {
                    if (t[key] is JsonValue sm)
                    {
                        string s = sm.ToString();
                        if (s != "sequential" && s != "random" && s != "single") return key + " 取值不合法";
                    }
                }
                return null;
            }
            if (path == "/api/tiaoxiu" && method == "POST")
            {
                var arr = body as JsonArray;
                if (arr == null) return "调休必须是 JSON 数组";
                if (arr.Count > 500) return "调休条目过多（>500）";
                foreach (var it in arr)
                {
                    var r = it as JsonObject;
                    if (r == null) return "调休条目必须是 JSON 对象";
                    if (r["_type"]?.ToString() == "subscription") continue;   // 订阅配置原样带回，不做结构校验
                    foreach (var key in new[] { "date", "start_date", "end_date" })
                    {
                        string s = r[key]?.ToString();
                        if (s == null) continue;
                        if (s.Length != 10 || s[4] != '-' || s[7] != '-'
                            || !s.Remove(4, 1).Remove(6, 1).All(char.IsDigit)) return key + " 必须是 YYYY-MM-DD";
                    }
                    string act = r["action"]?.ToString();
                    if (act != null && act != "disable" && act != "enable") return "action 取值不合法";
                    if ((r["note"]?.ToString()?.Length ?? 0) > 60) return "note 过长";
                    if (r["marker"] is JsonValue)
                    {
                        int mk; try { mk = r["marker"].GetValue<int>(); } catch { return "marker 必须是整数"; }
                        if (mk < 0 || mk > 10) return "marker 取值必须是 0..10（0 是「调休管理」专用，编辑器里只给 1-10）";
                    }
                }
                return null;
            }
            return null;
        }

        // ---- 安检门限速：按来源做令牌桶（20/秒，突发 60） ----
        static readonly Dictionary<string, (double Tokens, DateTime At)> _gateBuckets = new Dictionary<string, (double, DateTime)>();
        static readonly object _gateLock = new object();

        static bool RateAllow(string sourceKey)
        {
            const double rate = 20.0, burst = 60.0;
            var now = DateTime.UtcNow;
            lock (_gateLock)
            {
                if (_gateBuckets.Count > 64) _gateBuckets.Clear();
                if (!_gateBuckets.TryGetValue(sourceKey, out var st)) st = (burst, now);
                double tokens = Math.Min(burst, st.Tokens + (now - st.At).TotalSeconds * rate);
                bool ok = tokens >= 1.0;
                if (ok) tokens -= 1.0;
                _gateBuckets[sourceKey] = (tokens, now);
                return ok;
            }
        }

        /// <summary>
        /// 只允许转发到本机回环的 /api/ 与 /res/，且方法受限。
        ///
        /// 方法白名单（手机端要用到的）：
        ///   · GET  —— 读数据（/api/sys、/api/tasks、/api/tiaoxiu、/api/music…）
        ///   · POST —— 新增/动作（/api/player/cmd、/api/tasks、/api/tiaoxiu…）
        ///   · PUT / DELETE —— **只允许 /api/tasks/{id}**（手机端「排程」页的修改与删除）；
        ///     其它路径不给，避免把"整库删除"这类接口暴露出去。
        /// </summary>
        public static string Validate(string method, string path)
        {
            bool write = method == "PUT" || method == "DELETE";
            if (method != "GET" && method != "POST" && !write) return "只允许 GET/POST/PUT/DELETE（收到 " + method + "）";
            if (string.IsNullOrWhiteSpace(path)) return "路径为空";
            if (!path.StartsWith("/")) return "路径必须以 / 开头";
            if (path.StartsWith("//") || path.Contains("://")) return "不允许绝对地址";
            if (path.Contains("..")) return "路径不允许包含 ..";
            if (!path.StartsWith("/api/") && !path.StartsWith("/res/")) return "只允许 /api/ 与 /res/ 前缀";
            if (write)
            {
                // 形如 /api/tasks/12（正好两段：/api/tasks/{id}）
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                bool okTask = parts.Length == 3 && parts[0] == "api" && parts[1] == "tasks"
                              && parts[2].All(char.IsDigit);
                if (!okTask) return method + " 只允许用于 /api/tasks/{id}（收到 " + path + "）";
            }
            return null;
        }

        /// <summary>转发到本机回环的 HTTP 接口（带网页口令的 Bearer 令牌）。</summary>
        static async Task<(int status, string text, string error)> Forward(string method, string path, string body)
        {
            HttpMethod M(string m) => m switch
            {
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                _ => HttpMethod.Get
            };
            string url = "http://127.0.0.1:" + Program.ListenPort + path;
            try
            {
                using var req = new HttpRequestMessage(M(method), url);
                if ((method == "POST" || method == "PUT") && body != null)
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                string tk = await EnsureToken();
                if (!string.IsNullOrEmpty(tk)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tk);
                using var resp = await _http.SendAsync(req);
                int status = (int)resp.StatusCode;
                string text = await resp.Content.ReadAsStringAsync();
                if (status == 401)
                {
                    // 令牌可能过期（有人改了口令/重启过）：清掉重登一次再试
                    lock (_tokenLock) { _token = ""; _tokenForPwd = ""; }
                    using var retry = new HttpRequestMessage(M(method), url);
                    if ((method == "POST" || method == "PUT") && body != null)
                        retry.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    string tk2 = await EnsureToken();
                    if (!string.IsNullOrEmpty(tk2)) retry.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tk2);
                    using var resp2 = await _http.SendAsync(retry);
                    return ((int)resp2.StatusCode, await resp2.Content.ReadAsStringAsync(), null);
                }
                return (status, text, null);
            }
            catch (TaskCanceledException) { return (0, null, "电脑端接口超时（" + TimeoutMs + "ms）"); }
            catch (Exception ex) { return (0, null, "转发到本机接口失败：" + ex.Message); }
        }

        /// <summary>
        /// 按需登录换令牌：设置里配了口令时必须先换令牌（不绕过口令）；没配口令则直接匿名调用。
        /// 登录失败（口令不对）时返回空令牌，最终表现为 401 → 明确的"请在 App 里填看板口令"提示。
        /// </summary>
        static async Task<string> EnsureToken()
        {
            string pwd = ReadWebPassword();
            if (string.IsNullOrEmpty(pwd)) return "";                       // 未设口令：免鉴权
            lock (_tokenLock)
            {
                if (!string.IsNullOrEmpty(_token) && _tokenForPwd == pwd && DateTime.UtcNow - _tokenAt < TimeSpan.FromHours(23))
                    return _token;
            }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + Program.ListenPort + "/api/login");
                req.Content = new StringContent(new JsonObject { ["password"] = pwd }.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req);
                string text = await resp.Content.ReadAsStringAsync();
                var o = JsonNode.Parse(text) as JsonObject;
                string tk = o?["token"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(tk))
                {
                    lock (_tokenLock) { _token = tk; _tokenForPwd = pwd; _tokenAt = DateTime.UtcNow; }
                    return tk;
                }
                PlayLog.Write("cbs-api", "登录失败", "warn", detail: "手机端会话无法换到看板令牌（看板口令可能被改过）：" + text);
            }
            catch (Exception ex)
            {
                PlayLog.Write("cbs-api", "登录异常", "warn", detail: ex.Message);
            }
            return "";
        }

        /// <summary>读取看板口令（setting.jsonl 的 global.password；空 = 未设口令）。</summary>
        static string ReadWebPassword()
        {
            try
            {
                foreach (var item in Program.ReadJsonl("setting.jsonl"))
                    if (item is JsonObject o && o["_type"]?.ToString() == "global")
                        return o["password"]?.ToString() ?? "";
            }
            catch { }
            return "";
        }

        static string Error(int status, string message, string id = "")
            => new JsonObject
            {
                ["v"] = ApiVersion,
                ["id"] = id,
                ["s"] = status,
                ["e"] = message
            }.ToJsonString();

        public static string Summary()
            => $"请求 {Requests}（成功 {Ok} / 失败 {Failed} / 拒绝 {Denied} / 过大 {TooLarge}）"
               + (_token.Length > 0 ? "；已换取看板令牌" : "；看板未设口令（免鉴权）");
    }
}
