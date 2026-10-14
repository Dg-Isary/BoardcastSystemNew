#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 气象预警订阅与播报（占用保留编号 <see cref="MusicId"/> = 1000）：
    ///
    /// 数据来源（接口盒子 apihz.cn，两步走）：
    ///   ① 列表：GET /api/tianqi/tqyb.php?id=&amp;key=&amp;place=&lt;地区名&gt;  → 返回 alarm[]：编号/标题/类型/等级/生效时间
    ///      （全国预警列表接口 dingzhi/tqyjall.php 需要额外购买授权，这里不用它）
    ///   ② 详情：GET /api/tianqi/yj.php?id=&amp;key=&amp;number=&lt;预警编号&gt; → 返回预警全文（msg）
    ///   注意：yj.php 是"按预警编号查详情"，编号里的时间戳是那条预警自己的发布时间，程序拼不出来，
    ///         所以必须先用 ① 拿到编号。
    ///
    /// 播报链路：预警**不自动播**，只做缓存；什么时候播由排程决定——排程里引用编号 1000 的任务到点时，
    ///   由 <see cref="Evaluate"/> 判断：**排程到点那一刻**落在某个预报节点内（节点 = 允许播报的时段）、且预警有效期还没过才播；
    ///   语音用 Windows 内置 SAPI 离线合成（不联网、不依赖在线 TTS），合成出的 WAV 交给现有 ffplay 播放链路。
    /// </summary>
    public static class WeatherWarn
    {
        // ---------------- 常量与默认值 ----------------

        /// <summary>保留音乐编号：排程里引用它 = 播报当前最新气象预警。</summary>
        public const int MusicId = 1000;

        /// <summary>地区名默认**留空**：程序不带任何地区默认值，避免把使用者的所在地写进程序/安装包，也避免配置、日志里泄漏位置。</summary>
        public const string DefaultPlace = "";
        public const string DefaultApiId = "";
        public const string DefaultApiKey = "";
        public const int DefaultIntervalMin = 0;    // 兜底轮询周期；0 = 关闭（默认改为"只在排程前按需拉取"）
        public const int DefaultPrefetchMin = 10;   // 排程前多少分钟拉取（按需拉取）
        public const int DefaultRate = 0;          // SAPI 语速 -10~10，0 为正常
        public const int DefaultRepeat = 2;        // 播报重复次数（一遍太短，学生可能没听到）
        public const bool DefaultGain = true;      // 合成后用 ffmpeg 做响度归一（否则比音乐小声）
        public const string GainFilter = "loudnorm=I=-14:TP=-1.5:LRA=11";   // 目标 -14 LUFS，真峰 -1.5dB，跟音乐一个量级
        public const int FallbackHours = 6;        // 正文里解析不出时长时，按 6 小时估算有效期

        /// <summary>编号占位符：预警详情地址里写 {number}，程序拉取时替换成实际的预警编号。</summary>
        public const string NumberToken = "{number}";

        /// <summary>所在地的 6 位区划代码（默认留空，由使用者自己填）。中央气象台的预警编号前 6 位就是这个码。</summary>
        public const string DefaultAreaCode = "";

        /// <summary>所在地的乡镇/地名关键词（默认留空 = 不做这层过滤），由使用者自己填。</summary>
        public const string DefaultTown = "";

        /// <summary>内置默认的"天气接口"（列表）：中央气象台免 key 接口，返回全国当前生效预警（分页）。</summary>
        public static string DefaultListUrl() => "https://www.nmc.cn/rest/findAlarm?pageNo=1&pageSize=500";   // 全国一次取完（实测 433 条 = 1 页），避免每轮 5 个请求

        /// <summary>内置默认的"预警接口"（详情）：中央气象台的预警详情页（HTML，程序抽正文）。</summary>
        public static string DefaultDetailUrl() => "https://www.nmc.cn/publish/alarm/" + NumberToken + ".html";

        /// <summary>兜底源（接口盒子 apihz，需要 key，二手聚合）：中央气象台取不到时用它；地名取配置里的地区。</summary>
        public static string FallbackListUrl()
        {
            string place = GetPlace();
            if (string.IsNullOrWhiteSpace(place)) return "";   // 没填地区名就不拼地址（程序不内置任何地名）
            return "https://cn.apihz.cn/api/tianqi/tqyb.php?id=" + DefaultApiId
                 + "&key=" + DefaultApiKey + "&place=" + Uri.EscapeDataString(place);
        }
        public static string FallbackDetailUrl()
            => "https://cn.apihz.cn/api/tianqi/yj.php?id=" + DefaultApiId
             + "&key=" + DefaultApiKey + "&number=" + NumberToken;

        /// <summary>判断某个地址是不是"历史版本的 apihz 默认地址"（迁移用）：只按接口路径识别，不涉及任何地名。</summary>
        public static bool IsLegacyApihzListUrl(string url)
            => !string.IsNullOrWhiteSpace(url) && url.IndexOf("cn.apihz.cn/api/tianqi/tqyb.php", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool IsLegacyApihzDetailUrl(string url)
            => !string.IsNullOrWhiteSpace(url) && url.IndexOf("cn.apihz.cn/api/tianqi/yj.php", StringComparison.OrdinalIgnoreCase) >= 0;

        public static string GetAreaCode()
        {
            string s = ReadSetting("area_code")?.ToString();
            return string.IsNullOrWhiteSpace(s) ? DefaultAreaCode : s.Trim();
        }

        /// <summary>原样的乡镇关键词串（界面上显示/编辑用），多个用逗号分隔。</summary>
        public static string GetTown()
        {
            string s = ReadSetting("town")?.ToString();
            return s == null ? DefaultTown : s.Trim();      // 允许用户清空（= 不做乡镇过滤）
        }

        /// <summary>
        /// 乡镇/地名关键词列表：逗号、顿号、分号、空格、换行都能分隔（例如"本站所在镇,邻近镇"）。
        /// 预警经常先点名邻近乡镇，所以支持填多个，命中任意一个就认为影响本地。
        /// </summary>
        public static List<string> GetTowns()
        {
            var list = new List<string>();
            string raw = GetTown();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var part in Regex.Split(raw, @"[,，、;；\s]+"))
            {
                string v = part.Trim();
                if (v.Length > 0 && !list.Contains(v)) list.Add(v);
            }
            return list;
        }

        public static string WarnDir => Path.Combine(Program.AppDataDir, "warn");

        // ---------------- 配置读写 ----------------
        // 气象预警的全部配置（开关、地区、两行接口地址、周期、播报参数、预报节点、预警缓存）
        // 都放在**独立文件 warn.jsonl** 里，不再和 setting.jsonl 混在一起：
        //   第一行 { "_type": "warn", ...各项配置 }，其余每行 { "_type": "warn_cache", ...一条预警缓存 }。

        /// <summary>配置文件：气象预警专用。</summary>
        public const string WarnFile = "warn.jsonl";

        /// <summary>读配置记录（第一行的 _type=warn 那条）。</summary>
        public static JsonObject ReadSettings()
        {
            try
            {
                foreach (var n in Program.ReadJsonl(WarnFile))
                    if (n is JsonObject o && o["_type"]?.ToString() == "warn") return o;
            }
            catch { }
            return null;
        }

        /// <summary>读单个配置项（没有就返回 null）。</summary>
        public static JsonNode ReadSetting(string key) => ReadSettings()?[key];

        /// <summary>改一个配置项并写回 warn.jsonl（没有配置记录就新建一条）。</summary>
        public static void SaveSetting(string key, JsonNode value)
        {
            try
            {
                var all = Program.ReadJsonl(WarnFile);
                JsonObject w = null;
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "warn") { w = o; break; }
                if (w == null) { w = new JsonObject { ["_type"] = "warn" }; all.Insert(0, w); }
                w[key] = value;
                Program.WriteJsonl(WarnFile, all);
            }
            catch (Exception ex) { LogHelper.Log("SaveWarnSetting Failed: " + key, ex); }
        }

        /// <summary>删掉一个配置项。</summary>
        public static void RemoveSetting(string key)
        {
            try
            {
                var all = Program.ReadJsonl(WarnFile);
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "warn") { o.Remove(key); break; }
                Program.WriteJsonl(WarnFile, all);
            }
            catch { }
        }

        /// <summary>
        /// 补齐默认值 + 升级历史地址（只写 warn.jsonl）。返回给迁移日志用的说明。
        /// 隐私：默认值只给"两行接口地址"和播报参数，地区名/区划码/地名筛选一律留空、由使用者自己填。
        /// </summary>
        public static List<string> Migrate()
        {
            var notes = new List<string>();
            var s = ReadSettings();
            void Def(string key, JsonNode v, string note)
            {
                if (s?[key] != null) return;
                SaveSetting(key, v);
                if (!string.IsNullOrEmpty(note)) notes.Add(note);
            }
            Def("enabled", JsonValue.Create(true), "气象预警订阅=启用");
            Def("interval_min", JsonValue.Create(DefaultIntervalMin), "预警兜底轮询=" + (DefaultIntervalMin == 0 ? "关闭（只在排程前按需拉取）" : DefaultIntervalMin + " 分钟"));
            Def("prefetch_min", JsonValue.Create(DefaultPrefetchMin), "预警按需拉取=排程前 " + DefaultPrefetchMin + " 分钟");
            Def("rate", JsonValue.Create(DefaultRate), null);
            Def("voice", JsonValue.Create(""), "预警音色=自动（优先中文女声）");
            Def("repeat", JsonValue.Create(DefaultRepeat), "预警播报重复=" + DefaultRepeat + " 遍");
            Def("gain", JsonValue.Create(DefaultGain), "预警播报响度归一=开（ffmpeg）");
            Def("follow_tiaoxiu", JsonValue.Create(true), "预报节点跟随调休规则=开");
            Def("windows", DefaultWindows(), "预报节点=周一~周五 07:30-12:00、13:30-18:00");
            Def("list_url", JsonValue.Create(DefaultListUrl()), "预警-天气接口地址=内置默认（可改）");
            Def("detail_url", JsonValue.Create(DefaultDetailUrl()), "预警-预警接口地址=内置默认（可改）");

            // 历史地址升级：apihz 主源 → 中央气象台；pageSize 100 → 500（全国一次取完）
            string listUrl = s?["list_url"]?.ToString() ?? "";
            if (IsLegacyApihzListUrl(listUrl))
            { SaveSetting("list_url", JsonValue.Create(DefaultListUrl())); notes.Add("天气接口换成中央气象台 nmc.cn（免 key）"); }
            else if (listUrl.IndexOf("nmc.cn/rest/findAlarm", StringComparison.OrdinalIgnoreCase) >= 0
                     && listUrl.IndexOf("pageSize=100", StringComparison.OrdinalIgnoreCase) >= 0)
            { SaveSetting("list_url", JsonValue.Create(DefaultListUrl())); notes.Add("预警天气接口 pageSize 100→500（全国一次取完，每轮请求 5→1）"); }
            if (IsLegacyApihzDetailUrl(s?["detail_url"]?.ToString()))
            { SaveSetting("detail_url", JsonValue.Create(DefaultDetailUrl())); notes.Add("预警接口换成中央气象台预警详情页"); }
            if (s?["interval_min"]?.GetValue<int>() == 30)
            { SaveSetting("interval_min", JsonValue.Create(0)); notes.Add("预警兜底轮询 30 分钟→关闭（改为排程前按需拉取，请求数大幅减少）"); }
            return notes;
        }

        /// <summary>迁移用：把旧版存在 setting.jsonl 里的预警缓存导入 warn.jsonl。</summary>
        public static void ImportLegacyCaches(JsonArray caches)
        {
            if (caches == null) return;
            var list = GetCaches();
            foreach (var n in caches)
            {
                if (n is not JsonObject o) continue;
                string id = o["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (list.Any(x => x["id"]?.ToString() == id)) continue;
                var c = o.DeepClone() as JsonObject;
                c["_type"] = "warn_cache";
                list.Add(c);
            }
            SaveCaches(list);
        }

        public static bool GetEnabled()
        {
            var g = ReadSettings();
            return g?["enabled"] == null ? true : (g["enabled"].GetValue<bool>());
        }
        public static void SetEnabled(bool on) => SaveSetting("enabled", JsonValue.Create(on));

        public static string GetPlace()
        {
            string s = ReadSetting("place")?.ToString();
            return string.IsNullOrWhiteSpace(s) ? DefaultPlace : s.Trim();
        }

        /// <summary>用户填的"天气接口"（列表）地址；没填就用内置默认。</summary>
        public static string GetListUrl()
        {
            string s = ReadSetting("list_url")?.ToString();
            return string.IsNullOrWhiteSpace(s) ? DefaultListUrl() : s.Trim();
        }

        /// <summary>用户填的"预警接口"（详情）地址；没填就用内置默认。</summary>
        public static string GetDetailUrl()
        {
            string s = ReadSetting("detail_url")?.ToString();
            return string.IsNullOrWhiteSpace(s) ? DefaultDetailUrl() : s.Trim();
        }

        /// <summary>把预警编号填进详情地址：有 {number} 就替换，没有就补一个 number 参数。</summary>
        public static string BuildDetailUrl(string detailUrl, string number)
        {
            string n = Uri.EscapeDataString(number ?? "");
            if (string.IsNullOrWhiteSpace(detailUrl)) detailUrl = DefaultDetailUrl();
            if (detailUrl.IndexOf(NumberToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return Regex.Replace(detailUrl, Regex.Escape(NumberToken), n, RegexOptions.IgnoreCase);
            return detailUrl + (detailUrl.Contains("?") ? "&" : "?") + "number=" + n;
        }

        /// <summary>列表地址里没写 place 参数时，按配置的地区补上。</summary>
        public static string BuildListUrl()
        {
            string url = GetListUrl();
            if (url.IndexOf("place=", StringComparison.OrdinalIgnoreCase) >= 0) return url;
            return url + (url.Contains("?") ? "&" : "?") + "place=" + Uri.EscapeDataString(GetPlace());
        }
        /// <summary>兜底轮询周期（分钟）。0 = 关闭，只用"排程前按需拉取"。</summary>
        public static int GetIntervalMin()
        {
            int v = ReadSetting("interval_min")?.GetValue<int>() ?? DefaultIntervalMin;
            return v < 0 ? DefaultIntervalMin : v;
        }

        /// <summary>提前多少分钟拉取（默认 10）：在引用编号 1000 的排程时刻之前 N 分钟拉一次。</summary>
        public static int GetPrefetchMin()
        {
            int v = ReadSetting("prefetch_min")?.GetValue<int>() ?? DefaultPrefetchMin;
            return Math.Max(0, Math.Min(120, v));
        }

        /// <summary>任务是否引用了保留编号 1000（气象预警播报）。</summary>
        public static bool TaskUsesWarn(JsonObject task)
        {
            var ids = task?["music_ids"] as JsonArray;
            if (ids == null) return false;
            foreach (var x in ids)
                if (string.Equals((x?.ToString() ?? "").Trim(), MusicId.ToString(), StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 现在是否落在"排程点前 N 分钟"的拉取窗口里：窗口 = [排程时刻 - N 分钟, 排程时刻)。
        /// 按需拉取用：只在快用到预警时才发请求。
        /// </summary>
        public static bool InPrefetchWindow(DateTime now, DateTime taskAt, int leadMin)
        {
            DateTime from = taskAt.AddMinutes(-Math.Max(0, leadMin));
            return now >= from && now < taskAt;
        }

        public static int GetRate()
        {
            int v = ReadSetting("rate")?.GetValue<int>() ?? DefaultRate;
            return Math.Max(-10, Math.Min(10, v));
        }

        /// <summary>播报重复次数（1~5）：太短学生听不到，默认念 2 遍。</summary>
        public static int GetRepeat()
        {
            int v = ReadSetting("repeat")?.GetValue<int>() ?? DefaultRepeat;
            return Math.Max(1, Math.Min(5, v));
        }

        /// <summary>是否用 ffmpeg 做响度归一（默认开：TTS 原声比音乐小声）。</summary>
        public static bool GetGain()
        {
            var n = ReadSetting("gain");
            return n == null ? DefaultGain : n.GetValue<bool>();
        }

        /// <summary>音色（SAPI 语音描述），空 = 自动优先中文女声。</summary>
        public static string GetVoice()
        {
            string s = ReadSetting("voice")?.ToString();
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        /// <summary>预报节点是否跟随调休规则（默认开）：放假日不播报，补班日按工作日节点播报。</summary>
        public static bool GetFollowTiaoxiu()
        {
            var n = ReadSetting("follow_tiaoxiu");
            return n == null ? true : n.GetValue<bool>();
        }

        /// <summary>
        /// 某天的调休判定（与排程的 ShouldPlayToday 同一套语义：分标记规则优先于全局规则，同优先级后写入覆盖先写入）。
        /// 返回 true = 调休上班/上课日，false = 放假日，null = 当天没有任何调休规则。
        /// </summary>
        public static bool? TiaoxiuVerdict(DateTime day, int marker = 0)
        {
            try
            {
                string date = day.ToString("yyyy-MM-dd");
                var matches = new List<Tuple<int, int, string>>();   // 优先级(0=分标记,1=全局) / 顺序 / 动作
                int order = 0;
                foreach (var n in Program.ReadSettingMerged())
                {
                    if (n is not JsonObject o || o["_type"]?.ToString() != "tiaoxiu") continue;
                    string s = o["start_date"]?.ToString() ?? o["date"]?.ToString();
                    string e = o["end_date"]?.ToString() ?? s;
                    if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(e)) continue;
                    if (string.Compare(date, s) < 0 || string.Compare(date, e) > 0) continue;
                    int mk = 0; if (o["marker"] != null) int.TryParse(o["marker"].ToString(), out mk);
                    string act = o["action"]?.ToString();
                    if (mk == 0) matches.Add(Tuple.Create(1, order++, act));
                    else if (mk == marker) matches.Add(Tuple.Create(0, order++, act));
                }
                if (matches.Count == 0) return null;
                matches.Sort((a, b) =>
                {
                    if (a.Item1 != b.Item1) return a.Item1.CompareTo(b.Item1);
                    return b.Item2.CompareTo(a.Item2);
                });
                return matches[0].Item3 == "enable";
            }
            catch { return null; }
        }

        public static string GetLastOk() => ReadSetting("last_ok")?.ToString() ?? "";
        public static string GetLastError() => ReadSetting("last_error")?.ToString() ?? "";
        public static string GetLastTry() => ReadSetting("last_try")?.ToString() ?? "";

        /// <summary>上次拉取的时间（跨重启有效）：启动时用它判断"距上次是否已超过配置周期"，避免每次重启都拉一遍。</summary>
        public static bool TryGetLastTry(out DateTime t)
        {
            t = DateTime.MinValue;
            string s = GetLastTry();
            return !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out t);
        }

        /// <summary>默认预报节点：周一~周五 07:30-12:00、13:30-18:00（与用户给的例子一致）。</summary>
        public static JsonArray DefaultWindows() => new JsonArray
        {
            new JsonObject { ["days"] = new JsonArray { 1, 2, 3, 4, 5 }, ["start"] = "07:30", ["end"] = "12:00" },
            new JsonObject { ["days"] = new JsonArray { 1, 2, 3, 4, 5 }, ["start"] = "13:30", ["end"] = "18:00" }
        };

        public static JsonArray GetWindows()
        {
            var w = ReadSetting("windows") as JsonArray;
            return w ?? DefaultWindows();
        }
        public static void SaveWindows(JsonArray windows) => SaveSetting("windows", windows ?? new JsonArray());

        /// <summary>缓存的"最新一条预警"（只用一条的地方，如自检项、状态行）。</summary>
        public static JsonObject GetCache()
        {
            var list = GetCaches();
            if (list.Count == 0) return null;
            return list.OrderByDescending(c => StampOf(c["id"]?.ToString() ?? "")).First();
        }

        /// <summary>当前缓存的全部预警（可能同时有雷电、暴雨等多条；每条是 warn.jsonl 里的一行 _type=warn_cache）。</summary>
        public static List<JsonObject> GetCaches()
        {
            var list = new List<JsonObject>();
            try
            {
                foreach (var n in Program.ReadJsonl(WarnFile))
                    if (n is JsonObject o && o["_type"]?.ToString() == "warn_cache") list.Add(o.DeepClone() as JsonObject);
            }
            catch { }
            return list;
        }

        /// <summary>写入全部预警缓存（只替换 _type=warn_cache 的行，配置行原样保留）。</summary>
        public static void SaveCaches(List<JsonObject> list)
        {
            try
            {
                var keep = new JsonArray();
                foreach (var n in Program.ReadJsonl(WarnFile))
                    if (n is JsonObject o && o["_type"]?.ToString() == "warn_cache") continue;
                    else keep.Add(n.DeepClone());
                foreach (var o in list ?? new List<JsonObject>())
                {
                    if (o == null) continue;
                    // 必须 DeepClone：传进来的对象可能已经挂在别的 JsonArray 上（例如刚从配置里读出来的），
                    // 直接 Add 会抛 "The node already has a parent"（多条缓存改成列表后踩过）。
                    var c = o.DeepClone() as JsonObject;
                    c["_type"] = "warn_cache";
                    keep.Add(c);
                }
                Program.WriteJsonl(WarnFile, keep);
            }
            catch (Exception ex) { LogHelper.Log("SaveWarnCaches Failed", ex); }
        }

        /// <summary>预警等级排序用的权值：红 > 橙 > 黄 > 蓝。</summary>
        public static int LevelRank(string level)
        {
            switch ((level ?? "").Trim())
            {
                case "红色": return 4;
                case "橙色": return 3;
                case "黄色": return 2;
                case "蓝色": return 1;
                default: return 0;
            }
        }

        // ---------------- 时间解析（纯函数，自检覆盖） ----------------

        static readonly Regex RePublish = new Regex(@"(\d{4})年\s*(\d{1,2})月\s*(\d{1,2})日\s*(\d{1,2})时\s*(\d{1,2})分", RegexOptions.Compiled);
        static readonly Regex RePublishNoYear = new Regex(@"(\d{1,2})月\s*(\d{1,2})日\s*(\d{1,2})时\s*(\d{1,2})分", RegexOptions.Compiled);
        static readonly Regex ReHoursA = new Regex(@"未来\s*(\d{1,3})\s*个?\s*小时", RegexOptions.Compiled);
        static readonly Regex ReHoursB = new Regex(@"(\d{1,3})\s*个?\s*小时内", RegexOptions.Compiled);
        static readonly Regex ReDaysA = new Regex(@"未来\s*(\d{1,3})\s*个?\s*天", RegexOptions.Compiled);
        static readonly Regex ReDaysB = new Regex(@"(\d{1,3})\s*个?\s*天内", RegexOptions.Compiled);
        static readonly Regex ReRangeFull = new Regex(@"(\d{1,2})月\s*(\d{1,2})日\s*(\d{1,2})时(?:\s*(\d{1,2})分)?\s*(?:至|到|~|-|—)\s*(?:(\d{1,2})月)?\s*(?:(\d{1,2})日)?\s*(\d{1,2})时(?:\s*(\d{1,2})分)?", RegexOptions.Compiled);
        static readonly Regex ReRangeDay = new Regex(@"(\d{1,2})日\s*(\d{1,2})时(?:\s*(\d{1,2})分)?\s*(?:至|到|~|-|—)\s*(?:(\d{1,2})日)?\s*(\d{1,2})时(?:\s*(\d{1,2})分)?", RegexOptions.Compiled);
        static readonly Regex ReVague = new Regex(@"今(?:天|日)\s*(白天|上午|中午|下午|傍晚|夜间|夜里)", RegexOptions.Compiled);

        /// <summary>解析列表接口给的"预警生效时间"，如 2026/09/23 14:27。</summary>
        public static bool TryParseEffective(string s, out DateTime t)
        {
            t = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string v = s.Trim().Replace('/', '-').Replace('.', '-');
            string[] fmts = { "yyyy-M-d H:m:s", "yyyy-M-d H:m", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };
            return DateTime.TryParseExact(v, fmts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out t);
        }

        /// <summary>从预警正文里解析"发布时刻"（如"2026年9月23日14时27分发布"），作为生效时间的兜底。</summary>
        public static bool TryParsePublishFromText(string text, DateTime yearHint, out DateTime t)
        {
            t = DateTime.MinValue;
            if (string.IsNullOrEmpty(text)) return false;
            var m = RePublish.Match(text);
            if (m.Success) return TryMake(yearHint.Year, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value, out t);
            var m2 = RePublishNoYear.Match(text);
            if (m2.Success) return TryMake(yearHint.Year, null, m2.Groups[1].Value, m2.Groups[2].Value, m2.Groups[3].Value, m2.Groups[4].Value, out t);
            return false;
        }

        static bool TryMake(int year, string y, string mo, string d, string h, string mi, out DateTime t)
        {
            t = DateTime.MinValue;
            try
            {
                if (!string.IsNullOrEmpty(y)) year = int.Parse(y);
                return DateTime.TryParse($"{year}-{int.Parse(mo)}-{int.Parse(d)} {int.Parse(h)}:{int.Parse(mi)}:00", out t);
            }
            catch { return false; }
        }

        /// <summary>
        /// 从正文算出"预计时间"（有效期最晚结束时刻）：把正文里所有能识别的时间表达式都算一遍取最晚的一个。
        /// 一条预警里可能出现多个时间（"预计未来6小时"、"6小时内"、"9月23日20时至24日08时"等），
        /// 取最晚的那个作为有效期上限，全部识别结果写进执行日志。
        /// estimated=true 表示正文里没写清时长，按默认 6 小时估算的。
        /// </summary>
        public static DateTime ParseExpect(string text, DateTime effective, out bool estimated, out List<string> notes)
        {
            return ParseExpect(text, effective, out estimated, out notes, out _);
        }

        /// <summary>
        /// 同上，另外给出"时长说法"（<paramref name="phrase"/>，例如"未来6小时"），播报文案直接念它。
        /// </summary>
        public static DateTime ParseExpect(string text, DateTime effective, out bool estimated, out List<string> notes, out string phrase)
        {
            var list = new List<string>();
            estimated = false;
            phrase = null;
            string bestPhrase = null;
            DateTime best = DateTime.MinValue;
            void Take(DateTime cand, string why, string say)
            {
                if (cand <= effective) return;
                list.Add(why + " → " + cand.ToString("yyyy-MM-dd HH:mm"));
                if (cand > best) { best = cand; bestPhrase = say; }
            }

            if (!string.IsNullOrEmpty(text))
            {
                foreach (Match m in ReHoursA.Matches(text))
                    if (int.TryParse(m.Groups[1].Value, out int h) && h > 0 && h <= 240) Take(effective.AddHours(h), $"未来 {h} 小时", $"未来{h}小时");
                foreach (Match m in ReHoursB.Matches(text))
                    if (int.TryParse(m.Groups[1].Value, out int h2) && h2 > 0 && h2 <= 240) Take(effective.AddHours(h2), $"{h2} 小时内", $"{h2}小时内");
                foreach (Match m in ReDaysA.Matches(text))
                    if (int.TryParse(m.Groups[1].Value, out int d) && d > 0 && d <= 30) Take(effective.AddDays(d), $"未来 {d} 天", $"未来{d}天");
                foreach (Match m in ReDaysB.Matches(text))
                    if (int.TryParse(m.Groups[1].Value, out int d2) && d2 > 0 && d2 <= 30) Take(effective.AddDays(d2), $"{d2} 天内", $"{d2}天内");

                // 绝对区间："9月23日20时至24日08时" / "23日20时至24日08时" / "24日10时42分到19时"
                foreach (Match m in ReRangeFull.Matches(text))
                {
                    try
                    {
                        int mo = int.Parse(m.Groups[1].Value), dd = int.Parse(m.Groups[2].Value);
                        string emo = m.Groups[5].Success ? m.Groups[5].Value : m.Groups[1].Value;
                        string edd = m.Groups[6].Success ? m.Groups[6].Value : m.Groups[2].Value;
                        int eh = int.Parse(m.Groups[7].Value);
                        int emi = m.Groups[8].Success ? int.Parse(m.Groups[8].Value) : 0;
                        var end = new DateTime(effective.Year, int.Parse(emo), int.Parse(edd), eh, Math.Min(59, emi), 0);
                        if (end < effective) end = end.AddYears(1);
                        Take(end, "区间 " + m.Value, m.Value);
                    }
                    catch { }
                }
                foreach (Match m in ReRangeDay.Matches(text))
                {
                    try
                    {
                        int sd = int.Parse(m.Groups[1].Value);
                        string edd = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[1].Value;
                        int eh = int.Parse(m.Groups[5].Value);
                        int emi = m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0;
                        var end = new DateTime(effective.Year, effective.Month, int.Parse(edd), eh, Math.Min(59, emi), 0);
                        if (int.Parse(edd) < sd) end = end.AddMonths(1);
                        if (end < effective) end = end.AddMonths(1);
                        Take(end, "区间 " + m.Value, m.Value);
                    }
                    catch { }
                }

                // 模糊时段（今天白天/夜间…）：只给到当天的一个大致时点
                foreach (Match m in ReVague.Matches(text))
                {
                    string w = m.Groups[1].Value;
                    int hour = w == "上午" ? 12 : w == "中午" ? 13 : w == "下午" ? 18 : w == "傍晚" ? 19 : w == "白天" ? 18 : 23;
                    Take(effective.Date.AddHours(hour), "模糊时段 " + w, "今天" + w);
                }
            }

            if (best == DateTime.MinValue)
            {
                estimated = true;
                best = effective.AddHours(FallbackHours);
                bestPhrase = $"未来{FallbackHours}小时";
                list.Add($"正文里没有可识别的时长，按默认 {FallbackHours} 小时估算");
            }
            phrase = bestPhrase;
            notes = list;
            return best;
        }

        /// <summary>规范化后的"预计时间"：优先用缓存里已经算好的 expect，没有就现算。</summary>
        public static DateTime GetExpect(JsonObject cache, out bool estimated)
        {
            estimated = false;
            if (cache == null) return DateTime.MinValue;
            if (!TryParseEffective(cache["effective"]?.ToString(), out DateTime eff)) return DateTime.MinValue;
            if (cache["expect"] != null && DateTime.TryParse(cache["expect"].ToString(), out DateTime exp) && exp > eff)
            {
                estimated = cache["estimated"]?.GetValue<bool>() ?? false;
                return exp;
            }
            return ParseExpect(cache["text"]?.ToString(), eff, out estimated, out _);
        }

        // ---------------- 预报节点判断 ----------------

        static readonly Regex ReTownName = new Regex(@"[\u4e00-\u9fa5]{2,6}(?:镇|乡|街道)", RegexOptions.Compiled);

        /// <summary>
        /// 乡镇精度判断（气象预警是县级发布的，没有乡镇级独立记录，所以只能看正文点名了哪些乡镇）：
        ///   · 正文/标题里出现任意一个配置的地名关键词 → 认为影响本地（支持填邻近乡镇）；
        ///   · 正文点名了别的乡镇、且一个本地关键词都没出现 → 跳过（返回原因）；
        ///   · 正文没有点名具体乡镇（全域预警）→ 认为影响本地。
        /// 返回空字符串表示"可以作为本地预警"，非空表示跳过原因。
        /// </summary>
        public static string TownScopeReason(JsonObject cache)
        {
            var towns = GetTowns();
            if (towns.Count == 0) return "";
            string text = (cache?["text"]?.ToString() ?? "") + " " + (cache?["title"]?.ToString() ?? "");
            foreach (var t in towns)
                if (text.IndexOf(t, StringComparison.Ordinal) >= 0) return "";   // 命中任意一个本地地名

            var names = new List<string>();
            foreach (Match m in ReTownName.Matches(text))
            {
                string v = m.Value;
                if (v.StartsWith("该") || v.StartsWith("各") || v.StartsWith("全") || v.StartsWith("本") || v.StartsWith("上述")) continue;
                if (!names.Contains(v)) names.Add(v);
            }
            if (names.Count >= 2)
                return $"预警点名的是 {string.Join("、", names.Take(6))} 等 {names.Count} 个乡镇，没有提到 {string.Join("/", towns)}，本次不播报";
            return "";
        }

        /// <summary>中央气象台详情页是 HTML：剥掉脚本样式和标签，取出含"预警信号"的那一行正文。</summary>
        public static string ExtractHtmlWarningBody(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            string t = Regex.Replace(html, "(?s)<script.*?</script>", " ");
            t = Regex.Replace(t, "(?s)<style.*?</style>", " ");
            t = Regex.Replace(t, "<[^>]+>", "\n");
            var lines = t.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            foreach (var line in lines)
                if (line.Length >= 25 && line.Contains("预警信号") && (line.Contains("发布") || line.Contains("气象台")))
                    return line;
            foreach (var line in lines)
                if (line.Length >= 40 && (line.Contains("预计") || line.Contains("小时") || line.Contains("防范")))
                    return line;
            return null;
        }

        /// <summary>星期几按程序里统一的约定：1=周一 … 7=周日。</summary>
        public static int DayNo(DateTime t) { int d = (int)t.DayOfWeek; return d == 0 ? 7 : d; }

        /// <summary>某个时刻是否落在预报节点内；命中时 hit 返回节点描述（如"周一~周五 13:30-18:00"）。</summary>
        public static bool InWindows(DateTime t, JsonArray windows, out string hit) => InWindows(t, windows, false, out hit);

        /// <summary>
        /// <paramref name="asWorkday"/>=true 表示"今天算工作日"（补班日）：忽略实际星期几，
        /// 只要窗口是按周一~周五设的就参与匹配。
        /// </summary>
        public static bool InWindows(DateTime t, JsonArray windows, bool asWorkday, out string hit)
        {
            hit = null;
            if (windows == null) return false;
            int dow = DayNo(t);
            foreach (var n in windows)
            {
                if (n is not JsonObject w) continue;
                var days = w["days"] as JsonArray;
                if (days != null && days.Count > 0)
                {
                    bool dayHit = false;
                    foreach (var d in days)
                    {
                        int dv = 0; try { dv = d.GetValue<int>(); } catch { }
                        if (asWorkday ? (dv >= 1 && dv <= 5) : (dv == dow)) { dayHit = true; break; }
                    }
                    if (!dayHit) continue;
                }
                if (!TryMinute(w["start"]?.ToString(), out int sm)) continue;
                if (!TryMinute(w["end"]?.ToString(), out int em)) continue;
                int now = t.Hour * 60 + t.Minute;
                bool ok = em >= sm ? (now >= sm && now <= em) : (now >= sm || now <= em);   // 支持跨零点
                if (ok)
                {
                    hit = DayText(days) + " " + (w["start"]?.ToString() ?? "") + "-" + (w["end"]?.ToString() ?? "");
                    return true;
                }
            }
            return false;
        }

        static bool TryMinute(string hhmm, out int min)
        {
            min = 0;
            if (string.IsNullOrWhiteSpace(hhmm)) return false;
            var m = Regex.Match(hhmm.Trim(), @"^(\d{1,2}):(\d{2})$");
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            if (h > 23 || mi > 59) return false;
            min = h * 60 + mi;
            return true;
        }

        public static string DayText(JsonArray days)
        {
            if (days == null || days.Count == 0) return "每天";
            var set = new List<int>();
            foreach (var d in days) { try { set.Add(d.GetValue<int>()); } catch { } }
            set = set.Distinct().OrderBy(x => x).ToList();
            if (set.Count == 7) return "每天";
            if (set.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) return "周一~周五";
            if (set.SequenceEqual(new[] { 6, 7 })) return "周六~周日";
            string[] nm = { "", "一", "二", "三", "四", "五", "六", "日" };
            return "周" + string.Join("、", set.Select(x => x >= 1 && x <= 7 ? nm[x] : "?"));
        }

        // ---------------- 播报判断（排程到点时调用） ----------------

        /// <summary>
        /// 排程到点时的判断：
        ///   · 预警有效期已过（超过预计结束时间）→ 不播；
        ///   · **当前时刻（排程到点这一刻）** 落在某个预报节点内 → 播；否则不播。
        /// 说明：预报节点 = "允许播报的时段"。判断只看"现在几点"，**不看预警是什么时候发布的**——
        /// 早上 6 点发布、有效期到中午的预警，只要 09:30 有排程且到点在节点内，就播。
        /// （历史上试过用"预计结束时间"或"生效时间"比节点，都会出现"正在下雨却不播/没下雨反而播"的反常。）
        /// 返回 true 表示"可以播"。
        /// </summary>
        public static bool Evaluate(JsonObject cache, JsonArray windows, DateTime now, out string reason, out DateTime expect)
        {
            expect = DateTime.MinValue;
            if (cache == null || string.IsNullOrWhiteSpace(cache["id"]?.ToString()))
            {
                reason = "还没有拉到任何气象预警，编号 1000 本次不播报";
                return false;
            }
            if (!TryParseEffective(cache["effective"]?.ToString(), out DateTime eff))
            {
                reason = "预警缓存里没有可用的生效时间，本次不播报";
                return false;
            }
            expect = GetExpect(cache, out bool estimated);
            if (expect == DateTime.MinValue) expect = eff;

            if (cache["resolved"]?.GetValue<bool>() ?? false)
            {
                reason = $"预警已解除/失效（{cache["resolved_at"]} 起它已不在当前生效列表里），不再播报";
                return false;
            }
            if (expect < now)
            {
                reason = $"预警（{cache["signaltype"]}{cache["signallevel"]}）有效期已过（预计结束 {expect:yyyy-MM-dd HH:mm}），不再播报";
                return false;
            }
            string townReason = TownScopeReason(cache);
            if (!string.IsNullOrEmpty(townReason)) { reason = townReason; return false; }

            // 跟随调休规则（默认开）：放假日不播；补班日按"周一~周五"的节点播（否则补班日会被节点跳过）
            bool asWorkday = false;
            string tiaoxiuNote = "";
            if (GetFollowTiaoxiu())
            {
                bool? verdict = TiaoxiuVerdict(now.Date, 0);
                if (verdict == false)
                {
                    reason = "今天是调休放假日（按全局调休规则），预警不播报";
                    return false;
                }
                if (verdict == true && DayNo(now) >= 6)
                {
                    asWorkday = true;
                    tiaoxiuNote = "（今天调休上班，按工作日节点判断）";
                }
            }

            if (!InWindows(now, windows, asWorkday, out string hit))
            {
                reason = $"当前时刻 {now:MM-dd HH:mm} 不在任何预报节点内，本次不播报"
                         + $"（预警生效 {eff:MM-dd HH:mm}，有效期到 {expect:MM-dd HH:mm}）" + tiaoxiuNote;
                return false;
            }
            reason = $"当前时刻 {now:MM-dd HH:mm} 落在预报节点 {hit} 内，播报" + tiaoxiuNote
                     + $"（预警生效 {eff:MM-dd HH:mm}，有效期到 {expect:MM-dd HH:mm}" + (estimated ? "，时长按默认 6 小时估算" : "") + "）";
            return true;
        }

        // ---------------- 播报文案 ----------------

        static string SpanText(DateTime start, DateTime end)
        {
            return start.Date == end.Date
                ? $"{start.Month}月{start.Day}日{start.Hour}点{start.Minute}分至{end.Hour}点{end.Minute}分"
                : $"{start.Month}月{start.Day}日{start.Hour}点{start.Minute}分至{end.Month}月{end.Day}日{end.Hour}点{end.Minute}分";
        }

        /// <summary>把发布时刻写成口语化的"下午2点27分"（不用"2:27"，冒号在中文 TTS 里可能被读成"比"）。</summary>
        public static string TimeWords(DateTime t)
        {
            int h = t.Hour;
            string period = h < 5 ? "凌晨" : h < 8 ? "早上" : h < 12 ? "上午" : h < 13 ? "中午" : h < 18 ? "下午" : "晚上";
            int disp = h > 12 ? h - 12 : h;
            return period + disp + "点" + t.Minute.ToString("00") + "分";
        }

        /// <summary>数字转中文（播报时长用："未来6小时" → "未来六小时"）。</summary>
        public static string ChineseNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s, @"\d+", m =>
            {
                if (!int.TryParse(m.Value, out int n)) return m.Value;
                if (n <= 0 || n > 999) return m.Value;
                string[] d = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
                if (n < 10) return d[n];
                if (n < 20) return n == 10 ? "十" : "十" + d[n % 10];
                if (n < 100)
                {
                    int t = n / 10, o = n % 10;
                    return d[t] + "十" + (o > 0 ? d[o] : "");
                }
                int hh = n / 100, r = n % 100;
                if (r == 0) return d[hh] + "百";
                if (r < 10) return d[hh] + "百零" + d[r];
                return d[hh] + "百" + ChineseNumber(r.ToString());
            });
        }

        /// <summary>播报里用的发布单位：优先预警标题里的官方台名（如"××县气象台"），取不到退回"地区名+气象台"。</summary>
        public static string StationOf(JsonObject cache)
        {
            var m = Regex.Match(cache?["title"]?.ToString() ?? "", @"([\u4e00-\u9fa5]{2,8})气象台");
            if (m.Success) return m.Groups[1].Value + "气象台";
            string name = cache?["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim() + "气象台";
            return "";      // 都不知道发布单位就不念台名（程序不内置任何地区名）
        }

        /// <summary>播报用的"预计时长说法"：优先用缓存里存好的（抓取时按正文算出来的），没有就现算。</summary>
        public static string GetDurationPhrase(JsonObject cache)
        {
            if (cache == null) return null;
            string p = cache["duration"]?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) return p;
            if (!TryParseEffective(cache["effective"]?.ToString(), out DateTime eff)) return null;
            ParseExpect(cache["text"]?.ToString(), eff, out _, out _, out string phrase);
            return phrase;
        }

        /// <summary>
        /// 播报文案（精简一句）：台名 + 发布时刻 + 类型等级 + 预计时段 + 注意防范。
        /// 例："××气象台下午2点27分发布雷电黄色预警，预计未来六小时影响本地，注意防范。"
        /// </summary>
        public static string BuildSpeech(JsonObject cache)
        {
            if (cache == null) return "";
            string station = StationOf(cache);
            string type = cache["signaltype"]?.ToString() ?? "";
            string level = cache["signallevel"]?.ToString() ?? "";

            TryParseEffective(cache["effective"]?.ToString(), out DateTime eff);
            DateTime expect = GetExpect(cache, out _);
            if (expect == DateTime.MinValue) expect = eff;

            string when = eff != DateTime.MinValue ? TimeWords(eff) + "发布" : "发布";
            string span = GetDurationPhrase(cache);
            if (string.IsNullOrWhiteSpace(span)) span = SpanText(eff, expect);   // 兜底：念具体起止时刻
            span = ChineseNumber(span);
            return station + when + type + level + "预警，预计" + span + "影响本地，注意防范。";
        }

        // ---------------- 离线语音合成（Windows 内置 SAPI，不联网） ----------------

        /// <summary>系统里全部可用的离线语音（界面上供挑音色）。</summary>
        public static List<string> InstalledVoices()
        {
            var list = new List<string>();
            try
            {
                var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t == null) return list;
                dynamic voice = Activator.CreateInstance(t);
                dynamic voices = voice.GetVoices();
                int n = voices.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic v = voices.Item(i);
                    string desc = null;
                    try { desc = v.GetDescription(); } catch { }
                    if (!string.IsNullOrEmpty(desc) && !list.Contains(desc)) list.Add(desc);
                }
            }
            catch { }
            return list;
        }

        /// <summary>系统里可用的中文语音名（没有中文语音时返回空列表，界面会给提示）。</summary>
        public static List<string> InstalledChineseVoices()
        {
            return InstalledVoices().Where(IsChineseVoice).ToList();
        }

        static bool IsChineseVoice(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return false;
            return desc.Contains("Chinese") || desc.Contains("中文") || desc.Contains("Huihui")
                || desc.Contains("Yaoyao") || desc.Contains("Kangkang") || desc.Contains("Xiaoxiao")
                || desc.Contains("Yunxi") || desc.Contains("晓晓") || desc.Contains("云希");
        }

        /// <summary>把文字合成为 WAV（离线）。voiceName 为空时自动挑中文语音；失败时 error 给出原因。</summary>
        public static bool Synthesize(string text, string wavPath, int rate, string voiceName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "播报文案为空"; return false; }
            string step = "准备";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wavPath));
                if (File.Exists(wavPath)) File.Delete(wavPath);

                step = "创建语音引擎";
                var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (voiceType == null) { error = "系统里找不到 SAPI 语音引擎（SAPI.SpVoice）"; return false; }
                dynamic voice = Activator.CreateInstance(voiceType);
                step = "创建文件流";
                var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
                if (streamType == null) { error = "系统里找不到 SAPI.SpFileStream"; return false; }
                dynamic stream = Activator.CreateInstance(streamType);

                step = "打开输出文件";
                stream.Open(wavPath, 3, false);            // 3 = SSFMCreateForWrite
                step = "绑定输出流";
                voice.AudioOutputStream = stream;
                try { voice.Rate = Math.Max(-10, Math.Min(10, rate)); } catch { }

                // 选音色：指定了就按名字找（全名或包含都认），没指定就自动挑中文
                step = "选择音色";
                try
                {
                    dynamic voices = voice.GetVoices();
                    int n = voices.Count;
                    string want = (voiceName ?? "").Trim();
                    for (int i = 0; i < n; i++)
                    {
                        dynamic v = voices.Item(i);
                        string desc = null;
                        try { desc = v.GetDescription(); } catch { }
                        if (string.IsNullOrEmpty(desc)) continue;
                        bool hit = want.Length > 0
                            ? (string.Equals(desc, want, StringComparison.OrdinalIgnoreCase) || desc.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                            : IsChineseVoice(desc);
                        if (hit) { voice.Voice = v; break; }
                    }
                }
                catch { }

                step = "朗读输出";
                voice.Speak(text, 0);                       // 0 = 同步朗读（这里输出到文件）
                step = "关闭文件";
                try { stream.Close(); } catch { }

                var fi = new FileInfo(wavPath);
                if (!fi.Exists || fi.Length < 1024) { error = "语音合成没有产生有效音频文件"; return false; }
                return true;
            }
            catch (Exception ex) { error = step + "失败：" + ex.Message; return false; }
        }

        /// <summary>兼容旧调用（不指定音色）。</summary>
        public static bool Synthesize(string text, string wavPath, int rate, out string error)
            => Synthesize(text, wavPath, rate, null, out error);

        /// <summary>
        /// 用 ffmpeg 做"响度归一 + 重复 N 遍"：TTS 原声比音乐小声，不归一的话跟音乐一起播就听不见；
        /// 一句话太短，重复几遍学生才听得到。失败时返回 false（调用方退回原声 + 多放几遍）。
        /// </summary>
        public static bool ProcessAudio(string rawWav, string outWav, int repeat, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(Program.FfmpegPath)) { error = "找不到 " + Program.FfmpegPath; return false; }
                if (!File.Exists(rawWav)) { error = "原始语音文件不存在"; return false; }
                if (File.Exists(outWav)) File.Delete(outWav);

                int loops = Math.Max(0, Math.Min(4, repeat - 1));
                var psi = new ProcessStartInfo
                {
                    FileName = Program.FfmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-y");
                if (loops > 0) { psi.ArgumentList.Add("-stream_loop"); psi.ArgumentList.Add(loops.ToString()); }
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(rawWav);
                psi.ArgumentList.Add("-af"); psi.ArgumentList.Add(GainFilter);
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(outWav);

                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } error = "ffmpeg 处理超时"; return false; }
                    if (p.ExitCode != 0 || !File.Exists(outWav) || new FileInfo(outWav).Length < 1024)
                    {
                        string tail = (err ?? "").Trim();
                        int cut = tail.LastIndexOf('\n');
                        error = "ffmpeg 退出码 " + p.ExitCode + "：" + (cut > 0 ? tail.Substring(cut + 1).Trim() : tail);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>读音频时长（秒），自检与日志用；读不到返回 0。</summary>
        public static double ProbeSeconds(string wav)
        {
            try
            {
                string ffprobe = Path.Combine(Program.BinDir, "ffprobe.exe");
                if (!File.Exists(ffprobe) || !File.Exists(wav)) return 0;
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("csv=p=0");
                psi.ArgumentList.Add(wav);
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return double.TryParse((outp ?? "").Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>用 ffmpeg 的 volumedetect 量平均响度（mean_volume，dB），自检用；读不到返回 0。</summary>
        public static double ProbeMeanVolumeDb(string wav)
        {
            try
            {
                if (!File.Exists(Program.FfmpegPath) || !File.Exists(wav)) return 0;
                var psi = new ProcessStartInfo
                {
                    FileName = Program.FfmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(wav);
                psi.ArgumentList.Add("-af"); psi.ArgumentList.Add("volumedetect");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null"); psi.ArgumentList.Add("-");
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    var m = Regex.Match(err ?? "", @"mean_volume:\s*(-?[\d.]+)\s*dB");
                    return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>用 ffmpeg 的 volumedetect 量最大音量（dB），自检用；读不到返回 0。</summary>
        public static double ProbeMaxVolumeDb(string wav)
        {
            try
            {
                if (!File.Exists(Program.FfmpegPath) || !File.Exists(wav)) return 0;
                var psi = new ProcessStartInfo
                {
                    FileName = Program.FfmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(wav);
                psi.ArgumentList.Add("-af"); psi.ArgumentList.Add("volumedetect");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null"); psi.ArgumentList.Add("-");
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    var m = Regex.Match(err ?? "", @"max_volume:\s*(-?[\d.]+)\s*dB");
                    return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
                }
            }
            catch { return 0; }
        }

        // ---------------- 抓取 ----------------

        /// <summary>列表里的一条预警（两种数据源统一成这个结构）。</summary>
        public class Alarm
        {
            public string Id, Title, SignalType, SignalLevel, Effective, Name;
            public DateTime EffectiveTime;
        }

        static string AreaLabel()
        {
            var towns = GetTowns();
            string label = towns.Count > 0 ? string.Join("/", towns) : GetPlace();
            string code = GetAreaCode();
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(code)) return "本地（未配置地区/区划码）";
            if (string.IsNullOrWhiteSpace(code)) return label;
            if (string.IsNullOrWhiteSpace(label)) return "区划码 " + code;
            return label + "（" + code + "）";
        }

        /// <summary>把预警标题里的"类型+等级"抠出来，如"××县气象台发布雷电黄色预警信号" → 雷电 / 黄色。</summary>
        public static bool TryParseTitleSignal(string title, out string type, out string level)
        {
            type = null; level = null;
            if (string.IsNullOrWhiteSpace(title)) return false;
            var m = Regex.Match(title, @"(蓝色|黄色|橙色|红色)预警");
            if (!m.Success) return false;
            level = m.Groups[1].Value;

            // 颜色前面那一截才是类型；先把台名和"发布/更新/解除"这类词去掉
            string head = title.Substring(0, m.Index);
            int cut = Math.Max(head.LastIndexOf('台'), head.LastIndexOf('局'));
            if (cut >= 0 && cut + 1 <= head.Length) head = head.Substring(cut + 1);
            foreach (var w in new[] { "继续发布", "继续", "发布", "更新", "升级", "降级", "变更", "解除", "发布信号", "预警信号" })
                head = head.Replace(w, "");
            head = head.Trim().Trim('：', ':', ' ', '　');
            if (head.Length < 2) return false;
            type = head;
            return true;
        }

        /// <summary>从预警编号取前 6 位区划码，如 "12345678901234_20260924105216" → 123456。</summary>
        public static string AreaCodeOf(string alertId)
        {
            if (string.IsNullOrWhiteSpace(alertId)) return "";
            int i = alertId.IndexOf('_');
            string head = i > 0 ? alertId.Substring(0, i) : alertId;
            return head.Length >= 6 ? head.Substring(0, 6) : head;
        }

        /// <summary>中央气象台列表：data.page.list[]，按区划码筛本地，翻页取全。</summary>
        public static List<Alarm> ParseNmcList(string text, string areaCode, out int totalPage, out int totalCount)
        {
            totalPage = 1; totalCount = 0;
            var list = new List<Alarm>();
            var root = JsonNode.Parse(text) as JsonObject;
            if (root == null) throw new Exception("列表接口返回的不是 JSON");
            if ((root["code"]?.GetValue<int>() ?? -1) != 0) throw new Exception("列表接口返回异常：" + (root["msg"]?.ToString() ?? ""));
            var page = root["data"]?["page"] as JsonObject;
            if (page == null) throw new Exception("列表接口没有 data.page 节点");
            totalPage = Math.Max(1, page["totalPage"]?.GetValue<int>() ?? 1);
            totalCount = page["count"]?.GetValue<int>() ?? 0;
            if (page["list"] is not JsonArray arr) return list;
            foreach (var n in arr)
            {
                if (n is not JsonObject a) continue;
                string id = a["alertid"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!string.IsNullOrWhiteSpace(areaCode) && !AreaCodeOf(id).StartsWith(areaCode, StringComparison.Ordinal)) continue;
                string title = a["title"]?.ToString();
                var al = new Alarm
                {
                    Id = id,
                    Title = title,
                    Effective = a["issuetime"]?.ToString(),
                    Name = GetPlace()
                };
                if (TryParseTitleSignal(title, out string t, out string lv)) { al.SignalType = t; al.SignalLevel = lv; }
                TryParseEffective(al.Effective, out al.EffectiveTime);
                list.Add(al);
            }
            return list;
        }

        /// <summary>接口盒子列表：alarm[]（按地名匹配，无区划码）。</summary>
        public static List<Alarm> ParseApihzList(string text, string place, out int totalPage, out int totalCount)
        {
            totalPage = 1; totalCount = 0;
            var list = new List<Alarm>();
            var root = JsonNode.Parse(text) as JsonObject;
            if (root == null) throw new Exception("列表接口返回的不是 JSON");
            int code = root["code"]?.GetValue<int>() ?? 0;
            if (code != 200) throw new Exception("列表接口返回 " + code + "：" + (root["msg"]?.ToString() ?? ""));
            string placeName = root["name"]?.ToString();
            if (root["alarm"] is not JsonArray arr) return list;
            foreach (var n in arr)
            {
                if (n is not JsonObject a) continue;
                var al = new Alarm
                {
                    Id = a["id"]?.ToString(),
                    Title = a["title"]?.ToString(),
                    SignalType = a["signaltype"]?.ToString(),
                    SignalLevel = a["signallevel"]?.ToString(),
                    Effective = a["effective"]?.ToString(),
                    Name = string.IsNullOrWhiteSpace(placeName) ? place : placeName
                };
                TryParseEffective(al.Effective, out al.EffectiveTime);
                if (!string.IsNullOrWhiteSpace(al.Id)) list.Add(al);
            }
            totalCount = list.Count;
            return list;
        }

        /// <summary>
        /// 拉"当前生效预警"列表：先试配置里的地址（默认中央气象台），失败再试兜底源。
        /// 返回 (列表, 数据源说明, 列表是否完整)。**只有完整列表才能用来判定"某条预警已解除"**，
        /// 所以翻页中途失败时保留已取到的分页，但把 complete 标成 false。
        /// </summary>
        static async Task<Tuple<List<Alarm>, string, bool>> FetchAlarmsAsync()
        {
            var tries = new List<Tuple<string, string>>
            {
                Tuple.Create(BuildListUrl(), GetDetailUrl()),
                Tuple.Create(FallbackListUrl(), FallbackDetailUrl())
            };
            string lastError = null;
            bool anyUrl = false;
            foreach (var t in tries)
            {
                string url = t.Item1;
                if (string.IsNullOrWhiteSpace(url)) continue;      // 没配的源直接跳过（程序不内置地名，所以兜底源可能是空的）
                anyUrl = true;
                // 中央气象台的地址必须配区划码才有意义：先判断再下载，别白发一个请求（历史日志里出现过这种浪费）
                bool looksNmc = url.IndexOf("nmc.cn", StringComparison.OrdinalIgnoreCase) >= 0 && url.IndexOf("findAlarm", StringComparison.OrdinalIgnoreCase) >= 0;
                if (looksNmc && string.IsNullOrWhiteSpace(GetAreaCode()))
                {
                    lastError = "中央气象台接口需要在「预警播报」页填区划码才能筛出本地预警（当前未填）";
                    PlayLog.Write("预警", "数据源跳过", "warn", detail: lastError);
                    continue;
                }
                try
                {
                    string text = await Program.DownloadTextAsync(url, 2, "气象预警列表");
                    bool isNmc = text.IndexOf("\"alertid\"", StringComparison.OrdinalIgnoreCase) >= 0
                              || text.IndexOf("\"findAlarm\"", StringComparison.OrdinalIgnoreCase) >= 0
                              || (JsonNode.Parse(text) as JsonObject)?["data"]?["page"] != null;

                    if (isNmc && string.IsNullOrWhiteSpace(GetAreaCode()))
                    {
                        // 中央气象台返回的是全国列表，没有区划码就分不出哪条是本地的 —— 宁可不拉，也不能把别处的预警当本地播
                        lastError = "中央气象台接口需要在「预警播报」页填区划码才能筛出本地预警（当前未填）";
                        PlayLog.Write("预警", "数据源跳过", "warn", detail: lastError);
                        continue;
                    }

                    if (isNmc)
                    {
                        // 中央气象台：全国分页，按区划码筛本地；最多翻 10 页。
                        // 第 1 页失败 → 整个源算失败（换兜底源）；后面某页失败 → 保留已取到的，只把"完整"标成 false。
                        var all = new List<Alarm>();
                        var first = ParseNmcList(text, GetAreaCode(), out int totalPage, out int totalCount);
                        all.AddRange(first);
                        int pages = Math.Min(totalPage, 10);
                        bool complete = true;
                        for (int p = 2; p <= pages; p++)
                        {
                            string purl = Regex.Replace(url, @"([?&]pageNo=)\d+", "${1}" + p);
                            if (purl == url) purl = url + (url.Contains("?") ? "&" : "?") + "pageNo=" + p;
                            try
                            {
                                string ptext = await Program.DownloadTextAsync(purl, 2, "气象预警列表(第 " + p + " 页)");
                                all.AddRange(ParseNmcList(ptext, GetAreaCode(), out _, out _));
                            }
                            catch (Exception pex)
                            {
                                complete = false;
                                PlayLog.Write("预警", "翻页失败", "warn", detail: "第 " + p + " 页取不到（" + pex.Message + "），已保留前 " + (p - 1) + " 页数据；本轮不判定预警解除");
                            }
                        }
                        return Tuple.Create(all, "中央气象台 nmc.cn（全国 " + totalCount + " 条，" + pages + " 页，区划码 " + GetAreaCode()
                            + (complete ? "" : "，有分页失败") + "）", complete);
                    }

                    var apihz = ParseApihzList(text, GetPlace(), out _, out _);
                    return Tuple.Create(apihz, "接口盒子 apihz（地名 " + GetPlace() + "）", true);
                }
                catch (Exception ex)
                {
                    lastError = url + " → " + ex.Message;
                    PlayLog.Write("预警", "数据源失败", "warn", detail: lastError);
                }
            }
            if (!anyUrl) throw new Exception("还没有配置预警数据源：请在「预警播报」页填写「天气接口」地址（并按需填区划码）后保存");
            throw new Exception("所有预警数据源都失败：" + lastError);
        }

        /// <summary>抓一次：先列表拿最新编号，再取全文并写缓存。返回给日志用的一句话。</summary>
        public static async Task<string> FetchAndCacheAsync()
        {
            string place = GetPlace();
            SaveSetting("last_try", JsonValue.Create(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            try
            {
                var fetched = await FetchAlarmsAsync();
                var alarms = fetched.Item1;
                string provider = fetched.Item2;
                bool complete = fetched.Item3;

                var caches = GetCaches();
                var nowStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // ① 已解除：缓存里的预警已不在"当前生效列表"里 → 标记失效，之后不再播。
                //    只有**完整列表**才敢这么判——翻页失败时列表缺数据，误判会把还在生效的预警标成解除。
                int resolvedNow = 0;
                if (complete)
                {
                    foreach (var c in caches)
                    {
                        string cid = c["id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(cid)) continue;
                        if (alarms.Any(a => a.Id == cid)) continue;
                        if (c["resolved"]?.GetValue<bool>() ?? false) continue;
                        c["resolved"] = true;
                        c["resolved_at"] = nowStamp;
                        resolvedNow++;
                        PlayLog.Write("预警", "已解除", "info", detail: $"预警 {cid}（{c["signaltype"]}{c["signallevel"]}）已不在当前生效列表中，标记为失效");
                    }
                }

                // ② 已在缓存里的预警：重算时长说法与播报文案（程序升级后文案逻辑可能变了，不能只认旧文本）
                foreach (var a in alarms)
                {
                    var c = caches.FirstOrDefault(x => x["id"]?.ToString() == a.Id);
                    if (c == null) continue;
                    if (TryParseEffective(c["effective"]?.ToString(), out DateTime effOld))
                    {
                        ParseExpect(c["text"]?.ToString(), effOld, out _, out _, out string phraseOld);
                        c["duration"] = phraseOld;
                        c["speech"] = BuildSpeech(c);
                    }
                    c["fetched"] = nowStamp;
                    c["resolved"] = false;
                }

                // ③ 新预警：取详情、解析时间、入缓存（每轮最多取 5 条，避免一次拉太多）
                var newOnes = alarms.Where(a => !caches.Any(x => x["id"]?.ToString() == a.Id))
                                    .OrderByDescending(a => StampOf(a.Id)).Take(5).ToList();
                int added = 0;
                foreach (var a in newOnes)
                {
                    string body = a.Title;
                    bool bodyOk = false;
                    try
                    {
                        string detailUrl = BuildDetailUrl(GetDetailUrl(), a.Id);
                        string detailText = await Program.DownloadTextAsync(detailUrl, 2, "气象预警详情");
                        if (detailText.TrimStart().StartsWith("{"))
                        {
                            var dRoot = JsonNode.Parse(detailText) as JsonObject;
                            string msg = dRoot?["msg"]?.ToString();
                            if ((dRoot?["code"]?.GetValue<int>() ?? 0) == 200 && !string.IsNullOrWhiteSpace(msg)) { body = msg; bodyOk = true; }
                        }
                        else
                        {
                            string html = ExtractHtmlWarningBody(detailText);
                            if (!string.IsNullOrWhiteSpace(html)) { body = html; bodyOk = true; }
                        }
                    }
                    catch (Exception ex) { PlayLog.Write("预警", "详情失败", "warn", detail: a.Id + " → " + ex.Message); }
                    if (!bodyOk) body = a.Title;    // 取不到详情就退回标题，至少还能播"类型+等级"

                    DateTime eff = a.EffectiveTime;
                    if (eff == DateTime.MinValue && !TryParsePublishFromText(body, DateTime.Now, out eff)) eff = DateTime.Now;
                    DateTime expect = ParseExpect(body, eff, out bool estimated, out var notes, out string phrase);

                    var cache = new JsonObject
                    {
                        ["id"] = a.Id,
                        ["name"] = a.Name,
                        ["title"] = a.Title,
                        ["text"] = body,
                        ["signaltype"] = a.SignalType,
                        ["signallevel"] = a.SignalLevel,
                        ["effective"] = eff.ToString("yyyy-MM-dd HH:mm"),
                        ["expect"] = expect.ToString("yyyy-MM-dd HH:mm"),
                        ["estimated"] = estimated,
                        ["duration"] = phrase,
                        ["speech"] = "",
                        ["resolved"] = false,
                        ["source"] = provider,
                        ["fetched"] = nowStamp,
                        ["place"] = place
                    };
                    cache["speech"] = BuildSpeech(cache);
                    caches.Add(cache);
                    added++;
                    PlayLog.Write("预警", "新预警", "info",
                        detail: $"{a.SignalType}{a.SignalLevel}｜生效 {eff:yyyy-MM-dd HH:mm}｜预计 {expect:yyyy-MM-dd HH:mm}"
                                + (estimated ? "（估算）" : "") + "｜数据源 " + provider
                                + "｜时间解析：" + string.Join("；", notes) + "｜播报文案：" + cache["speech"]);
                }

                // ④ 清理：已解除超过 2 天的记录不再保留（页面/日志里留两天足够追溯）
                caches.RemoveAll(c => (c["resolved"]?.GetValue<bool>() ?? false)
                    && DateTime.TryParse(c["resolved_at"]?.ToString(), out DateTime ra) && (DateTime.Now - ra).TotalDays > 2);

                SaveCaches(caches);
                RemoveSetting("cache");      // 旧版单条字段清掉，避免两份数据不一致
                SaveSetting("last_ok", JsonValue.Create(nowStamp));
                SaveSetting("last_error", JsonValue.Create(""));

                int active = caches.Count(c => !(c["resolved"]?.GetValue<bool>() ?? false));
                if (active == 0)
                {
                    PlayLog.Write("预警", "拉取成功", "info",
                        detail: $"{AreaLabel()} 当前没有生效中的气象预警（数据源：{provider}"
                                + (resolvedNow > 0 ? "，本轮标记解除 " + resolvedNow + " 条" : "") + "）");
                    return $"{AreaLabel()} 当前没有生效中的气象预警";
                }
                PlayLog.Write("预警", "拉取成功", "info",
                    detail: $"当前生效 {active} 条（新增 {added} 条"
                            + (resolvedNow > 0 ? "，解除 " + resolvedNow + " 条" : "")
                            + $"）｜数据源 {provider}｜" + string.Join("；", caches.Where(c => !(c["resolved"]?.GetValue<bool>() ?? false))
                                .OrderByDescending(c => LevelRank(c["signallevel"]?.ToString()))
                                .Select(c => c["signaltype"]?.ToString() + c["signallevel"]?.ToString())));
                return active + " 条生效中" + (added > 0 ? "（新增 " + added + " 条）" : "");
            }
            catch (Exception ex)
            {
                SaveSetting("last_error", JsonValue.Create(ex.Message));
                // 缺配置不算"失败"，是还没配好——用 warn 级别，别在日志里看着像故障
                bool notConfigured = ex.Message.Contains("填区划码") || ex.Message.Contains("还没有配置预警数据源");
                PlayLog.Write("预警", notConfigured ? "未配置" : "拉取失败", notConfigured ? "warn" : "error", detail: ex.Message);
                return (notConfigured ? "未配置：" : "拉取失败：") + ex.Message;
            }
        }

        static long StampOf(string id)        {
            try
            {
                int i = (id ?? "").LastIndexOf('_');
                if (i >= 0 && long.TryParse(id.Substring(i + 1), out long v)) return v;
            }
            catch { }
            return 0;
        }

        // ---------------- 播放入口（ExpandTaskQueue 调它） ----------------

        /// <summary>文案哈希（前 4 字节），用于语音文件名，文案变了就会重新合成。</summary>
        static string Hash8(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>准备单条预警的可播音频：离线合成（音色/语速）→ 响度归一 + 重复 N 遍；force=true 强制重新合成（试听用）。</summary>
        public static List<string> PrepareOne(JsonObject cache, bool force, out string error)
        {
            var files = new List<string>();
            error = null;
            string speech = BuildSpeech(cache);
            if (string.IsNullOrWhiteSpace(speech)) speech = cache["speech"]?.ToString();
            Directory.CreateDirectory(WarnDir);
            string safe = Regex.Replace(cache["id"]?.ToString() ?? "warn", @"[^0-9A-Za-z_\-]", "");
            int rate = GetRate();
            string voice = GetVoice();
            int repeat = GetRepeat();

            // ① 原声：按"文案 + 音色 + 语速"缓存，三者任一变了就重新合成
            string stamp = Hash8(speech + "|" + voice + "|" + rate);
            string raw = Path.Combine(WarnDir, "warn-" + safe + "-" + stamp + ".wav");
            if (force || !File.Exists(raw) || new FileInfo(raw).Length < 1024)
            {
                try { foreach (var f in Directory.GetFiles(WarnDir, "warn-" + safe + "-*.wav")) File.Delete(f); } catch { }
                if (!Synthesize(speech, raw, rate, voice, out string err))
                {
                    error = "语音合成失败：" + err;
                    return files;
                }
            }

            // ② 响度归一 + 重复（按"原声 + 重复次数"缓存）
            if (GetGain())
            {
                string processed = Path.Combine(WarnDir, "warn-" + safe + "-" + stamp + "-g" + repeat + ".wav");
                bool ready = File.Exists(processed) && new FileInfo(processed).Length >= 1024;
                if (!ready)
                {
                    ready = ProcessAudio(raw, processed, repeat, out string perr);
                    if (!ready)
                        PlayLog.Write("预警", "增益失败", "warn",
                            detail: "ffmpeg 处理失败（" + perr + "），本次按原声音量播放 " + repeat + " 遍");
                }
                if (ready) { files.Add(processed); return files; }
            }

            for (int i = 0; i < repeat; i++) files.Add(raw);
            return files;
        }

        /// <summary>
        /// 排程引用编号 1000 时调用：对缓存里**每一条**生效预警分别判断，
        /// 通过的按等级从高到低（红→橙→黄→蓝）依次合成，返回要播的音频文件列表。
        /// </summary>
        public static List<string> PrepareForPlayback(out string reason)
        {
            var files = new List<string>();
            var caches = GetCaches();
            if (caches.Count == 0)
            {
                reason = "还没有拉到任何气象预警，编号 1000 本次不播报";
                return files;
            }

            var notes = new List<string>();
            var playable = new List<JsonObject>();
            foreach (var c in caches.OrderByDescending(c => LevelRank(c["signallevel"]?.ToString())))
            {
                string label = (c["signaltype"]?.ToString() ?? "") + (c["signallevel"]?.ToString() ?? "");
                if (Evaluate(c, GetWindows(), DateTime.Now, out string why, out _)) playable.Add(c);
                else notes.Add(label + "：" + why);
            }

            if (playable.Count == 0)
            {
                reason = "本次没有可播报的预警：" + string.Join("；", notes);
                return files;
            }

            foreach (var c in playable)
            {
                string label = (c["signaltype"]?.ToString() ?? "") + (c["signallevel"]?.ToString() ?? "");
                var one = PrepareOne(c, false, out string perr);
                if (one.Count == 0) { notes.Add(label + "：" + perr); continue; }
                files.AddRange(one);
            }

            reason = "播报 " + playable.Count + " 条：" + string.Join("、",
                playable.Select(c => (c["signaltype"]?.ToString() ?? "") + (c["signallevel"]?.ToString() ?? "")));
            if (notes.Count > 0) reason += "；跳过 " + string.Join("；", notes);
            return files;
        }

        /// <summary>给界面用的一行状态。</summary>
        public static string StatusLine()
        {
            var cache = GetCache();
            if (cache == null || string.IsNullOrWhiteSpace(cache["id"]?.ToString()))
                return "还没有拉到预警数据" + (string.IsNullOrWhiteSpace(GetLastError()) ? "" : "（上次失败：" + GetLastError() + "）");
            DateTime expect = GetExpect(cache, out bool estimated);
            bool ok = Evaluate(cache, GetWindows(), DateTime.Now, out string reason, out _);
            return $"最新：{cache["name"]}{cache["signaltype"]}{cache["signallevel"]}｜编号 {cache["id"]}\n"
                 + $"生效 {cache["effective"]}｜预计 {expect:yyyy-MM-dd HH:mm}" + (estimated ? "（按默认 6 小时估算）" : "") + "\n"
                 + (ok ? "当前判断：可以播报 → " : "当前判断：不播报 → ") + reason;
        }
    }
}
