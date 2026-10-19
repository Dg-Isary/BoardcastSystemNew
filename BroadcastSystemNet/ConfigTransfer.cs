#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 按项导入 / 导出的统一实现 —— **四个入口共用同一批导入器与同一份校验规则**：
    ///   1) 桌面「导入 / 导出」窗口；
    ///   2) 桌面拖入（排程页拖排程、调休页拖调休）；
    ///   3) 整包还原（「从备份中恢复…」，走同一批导入器而不是盲目解压覆盖）；
    ///   4) 以后可能的网页 / 手机端（复用同一组 HTTP 接口）。
    ///
    /// 设计要点（与 docs\导入导出改造-需求确认.md 一一对应）：
    ///   - 校验**复用 <see cref="MicApiBridge.ValidateBody"/>**（手机端写配置走的同一道门），绝不另写一套；
    ///   - 排程导入前先做「旧格式 → 当前字段集」迁移，与启动迁移用同一个 <see cref="MigrateTaskFields"/>；
    ///   - vnt 段的密钥类字段（device_id / token / data_password / cred_secret / code_seed）**永不覆盖**；
    ///   - 音乐库只导入**编号不冲突**的条目，冲突的跳过并保留现有（规格 §0-3，不受「完全覆盖」影响）；
    ///   - 铃声**同名覆盖、不同名新增**（规格 §4-3）；
    ///   - 预警只导入 `_type=warn` 配置，**不导入** `_type=warn_cache`（预警历史）；
    ///   - 包格式 = 老格式（`config\*.jsonl` + `ring\*`）**加一个根目录清单** `transfer.json`，
    ///     因此新包能被旧逻辑读懂，旧包也能被本导入器读懂（清单缺失时按"来源主机未知"处理）。
    /// </summary>
    public static class ConfigTransfer
    {
        // ---------------- 项标识（与界面复选一一对应） ----------------
        public const string Tasks = "tasks";
        public const string Tiaoxiu = "tiaoxiu";
        public const string Warn = "warn";
        public const string Settings = "settings";
        public const string Vnt = "vnt";
        public const string Music = "music";
        public const string Ring = "ring";

        /// <summary>界面复选顺序（规格 §1.1 的中文项顺序）。</summary>
        public static readonly string[] Order = { Tasks, Tiaoxiu, Warn, Settings, Vnt, Music, Ring };

        public static string Label(string key)
        {
            switch (key)
            {
                case Tasks: return "排程";
                case Tiaoxiu: return "调休";
                case Warn: return "预警配置";
                case Settings: return "系统设置";
                case Vnt: return "vnt（仅服务器地址）";
                case Music: return "音乐库";
                case Ring: return "铃声";
                default: return key;
            }
        }

        /// <summary>新格式包的根目录清单文件名。</summary>
        public const string ManifestName = "transfer.json";
        /// <summary>清单结构版本（将来字段变动时用它做兼容判断）。</summary>
        public const int Schema = 1;

        /// <summary>vnt 段**任何情况都不覆盖**的密钥类字段（规格 §6.1：设备 ID 是本机身份）。</summary>
        static readonly string[] VntNeverOverwrite = { "device_id", "token", "data_password", "cred_secret", "code_seed" };

        /// <summary>
        /// "不含隐私"时要从 vnt 段清空的字段 = 上面 5 个密钥类字段 **加上** 管理员设备名单。
        /// 这是**唯一来源**：新导出路径（<see cref="Export"/>）与老的「备份系统数据」
        /// （<c>Program.StripPrivacyFromBackup</c>）都读它，避免两条路径清得不一样
        /// （老路径曾经漏掉 device_id 与 admin_devices）。
        /// </summary>
        public static readonly string[] PrivacyVntFields = { "device_id", "token", "data_password", "cred_secret", "code_seed", "admin_devices" };

        /// <summary>系统设置里**不导入**的本机相关字段（屏幕坐标、迁移标记、口令见代码内说明）。</summary>
        static readonly string[] SettingLocalOnly = { "ball_x", "ball_y", "warn_moved_to_file", "warn_privacy_v2" };

        /// <summary>允许导入的铃声扩展名（防止把任意文件写进 ring 目录）。</summary>
        static readonly string[] RingExts = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".ape" };

        /// <summary>单条铃声的体积上限（200 MB）。</summary>
        const long RingMaxBytes = 200L * 1024 * 1024;

        // =====================================================================
        //  排程字段迁移（与 Program 启动迁移共用，保证"只保留一份默认值"）
        // =====================================================================
        /// <summary>
        /// 把一条排程补齐到当前字段集（旧格式迁移）。返回是否发生了改动。
        /// 默认值与 Program 启动迁移完全一致：标记 1、数组为空、模式 sequential、stop_mode immediate。
        /// </summary>
        public static bool MigrateTaskFields(JsonObject tk)
        {
            if (tk == null) return false;
            bool d = false;
            if (tk["tiaoxiu_marker"] == null) { tk["tiaoxiu_marker"] = 1; d = true; }   // 任务侧只用 1-10；0 只留给「调休管理」
            if (tk["days"] == null) { tk["days"] = new JsonArray(); d = true; }
            if (tk["specific_dates"] == null) { tk["specific_dates"] = new JsonArray(); d = true; }
            if (tk["music_ids"] == null) { tk["music_ids"] = new JsonArray(); d = true; }
            if (tk["sequence_mode"] == null) { tk["sequence_mode"] = "sequential"; d = true; }
            if (tk["stop_mode"] == null) { tk["stop_mode"] = "immediate"; d = true; }
            if (tk["folder_mode"] == null) { tk["folder_mode"] = "sequential"; d = true; }
            if (tk["folder_play_count"] == null) { tk["folder_play_count"] = 1; d = true; }
            if (tk["duration_m"] == null) { tk["duration_m"] = 0; d = true; }
            if (tk["duration_s"] == null) { tk["duration_s"] = 0; d = true; }
            return d;
        }

        // =====================================================================
        //  导出
        // =====================================================================
        /// <summary>
        /// 按项导出为 ZIP。默认**不导出隐私数据**（网页口令、组网令牌、数据密码、凭证密钥）；
        /// 不勾选隐私时 vnt 段只写服务器地址（规格 §1.2）。
        /// 返回报告对象，供界面显示。
        /// </summary>
        public static JsonObject Export(string zipPath, IEnumerable<string> items, bool includePrivacy)
        {
            var want = new HashSet<string>((items ?? Order).Where(k => Order.Contains(k)));
            var rep = new JsonObject
            {
                ["status"] = "success",
                ["privacy"] = includePrivacy,
                ["host"] = Environment.MachineName,
                ["items"] = new JsonArray()
            };
            try
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    // ---- 根目录清单：记录来源主机名（"主机名一致"判断的依据）与本次包含的项 ----
                    var man = new JsonObject
                    {
                        ["_type"] = "transfer",
                        ["app"] = "BroadcastSystemNet",
                        ["schema"] = Schema,
                        ["version"] = typeof(ConfigTransfer).Assembly.GetName().Version?.ToString() ?? "",
                        ["host"] = Environment.MachineName,
                        ["exported_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["privacy"] = includePrivacy,
                        ["items"] = new JsonArray(want.Select(k => (JsonNode)k).ToArray())
                    };
                    WriteEntry(zip, ManifestName, man.ToJsonString(Program.JsonWriteOptions));

                    // ---- 各类配置 ----
                    if (want.Contains(Tasks))
                        WriteEntry(zip, "config/tasks.jsonl", JsonlText(Program.ReadJsonl("tasks.jsonl")));

                    if (want.Contains(Tiaoxiu))
                    {
                        // 调休只导出 tiaoxiu 记录；订阅配置与非法云端数据属于"本机订阅状态"，不带出去
                        var arr = new JsonArray();
                        foreach (var n in Program.ReadTiaoxiuRecords()) arr.Add(n.DeepClone());
                        WriteEntry(zip, "config/tiaoxiu.jsonl", JsonlText(arr));
                    }

                    if (want.Contains(Warn))
                    {
                        var arr = new JsonArray();
                        foreach (var n in Program.ReadJsonl(WeatherWarn.WarnFile))
                            if (n is JsonObject o && o["_type"]?.ToString() == "warn") arr.Add(o.DeepClone());
                        WriteEntry(zip, "config/warn.jsonl", JsonlText(arr));
                    }

                    if (want.Contains(Settings))
                    {
                        var arr = new JsonArray();
                        foreach (var n in Program.ReadJsonl("setting.jsonl"))
                        {
                            if (n is not JsonObject g) continue;
                            var copy = g.DeepClone() as JsonObject;
                            if (copy["_type"]?.ToString() == "global" && !includePrivacy) copy["password"] = "";
                            arr.Add(copy);
                        }
                        WriteEntry(zip, "config/setting.jsonl", JsonlText(arr));
                    }

                    if (want.Contains(Vnt))
                    {
                        var arr = new JsonArray();
                        foreach (var n in Program.ReadJsonl("vnt.jsonl"))
                        {
                            if (n is not JsonObject v) continue;
                            var copy = v.DeepClone() as JsonObject;
                            if (!includePrivacy)
                            {
                                // 不导出隐私：只留服务器地址（规格 §1.2）；密钥类与本机身份字段一律清空
                                foreach (var k in PrivacyVntFields) if (copy[k] != null) copy[k] = "";
                            }
                            arr.Add(copy);
                        }
                        WriteEntry(zip, "config/vnt.jsonl", JsonlText(arr));
                    }

                    if (want.Contains(Music))
                        WriteEntry(zip, "config/music.jsonl", JsonlText(Program.ReadJsonl("music.jsonl")));

                    // ---- 铃声（按文件名平铺，保留中文与空格） ----
                    int ringCount = 0;
                    if (want.Contains(Ring) && Directory.Exists(Program.RingDir))
                    {
                        foreach (var f in Directory.GetFiles(Program.RingDir))
                        {
                            try
                            {
                                var e = zip.CreateEntry("ring/" + Path.GetFileName(f), CompressionLevel.Optimal);
                                using var es = e.Open();
                                using var fs = File.OpenRead(f);
                                fs.CopyTo(es);
                                ringCount++;
                            }
                            catch (Exception ex) { LogHelper.Log("导出铃声失败: " + f, ex); }
                        }
                    }

                    var itemRep = (JsonArray)rep["items"];
                    foreach (var k in Order)
                    {
                        if (!want.Contains(k)) continue;
                        int n = k switch
                        {
                            Tasks => Program.ReadJsonl("tasks.jsonl").Count,
                            Tiaoxiu => Program.ReadTiaoxiuRecords().Count,
                            Warn => 1,
                            Settings => 1,
                            Vnt => 1,
                            Music => Program.ReadJsonl("music.jsonl").Count,
                            Ring => ringCount,
                            _ => 0
                        };
                        itemRep.Add(new JsonObject { ["key"] = k, ["label"] = Label(k), ["ok"] = true, ["exported"] = n });
                    }
                    rep["message"] = "已导出：" + string.Join("、", want.Select(Label))
                                   + (includePrivacy ? "（含隐私数据，明文，请妥善保管）" : "（未含隐私数据）");
                }
                PlayLog.Write("数据", "导出", includePrivacy ? "warn" : "info",
                    detail: $"{zipPath}（项：{string.Join("、", want.Select(Label))}；隐私数据：{(includePrivacy ? "已含" : "未含")}）");
            }
            catch (Exception ex)
            {
                rep["status"] = "error";
                rep["message"] = "导出失败：" + ex.Message;
                LogHelper.Log("ConfigTransfer.Export Failed", ex);
            }
            return rep;
        }

        static void WriteEntry(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = e.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(text ?? "");
        }

        /// <summary>
        /// 把记录数组写成 **jsonl（一行一条）**。
        /// 必须与磁盘上的 config\*.jsonl 同格式：如果直接 ToJsonString 整个数组，包里就是
        /// 一整行 JSON 数组，导入侧按行解析会把整条数组判成"非法行"→ 导出后导不回来
        /// （自检 TestConfigTransfer 抓到过这个缺陷）。
        /// </summary>
        static string JsonlText(JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var n in arr ?? new JsonArray())
            {
                if (n == null) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(n.ToJsonString(Program.JsonWriteOptions));
            }
            return sb.ToString();
        }

        // =====================================================================
        //  预览 / 导入（同一套核心，预览 = dryRun）
        // =====================================================================
        /// <summary>只读预览：解析来源、逐项校验、报告"将导入多少条 / 跳过多少条 / 非法多少条"，**不写盘**。</summary>
        public static JsonObject Preview(string sourcePath, IEnumerable<string> items, bool overwrite, string singleKind = null)
            => Run(sourcePath, items, overwrite, true, singleKind);

        /// <summary>真正导入。所有写盘都经 <see cref="Program.WriteJsonl"/>（写前自动滚动备份）。</summary>
        public static JsonObject Import(string sourcePath, IEnumerable<string> items, bool overwrite, string singleKind = null)
            => Run(sourcePath, items, overwrite, false, singleKind);

        /// <summary>整包还原入口：把包里**所有**项按同一批导入器（含校验与格式迁移）导入，而不是盲目解压覆盖。</summary>
        public static JsonObject ImportWholePackage(string zipPath) => Run(zipPath, Order, false, false, null);

        /// <summary>按内容推断一个单文件属于哪一类（拖入导入用；判不出来返回 null）。</summary>
        public static string DetectKind(string filePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                foreach (var raw in lines)
                {
                    string t = raw.Trim();
                    if (t.Length == 0) continue;
                    if (t[0] != '{') continue;
                    var o = JsonNode.Parse(t) as JsonObject;
                    if (o == null) continue;
                    string ty = o["_type"]?.ToString();
                    if (ty == "tiaoxiu") return Tiaoxiu;
                    if (ty == "warn") return Warn;
                    if (ty == "warn_cache") return null;          // 预警历史：不导入
                    if (ty == "global") return Settings;
                    if (ty == "vnt" || ty == "mic") return Vnt;
                    if (o["start_time"] != null) return Tasks;
                    if (o["id"] != null && o["path"] != null && o["type"] != null) return Music;
                    if (o["date"] != null && (o["action"] != null || o["start_date"] != null)) return Tiaoxiu;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------------
        sealed class Source : IDisposable
        {
            public string ZipPath;
            public string SinglePath;
            public string SingleKind;
            public string Host;              // 来源主机名（清单缺失 = null，按"来源未知"从严处理）
            public bool HasManifest;
            public bool PrivacyIncluded;
            ZipArchive _zip;
            readonly Dictionary<string, string> _configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            static readonly string[] ConfigFiles = { "tasks.jsonl", "tiaoxiu.jsonl", "warn.jsonl", "setting.jsonl", "vnt.jsonl", "music.jsonl" };

            public static Source Open(string path, string singleKind)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("找不到文件：" + path, path);
                var s = new Source();
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    s.ZipPath = path;
                    s._zip = ZipFile.OpenRead(path);
                    // 清单（新格式包）；没有就是旧格式的整目录备份
                    string man = s.EntryText(e => string.Equals(e, ManifestName, StringComparison.OrdinalIgnoreCase)
                                               || e.EndsWith("/" + ManifestName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(man))
                    {
                        try
                        {
                            var o = JsonNode.Parse(man) as JsonObject;
                            if (o != null && o["_type"]?.ToString() == "transfer")
                            {
                                s.HasManifest = true;
                                s.Host = o["host"]?.ToString();
                                try { s.PrivacyIncluded = o["privacy"]?.GetValue<bool>() ?? false; } catch { }
                            }
                        }
                        catch { }
                    }
                    foreach (var f in ConfigFiles)
                    {
                        string txt = s.EntryText(e => string.Equals(e, "config/" + f, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(e, f, StringComparison.OrdinalIgnoreCase)
                                                   || e.EndsWith("/config/" + f, StringComparison.OrdinalIgnoreCase)
                                                   || e.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase));
                        if (txt != null) s._configs[f] = txt;
                    }
                }
                else
                {
                    s.SinglePath = path;
                    s.SingleKind = singleKind ?? DetectKind(path);
                    if (s.SingleKind != null) s._configs[s.SingleKind + ".jsonl"] = File.ReadAllText(path, Encoding.UTF8);
                }
                return s;
            }

            string EntryText(Func<string, bool> match)
            {
                if (_zip == null) return null;
                foreach (var e in _zip.Entries)
                {
                    string n = (e.FullName ?? "").Replace('\\', '/');
                    if (!match(n)) continue;
                    using var st = e.Open();
                    using var sr = new StreamReader(st, Encoding.UTF8);
                    return sr.ReadToEnd();
                }
                return null;
            }

            /// <summary>取某类配置的 jsonl 文本；不存在返回 null。</summary>
            public string Config(string key) => _configs.TryGetValue(key + ".jsonl", out var t) ? t : null;

            /// <summary>包里有没有这一类（用来区分"没勾"与"包里就没有"）。</summary>
            public bool Has(string key) => _configs.ContainsKey(key + ".jsonl");

            /// <summary>包里是否含有可导入的项（用于旧包/异常包的友好报错）。</summary>
            public bool HasAnything => _configs.Count > 0 || RingCount > 0;

            public int RingCount
            {
                get
                {
                    if (_zip != null) return _zip.Entries.Count(e => IsRingEntry(e.FullName));
                    return 0;
                }
            }

            static bool IsRingEntry(string fullName)
            {
                string n = (fullName ?? "").Replace('\\', '/');
                return n.StartsWith("ring/", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("/ring/", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>遍历包里的铃声（名字取最后一段，忽略子目录）。</summary>
            public IEnumerable<(string Name, byte[] Data)> RingFiles()
            {
                if (_zip == null) yield break;
                foreach (var e in _zip.Entries)
                {
                    if (!IsRingEntry(e.FullName)) continue;
                    if (e.Length <= 0) continue;
                    string name = e.FullName.Replace('\\', '/');
                    name = name.Substring(name.LastIndexOf('/') + 1);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (e.Length > RingMaxBytes) continue;
                    byte[] data;
                    using (var st = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        st.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    yield return (name, data);
                }
            }

            public void Dispose() { try { _zip?.Dispose(); } catch { } }
        }

        // ---------------------------------------------------------------------
        static JsonObject Run(string sourcePath, IEnumerable<string> items, bool overwrite, bool dryRun, string singleKind)
        {
            var rep = new JsonObject
            {
                ["status"] = "success",
                ["dry_run"] = dryRun,
                ["overwrite"] = overwrite,
                ["source"] = sourcePath,
                ["items"] = new JsonArray()
            };
            var itemRep = (JsonArray)rep["items"];
            Source src = null;
            try
            {
                src = Source.Open(sourcePath, singleKind);
                rep["has_manifest"] = src.HasManifest;
                rep["source_host"] = src.Host;
                rep["privacy_included"] = src.PrivacyIncluded;
                bool sameHost = !string.IsNullOrWhiteSpace(src.Host)
                             && string.Equals(src.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                rep["same_host"] = sameHost;
                if (!src.HasAnything)
                {
                    rep["status"] = "error";
                    rep["message"] = "这个文件里没有可导入的配置（既不是本程序的备份包，也不是导出的配置文件）。";
                    return rep;
                }

                var want = new List<string>();
                foreach (var k in Order) if (items != null && items.Contains(k)) want.Add(k);
                if (want.Count == 0) want.AddRange(Order);
                // 单文件导入时只处理它自己那一类
                if (src.SinglePath != null && src.SingleKind != null) want = want.Where(k => k == src.SingleKind).ToList();

                foreach (var key in want)
                {
                    if (!src.Has(key) && key != Ring) continue;
                    if (key == Ring && src.SinglePath != null) continue;
                    JsonObject r;
                    switch (key)
                    {
                        case Tasks: r = DoTasks(src, overwrite, dryRun); break;
                        case Tiaoxiu: r = DoTiaoxiu(src, overwrite, dryRun); break;
                        case Warn: r = DoWarn(src, overwrite, dryRun); break;
                        case Settings: r = DoSettings(src, overwrite, dryRun); break;
                        case Vnt: r = DoVnt(src, sameHost, dryRun); break;
                        case Music: r = DoMusic(src, dryRun); break;
                        case Ring: r = DoRing(src, dryRun); break;
                        default: continue;
                    }
                    r["key"] = key;
                    r["label"] = Label(key);
                    itemRep.Add(r);
                }

                int totIn = 0, totSkip = 0, totBad = 0;
                foreach (var n in itemRep)
                {
                    totIn += n["imported"]?.GetValue<int>() ?? 0;
                    totSkip += n["skipped"]?.GetValue<int>() ?? 0;
                    totBad += n["invalid"]?.GetValue<int>() ?? 0;
                }
                rep["imported"] = totIn;
                rep["skipped"] = totSkip;
                rep["invalid"] = totBad;
                if (totBad > 0) rep["status"] = "partial";
                string verb = dryRun ? "预览" : "导入";
                rep["message"] = $"{verb}完成：写入 {totIn} 条，跳过 {totSkip} 条，非法 {totBad} 条"
                               + (src.HasManifest
                                    ? $"（来源主机：{src.Host ?? "未记录"}，{(sameHost ? "与本机一致" : "与本机不同")}）"
                                    : "（旧格式备份包：未记录来源主机，vnt 只取服务器地址）");
                if (!dryRun)
                    PlayLog.Write("数据", verb, totBad > 0 ? "warn" : "info",
                        detail: $"{sourcePath}；写入 {totIn} 条，跳过 {totSkip} 条，非法 {totBad} 条");
            }
            catch (Exception ex)
            {
                rep["status"] = "error";
                rep["message"] = (dryRun ? "预览失败：" : "导入失败：") + ex.Message;
                LogHelper.Log("ConfigTransfer.Run Failed", ex);
            }
            finally { src?.Dispose(); }
            return rep;
        }

        // ---------------- 逐类实现 ----------------

        /// <summary>解析 jsonl 文本。返回条目与"解析失败行数"——失败行计入 invalid，保证不静默失败。</summary>
        static (JsonArray Items, int ParseErrors) ParseJsonl(string text, List<string> msgs)
        {
            var arr = new JsonArray();
            int bad = 0;
            if (string.IsNullOrWhiteSpace(text)) return (arr, 0);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                try
                {
                    var n = JsonNode.Parse(t);
                    if (n == null) { bad++; msgs?.Add($"第 {i + 1} 行：解析为空，已跳过"); continue; }
                    arr.Add(n);
                }
                catch (Exception ex) { bad++; msgs?.Add($"第 {i + 1} 行不是合法 JSON，已跳过（{ex.Message}）"); }
            }
            return (arr, bad);
        }

        /// <summary>排程：迁移旧格式 → 逐条校验 → 覆盖则全量替换、否则追加去重。</summary>
        static JsonObject DoTasks(Source src, bool overwrite, bool dryRun)
        {
            var msgs = new List<string>();
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = 0, ["messages"] = new JsonArray() };
            var parsed = ParseJsonl(src.Config(Tasks), msgs);
            var raw = parsed.Items;
            var valid = new List<JsonObject>();
            int invalid = parsed.ParseErrors, migrated = 0;
            foreach (var n in raw)
            {
                if (n is not JsonObject o) { invalid++; msgs.Add("有一条不是 JSON 对象，已跳过"); continue; }
                if (MigrateTaskFields(o)) migrated++;
                string err = MicApiBridge.ValidateBody("POST", "/api/tasks", o);
                if (err != null) { invalid++; msgs.Add($"时间 {o["start_time"]}：{err}"); continue; }
                valid.Add(o);
            }
            var local = Program.ReadJsonl("tasks.jsonl");
            var outArr = new JsonArray();
            if (overwrite)
            {
                // 完全覆盖：按开始时间重排编号 1..N（与「按时间重排编号」同一语义，保证编号连续）
                var list = valid.OrderBy(o => o["start_time"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
                int n = 0;
                foreach (var o in list) { var c = (JsonObject)o.DeepClone(); c["id"] = ++n; outArr.Add(c); }
                r["imported"] = n;
            }
            else
            {
                int maxId = 0;
                var used = new HashSet<int>();
                foreach (var t in local)
                {
                    int id = 0; int.TryParse(t?["id"]?.ToString(), out id);
                    if (id > maxId) maxId = id;
                    used.Add(id);
                    outArr.Add(t.DeepClone());
                }
                foreach (var o in valid)
                {
                    if (IsSameTask(local, o)) { r["skipped"] = r["skipped"].GetValue<int>() + 1; continue; }
                    var c = (JsonObject)o.DeepClone();
                    int want = 0; int.TryParse(o["id"]?.ToString(), out want);
                    if (want <= 0 || used.Contains(want)) { do { want = ++maxId; } while (used.Contains(want)); }
                    else if (want > maxId) maxId = want;
                    used.Add(want);
                    c["id"] = want;
                    outArr.Add(c);
                    r["imported"] = r["imported"].GetValue<int>() + 1;
                }
            }
            if (migrated > 0) msgs.Insert(0, $"{migrated} 条按当前字段集补齐（旧格式迁移）");
            if (!dryRun) Program.WriteJsonl("tasks.jsonl", outArr);
            r["invalid"] = invalid;
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            r["ok"] = invalid == 0;
            return r;
        }

        /// <summary>判断某条排程是否已存在于本机（内容级去重，避免同一份包导入两次导致排程翻倍）。</summary>
        static bool IsSameTask(JsonArray local, JsonObject t)
        {
            string Key(JsonNode n)
            {
                var o = n as JsonObject;
                if (o == null) return null;
                return string.Join("|",
                    o["start_time"]?.ToString() ?? "",
                    o["music_ids"]?.ToJsonString() ?? "[]",
                    o["days"]?.ToJsonString() ?? "[]",
                    o["specific_dates"]?.ToJsonString() ?? "[]",
                    o["duration_m"]?.ToString() ?? "0",
                    o["duration_s"]?.ToString() ?? "0");
            }
            string k = Key(t);
            foreach (var n in local) if (Key(n) == k) return true;
            return false;
        }

        /// <summary>调休：校验后合并（按 日期区间+标记+动作 去重）或全量替换。</summary>
        static JsonObject DoTiaoxiu(Source src, bool overwrite, bool dryRun)
        {
            var msgs = new List<string>();
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = 0, ["messages"] = new JsonArray() };
            var parsed = ParseJsonl(src.Config(Tiaoxiu), msgs);
            var raw = parsed.Items;
            var valid = new List<JsonObject>();
            int invalid = parsed.ParseErrors;
            foreach (var n in raw)
            {
                if (n is not JsonObject o) { invalid++; continue; }
                string ty = o["_type"]?.ToString();
                if (ty == "subscription" || ty == "sub_invalid") continue;   // 订阅状态与非法云端数据不带出去也不带进来
                var one = new JsonArray(o.DeepClone());
                string err = MicApiBridge.ValidateBody("POST", "/api/tiaoxiu", one);
                if (err != null) { invalid++; msgs.Add($"{o["date"] ?? o["start_date"]}：{err}"); continue; }
                o["_type"] = "tiaoxiu";
                valid.Add(o);
            }
            var all = Program.ReadSettingMerged();
            var outAll = new JsonArray();
            foreach (var n in all) if (n?["_type"]?.ToString() != "tiaoxiu") outAll.Add(n.DeepClone());
            if (overwrite)
            {
                foreach (var o in valid) { outAll.Add(o.DeepClone()); r["imported"] = r["imported"].GetValue<int>() + 1; }
            }
            else
            {
                var seen = new HashSet<string>();
                foreach (var n in all)
                {
                    if (n?["_type"]?.ToString() != "tiaoxiu") continue;
                    seen.Add(TiaoxiuKey(n as JsonObject));
                }
                foreach (var o in valid)
                {
                    if (seen.Contains(TiaoxiuKey(o))) { r["skipped"] = r["skipped"].GetValue<int>() + 1; continue; }
                    outAll.Add(o.DeepClone());
                    r["imported"] = r["imported"].GetValue<int>() + 1;
                }
            }
            if (!dryRun) Program.WriteSettingMerged(outAll);
            r["invalid"] = invalid;
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            r["ok"] = invalid == 0;
            return r;
        }

        static string TiaoxiuKey(JsonObject o)
        {
            if (o == null) return "";
            string d = o["date"]?.ToString();
            if (string.IsNullOrEmpty(d)) d = (o["start_date"]?.ToString() ?? "") + "~" + (o["end_date"]?.ToString() ?? "");
            return d + "|" + (o["marker"]?.ToString() ?? "0") + "|" + (o["action"]?.ToString() ?? "");
        }

        /// <summary>预警配置：只认 `_type=warn`；运行时时间戳（last_*）一律保留本机值。</summary>
        static JsonObject DoWarn(Source src, bool overwrite, bool dryRun)
        {
            var msgs = new List<string>();
            var parsed = ParseJsonl(src.Config(Warn), msgs);
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = parsed.ParseErrors, ["messages"] = new JsonArray() };
            var raw = parsed.Items;
            JsonObject srcWarn = null;
            foreach (var n in raw)
            {
                if (n is not JsonObject o) continue;
                if (o["_type"]?.ToString() == "warn") { srcWarn = o; break; }
            }
            if (srcWarn == null) { r["ok"] = false; r["invalid"] = r["invalid"].GetValue<int>() + 1; msgs.Add("包里没有预警配置记录（_type=warn）"); }
            else
            {
                string err = ValidateWarn(srcWarn);
                if (err != null) { r["ok"] = false; r["invalid"] = r["invalid"].GetValue<int>() + 1; msgs.Add(err); }
                else
                {
                    var all = Program.ReadJsonl(WeatherWarn.WarnFile);
                    var outArr = new JsonArray();
                    JsonObject localWarn = null;
                    foreach (var n in all)
                    {
                        if (n is JsonObject o && o["_type"]?.ToString() == "warn") { localWarn = o; continue; }   // 稍后统一写出
                        outArr.Add(n.DeepClone());                                                                  // warn_cache（历史）原样保留
                    }
                    var merged = localWarn != null ? (JsonObject)localWarn.DeepClone() : new JsonObject();
                    merged["_type"] = "warn";
                    if (overwrite)
                        foreach (var k in merged.Select(kv => kv.Key).ToList())
                            if (k != "_type" && k != "last_try" && k != "last_ok" && k != "last_error") merged.Remove(k);
                    foreach (var kv in srcWarn)
                    {
                        if (kv.Key == "_type") continue;
                        if (kv.Key == "last_try" || kv.Key == "last_ok" || kv.Key == "last_error") continue;   // 运行时状态：保留本机
                        merged[kv.Key] = kv.Value?.DeepClone();
                    }
                    outArr.Insert(0, merged);
                    if (!dryRun) Program.WriteJsonl(WeatherWarn.WarnFile, outArr);
                    r["imported"] = 1;
                }
            }
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            return r;
        }

        static string ValidateWarn(JsonObject w)
        {
            int[] bounds = { 1440, 1440, 10, 100 };
            string[] keys = { "interval_min", "prefetch_min", "repeat", "rate" };
            for (int i = 0; i < keys.Length; i++)
            {
                if (w[keys[i]] == null) continue;
                int v;
                try { v = w[keys[i]].GetValue<int>(); } catch { return keys[i] + " 必须是整数"; }
                if (v < 0 || v > bounds[i]) return $"{keys[i]} 超出范围（0..{bounds[i]}）";
            }
            foreach (var k in new[] { "enabled", "gain", "follow_tiaoxiu" })
                if (w[k] != null && w[k] is not JsonValue) return k + " 必须是布尔值";
            if (w["windows"] is JsonArray wins)
            {
                if (wins.Count > 20) return "windows 过多（>20）";
                foreach (var n in wins)
                {
                    if (n is not JsonObject win) return "windows 元素必须是对象";
                    if (win["days"] is JsonArray ds)
                    {
                        foreach (var d in ds)
                        {
                            int dv; try { dv = d.GetValue<int>(); } catch { return "windows.days 必须是整数"; }
                            if (dv < 1 || dv > 7) return "windows.days 取值必须是 1..7";
                        }
                    }
                    foreach (var k in new[] { "start", "end" })
                    {
                        string s = win[k]?.ToString();
                        if (s == null) continue;
                        if (s.Length != 5 || s[2] != ':') return $"windows.{k} 必须是 HH:mm";
                    }
                }
            }
            foreach (var k in new[] { "list_url", "detail_url" })
            {
                string s = w[k]?.ToString();
                if (!string.IsNullOrEmpty(s) && !(s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    return k + " 必须是 http/https 地址";
            }
            return null;
        }

        /// <summary>系统设置：只处理 `_type=global`；屏幕相关与迁移标记不导入；来源口令为空时不覆盖本机口令。</summary>
        static JsonObject DoSettings(Source src, bool overwrite, bool dryRun)
        {
            var msgs = new List<string>();
            var parsed = ParseJsonl(src.Config(Settings), msgs);
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = parsed.ParseErrors, ["messages"] = new JsonArray() };
            var raw = parsed.Items;
            JsonObject srcGlobal = null;
            foreach (var n in raw) if (n is JsonObject o && o["_type"]?.ToString() == "global") { srcGlobal = o; break; }
            if (srcGlobal == null) { r["ok"] = false; r["invalid"] = r["invalid"].GetValue<int>() + 1; msgs.Add("包里没有系统设置记录（_type=global）"); }
            else
            {
                var all = Program.ReadJsonl("setting.jsonl");
                var outArr = new JsonArray();
                JsonObject local = null;
                foreach (var n in all)
                {
                    if (n is JsonObject o && o["_type"]?.ToString() == "global") { local = o; continue; }
                    outArr.Add(n.DeepClone());
                }
                var merged = local != null ? (JsonObject)local.DeepClone() : new JsonObject();
                merged["_type"] = "global";
                if (overwrite)
                    foreach (var k in merged.Select(kv => kv.Key).ToList())
                        if (k != "_type" && !SettingLocalOnly.Contains(k) && k != "password") merged.Remove(k);
                int applied = 0;
                foreach (var kv in srcGlobal)
                {
                    string k = kv.Key;
                    if (k == "_type") continue;
                    if (SettingLocalOnly.Contains(k)) continue;
                    if (k == "password")
                    {
                        // 来源没带口令（导出时未勾隐私）→ 保留本机口令，绝不把本机口令清空
                        string sp = kv.Value?.ToString() ?? "";
                        if (sp.Length == 0) continue;
                    }
                    merged[k] = kv.Value?.DeepClone();
                    applied++;
                }
                outArr.Insert(0, merged);
                if (!dryRun) Program.WriteJsonl("setting.jsonl", outArr);
                r["imported"] = 1;
                if (applied == 0) { r["skipped"] = 1; msgs.Add("没有可应用的设置项（来源可能未包含系统设置）"); }
            }
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            return r;
        }

        /// <summary>vnt：密钥类字段永不覆盖；主机名不同时只取服务器地址（规格 §0-2 与 §6.1）。</summary>
        static JsonObject DoVnt(Source src, bool sameHost, bool dryRun)
        {
            var msgs = new List<string>();
            var parsed = ParseJsonl(src.Config(Vnt), msgs);
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = parsed.ParseErrors, ["messages"] = new JsonArray() };
            var raw = parsed.Items;
            var all = Program.ReadJsonl("vnt.jsonl");
            var outArr = new JsonArray();
            var localByType = new Dictionary<string, JsonObject>();
            foreach (var n in all)
            {
                if (n is not JsonObject o) continue;
                // 关键：入表的就是"要写出去的那一份副本"本身。
                // 若这里放 DeepClone 进 outArr、却把原对象放进 localByType，后面改的就不是写出去的对象
                // （曾经因此 server 永远更新不了 —— 自检 TestConfigTransfer 抓到）。
                var copy = (JsonObject)o.DeepClone();
                outArr.Add(copy);
                if (copy["_type"] != null) localByType[copy["_type"].ToString()] = copy;
            }
            int applied = 0, protectedCount = 0;
            foreach (var n in raw)
            {
                if (n is not JsonObject so) continue;
                string ty = so["_type"]?.ToString();
                if (ty != "vnt" && ty != "mic") continue;
                if (!sameHost && ty != "vnt")
                {
                    msgs.Add("来源主机与本机不同：话筒配置（mic）未导入");
                    continue;
                }
                var target = localByType.TryGetValue(ty, out var t) ? t : null;
                if (target == null)
                {
                    target = new JsonObject { ["_type"] = ty };
                    outArr.Add(target);
                    localByType[ty] = target;
                }
                foreach (var kv in so)
                {
                    string k = kv.Key;
                    if (k == "_type") continue;
                    if (VntNeverOverwrite.Contains(k)) { protectedCount++; continue; }          // 永不覆盖
                    if (!sameHost && k != "server")                                             // 跨机只取服务器地址
                    {
                        bool empty = target[k] == null || (target[k] is JsonValue lv && string.IsNullOrEmpty(lv.ToString()));
                        if (!empty) { protectedCount++; continue; }
                    }
                    target[k] = kv.Value?.DeepClone();
                    applied++;
                }
            }
            if (applied > 0 && !dryRun) Program.WriteJsonl("vnt.jsonl", outArr);
            r["imported"] = applied > 0 ? 1 : 0;
            r["skipped"] = protectedCount;
            if (applied == 0) msgs.Add("没有可应用的 vnt 字段");
            if (protectedCount > 0) msgs.Add($"{protectedCount} 个密钥类/本机字段按规则未覆盖（设备 ID、令牌、数据密码、凭证密钥、动态码种子一律以本机为准）");
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            return r;
        }

        /// <summary>音乐库：只导入编号不冲突的条目，冲突的跳过并保留现有（规格 §0-3）。</summary>
        static JsonObject DoMusic(Source src, bool dryRun)
        {
            var msgs = new List<string>();
            var parsed = ParseJsonl(src.Config(Music), msgs);
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = parsed.ParseErrors, ["messages"] = new JsonArray() };
            var raw = parsed.Items;
            var local = Program.ReadJsonl("music.jsonl");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outArr = new JsonArray();
            foreach (var n in local)
            {
                if (n is JsonObject lo && lo["_type"]?.ToString() == "settings") { outArr.Add(lo.DeepClone()); continue; }
                string id = n?["id"]?.ToString() ?? "";
                if (id.Length > 0) used.Add(id);
                outArr.Add(n.DeepClone());
            }
            int invalid = parsed.ParseErrors;
            foreach (var n in raw)
            {
                if (n is not JsonObject o) { invalid++; continue; }
                if (o["_type"]?.ToString() == "settings") continue;
                string id = o["id"]?.ToString() ?? "";
                if (id.Length == 0 || id == WeatherWarn.MusicId.ToString()) { r["skipped"] = r["skipped"].GetValue<int>() + 1; continue; }
                string type = o["type"]?.ToString() ?? "file";
                if (type != "file" && type != "folder" && type != "hls") { invalid++; msgs.Add($"编号 {id}：type 取值不合法（{type}）"); continue; }
                if (used.Contains(id)) { r["skipped"] = r["skipped"].GetValue<int>() + 1; continue; }   // 编号冲突：保留现有
                var c = (JsonObject)o.DeepClone();
                c["path"] = Program.NormalizeMusicPath(c["path"]?.ToString() ?? "");
                if (c["name"] == null) c["name"] = "";
                outArr.Add(c);
                used.Add(id);
                r["imported"] = r["imported"].GetValue<int>() + 1;
            }
            if (!dryRun) Program.WriteJsonl("music.jsonl", outArr);
            r["invalid"] = invalid;
            if (r["skipped"].GetValue<int>() > 0) msgs.Insert(0, $"{r["skipped"]} 条因编号冲突被跳过（保留现有条目）");
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            r["ok"] = invalid == 0;
            return r;
        }

        /// <summary>铃声：同名覆盖、不同名新增（规格 §4-3）；只接受音频扩展名，且文件名不允许带路径分隔符。</summary>
        static JsonObject DoRing(Source src, bool dryRun)
        {
            var msgs = new List<string>();
            var r = new JsonObject { ["ok"] = true, ["imported"] = 0, ["skipped"] = 0, ["invalid"] = 0, ["messages"] = new JsonArray() };
            if (!dryRun) Directory.CreateDirectory(Program.RingDir);
            int invalid = 0;
            foreach (var (name, data) in src.RingFiles())
            {
                if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..")) { invalid++; msgs.Add($"文件名不合法：{name}"); continue; }
                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (!RingExts.Contains(ext)) { invalid++; msgs.Add($"不是支持的音频扩展名：{name}"); continue; }
                if (!dryRun)
                {
                    try { File.WriteAllBytes(Path.Combine(Program.RingDir, name), data); }
                    catch (Exception ex) { invalid++; msgs.Add($"写入失败 {name}：{ex.Message}"); continue; }
                }
                r["imported"] = r["imported"].GetValue<int>() + 1;
            }
            if (r["imported"].GetValue<int>() == 0 && invalid == 0) msgs.Add("包里没有铃声文件");
            r["invalid"] = invalid;
            foreach (var m in msgs) ((JsonArray)r["messages"]).Add(m);
            r["ok"] = invalid == 0;
            return r;
        }
    }
}
