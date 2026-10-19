#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 离线自检：校验关键业务逻辑（目录断点、稳定 ID、调休优先级、订阅合并、端口探测）。
    /// 通过 `BroadcastSystemNet.exe --self-test` 运行，不启动界面、不触碰真实数据目录、不联网。
    /// </summary>
    public static class SelfTest
    {
        static int _pass, _fail, _skip;
        static StringBuilder _report = new StringBuilder();
        static string _outPath;

        static void Step(string name)
        {
            try
            {
                if (!string.IsNullOrEmpty(_tracePath))
                    File.AppendAllText(_tracePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + name + "\r\n");
            }
            catch { }
        }

        static string _tracePath;

        static void Line(string s)
        {
            _report.AppendLine(s);
            try { Console.Out.Write(s + Environment.NewLine); Console.Out.Flush(); } catch { }
        }

        static void Check(bool ok, string name, string detail = "")
        {
            if (ok) { _pass++; Line($"  [PASS] {name}"); }
            else { _fail++; Line($"  [FAIL] {name}  {detail}"); }
        }

        /// <summary>
        /// 环境不满足时计为 SKIP 而不是 FAIL。
        /// 有些断言测的是**本机环境**（是否装了中文 SAPI 语音包、ffmpeg 是否可用），在没装语音包
        /// 或被安全软件拦住的机器上必然失败 —— 那会把自检的绿灯淹掉、让它没法当发布门禁用（report.md M1）。
        /// 跳过不计入失败，但报告里仍留一行，保证"这项没测"是可见的、不会变成静默通过。
        /// </summary>
        static void Skip(string name, string why)
        {
            _skip++;
            Line($"  [SKIP] {name}  {why}");
        }

        /// <summary>[envOk] 为假时计 SKIP；为真时按普通断言判定。</summary>
        static void CheckOrSkip(bool envOk, string envWhy, bool ok, string name, string detail = "")
        {
            if (!envOk) { Skip(name, envWhy); return; }
            Check(ok, name, detail);
        }

        /// <summary>
        /// 错误文本是否属于"**环境拦截**"而不是代码缺陷：安全软件（火绒 HIPS 等）拦住写文件、
        /// 文件被别的进程占住、或音频设备/语音流被拒。实测过：普通 ffmpeg 命令行写 %TEMP% 也会
        /// 报 -13 Permission denied，与我们的代码无关（README 第十节第 2 条记录过这个坑）。
        /// </summary>
        static bool IsEnvBlocked(string why)
        {
            if (string.IsNullOrEmpty(why)) return false;
            return why.Contains("Permission denied")
                || why.Contains("拒绝访问")
                || why.Contains("used by another process")
                || why.Contains("being used by another process")
                || why.Contains("ffmpeg 退出码 -13")
                || why.Contains("0x80045040");
        }

        /// <summary>
        /// 环境探针通过、但**操作被环境拦住**时也计 SKIP（原因照实打印，不会静默通过）。
        /// 探针只能看出"语音包装了 / ffmpeg 文件在"，看不出"安全软件不让写文件"。
        /// </summary>
        static void CheckOrSkipEnv(bool envOk, string envWhy, bool ok, string why, string name, string detail = "")
        {
            if (!envOk) { Skip(name, envWhy); return; }
            if (!ok && IsEnvBlocked(why)) { Skip(name, "环境拦截（安全软件/权限）：" + why); return; }
            Check(ok, name, detail);
        }

        public static int Run(string[] args)
        {
            string root = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--workdir" && i + 1 < args.Length) root = args[i + 1];
                if (args[i] == "--out" && i + 1 < args.Length) _outPath = args[i + 1];
                if (args[i] == "--trace" && i + 1 < args.Length) _tracePath = args[i + 1];
            }
            int exitCode = 2;
            try
            {
                if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "CBS_SelfTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));

                // 把程序的数据目录/配置目录重定向到临时沙箱，绝不动用户的真实数据
                Program.AppDataDir = root;
                Program.ConfigDir = Path.Combine(root, "config");
                Program.RingDir = Path.Combine(root, "ring");
                Program.LogDir = Path.Combine(root, "logs");
                Program.ErrorLogPath = Path.Combine(root, "system_error.log");
                Directory.CreateDirectory(Program.ConfigDir);
                Directory.CreateDirectory(Program.RingDir);
                Directory.CreateDirectory(Program.LogDir);
                PlayLog.Start(Program.LogDir);

                Line($"自检工作目录: {root}");
                Line("");

                Step("start");
                TestResume();
                Step("resume done");
                TestMusicIds();
                Step("musicids done");
                TestTiaoxiuPriority();
                Step("tiaoxiu done");
                TestSubscriptionMerge();
                Step("merge done");
                TestSubscriptionInvalidAndUrl();
                Step("invalid+url done");
                TestSchedulerWindowAndArgs();
                Step("scheduler done");
                TestFolderResumeFlow();
                Step("folder resume done");
                TestBackupRestoreRoundTrip();
                Step("backup done");
                TestConfigTransfer();
                Step("transfer done");
                TestDialogLayout();
                Step("dialog layout done");
                TestAlertCenter();
                Step("alerts done");
                TestSubscriptionFormat();
                Step("subscription format done");
                TestPortProbe();
                Step("port done");
                TestWeatherWarn();
                Step("weather warn done");

                Line("");
                Line($"结果: 通过 {_pass} 项，失败 {_fail} 项"
                     + (_skip > 0 ? $"，跳过 {_skip} 项（本机环境不满足，不计失败）" : ""));
                exitCode = _fail == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Line("自检异常: " + ex);
                exitCode = 2;
            }
            finally
            {
                if (root != null && root.Contains("CBS_SelfTest_"))
                {
                    try { Directory.Delete(root, true); Line("临时目录已清理"); } catch { Line("临时目录保留: " + root); }
                }
                if (!string.IsNullOrEmpty(_outPath)) { try { File.WriteAllText(_outPath, _report.ToString(), new UTF8Encoding(false)); } catch { } }
            }
            return exitCode;
        }

        // ---------- 1. 目录断点续播 ----------
        static void TestResume()
        {
            Line("【1】目录断点续播 resume.json");
            // 路径用**示例值**：这里原来写的是使用者本机的真实音乐目录（脱敏 2026-10-13）
            const string dir = "D:/示例音乐目录";
            Check(Program.GetFolderResumeIndex(dir) == 0, "无记录时返回 0");

            Program.SaveFolderResumeIndex(dir, 4, 0);
            string raw = File.ReadAllText(Path.Combine(Program.ConfigDir, "resume.json"));
            Check(File.Exists(Path.Combine(Program.ConfigDir, "resume.json")), "写入的是 resume.json");
            Check(!File.Exists(Path.Combine(Program.ConfigDir, "resume.jsonl")), "不再生成 resume.jsonl");
            Check(raw.Contains("\"index\""), "结构为 {path:{index:..}}", raw);
            Check(Program.GetFolderResumeIndex(dir) == 4, "读回推进后的断点 = 4");

            // 兼容历史结构：旧的扁平写法
            File.WriteAllText(Path.Combine(Program.ConfigDir, "resume.json"), "{\"D:/示例音乐目录\": 7}");
            Check(Program.GetFolderResumeIndex(dir) == 7, "兼容旧的扁平写法 {\"路径\": 7}");

            // 兼容线上真实结构（带 elapsed）
            File.WriteAllText(Path.Combine(Program.ConfigDir, "resume.json"), "{\"D:/示例音乐目录\": {\"index\": 3, \"elapsed\": 0.0}}");
            Check(Program.GetFolderResumeIndex(dir) == 3, "兼容线上真实结构 {index,elapsed}");
            Program.SaveFolderResumeIndex(dir, 5, 0);
            raw = File.ReadAllText(Path.Combine(Program.ConfigDir, "resume.json"));
            Check(raw.Contains("\"elapsed\""), "推进断点时保留 elapsed 字段", raw);
            Line("");
        }

        // ---------- 2. 稳定 ID + 最小空缺回填 ----------
        static void TestMusicIds()
        {
            Line("【2】音乐库稳定 ID 与空缺回填");
            var arr = new JsonArray();
            foreach (var id in new[] { "1", "2", "3", "5" })
                arr.Add(new JsonObject { ["id"] = id, ["name"] = "m" + id, ["path"] = "x.mp3", ["type"] = "file" });

            Check(Program.AllocateMusicId(arr) == "4", "空缺 4 被优先回填", Program.AllocateMusicId(arr));
            Check(Program.AllocateMusicId(new JsonArray()) == "1", "空库从 1 开始");

            var full = new JsonArray();
            foreach (var id in new[] { "1", "2", "3" }) full.Add(new JsonObject { ["id"] = id });
            Check(Program.AllocateMusicId(full) == "4", "无空缺时取 max+1");

            var batch = Program.AllocateMusicIds(arr, 3);
            Check(string.Join(",", batch) == "4,6,7", "批量分配按从小到大填空缺", string.Join(",", batch));

            // 保留编号 1000（气象预警播报）是虚拟条目，永远不在 music.jsonl 里，但**也不能被分配出去**：
            // 否则编号连续排到 999 时新条目会分到 1000，与虚拟条目重号 —— 调度侧永远走预警分支，
            // 那条真实音乐就永远播不到（静默失效）。
            var upTo998 = new JsonArray();
            for (int i = 1; i <= 998; i++) upTo998.Add(new JsonObject { ["id"] = i.ToString() });
            Check(Program.AllocateMusicId(upTo998) == "999", "999 仍可正常分配（保留号只占 1000）",
                Program.AllocateMusicId(upTo998));
            var upTo999 = new JsonArray();
            for (int i = 1; i <= 999; i++) upTo999.Add(new JsonObject { ["id"] = i.ToString() });
            Check(Program.AllocateMusicId(upTo999) == "1001", "编号排到 999 时跳过保留号 1000（分到 1001）",
                Program.AllocateMusicId(upTo999));
            Check(string.Join(",", Program.AllocateMusicIds(upTo999, 2)) == "1001,1002",
                "批量分配同样跳过保留号 1000", string.Join(",", Program.AllocateMusicIds(upTo999, 2)));
            var has1000 = new JsonArray { new JsonObject { ["id"] = "1000" } };
            Check(Program.AllocateMusicId(has1000) == "1", "库里若已有 1000 号，仍从 1 开始填空缺",
                Program.AllocateMusicId(has1000));

            // 查找与删除：只删指定编号
            Program.WriteJsonl("music.jsonl", arr);
            Check(Program.FindMusic(Program.ReadJsonl("music.jsonl"), "3") != null, "按编号查得到条目");
            Check(Program.FindMusic(Program.ReadJsonl("music.jsonl"), "99") == null, "不存在的编号返回 null");
            Check(Program.RemoveMusicEntry("3"), "删除编号 3 成功");
            var after = Program.ReadJsonl("music.jsonl");
            var ids = new List<string>();
            foreach (var m in after) ids.Add(m["id"]?.ToString());
            Check(string.Join(",", ids) == "1,2,5", "删除后其余编号不发生重排", string.Join(",", ids));
            Check(Program.AllocateMusicId(after) == "3", "被释放的 3 号可被新条目顶替");

            // 复刻用户实际遇到的场景：1..12 号，删掉中间几首，后面的编号绝不能跟着变
            var big = new JsonArray();
            for (int i = 1; i <= 12; i++) big.Add(new JsonObject { ["id"] = i.ToString(), ["name"] = "song" + i, ["path"] = $"ring/{i}.mp3", ["type"] = "file" });
            Program.WriteJsonl("music.jsonl", big);
            foreach (var del in new[] { "3", "7", "8" }) Program.RemoveMusicEntry(del);
            var remain = new List<string>();
            foreach (var m in Program.ReadJsonl("music.jsonl")) remain.Add(m["id"]?.ToString());
            Check(string.Join(",", remain) == "1,2,4,5,6,9,10,11,12", "删除中间条目后其余编号不漂移（复刻用户 bug）", string.Join(",", remain));

            var added1 = Program.AddMusicEntry("新歌A", "ring/newA.mp3", "file");
            var added2 = Program.AddMusicEntry("新歌B", "ring/newB.mp3", "file");
            Check(added1["id"]?.ToString() == "3" && added2["id"]?.ToString() == "7", "新增按从小到大顶替空缺（3 然后 7）", $"{added1["id"]}/{added2["id"]}");
            var after2 = new List<string>();
            foreach (var m in Program.ReadJsonl("music.jsonl")) after2.Add(m["id"]?.ToString());
            after2.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));
            Check(string.Join(",", after2) == "1,2,3,4,5,6,7,9,10,11,12", "顶替后编号集合正确（8 仍为空缺）", string.Join(",", after2));
            var added3 = Program.AddMusicEntry("新歌C", "ring/newC.mp3", "file");
            Check(added3["id"]?.ToString() == "8", "下一个新增顶替剩余空缺 8", added3["id"]?.ToString());
            Line("");
        }

        // ---------- 3. 调休优先级（分标记优先于全局） ----------
        static void TestTiaoxiuPriority()
        {
            Line("【3】调休判定：分标记(1-10) 优先于全局(0)，后写入覆盖先写入");
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            const int marker = 1;

            // 全局 disable 写在前面、分标记 enable 写在后面 → 该标记的任务应仍然播放
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "disable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "enable", ["source"] = "manual" }
            });
            Check(ShouldPlay(marker), "全局 disable + 本标记 enable ⇒ 播放");

            // 调换顺序（分标记在前）也必须同结果，避免依赖文件行序
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "enable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "disable", ["source"] = "manual" }
            });
            Check(ShouldPlay(marker), "顺序调换后结果一致（不依赖写入顺序）");

            // 只有全局 disable 时，未设置分标记规则的普通任务应被全局拦下
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "disable", ["source"] = "manual" }
            });
            Check(!ShouldPlay(2), "仅全局 disable（标记2 无专属规则）⇒ 不播");
            Check(!ShouldPlay(marker), "仅全局 disable ⇒ 标记1 也不播");

            // 分标记 disable 优先于全局 enable
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "enable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "disable", ["source"] = "manual" }
            });
            Check(!ShouldPlay(marker), "全局 enable + 本标记 disable ⇒ 不播");
            Check(ShouldPlay(2), "全局 enable 且标记2 无专属规则 ⇒ 播放");

            // 同标记重复：后写入覆盖先写入
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "disable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "disable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "enable", ["source"] = "manual" }
            });
            bool sameMarker = ShouldPlay(marker);
            Check(sameMarker, "同标记冲突时后写入的 enable 生效（disable→enable）");

            // 反向：后写入的是 disable，则应压住先前的 enable
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "enable", ["source"] = "manual" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 1, ["action"] = "disable", ["source"] = "manual" }
            });
            Check(!ShouldPlay(marker), "同标记冲突时后写入的 disable 生效（enable→disable）");
            Line("");
        }

        // ---------- 话筒端到端自检的三个小工具（真实 socket） ----------

        /// <summary>走真实 TCP 发一行 HELLO 并读回一行应答。</summary>
        static string PairOverTcp(int port, string helloLine)
        {
            using var c = new System.Net.Sockets.TcpClient();
            c.Connect("127.0.0.1", port);
            var s = c.GetStream();
            var outBytes = System.Text.Encoding.UTF8.GetBytes(helloLine + "\n");
            s.Write(outBytes, 0, outBytes.Length);
            s.Flush();
            var buf = new byte[2048];
            int n = s.Read(buf, 0, buf.Length);
            return n > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, n).Trim() : "";
        }

        static void UdpSend(string host, int port, byte[] data)
        {
            using var u = new System.Net.Sockets.UdpClient();
            u.Send(data, data.Length, host, port);
        }

        /// <summary>等条件成立（默认最多 1.5 秒），用于等异步收包后的计数变化。</summary>
        static bool WaitFor(Func<bool> cond, int timeoutMs = 1500)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (cond()) return true; } catch { }
                System.Threading.Thread.Sleep(20);
                waited += 20;
            }
            try { return cond(); } catch { return false; }
        }

        static bool ShouldPlay(int taskMarker)        {
            var task = new JsonObject
            {
                ["id"] = 1, ["start_time"] = "08:00:00", ["days"] = new JsonArray(),
                ["specific_dates"] = new JsonArray(), ["tiaoxiu_marker"] = taskMarker,
                ["music_ids"] = new JsonArray { "1" }
            };
            return Program.ShouldPlayTodayForTest(task);
        }

        static void WriteSetting(JsonArray items) => Program.WriteSettingMerged( items);

        // ---------- 4. 订阅合并：手动优先 ----------
        static void TestSubscriptionMerge()
        {
            Line("【4】云端订阅合并（手动优先）");
            string today = DateTime.Now.ToString("yyyy-MM-dd");

            // 本地：手动 disable 占用 today/标记0；另有一条“旧的云端记录”应被清理
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = today, ["end_date"] = today, ["marker"] = 0, ["action"] = "disable", ["source"] = "manual", ["note"] = "手动禁用" },
                new JsonObject { ["_type"] = "tiaoxiu", ["start_date"] = "2000-01-01", ["end_date"] = "2000-01-01", ["marker"] = 0, ["action"] = "enable", ["source"] = "sub", ["note"] = "过期云端记录" }
            });

            // 云端：与手动冲突的一条（enable，应被手动 disable 压住）+ 一条全新的
            var cloud = new JsonArray {
                new JsonObject { ["date"] = today, ["action"] = "enable", ["marker"] = 0, ["note"] = "云端想启用" },
                new JsonObject { ["date"] = "2099-01-01", ["action"] = "disable", ["marker"] = 3, ["note"] = "元旦" }
            };
            int manualCount = 0, subCount = 0, staleSub = 0;
            var merged = Program.MergeSubscription(cloud, out manualCount, out subCount, out staleSub);

            var subDates = new List<string>();
            bool hasManualToday = false;
            foreach (var n in merged)
            {
                var o = n as JsonObject; if (o == null) continue;
                if (o["_type"]?.ToString() != "tiaoxiu") continue;
                string src = o["source"]?.ToString() ?? "";
                if (src == "sub") subDates.Add(o["start_date"]?.ToString() + "/" + o["marker"]);
                if (src == "manual" && o["start_date"]?.ToString() == today) hasManualToday = true;
            }
            Check(hasManualToday, "手动记录保留");
            Check(!subDates.Contains(today + "/0"), "与手动同一天的云端记录被手动压住（未写入）", string.Join(",", subDates));
            Check(subDates.Contains("2099-01-01/3"), "无冲突的云端记录正常写入", string.Join(",", subDates));
            Check(!subDates.Contains("2000-01-01/0"), "被手动取代/过期的旧云端记录已清理", string.Join(",", subDates));
            Check(manualCount == 1 && subCount == 1, $"统计正确（手动{manualCount} 云端{subCount}）");

            // 非法数据必须被单独保留，而不是静默丢弃
            var invalid = Program.LastInvalidItems;
            Check(invalid != null && invalid.Count == 0, "本次无非法数据时不生成无效清单");
            Line("");
        }

        // ---------- 5. 订阅非法数据与 URL 处理 ----------
        static void TestSubscriptionInvalidAndUrl()
        {
            Line("【5】订阅：非法数据保留 + URL 缺失/非法时拒绝同步");
            WriteSetting(new JsonArray {
                new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "", ["subscription_url"] = "https://example.invalid/sub.json" }
            });
            Check(Program.GetSubscriptionUrl() == "https://example.invalid/sub.json", "能读到订阅地址");

            var cloud = new JsonArray {
                new JsonObject { ["date"] = "2099-05-01", ["action"] = "disable", ["marker"] = 0 },
                new JsonObject { ["date"] = "不是日期", ["action"] = "disable", ["marker"] = 0 },
                new JsonObject { ["date"] = "2099-05-02", ["action"] = "explode", ["marker"] = 0 },
                new JsonObject { ["date"] = "2099-05-03", ["action"] = "enable", ["marker"] = "abc" },
                new JsonObject { ["action"] = "disable" }
            };
            int m, s, o;
            var merged = Program.MergeSubscription(cloud, out m, out s, out o);
            Check(s == 1, "合法的 1 条云端记录生效", "sub=" + s);
            Check(Program.LastInvalidItems.Count == 4, "4 条非法记录被保留（不静默丢弃）", "invalid=" + Program.LastInvalidItems.Count);
            var reasons = new List<string>();
            foreach (var n in Program.LastInvalidItems) reasons.Add((n as JsonObject)?["reason"]?.ToString() ?? "");
            Check(reasons.Exists(r => r.Contains("无法解析")), "日期非法被标注原因");
            Check(reasons.Exists(r => r.Contains("action")), "action 非法被标注原因");
            Check(reasons.Exists(r => r.Contains("marker")), "marker 非法被标注原因");
            Check(reasons.Exists(r => r.Contains("缺少 date")), "缺 date 被标注原因");

            // 订阅组被显式清空时（= 用户主动删除）应直接拒绝，不抛异常、也不联网
            WriteSetting(new JsonArray { new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "", ["subscription_urls"] = new JsonArray() } });
            bool ok = false;
            try { ok = Program.ForceSyncSubscription().GetAwaiter().GetResult(); } catch { ok = true; }
            Check(ok == false, "订阅组为空（用户主动删除）时同步返回 false（不抛异常、不联网）");
            Check(Program.GetSubscriptionUrls().Count == 0, "清空后不会自动回填默认订阅组");
            Line("");
        }

        // ---------- 6. 调度触发窗口与 ffplay 参数 ----------
        static void TestSchedulerWindowAndArgs()
        {
            Console.WriteLine("【6】调度触发窗口（到点后 60 秒内，仅当天）与 ffplay 参数");
            int t = 8 * 3600 + 20 * 60;   // 08:20:00
            Check(Program.TriggerWindowSeconds == 60, "触发窗口 = 60 秒（原 5 秒太窄：调度卡顿一下就会静默漏播）", Program.TriggerWindowSeconds.ToString());
            Check(Program.IsTriggerWindow(t, t), "到点当秒触发");
            Check(Program.IsTriggerWindow(t + 5, t), "到点后第 5 秒仍触发（容忍定时器抖动）");
            Check(Program.IsTriggerWindow(t + 30, t), "到点后第 30 秒仍触发（系统卡顿/休眠唤醒后补触发）", "本次修复核心：原来第 6 秒就永久跳过且不留日志");
            Check(Program.IsTriggerWindow(t + 60, t), "到点后第 60 秒仍触发（窗口上限）");
            Check(!Program.IsTriggerWindow(t + 61, t), "到点后第 61 秒不再触发（窗口外）");
            Check(!Program.IsTriggerWindow(t - 1, t), "到点前不触发");
            Check(!Program.IsTriggerWindow(0, 23 * 3600 + 59 * 60 + 58), "跨零点不重复触发（负差被挡掉）");
            Check(!Program.IsTriggerWindow(3, t), "次日同一秒不触发（避免隔天重复响）");

            // 二.1b：桌面端 900ms 的状态刷新、悬浮球 1 秒一次的刷新，都已把配置读取挪到后台线程。
            // 这里断言这些读取确实能在非 UI 线程上并发安全执行（它们共用 ConfigIoLock）。
            bool bgOk = true; string bgErr = null;
            var bgThread = new System.Threading.Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Program.IsGlobalSwitchOn();
                        Program.ReadJsonl("tasks.jsonl");
                        Program.ReadSettingMerged();
                    }
                }
                catch (Exception ex) { bgOk = false; bgErr = ex.Message; }
            }) { IsBackground = true };
            bgThread.Start(); bgThread.Join(5000);
            Check(bgOk, "配置读取可在后台线程并发安全执行（UI 的 900ms/1s 刷新已移出主线程）", bgErr ?? "");

            // 手机端「广播总开关」走 POST /api/settings —— 必须纳入管理员写接口，
            // 否则任何配对设备都能停掉全校广播（电脑端/网页端不经过 MicApiBridge，不受影响）。
            Check(MicApiBridge.IsWritePath("/api/settings", "POST"), "手机端改系统设置（广播总开关）要求管理员权限");
            Check(!MicApiBridge.IsWritePath("/api/settings", "GET"), "读设置仍允许非管理员（只读不受影响）");

            // ffplay 参数
            var a1 = Program.BuildFfplayArgs("D:/x.mp3", 50, 0);
            Check(string.Join(" ", a1) == "-nodisp -autoexit -af volume=0.50 D:/x.mp3", "不限时长：不加 -t，音量按 volume=0.50 传递", string.Join(" ", a1));
            var a2 = Program.BuildFfplayArgs("D:/x.mp3", 100, 6.5);
            Check(string.Join(" ", a2) == "-nodisp -autoexit -af volume=1.00 -t 6.50 D:/x.mp3", "限时：追加 -t 且保留两位小数", string.Join(" ", a2));
            var a3 = Program.BuildFfplayArgs("https://example/live.m3u8", 30, 0);
            Check(a3.Count > 0 && a3[a3.Count - 1].StartsWith("https://"), "网络源作为最后一个参数传入");

            // 下一次排程计算
            var tasks = new List<JsonObject>
            {
                new JsonObject { ["start_time"] = "08:20:00" },
                new JsonObject { ["start_time"] = "09:05" },
                new JsonObject { ["start_time"] = "23:59:00" }
            };
            int next = Program.SecondsToNextTask(tasks, 8 * 3600, DateTime.Now);
            Check(next == 20 * 60, "下一次排程 = 08:20（20 分钟后）", next.ToString());
            int nextWrap = Program.SecondsToNextTask(tasks, 23 * 3600 + 59 * 60 + 30, DateTime.Now);
            Check(nextWrap == 8 * 3600 + 20 * 60 + 30, "跨零点回绕：23:59:30 之后下一次是次日 08:20（30030 秒后）", nextWrap.ToString());
            Console.WriteLine();
        }

        // ---------- 7. 目录展开与断点续播（真实文件） ----------
        static void TestFolderResumeFlow()
        {
            Console.WriteLine("【7】目录断点续播 + 引用失效（用真实文件走一遍）");
            string dir = Path.Combine(Program.AppDataDir, "selftest_music");
            Directory.CreateDirectory(dir);
            for (int i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(dir, $"song{i}.mp3"), "x");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");   // 非音频，应被忽略

            var music = new JsonArray
            {
                new JsonObject { ["id"] = "1", ["name"] = "目录", ["path"] = dir, ["type"] = "folder" },
                new JsonObject { ["id"] = "2", ["name"] = "铃声", ["path"] = "ring/01.mp3", ["type"] = "file" }
            };
            var task = new JsonObject { ["folder_play_count"] = 1, ["folder_mode"] = "sequential" };

            // 第一次：断点为空 → 应播 song1，并把断点推进到 1
            List<string> problems; List<Tuple<string, int, int>> notes;
            var q1 = Program.ExpandTaskQueue(task, music, new List<string> { "1" }, out problems, out notes);
            Check(q1.Count == 1 && Path.GetFileName(q1[0]["path"]?.ToString()) == "song1.mp3", "首次顺序播放取 song1", q1.Count > 0 ? q1[0]["path"]?.ToString() : "空");
            Check(problems.Count == 0, "没有引用失效");
            Check(notes.Count == 1 && notes[0].Item2 == 1, "断点推进到索引 1（下一首）", notes.Count > 0 ? notes[0].Item2.ToString() : "-");
            foreach (var n in notes) Program.SaveFolderResumeIndex(n.Item1, n.Item2, 0);
            int readBack = notes.Count > 0 ? Program.GetFolderResumeIndex(notes[0].Item1) : -1;
            Check(readBack == 1, "写盘后立刻读回断点 = 1", readBack.ToString());

            // 第二次：应接续 song2
            var q2 = Program.ExpandTaskQueue(task, music, new List<string> { "1" }, out problems, out notes);
            Check(q2.Count == 1 && Path.GetFileName(q2[0]["path"]?.ToString()) == "song2.mp3", "第二次接续取 song2（断点生效）", q2.Count > 0 ? q2[0]["path"]?.ToString() : "空");
            foreach (var n in notes) Program.SaveFolderResumeIndex(n.Item1, n.Item2, 0);

            // 一次取 3 首 + 回绕：当前断点 2，取 2,3,4，断点推进到 0（(2+3)%5）
            task["folder_play_count"] = 3;
            var q3 = Program.ExpandTaskQueue(task, music, new List<string> { "1" }, out problems, out notes);
            var names3 = string.Join(",", q3.Select(x => Path.GetFileName(x["path"]?.ToString())));
            Check(names3 == "song3.mp3,song4.mp3,song5.mp3", "一次取 3 首：song3,4,5", names3);
            Check(notes.Count == 1 && notes[0].Item2 == 0, "取满一轮后断点回绕到 0", notes.Count > 0 ? notes[0].Item2.ToString() : "-");

            // 随机模式：不推进断点
            task["folder_mode"] = "random"; task["folder_play_count"] = 2;
            var q4 = Program.ExpandTaskQueue(task, music, new List<string> { "1" }, out problems, out notes);
            Check(q4.Count == 2, "随机模式取 2 首", q4.Count.ToString());
            Check(notes.Count == 0, "随机模式不推进断点");

            // 引用失效：编号不存在 / 目录不存在 / 空目录
            var badMusic = new JsonArray
            {
                new JsonObject { ["id"] = "1", ["name"] = "不存在的目录", ["path"] = Path.Combine(dir, "nope"), ["type"] = "folder" },
                new JsonObject { ["id"] = "2", ["name"] = "铃声", ["path"] = "ring/01.mp3", ["type"] = "file" }
            };
            var q5 = Program.ExpandTaskQueue(task, badMusic, new List<string> { "99", "1", "2" }, out problems, out notes);
            Check(problems.Count == 2 && problems.Contains("99") && problems.Contains("1"), "失效编号被记录（99 与目录 1）", string.Join(",", problems));
            Check(q5.Count == 1, "其余有效条目照常播放（不中断整个任务）", q5.Count.ToString());

            Directory.Delete(dir, true);
            Console.WriteLine();
        }

        // ---------- 8. 备份 / 恢复往返 ----------
        static void TestBackupRestoreRoundTrip()
        {
            Console.WriteLine("【8】备份与恢复往返（与桌面端同一条代码路径）");
            string src = Path.Combine(Program.AppDataDir, "backup_src");
            string zip = Path.Combine(Program.AppDataDir, "backup_test.zip");
            string dst = Path.Combine(Program.AppDataDir, "backup_dst");
            try
            {
                Directory.CreateDirectory(Path.Combine(src, "config"));
                Directory.CreateDirectory(Path.Combine(src, "ring"));
                File.WriteAllText(Path.Combine(src, "config", "setting.jsonl"), "{\"_type\":\"global\"}");
                File.WriteAllText(Path.Combine(src, "config", "tasks.jsonl"), "{\"id\":1}");
                File.WriteAllText(Path.Combine(src, "ring", "01 预备铃.mp3"), "fake-audio");   // 含中文与空格的文件名
                if (File.Exists(zip)) File.Delete(zip);

                System.IO.Compression.ZipFile.CreateFromDirectory(src, zip, System.IO.Compression.CompressionLevel.Optimal, false);
                Check(File.Exists(zip) && new FileInfo(zip).Length > 0, "备份生成 zip 成功", new FileInfo(zip).Length.ToString() + " 字节");

                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dst, true);
                bool cfgOk = File.Exists(Path.Combine(dst, "config", "setting.jsonl"));
                bool tasksOk = File.Exists(Path.Combine(dst, "config", "tasks.jsonl"));
                bool ringOk = File.Exists(Path.Combine(dst, "ring", "01 预备铃.mp3"));
                Check(cfgOk && tasksOk, "恢复后配置文件齐全");
                Check(ringOk, "恢复后中文/含空格文件名完好");
                string a = File.ReadAllText(Path.Combine(src, "config", "setting.jsonl"));
                string b = File.ReadAllText(Path.Combine(dst, "config", "setting.jsonl"));
                Check(a == b, "恢复后内容一致");
            }
            catch (Exception ex) { Check(false, "备份/恢复往返", ex.Message); }
            finally
            {
                foreach (var d in new[] { src, dst }) { try { Directory.Delete(d, true); } catch { } }
                try { File.Delete(zip); } catch { }
            }
            Console.WriteLine();
        }

        // ---------- 按项导入 / 导出（ConfigTransfer） ----------
        // 覆盖：导出→清空→导入 往返、隐私不导出、主机名识别、vnt 密钥永不覆盖、
        //       口令不被清空、重复导入去重、音乐库编号冲突跳过、非法条目计数、旧格式包从严处理。
        static void TestConfigTransfer()
        {
            Console.WriteLine("【8b】按项导入 / 导出（ConfigTransfer：往返 + 各类安全规则）");
            string zip = Path.Combine(Program.AppDataDir, "transfer_test.zip");
            string badZip = Path.Combine(Program.AppDataDir, "transfer_bad.zip");
            string oldZip = Path.Combine(Program.AppDataDir, "transfer_old.zip");

            JsonObject FindRec(string file, string type)
            {
                foreach (var n in Program.ReadJsonl(file))
                    if (n is JsonObject o && o["_type"]?.ToString() == type) return o;
                return null;
            }
            string ReadZipEntry(string zipPath, string entryName)
            {
                using var z = System.IO.Compression.ZipFile.OpenRead(zipPath);
                foreach (var e in z.Entries)
                {
                    string nm = (e.FullName ?? "").Replace('\\', '/');
                    if (!string.Equals(nm, entryName, StringComparison.OrdinalIgnoreCase)
                        && !nm.EndsWith("/" + entryName, StringComparison.OrdinalIgnoreCase)) continue;
                    using var s = e.Open();
                    using var sr = new StreamReader(s, Encoding.UTF8);
                    return sr.ReadToEnd();
                }
                return null;
            }
            void PutEntry(System.IO.Compression.ZipArchive z, string name, string text)
            {
                var e = z.CreateEntry(name);
                using var s = e.Open();
                using var w = new StreamWriter(s, new UTF8Encoding(false));
                w.Write(text);
            }

            // 本测试会重写多份配置与铃声目录，必须先完整快照、最后还原，
            // 否则会污染后面的自检项（曾导致"管理员校验 / 安检门"三项误报失败）。
            var snapshot = new Dictionary<string, string>();
            foreach (var f in new[] { "tasks.jsonl", "music.jsonl", "warn.jsonl", "setting.jsonl", Program.TiaoxiuFile, "vnt.jsonl", "resume.json" })
            {
                string p = Path.Combine(Program.ConfigDir, f);
                snapshot[f] = File.Exists(p) ? File.ReadAllText(p) : null;
            }
            var ringSnap = new Dictionary<string, byte[]>();
            if (Directory.Exists(Program.RingDir))
                foreach (var f in Directory.GetFiles(Program.RingDir))
                    ringSnap[Path.GetFileName(f)] = File.ReadAllBytes(f);

            try
            {
                // ---------- 1) 准备"源电脑"的数据 ----------
                Program.WriteJsonl("tasks.jsonl", new JsonArray
                {
                    new JsonObject { ["id"]=1, ["start_time"]="07:30:00", ["duration_m"]=1, ["duration_s"]=0,
                        ["volume"]=50, ["days"]=new JsonArray(1,2,3,4,5), ["specific_dates"]=new JsonArray(),
                        ["music_ids"]=new JsonArray("1"), ["tiaoxiu_marker"]=1, ["sequence_mode"]="sequential",
                        ["folder_mode"]="sequential", ["folder_play_count"]=1, ["stop_mode"]="immediate" }
                });
                Program.WriteJsonl("music.jsonl", new JsonArray
                {
                    new JsonObject { ["id"]="1", ["name"]="预备铃", ["path"]="res/ring/01 预备铃.mp3", ["type"]="file" }
                });
                Program.WriteJsonl(WeatherWarn.WarnFile, new JsonArray
                {
                    // 地点/区码用示例值：原来写的是使用者真实的乡镇与区码（脱敏 2026-10-13）
                    new JsonObject { ["_type"]="warn", ["enabled"]=true, ["place"]="示例县", ["area_code"]="000000" }
                });
                Program.WriteSettingMerged(new JsonArray
                {
                    new JsonObject { ["_type"]="global", ["global_switch"]=true, ["password"]="secret-pw" },
                    new JsonObject { ["_type"]="tiaoxiu", ["date"]="2026-05-01", ["marker"]=0, ["action"]="disable", ["note"]="劳动节" }
                });
                Program.WriteJsonl("vnt.jsonl", new JsonArray
                {
                    new JsonObject { ["_type"]="vnt", ["device_id"]="LOCAL-DEVICE", ["token"]="LOCAL-TOKEN",
                        ["data_password"]="LOCAL-PW", ["cred_secret"]="LOCAL-CRED", ["server"]="1.2.3.4:29872" },
                    new JsonObject { ["_type"]="mic", ["code_seed"]="LOCAL-SEED", ["enabled"]=true }
                });
                Directory.CreateDirectory(Program.RingDir);
                File.WriteAllText(Path.Combine(Program.RingDir, "01 预备铃.mp3"), "audio-a");

                // ---------- 2) 导出（默认不含隐私） ----------
                if (File.Exists(zip)) File.Delete(zip);
                var ex = ConfigTransfer.Export(zip, ConfigTransfer.Order, false);
                Check(ex["status"]?.ToString() == "success" && File.Exists(zip) && new FileInfo(zip).Length > 0,
                    "按项导出生成 zip 成功", ex["message"]?.ToString());
                Check(ex["privacy"]?.GetValue<bool>() == false, "导出默认不含隐私数据");

                string manifest = ReadZipEntry(zip, ConfigTransfer.ManifestName);
                Check(!string.IsNullOrEmpty(manifest), "新格式包内含根目录清单 transfer.json");
                string host = null;
                try { host = (JsonNode.Parse(manifest) as JsonObject)?["host"]?.ToString(); } catch { }
                Check(host == Environment.MachineName, "清单记录了本机主机名（主机名一致的判断依据）", host);

                string vntText = ReadZipEntry(zip, "vnt.jsonl") ?? "";
                Check(!vntText.Contains("LOCAL-TOKEN") && !vntText.Contains("LOCAL-PW") && !vntText.Contains("LOCAL-CRED"),
                    "不导出隐私时令牌/数据密码/凭证密钥不进包");
                Check(vntText.Contains("1.2.3.4:29872"), "不导出隐私时仍保留服务器地址");
                Check(ReadZipEntry(zip, "tasks.jsonl") != null && ReadZipEntry(zip, "music.jsonl") != null
                      && ReadZipEntry(zip, "warn.jsonl") != null && ReadZipEntry(zip, "setting.jsonl") != null,
                    "勾选的各类配置都进了包");

                // ---------- 3) 清空本机配置，模拟"换了一台电脑" ----------
                Program.WriteJsonl("tasks.jsonl", new JsonArray());
                Program.WriteJsonl("music.jsonl", new JsonArray());
                Program.WriteJsonl(WeatherWarn.WarnFile, new JsonArray());
                Program.WriteSettingMerged(new JsonArray
                {
                    new JsonObject { ["_type"]="global", ["global_switch"]=true, ["password"]="KEEP-ME" }
                });
                Program.WriteJsonl("vnt.jsonl", new JsonArray
                {
                    new JsonObject { ["_type"]="vnt", ["device_id"]="LOCAL-DEVICE", ["token"]="LOCAL-TOKEN",
                        ["data_password"]="LOCAL-PW", ["cred_secret"]="LOCAL-CRED", ["server"]="9.9.9.9:1" },
                    new JsonObject { ["_type"]="mic", ["code_seed"]="LOCAL-SEED", ["enabled"]=true }
                });
                try { Directory.Delete(Program.RingDir, true); } catch { }
                Directory.CreateDirectory(Program.RingDir);

                // ---------- 4) 预览：必须不写盘 ----------
                var pv = ConfigTransfer.Preview(zip, ConfigTransfer.Order, false);
                Check(pv["dry_run"]?.GetValue<bool>() == true, "预览标记 dry_run=true");
                Check(pv["same_host"]?.GetValue<bool>() == true, "预览能识别来源主机与本机一致");
                Check(Program.ReadJsonl("tasks.jsonl").Count == 0, "预览不写盘（排程仍为空）");
                Check(Program.ReadJsonl("music.jsonl").Count == 0, "预览不写盘（音乐库仍为空）");

                // ---------- 5) 真正导入 ----------
                var im = ConfigTransfer.Import(zip, ConfigTransfer.Order, false);
                Check(im["status"]?.ToString() != "error", "导入返回非 error", im["message"]?.ToString());
                Check(Program.ReadJsonl("tasks.jsonl").Count == 1, "排程已导入 1 条",
                    Program.ReadJsonl("tasks.jsonl").Count.ToString());
                Check(Program.ReadJsonl("music.jsonl").Count == 1, "音乐库已导入 1 条");
                Check(File.Exists(Path.Combine(Program.RingDir, "01 预备铃.mp3")), "铃声已导入（中文/含空格文件名完好）");
                int tx = 0;
                foreach (var n in Program.ReadSettingMerged()) if (n?["_type"]?.ToString() == "tiaoxiu") tx++;
                Check(tx == 1, "调休已导入 1 条", tx.ToString());
                var warnRec = FindRec(WeatherWarn.WarnFile, "warn");
                Check(warnRec?["place"]?.ToString() == "示例县", "预警配置已导入（place 生效）", warnRec?["place"]?.ToString());

                // ---------- 6) 关键安全规则：vnt 密钥类字段永不被覆盖（规格 §6.1） ----------
                var vnt = FindRec("vnt.jsonl", "vnt");
                Check(vnt?["device_id"]?.ToString() == "LOCAL-DEVICE", "vnt device_id 未被覆盖（本机身份保住）", vnt?["device_id"]?.ToString());
                Check(vnt?["token"]?.ToString() == "LOCAL-TOKEN", "vnt token 未被覆盖", vnt?["token"]?.ToString());
                Check(vnt?["data_password"]?.ToString() == "LOCAL-PW", "vnt data_password 未被覆盖");
                Check(vnt?["cred_secret"]?.ToString() == "LOCAL-CRED", "vnt cred_secret 未被覆盖");
                Check(vnt?["server"]?.ToString() == "1.2.3.4:29872", "vnt server 已按包内容更新（同机）", vnt?["server"]?.ToString());
                var mic = FindRec("vnt.jsonl", "mic");
                Check(mic?["code_seed"]?.ToString() == "LOCAL-SEED", "话筒 code_seed 未被覆盖");

                // ---------- 7) 系统设置：来源口令为空时不得清空本机口令 ----------
                string pw = null;
                foreach (var n in Program.ReadJsonl("setting.jsonl"))
                    if (n is JsonObject o && o["_type"]?.ToString() == "global") pw = o["password"]?.ToString();
                Check(pw == "KEEP-ME", "来源未带口令时本机口令不被清空", pw);

                // ---------- 8) 去重：同一份包导入两次不应翻倍 ----------
                ConfigTransfer.Import(zip, ConfigTransfer.Order, false);
                Check(Program.ReadJsonl("tasks.jsonl").Count == 1, "重复导入排程不翻倍（内容级去重）",
                    Program.ReadJsonl("tasks.jsonl").Count.ToString());

                // ---------- 9) 音乐库编号冲突：跳过并保留现有（规格 §0-3） ----------
                Program.WriteJsonl("music.jsonl", new JsonArray
                {
                    new JsonObject { ["id"]="1", ["name"]="本机原有的1号", ["path"]="D:/x.mp3", ["type"]="file" }
                });
                ConfigTransfer.Import(zip, new[] { ConfigTransfer.Music }, false);
                string mname = null;
                foreach (var n in Program.ReadJsonl("music.jsonl")) { mname = n?["name"]?.ToString(); break; }
                Check(mname == "本机原有的1号", "音乐库编号冲突时保留现有条目", mname);

                // ---------- 10) 非法条目被跳过并计数，不静默失败 ----------
                if (File.Exists(badZip)) File.Delete(badZip);
                using (var z = System.IO.Compression.ZipFile.Open(badZip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    PutEntry(z, "config/tasks.jsonl",
                        "{\"start_time\":\"25:99:99\",\"days\":[9],\"volume\":999,\"id\":1}\n" +      // 天数/音量越界
                        "{这一行不是合法 JSON}\n" +                                                    // 解析失败
                        "{\"start_time\":\"08:00:00\",\"days\":[1],\"music_ids\":[\"1\"],\"id\":2}\n");  // 合法
                }
                Program.WriteJsonl("tasks.jsonl", new JsonArray());
                var bad = ConfigTransfer.Import(badZip, new[] { ConfigTransfer.Tasks }, false);
                Check(bad["invalid"]?.GetValue<int>() >= 2, "非法排程被统计为 invalid（含解析失败行）", bad["invalid"]?.ToString());
                Check(Program.ReadJsonl("tasks.jsonl").Count == 1, "非法条目被跳过、合法条目仍写入",
                    Program.ReadJsonl("tasks.jsonl").Count.ToString());

                // ---------- 11) 旧格式包（无清单）：来源主机未知 → vnt 只取服务器地址 ----------
                if (File.Exists(oldZip)) File.Delete(oldZip);
                using (var z = System.IO.Compression.ZipFile.Open(oldZip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    PutEntry(z, "config/vnt.jsonl",
                        "{\"_type\":\"vnt\",\"server\":\"5.6.7.8:29872\",\"device_id\":\"OTHER\",\"token\":\"OTHER-TOKEN\"}\n");
                }
                var old = ConfigTransfer.Import(oldZip, new[] { ConfigTransfer.Vnt }, false);
                Check(old["has_manifest"]?.GetValue<bool>() == false && old["same_host"]?.GetValue<bool>() == false,
                    "旧格式包被识别为无清单、来源主机未知");
                var vnt2 = FindRec("vnt.jsonl", "vnt");
                Check(vnt2?["server"]?.ToString() == "5.6.7.8:29872", "旧包导入时取到了 server", vnt2?["server"]?.ToString());
                Check(vnt2?["device_id"]?.ToString() == "LOCAL-DEVICE" && vnt2?["token"]?.ToString() == "LOCAL-TOKEN",
                    "旧包导入时 device_id/token 仍以本机为准");

                // ---------- 12) 单文件拖入导入：按内容识别类别 ----------
                string one = Path.Combine(Program.AppDataDir, "one_tasks.jsonl");
                File.WriteAllText(one, "{\"start_time\":\"09:15:00\",\"days\":[3],\"music_ids\":[\"1\"],\"id\":7}\n", new UTF8Encoding(false));
                Check(ConfigTransfer.DetectKind(one) == ConfigTransfer.Tasks, "单文件能按内容识别为「排程」",
                    ConfigTransfer.DetectKind(one));
                Program.WriteJsonl("tasks.jsonl", new JsonArray());
                var oneRep = ConfigTransfer.Import(one, ConfigTransfer.Order, false);
                Check(oneRep["imported"]?.GetValue<int>() == 1, "单文件导入写入 1 条排程",
                    oneRep["imported"]?.ToString());
                try { File.Delete(one); } catch { }
                // ---------- 13) 老「备份系统数据」的隐私清理，必须与新导出路径清得一样 ----------
                string privZip = Path.Combine(Program.AppDataDir, "transfer_priv.zip");
                if (File.Exists(privZip)) File.Delete(privZip);
                using (var z = System.IO.Compression.ZipFile.Open(privZip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    PutEntry(z, "config/vnt.jsonl",
                        "{\"_type\":\"vnt\",\"server\":\"1.2.3.4:29872\",\"device_id\":\"DEVV\",\"token\":\"TOKK\"," +
                        "\"data_password\":\"PWW\",\"cred_secret\":\"CREDD\",\"admin_devices\":\"ADMM\"}\n");
                    PutEntry(z, "config/setting.jsonl", "{\"_type\":\"global\",\"password\":\"PWDX\"}\n");
                }
                int cleared = Program.StripPrivacyFromBackup(privZip);
                string vt = ReadZipEntry(privZip, "vnt.jsonl") ?? "";
                Check(!vt.Contains("DEVV") && !vt.Contains("TOKK") && !vt.Contains("PWW")
                      && !vt.Contains("CREDD") && !vt.Contains("ADMM"),
                    "老备份路径的隐私清理与新导出路径一致（device_id / admin_devices 也清空）", vt);
                Check(vt.Contains("1.2.3.4:29872"), "隐私清理后仍保留服务器地址");
                string stt = ReadZipEntry(privZip, "setting.jsonl") ?? "";
                Check(!stt.Contains("PWDX"), "隐私清理会清空网页口令");
                Check(cleared >= 6, "隐私清理返回被清空的字段数", cleared.ToString());
                try { File.Delete(privZip); } catch { }
            }
            catch (Exception ex) { Check(false, "按项导入/导出往返", ex.Message); }
            finally
            {
                foreach (var f in new[] { zip, badZip, oldZip }) { try { File.Delete(f); } catch { } }
                // 还原配置与铃声目录，保证不影响后续自检项
                foreach (var kv in snapshot)
                {
                    string p = Path.Combine(Program.ConfigDir, kv.Key);
                    try
                    {
                        if (kv.Value == null) { if (File.Exists(p)) File.Delete(p); }
                        else File.WriteAllText(p, kv.Value);
                    }
                    catch { }
                }
                try { if (Directory.Exists(Program.RingDir)) Directory.Delete(Program.RingDir, true); } catch { }
                try
                {
                    Directory.CreateDirectory(Program.RingDir);
                    foreach (var kv in ringSnap) File.WriteAllBytes(Path.Combine(Program.RingDir, kv.Key), kv.Value);
                }
                catch { }
            }
            Console.WriteLine();
        }

        // ---------- 高缩放下的对话框布局 ----------
        // 用户实测：175% 缩放下任务编辑窗看不到"保存"。根因是 SizeToContent.Height 与
        // MaxHeight(WorkArea-40) 叠加，而正文的 MaxHeight 带 220 保底值 —— 矮工作区上
        // 内容总高超过窗口上限，底部按钮被裁掉。现在改成 Grid 三行(Auto/*/Auto)。
        // 这里用受限尺寸离屏布局，断言底部按钮行始终落在可视区内（纯计算，不进屏）。
        static void TestDialogLayout()
        {
            Console.WriteLine("【8c】高缩放下的任务编辑窗布局（底部按钮必须始终可见）");
            var cases = new[]
            {
                new[] { 660.0, 420.0 },   // 175% 笔记本的典型工作区
                new[] { 660.0, 300.0 },   // 更极端：更矮
                new[] { 560.0, 360.0 },   // 更窄更矮
                new[] { 840.0, 720.0 }    // 100% 正常尺寸（不应回归）
            };
            foreach (var wh in cases)
            {
                string r = Desktop.TaskEditorWindow.CheckFooterVisibleForTest(wh[0], wh[1]);
                Check(r.StartsWith("OK"), $"任务编辑窗 {wh[0]:F0}×{wh[1]:F0} 下底部按钮仍在可视区", r);
                string r2 = Desktop.AboutDialog.CheckFooterVisibleForTest(wh[0], wh[1]);
                Check(r2.StartsWith("OK"), $"关于窗 {wh[0]:F0}×{wh[1]:F0} 下关闭按钮仍在可视区", r2);
            }
            Console.WriteLine();
        }

        // ---------- 提示横幅通路 ----------
        static void TestAlertCenter()
        {
            Console.WriteLine("【9】后台提示通路（桌面横幅/网页提示条共用同一消息中心）");
            long baseline = 0;
            var existing = AlertCenter.PendingJson(0);
            foreach (var n in existing) { if (n is JsonObject o) { long id = o["id"]?.GetValue<long>() ?? 0; if (id > baseline) baseline = id; } }

            var a = AlertCenter.Publish("测试", "这是一条自检用的提示消息", "info", dedupKey: "selftest-alert-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Check(a != null, "发布提示成功");
            var pending = AlertCenter.PendingJson(baseline);
            Check(pending.Count == 1, "按游标回读只拿到新增的那一条", pending.Count.ToString());
            bool msgOk = false, levelOk = false;
            foreach (var n in pending) if (n is JsonObject o) { msgOk = o["message"]?.ToString() == "这是一条自检用的提示消息"; levelOk = o["level"]?.ToString() == "info"; }
            Check(msgOk, "消息内容正确（桌面横幅就是显示这个字段）");
            Check(levelOk, "级别字段正确（error 会标红）");

            // 去重键：同一键不会重复弹出
            string key = "selftest-dedup-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            AlertCenter.Publish("测试", "去重测试1", "warn", dedupKey: key);
            long b2 = 0; foreach (var n in AlertCenter.PendingJson(baseline)) if (n is JsonObject o) { long id = o["id"]?.GetValue<long>() ?? 0; if (id > b2) b2 = id; }
            var dup = AlertCenter.Publish("测试", "去重测试2", "warn", dedupKey: key);
            Check(dup == null, "同去重键不再重复发布（避免刷屏）");
            Check(AlertCenter.PendingJson(b2).Count == 0, "去重后游标处没有新消息");
            AlertCenter.Clear();
            Check(AlertCenter.PendingJson(0).Count == 0, "清空后无待提示消息");
            Console.WriteLine();
        }

        // ---------- 10. 云端订阅格式适配 ----------
        static void TestSubscriptionFormat()
        {
            Line("【10】云端订阅格式适配（原生数组 / holiday-cn / 小米接口 / 苹果 iCloud ICS）");

            // 1) 原生格式原样透传
            string nativeJson = "[{\"date\":\"2026-05-01\",\"action\":\"disable\"},{\"date\":\"2026-05-09\",\"action\":\"enable\",\"marker\":1}]";
            string err;
            var native = Program.AdaptCloudPayload(JsonNode.Parse(nativeJson), out err);
            Check(native != null && native.Count == 2, "原生数组原样透传", err ?? (native?.Count.ToString() ?? "null"));

            // 2) holiday-cn 格式（真实结构）
            string holidayJson = @"{
              ""$schema"": ""https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/schema.json"",
              ""year"": 2026,
              ""papers"": [""https://www.gov.cn/xxx""],
              ""days"": [
                { ""name"": ""元旦"", ""date"": ""2026-01-01"", ""isOffDay"": true },
                { ""name"": ""元旦"", ""date"": ""2026-01-04"", ""isOffDay"": false },
                { ""name"": ""春节"", ""date"": ""2026-02-15"", ""isOffDay"": true },
                { ""name"": ""春节"", ""date"": ""2026-02-28"", ""isOffDay"": false }
              ]
            }";
            var adapted = Program.AdaptCloudPayload(JsonNode.Parse(holidayJson), out err);
            Check(adapted != null && adapted.Count == 4, "holiday-cn 的 days 被展开成 4 条", err ?? (adapted?.Count.ToString() ?? "null"));
            if (adapted != null && adapted.Count == 4)
            {
                var d0 = adapted[0] as JsonObject;
                var d1 = adapted[1] as JsonObject;
                Check(d0?["start_date"]?.ToString() == "2026-01-01" && d0?["action"]?.ToString() == "disable",
                    "放假(isOffDay=true) → disable", d0?.ToJsonString());
                Check(d1?["start_date"]?.ToString() == "2026-01-04" && d1?["action"]?.ToString() == "enable",
                    "调休上班(isOffDay=false) → enable", d1?.ToJsonString());
                Check(d0?["marker"]?.ToString() == "0", "标记统一为 0（全局规则，对当天所有任务生效）");
                Check(d0?["note"]?.ToString() == "元旦", "节日名写进 note（界面能看出是哪天放假）", d0?["note"]?.ToString());
            }

            // 2b) 连续日期要合并成一条区间记录（holiday-cn 是逐天下发的）
            var merged3 = Program.AdaptCloudPayload(JsonNode.Parse(@"{
              ""days"": [
                { ""name"": ""元旦"", ""date"": ""2026-01-01"", ""isOffDay"": true },
                { ""name"": ""元旦"", ""date"": ""2026-01-02"", ""isOffDay"": true },
                { ""name"": ""元旦"", ""date"": ""2026-01-03"", ""isOffDay"": true },
                { ""name"": ""元旦"", ""date"": ""2026-01-04"", ""isOffDay"": false }
              ] }"), out err);
            Check(merged3 != null && merged3.Count == 2,
                "连续 3 天放假合并成 1 条，调休上班日单独 1 条", err ?? (merged3?.Count.ToString() ?? "null"));
            if (merged3 != null && merged3.Count == 2)
            {
                var seg = merged3[0] as JsonObject;
                Check(seg?["start_date"]?.ToString() == "2026-01-01" && seg?["end_date"]?.ToString() == "2026-01-03",
                    "区间为 01-01 ~ 01-03", seg?.ToJsonString());
                Check(seg?["note"]?.ToString() == "元旦", "区间保留节日名");
            }
            var mixed = Program.MergeConsecutiveDays(JsonNode.Parse(@"[
                { ""date"": ""2026-01-01"", ""action"": ""disable"", ""note"": ""A"" },
                { ""date"": ""2026-01-02"", ""action"": ""disable"", ""note"": ""B"" },
                { ""date"": ""2026-01-03"", ""action"": ""enable"",  ""note"": ""B"" },
                { ""date"": ""2026-01-05"", ""action"": ""enable"",  ""note"": ""B"" }
            ]") as JsonArray);
            Check(mixed != null && mixed.Count == 4, "备注不同 / 动作不同 / 日期不连续都不合并", mixed?.Count.ToString());

            // 3) 坏数据要给出人话原因，而不是静默失败
            var noDays = Program.AdaptCloudPayload(JsonNode.Parse("{\"year\":2026}"), out err);
            Check(noDays == null && !string.IsNullOrEmpty(err), "对象里没有 days 时返回可读原因", err);
            var emptyDays = Program.AdaptCloudPayload(JsonNode.Parse("{\"days\":[]}"), out err);
            Check(emptyDays == null && !string.IsNullOrEmpty(err), "days 为空时返回可读原因", err);
            var notJson = Program.AdaptCloudPayload(null, out err);
            Check(notJson == null && !string.IsNullOrEmpty(err), "内容不是 JSON 时返回可读原因", err);

            // 4) 订阅地址里的年份占位符
            string url = Program.ExpandSubscriptionUrl("https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json");
            Check(url.EndsWith("/" + DateTime.Now.Year + ".json"), "地址里的 {year} 会替换成当前年份", url);
            Check(Program.ExpandSubscriptionUrl("https://a/{yyyy}.json").Contains(DateTime.Now.Year.ToString()), "{yyyy} 同样支持");

            // 4b) 手动指定类型：强制按 holiday-cn 转写（数组形式、data 字段都要能认）
            string det;
            var daysArray = "[" +
                "{\"name\":\"元旦\",\"date\":\"2026-01-01\",\"isOffDay\":true}," +
                "{\"name\":\"元旦\",\"date\":\"2026-01-04\",\"isOffDay\":false}]";
            var forced = Program.AdaptCloudPayload(JsonNode.Parse(daysArray), "holiday-cn", out det, out err);
            Check(forced != null && forced.Count == 2 && det == "holiday-cn",
                "指定 holiday-cn 时，纯 days 数组也能转写", err ?? (forced?.Count.ToString() ?? "null"));
            var dataField = Program.AdaptCloudPayload(
                JsonNode.Parse("{\"data\":[{\"date\":\"2026-05-01\",\"offDay\":true},{\"date\":\"2026-05-09\",\"offDay\":false}]}"),
                "holiday-cn", out det, out err);
            Check(dataField != null && dataField.Count == 2 && det == "holiday-cn", "holiday-cn 也支持 data 字段名", err);

            var wrongType = Program.AdaptCloudPayload(JsonNode.Parse(holidayJson), "native", out det, out err);
            Check(wrongType == null && err != null && err.Contains("不是 JSON 数组"),
                "指定 native 但内容是对象时，明确报错而不是猜", err);

            var passThrough = Program.AdaptCloudPayload(
                JsonNode.Parse("{\"days\":[{\"date\":\"2026-05-01\",\"action\":\"disable\",\"note\":\"劳动节\"}]}"),
                "holiday-cn", out det, out err);
            Check(passThrough != null && (passThrough[0] as JsonObject)?["action"]?.ToString() == "disable",
                "holiday-cn 里若已带 action，则原样透传不重复转换", err);

            // 5) 手动区间记录要压住云端逐日记录（否则界面上出现重复假期行）
            var settings = Program.ReadSettingMerged();
            settings.Add(new JsonObject
            {
                ["_type"] = "tiaoxiu",
                ["source"] = "manual",
                ["start_date"] = "2031-03-01",
                ["end_date"] = "2031-03-03",
                ["marker"] = 0,
                ["action"] = "disable"
            });
            Program.WriteSettingMerged( settings);

            var manualRange = new JsonArray
            {
                new JsonObject { ["date"] = "2031-03-01", ["action"] = "disable", ["marker"] = 0 },
                new JsonObject { ["date"] = "2031-03-02", ["action"] = "disable", ["marker"] = 0 },
                new JsonObject { ["date"] = "2031-03-03", ["action"] = "disable", ["marker"] = 0 },
                new JsonObject { ["date"] = "2031-03-04", ["action"] = "disable", ["marker"] = 0 }
            };
            int mc, sc, ov;
            var merged = Program.MergeSubscription(manualRange, out mc, out sc, out ov);
            Check(ov == 3 && sc == 1, "手动 03-01~03-03 区间挡住云端同三天的记录，区间外的照常生效",
                $"覆盖 {ov} / 生效 {sc}");
            int manualKept = 0;
            foreach (var n in merged) if (n is JsonObject o && o["_type"]?.ToString() == "tiaoxiu" && o["source"]?.ToString() == "manual") manualKept++;
            Check(manualKept >= 1, "手动记录本身仍在配置里", manualKept.ToString());

            // 5b) 云端区间被手动记录从中间切开时，剩下的两段要重新合并成区间
            var settings2 = Program.ReadSettingMerged();
            settings2.Add(new JsonObject
            {
                ["_type"] = "tiaoxiu",
                ["source"] = "manual",
                ["start_date"] = "2032-04-03",
                ["end_date"] = "2032-04-03",
                ["marker"] = 0,
                ["action"] = "enable",
                ["note"] = "手动覆盖中间一天"
            });
            Program.WriteSettingMerged( settings2);

            var rangeCase = new JsonArray
            {
                new JsonObject { ["start_date"] = "2032-04-01", ["end_date"] = "2032-04-05", ["action"] = "disable", ["marker"] = 0, ["note"] = "测试假" }
            };
            int mc2, sc2, ov2;
            var merged2 = Program.MergeSubscription(rangeCase, out mc2, out sc2, out ov2);
            var subs = new List<JsonObject>();
            foreach (var n in merged2)
                if (n is JsonObject o && o["source"]?.ToString() == "sub" && o["start_date"]?.ToString()?.StartsWith("2032-04") == true) subs.Add(o);
            bool splitOk = subs.Count == 2
                && subs[0]["start_date"]?.ToString() == "2032-04-01" && subs[0]["end_date"]?.ToString() == "2032-04-02"
                && subs[1]["start_date"]?.ToString() == "2032-04-04" && subs[1]["end_date"]?.ToString() == "2032-04-05";
            Check(splitOk, "云端 04-01~04-05 被手动(04-03)切开后，重组成 04-01~02 与 04-04~05 两条",
                string.Join(" | ", subs.ConvertAll(x => x["start_date"] + "~" + x["end_date"])));
            Check(ov2 == 1, "被手动挡住的按天计入覆盖数", ov2.ToString());

            // 6) 跨年前后窗口：这段时间拉取失败要额外横幅提醒
            Check(Program.IsYearTurnWindow(new DateTime(2026, 12, 25)), "12-25 属于跨年窗口");
            Check(Program.IsYearTurnWindow(new DateTime(2026, 12, 31)), "12-31 属于跨年窗口");
            Check(Program.IsYearTurnWindow(new DateTime(2027, 1, 2)), "01-02 属于跨年窗口");
            Check(!Program.IsYearTurnWindow(new DateTime(2026, 11, 30)), "11-30 不属于跨年窗口");
            Check(!Program.IsYearTurnWindow(new DateTime(2026, 6, 1)), "06-01 不属于跨年窗口");
            Check(Program.YearTurnWarning(new DateTime(2026, 12, 28)).Contains("跨年前后"), "窗口内会给出跨年提示语");
            Check(Program.YearTurnWarning(new DateTime(2026, 6, 1)) == "", "平时不附加跨年提示语");

            // 7) 默认订阅组：没配置时用内置默认，用户主动清空后不再回填
            Check(Program.DefaultSubscriptionUrls.Length >= 2, "内置默认订阅组至少两条（主源+备用）");
            bool hasMirror = false, hasRaw = false;
            foreach (var u in Program.DefaultSubscriptionUrls)
            {
                if (u.Contains("cdn.jsdelivr.net")) hasMirror = true;
                if (u.Contains("raw.githubusercontent.com")) hasRaw = true;
            }
            Check(hasMirror && hasRaw, "默认组同时包含 jsDelivr 镜像与 GitHub 原始地址");
            // 安装模板 res\config\setting.jsonl 里存着一份显式订阅组，它的优先级高于内置默认，
            // 一旦两者不一致，新装的机器就会拿到一份旧源列表（历史问题：模板里只有 2 条，且恰好是被拦截的两条）。
            try
            {
                string tpl = System.IO.Path.Combine(Program.ResDir, "config", "setting.jsonl");
                if (System.IO.File.Exists(tpl))
                {
                    var tplUrls = new System.Collections.Generic.List<string>();
                    foreach (string line in System.IO.File.ReadAllLines(tpl))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject tplObj) continue;
                        if (tplObj["_type"]?.ToString() != "global") continue;
                        if (tplObj["subscription_urls"] is System.Text.Json.Nodes.JsonArray tplArr)
                            foreach (var tu in tplArr) tplUrls.Add(tu?.ToString());
                        break;
                    }
                    bool same = tplUrls.Count == Program.DefaultSubscriptionUrls.Length;
                    if (same)
                    {
                        for (int k = 0; k < tplUrls.Count; k++)
                            if (tplUrls[k] != Program.DefaultSubscriptionUrls[k]) { same = false; break; }
                    }
                    Check(same, "安装模板的订阅组与内置默认逐条一致", tplUrls.Count + " 条");
                }
            }
            catch (Exception ex) { Check(false, "读取安装模板 setting.jsonl 失败", ex.Message); }
            // "上次成功的源优先"：把 last_ok 指向列表里第 3 条，应被提到最前面
            var orderProbe = new System.Collections.Generic.List<string> { "https://a/1.json", "https://b/2.json", "https://c/3.json" };
            var cfgNow = Program.ReadSettingMerged();
            System.Text.Json.Nodes.JsonObject globalNow = null;
            foreach (var n in cfgNow) if (n is System.Text.Json.Nodes.JsonObject go && go["_type"]?.ToString() == "global") { globalNow = go; break; }
            string oldLastOk = globalNow?["subscription_last_ok"]?.ToString();
            if (globalNow != null)
            {
                globalNow["subscription_last_ok"] = "https://c/3.json";
                Program.WriteSettingMerged( cfgNow);
                var reordered = Program.PrioritizeLastOk(orderProbe);
                Check(reordered.Count == 3 && reordered[0] == "https://c/3.json", "上次成功的源会被提到最前面", reordered.Count > 0 ? reordered[0] : "-");
                if (oldLastOk == null) globalNow.Remove("subscription_last_ok"); else globalNow["subscription_last_ok"] = oldLastOk;
                Program.WriteSettingMerged( cfgNow);
            }
            // 连续日期合并：三条连续（01-01/02/03）+ 一条独立（01-05）→ 2 条；重叠区间也要并成一条
            var mergeSrc = System.Text.Json.Nodes.JsonNode.Parse(
                "[{\"start_date\":\"2026-01-01\",\"end_date\":\"2026-01-01\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"元旦\"}," +
                "{\"start_date\":\"2026-01-02\",\"end_date\":\"2026-01-02\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"元旦\"}," +
                "{\"start_date\":\"2026-01-03\",\"end_date\":\"2026-01-03\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"元旦\"}," +
                "{\"start_date\":\"2026-01-05\",\"end_date\":\"2026-01-05\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"\"}]") as System.Text.Json.Nodes.JsonArray;
            var mergedRanges = HolidayMerge.MergeRanges(mergeSrc);
            Check(mergedRanges != null && mergedRanges.Count == 2, "连续日期自动合并成区间（3+1 条 → 2 条）", mergedRanges?.Count.ToString());
            if (mergedRanges != null && mergedRanges.Count == 2)
                Check(mergedRanges[0]?["start_date"]?.ToString() == "2026-01-01" && mergedRanges[0]?["end_date"]?.ToString() == "2026-01-03",
                      "合并后的区间为 01-01 ~ 01-03", mergedRanges[0]?["start_date"] + "~" + mergedRanges[0]?["end_date"]);

            var overlapSrc = System.Text.Json.Nodes.JsonNode.Parse(
                "[{\"start_date\":\"2026-02-15\",\"end_date\":\"2026-02-15\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"a\"}," +
                "{\"start_date\":\"2026-02-15\",\"end_date\":\"2026-02-18\",\"action\":\"disable\",\"marker\":\"0\",\"note\":\"b\"}]") as System.Text.Json.Nodes.JsonArray;
            var overlapMerged = HolidayMerge.MergeRanges(overlapSrc);
            Check(overlapMerged != null && overlapMerged.Count == 1 && overlapMerged[0]?["end_date"]?.ToString() == "2026-02-18",
                  "重叠区间并成一条并取最大范围", overlapMerged != null && overlapMerged.Count > 0 ? overlapMerged[0]?["end_date"]?.ToString() : "-");
            // 默认组里：带 {year} 的地址要正常占位；不带 {year} 的必须是「多年份一次性返回」的接口（小米），
            // 否则就说明有人塞了一条既没有年份、也只能返回单年的地址进来。
            bool sawYearPlaceholder = false;
            foreach (var u in Program.DefaultSubscriptionUrls)
            {
                if (u.Contains("{year}")) { sawYearPlaceholder = true; continue; }
                Check(u.Contains("api.comm.miui.com") || u.Contains("p10-calendars.icloud.com"), "不含 {year} 的默认地址只允许是多年的接口（小米 / iCloud ICS）：" + u);
            }
            Check(sawYearPlaceholder, "默认组里至少有一条带 {year} 占位符的地址");

            // 小米接口：把"年内第几天"正确换算成日期（2026 年第 1 天 = 01-01，第 46 天 = 02-15）
            var miuiRoot = System.Text.Json.Nodes.JsonNode.Parse(
                "{\"versioncode\":24,\"holiday\":[{\"year\":" + DateTime.Now.Year + ",\"workday\":[4],\"freeday\":[1,2,3]}]}");
            string mErr;
            var miuiItems = Program.AdaptMiuiHoliday(miuiRoot, out mErr);
            Check(miuiItems != null && miuiItems.Count == 4, "小米接口能解析出 4 条记录（3 放假 + 1 调休上班）", mErr ?? "");
            if (miuiItems != null && miuiItems.Count >= 4)
            {
                Check(miuiItems[0]?["date"]?.ToString() == DateTime.Now.Year + "-01-01", "第 1 天 = 01-01", miuiItems[0]?["date"]?.ToString());
                Check(miuiItems[0]?["action"]?.ToString() == "disable", "放假日写成 disable", miuiItems[0]?["action"]?.ToString());
                Check(miuiItems[3]?["action"]?.ToString() == "enable", "调休上班日写成 enable", miuiItems[3]?["action"]?.ToString());
                Check(miuiItems[3]?["date"]?.ToString() == DateTime.Now.Year + "-01-04", "第 4 天 = 01-04", miuiItems[3]?["date"]?.ToString());
            }

            // 2d) 苹果 iCloud 日历（ICS）：只有带 X-APPLE-SPECIAL-DAY 的事件才算节假日。
            //     这是"苹果订阅源能用"的核心规则，之前完全没有测试覆盖。
            int iy = DateTime.Now.Year;
            string ics =
                "BEGIN:VCALENDAR\r\n" +
                "BEGIN:VEVENT\r\n" +                                   // 只有标题的节气：必须忽略
                "SUMMARY:小寒\r\n" +
                "DTSTART;VALUE=DATE:" + iy + "0105\r\n" +
                "END:VEVENT\r\n" +
                "BEGIN:VEVENT\r\n" +                                   // 法定放假：3 天区间（DTEND 不含当天）
                "SUMMARY:元旦（休）\r\n" +
                "DTSTART;VALUE=DATE:" + iy + "0101\r\n" +
                "DTEND;VALUE=DATE:" + iy + "0104\r\n" +
                "X-APPLE-SPECIAL-DAY:WORK-HOLIDAY\r\n" +
                "END:VEVENT\r\n" +
                "BEGIN:VEVENT\r\n" +                                   // 调休上班 + 长行折行（CRLF + 空格）
                "SUMMARY:元旦（班）\r\n" +
                "DTSTART;VALUE=DATE:" + iy + "0104\r\n" +
                "X-APPLE-SPECIAL-DAY:ALTERNATE-\r\n" +
                " WORKDAY\r\n" +
                "END:VEVENT\r\n" +
                "BEGIN:VEVENT\r\n" +                                   // 后年数据：必须忽略（只取当年与下一年）
                "SUMMARY:元旦（休）\r\n" +
                "DTSTART;VALUE=DATE:" + (iy + 2) + "0101\r\n" +
                "X-APPLE-SPECIAL-DAY:WORK-HOLIDAY\r\n" +
                "END:VEVENT\r\n" +
                "END:VCALENDAR\r\n";
            string icsErr;
            var icsItems = IcsHoliday.Parse(ics, out icsErr);
            Check(icsItems != null && icsItems.Count == 2,
                "ICS：只有带 X-APPLE-SPECIAL-DAY 的才算节假日（节气与后年数据被忽略）",
                icsErr ?? (icsItems?.Count.ToString() ?? "null"));
            if (icsItems != null && icsItems.Count == 2)
            {
                Check(icsItems[0]?["start_date"]?.ToString() == iy + "-01-01" && icsItems[0]?["end_date"]?.ToString() == iy + "-01-03",
                    "ICS：DTEND 不含当天（0101+0104 → 01-01~01-03）", icsItems[0]?.ToJsonString());
                Check(icsItems[0]?["action"]?.ToString() == "disable",
                    "ICS：WORK-HOLIDAY → 放假日动作", icsItems[0]?["action"]?.ToString());
                Check(icsItems[0]?["note"]?.ToString() == "元旦",
                    "ICS：备注去掉（休）/（班）后缀", icsItems[0]?["note"]?.ToString());
                Check(icsItems[1]?["action"]?.ToString() == "enable",
                    "ICS：ALTERNATE-WORKDAY → 上班日动作（长行折行也能还原）", icsItems[1]?["action"]?.ToString());
            }
            var icsEmpty = IcsHoliday.Parse("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", out icsErr);
            Check(icsEmpty == null && !string.IsNullOrEmpty(icsErr), "ICS：没有 VEVENT 时给出人话原因", icsErr);

            var cfg = Program.ReadSettingMerged();
            var keepCfg = new JsonArray();
            foreach (var n in cfg) if (n is JsonObject o && o["_type"]?.ToString() != "global") keepCfg.Add(n.DeepClone());
            Program.WriteSettingMerged( keepCfg);   // 暂时拿掉 global 记录
            Check(Program.IsUsingDefaultSubscription(), "配置里没有订阅字段时，判定为使用默认订阅组");
            Check(Program.GetSubscriptionUrls().Count == Program.DefaultSubscriptionUrls.Length,
                "没配置时返回内置默认组", Program.GetSubscriptionUrls().Count.ToString());

            keepCfg.Insert(0, new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["subscription_urls"] = new JsonArray() });
            Program.WriteSettingMerged( keepCfg);
            Check(!Program.IsUsingDefaultSubscription(), "用户主动清空后不再判定为默认");
            Check(Program.GetSubscriptionUrls().Count == 0, "主动清空后不再自动回填默认地址");

            keepCfg[0] = new JsonObject
            {
                ["_type"] = "global",
                ["global_switch"] = true,
                ["subscription_urls"] = new JsonArray { "https://example.com/a.json", "https://example.com/b.json" }
            };
            Program.WriteSettingMerged( keepCfg);
            Check(Program.GetSubscriptionUrls().Count == 2, "配置了订阅组就按配置来", string.Join(",", Program.GetSubscriptionUrls()));
            Console.WriteLine();
        }

        // ---------- 9. 端口探测 ----------
        static void TestPortProbe()
        {
            Line("【5】端口探测");
            bool free = Program.IsPortFree(59999);
            Check(free || !free, "端口探测可正常返回（不抛异常）");
            int saved = Program.ListenPort;
            Program.ResolveListenPort();
            Check(Program.ListenPort >= 1 && Program.ListenPort <= 65535, "解析出的监听端口合法: " + Program.ListenPort);
            Check(Program.ListenPort == saved || Program.ListenPort == 5000 || Program.ListenPort > 5000, "端口解析结果符合预期: " + Program.ListenPort);
            Line("");
        }

        // ---------- 10. 气象预警（编号 1000） ----------
        static void TestWeatherWarn()
        {
            Line("【6】气象预警播报（保留编号 1000）");

            // 用户给的真实样例
            const string text = "示例县气象台2026年9月23日14时27分发布雷电黄色预警信号：目前雷雨云团正逐渐靠近示例县，预计未来6小时，示例县大部将自东向西出现雷电，并伴有强降雨和6-7级短时大风，请注意防范。（预警信息来源：国家预警信息发布中心）";

            Check(WeatherWarn.TryParsePublishFromText(text, new DateTime(2026, 9, 24), out DateTime pub)
                  && pub == new DateTime(2026, 9, 23, 14, 27, 0),
                  "从正文解析发布时刻 = 2026-09-23 14:27", pub.ToString("yyyy-MM-dd HH:mm"));

            var eff = new DateTime(2026, 9, 23, 14, 27, 0);
            DateTime expect = WeatherWarn.ParseExpect(text, eff, out bool estimated, out var notes);
            Check(expect == new DateTime(2026, 9, 23, 20, 27, 0), "预计时间 = 2026-09-23 20:27（14:27 + 未来6小时）", expect.ToString("yyyy-MM-dd HH:mm"));
            Check(!estimated, "正文写了时长，不按默认值估算");
            Check(notes.Count > 0, "时间识别过程有记录（写进日志）", string.Join("；", notes));

            // 各种时长写法
            Check(WeatherWarn.ParseExpect("预计未来12小时有大风", eff, out _, out _) == eff.AddHours(12), "未来12小时");
            Check(WeatherWarn.ParseExpect("6小时内将出现雷电活动", eff, out _, out _) == eff.AddHours(6), "6小时内");
            Check(WeatherWarn.ParseExpect("预计未来24小时暴雨", eff, out _, out _) == eff.AddHours(24), "未来24小时");
            Check(WeatherWarn.ParseExpect("预计未来3天高温", eff, out _, out _) == eff.AddDays(3), "未来3天");
            Check(WeatherWarn.ParseExpect("23日20时至24日08时出现大风", eff, out _, out _) == new DateTime(2026, 9, 24, 8, 0, 0), "绝对区间 23日20时-24日08时");
            Check(WeatherWarn.ParseExpect("9月23日20时至9月24日08时出现大风", eff, out _, out _) == new DateTime(2026, 9, 24, 8, 0, 0), "绝对区间（带月份）");
            Check(WeatherWarn.ParseExpect("预计今天夜间有雾", eff, out _, out _) == new DateTime(2026, 9, 23, 23, 0, 0), "模糊时段 今天夜间 → 当天 23:00");
            var fb = WeatherWarn.ParseExpect("气象台发布雷电黄色预警信号，请注意防范。", eff, out bool est2, out _);
            Check(est2 && fb == eff.AddHours(WeatherWarn.FallbackHours), "正文无时长时按默认 6 小时估算", fb.ToString("yyyy-MM-dd HH:mm"));

            // 播报文案（精简成一句）
            var cache = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "12345678901234_20260923142952",
                ["name"] = "示例",
                ["signaltype"] = "雷电",
                ["signallevel"] = "黄色",
                ["effective"] = "2026-09-23 14:27",
                ["expect"] = "2026-09-23 20:27",
                ["text"] = text
            };
            string speech = WeatherWarn.BuildSpeech(cache);
            Check(speech == "示例气象台下午2点27分发布雷电黄色预警，预计未来六小时影响本地，注意防范。",
                "播报文案格式：台名+发布时刻+类型等级+预计时段+注意防范", speech);
            Check(WeatherWarn.GetDurationPhrase(cache) == "未来6小时", "时长说法 = 未来6小时（播报时转中文数字）", WeatherWarn.GetDurationPhrase(cache));
            Check(WeatherWarn.ChineseNumber("未来24小时") == "未来二十四小时" && WeatherWarn.ChineseNumber("未来3天") == "未来三天"
                  && WeatherWarn.ChineseNumber("6小时内") == "六小时内", "数字转中文（六/二十四/三）");
            Check(WeatherWarn.TimeWords(new DateTime(2026, 9, 23, 14, 27, 0)) == "下午2点27分"
                  && WeatherWarn.TimeWords(new DateTime(2026, 9, 23, 9, 5, 0)) == "上午9点05分"
                  && WeatherWarn.TimeWords(new DateTime(2026, 9, 23, 0, 30, 0)) == "凌晨0点30分"
                  && WeatherWarn.TimeWords(new DateTime(2026, 9, 23, 19, 0, 0)) == "晚上7点00分",
                "发布时刻口语化（下午2点27分 / 上午9点05分 / 凌晨0点30分 / 晚上7点00分）");

            // 两行接口地址：默认地址 + 编号占位符替换
            Check(WeatherWarn.DefaultListUrl().Contains("findAlarm") && WeatherWarn.DefaultDetailUrl().Contains("publish/alarm"),
                "内置默认源是中央气象台（findAlarm + 详情页）", WeatherWarn.DefaultListUrl());
            Check(WeatherWarn.DefaultListUrl().Contains("pageSize=500"),
                "默认列表地址用 pageSize=500（全国一次取完，每轮请求数 5→1）", WeatherWarn.DefaultListUrl());

            // 按需拉取：只在"排程点前 N 分钟"才发请求（默认 10 分钟），兜底轮询默认关闭
            Check(WeatherWarn.GetPrefetchMin() == 10, "默认提前 10 分钟拉取", WeatherWarn.GetPrefetchMin().ToString());
            Check(WeatherWarn.GetIntervalMin() == 0, "默认关闭兜底轮询（0 = 只在排程前按需拉取）", WeatherWarn.GetIntervalMin().ToString());
            var taskAt = new DateTime(2026, 9, 24, 11, 15, 0);
            Check(WeatherWarn.InPrefetchWindow(new DateTime(2026, 9, 24, 11, 5, 0), taskAt, 10), "11:15 的排程 → 11:05 进入拉取窗口");
            Check(WeatherWarn.InPrefetchWindow(new DateTime(2026, 9, 24, 11, 14, 59), taskAt, 10), "窗口右边界前一刻仍算进入");
            Check(!WeatherWarn.InPrefetchWindow(new DateTime(2026, 9, 24, 11, 4, 0), taskAt, 10), "11:04 还没进窗口（不拉）");
            Check(!WeatherWarn.InPrefetchWindow(new DateTime(2026, 9, 24, 11, 15, 0), taskAt, 10), "11:15 到点了（窗口结束，不再拉）");
            var tUse = new System.Text.Json.Nodes.JsonObject { ["music_ids"] = new System.Text.Json.Nodes.JsonArray { "3", "1000" } };
            var tNo = new System.Text.Json.Nodes.JsonObject { ["music_ids"] = new System.Text.Json.Nodes.JsonArray { "3" } };
            Check(WeatherWarn.TaskUsesWarn(tUse) && !WeatherWarn.TaskUsesWarn(tNo) && !WeatherWarn.TaskUsesWarn(null),
                "能识别引用编号 1000 的排程（用于按需拉取）");
            Check(WeatherWarn.FallbackDetailUrl().Contains("yj.php"), "兜底详情地址仍是 apihz yj.php");

            // 停止播放：渐弱已移除（2026-10-15）→ 现在一律"立即结束进程、不碰会话音量"。
            // 这里断言新 API 的空值安全；真实进程的结束由运行期覆盖（自检不额外拉起外部进程）。
            Check(!AudioFade.StopNow(null), "StopNow 传空进程不抛异常（返回 false）");
            Check(AudioFade.StopNowAll(null) == 0, "StopNowAll 传空集合返回 0");
            // 非阻塞改造：恢复音量里的 COM 调用已丢到线程池，调用方（UI/HTTP）必须立即返回
            var swRestore = System.Diagnostics.Stopwatch.StartNew();
            Program.RestoreAudioAfterEnable();
            swRestore.Stop();
            Check(swRestore.ElapsedMilliseconds < 100, "RestoreAudioAfterEnable 不阻塞调用方（COM 已丢到线程池）",
                swRestore.ElapsedMilliseconds + " ms");

            // 静默启动是靠启动项里的 --silent 实现的：命令行拼错就等于"静默启动无效"（用户反馈过）
            Check(Desktop.SettingsPage.AutostartCommand(@"C:\Program Files\a\b.exe", true) == @"""C:\Program Files\a\b.exe"" --silent",
                "开机自启命令行（静默启动）带 --silent 且路径加引号", Desktop.SettingsPage.AutostartCommand(@"C:\Program Files\a\b.exe", true));
            Check(Desktop.SettingsPage.AutostartCommand(@"C:\x.exe", false) == @"""C:\x.exe""",
                "不开静默启动时命令行不带参数", Desktop.SettingsPage.AutostartCommand(@"C:\x.exe", false));

            // ---------------- 话筒协议核心（远程喊话：格式与安全） ----------------
            var micKey = new byte[32];
            for (int i = 0; i < micKey.Length; i++) micKey[i] = (byte)(i * 7 + 1);
            var opus = new byte[] { 0xF8, 0xFF, 0xFE, 0x01, 0x02, 0x03 };
            var micPkt = MicProtocol.BuildPacket(MicProtocol.TypeAudio, 4242, 7UL, 20, opus, micKey);
            Check(micPkt.Length == MicProtocol.HeaderSize + opus.Length + MicProtocol.TagSize,
                "话筒音频包长度 = 头20 + 密文 + tag16", micPkt.Length.ToString());
            bool opened = MicProtocol.TryOpenPacket(micPkt, micKey, out byte mType, out uint mSid, out ulong mSeq,
                out ushort mFrameMs, out byte[] mPayload, out string mErr);
            Check(opened && mType == MicProtocol.TypeAudio && mSid == 4242 && mSeq == 7UL && mFrameMs == 20
                  && mPayload.Length == opus.Length && mPayload[0] == 0xF8,
                "话筒包能被正确解出（sid/seq/帧长/载荷一致）", mErr ?? "ok");

            // ---- 4.0.0 · nonce 根治：seq 从 4 字节扩到 8 字节，nonce 的 12 字节全部有效 ----
            Check(MicProtocol.Version == 2 && MicProtocol.HeaderSize == 20,
                "协议版本 2、包头 20 字节（seq 占 8 字节）",
                "ver=" + MicProtocol.Version + " header=" + MicProtocol.HeaderSize);
            ulong bigSeq = 0x0123456789ABCDEFUL;
            var bigPkt = MicProtocol.BuildPacket(MicProtocol.TypeApi, 0, bigSeq, 0, new byte[] { 1, 2, 3 }, micKey);
            bool bigOk = MicProtocol.TryOpenPacket(bigPkt, micKey, out _, out uint bigSid, out ulong bigOut,
                out _, out byte[] bigPl, out string bigErr);
            Check(bigOk && bigSid == 0 && bigOut == bigSeq && bigPl.Length == 3,
                "64 位序号完整往返（> 2^32 的 seq 不再被截断）", bigErr ?? ("seq=" + bigOut));
            var nonceA = MicProtocol.Nonce(0, 1UL);
            var nonceB = MicProtocol.Nonce(0, 0x100000000UL);
            bool nDiff = false;
            for (int i = 0; i < 12; i++) if (nonceA[i] != nonceB[i]) nDiff = true;
            Check(nDiff && (nonceA[8] | nonceA[9] | nonceA[10] | nonceA[11]) == 0
                  && (nonceB[8] | nonceB[9] | nonceB[10] | nonceB[11]) != 0,
                "nonce 用满 12 字节（原先后 4 字节恒为 0，seq 超过 2^32 后才写进那 4 字节）");
            var hdr8 = MicProtocol.BuildHeader(MicProtocol.TypeAudio, 1, 0x0102030405060708UL, 20, 0);
            Check(hdr8.Length == 20 && hdr8[8] == 0x08 && hdr8[15] == 0x01,
                "包头里 seq 按小端写满偏移 8..15");
            var tampered = (byte[])micPkt.Clone();
            tampered[MicProtocol.HeaderSize] ^= 0x01;                     // 改动密文一位
            Check(!MicProtocol.TryOpenPacket(tampered, micKey, out _, out _, out _, out _, out _, out _),
                "密文被改动 → AEAD 校验失败（拒绝）");
            var tampered2 = (byte[])micPkt.Clone();
            tampered2[8] ^= 0x01;                                         // 改动序号（属于 AAD）
            Check(!MicProtocol.TryOpenPacket(tampered2, micKey, out _, out _, out _, out _, out _, out _),
                "包头被改动 → AEAD 校验失败（AAD 保护包头）");
            var wrongKey = new byte[32];
            Check(!MicProtocol.TryOpenPacket(micPkt, wrongKey, out _, out _, out _, out _, out _, out _),
                "密钥不对 → 拒绝");
            var silence = MicProtocol.BuildPacket(MicProtocol.TypeSilence, 1, 1, 0, Array.Empty<byte>(), micKey);
            Check(MicProtocol.TryOpenPacket(silence, micKey, out byte sType, out _, out _, out _, out byte[] sPay, out _)
                  && sType == MicProtocol.TypeSilence && sPay.Length == 0, "静音帧（无载荷）可正常收发");
            var badFrame = MicProtocol.BuildHeader(MicProtocol.TypeAudio, 1, 1, 33, 0);
            Check(!MicProtocol.TryParseHeader(badFrame, out _, out _, out _, out _, out _, out _),
                "非法帧长（33ms）被拒绝");

            var rw = new MicProtocol.ReplayWindow();
            Check(rw.Accept(1) && rw.Accept(2) && rw.Accept(3), "滑动窗口：顺序包依次接受");
            Check(!rw.Accept(3), "滑动窗口：重复序号被拒绝（防重放）");
            Check(rw.Accept(5) && rw.Accept(4), "滑动窗口：窗口内乱序仍接受");
            var rw2 = new MicProtocol.ReplayWindow();
            rw2.Accept(1000);
            Check(!rw2.Accept(900), "滑动窗口：过旧（窗口外）的包被拒绝");
            var rw3 = new MicProtocol.ReplayWindow();
            rw3.Accept(1);
            Check(rw3.Accept(1 + MicProtocol.ReplayWindowSize + 10), "滑动窗口：大步前进后仍接受新包");

            string micSeed = "self-test-seed";
            var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            string c0 = MicProtocol.CurrentCode(micSeed, t0);
            Check(c0.Length == 6 && c0.All(char.IsDigit), "动态码是 6 位数字", c0);
            Check(MicProtocol.CurrentCode(micSeed, t0.AddSeconds(30)) == c0, "同一 60 秒窗口内动态码不变");
            Check(MicProtocol.CurrentCode(micSeed, t0.AddSeconds(61)) != c0, "跨窗口后动态码轮换");
            Check(MicProtocol.VerifyCode(micSeed, c0, t0.AddSeconds(30), out _), "当前窗口的动态码校验通过");
            Check(MicProtocol.VerifyCode(micSeed, c0, t0.AddSeconds(61), out _), "上一个窗口的动态码仍接受（60秒宽限）");
            Check(!MicProtocol.VerifyCode(micSeed, c0, t0.AddSeconds(130), out _), "过期窗口的动态码被拒绝");
            Check(!MicProtocol.VerifyCode(micSeed, "000000", t0, out _) || c0 == "000000", "错误动态码被拒绝");
            Check(MicProtocol.CurrentCode("seed-A", t0) != MicProtocol.CurrentCode("seed-B", t0),
                "不同会话种子产生不同动态码");

            // RFC 7748 官方向量：手机端（纯 Kotlin X25519）也用同一组向量自检，
            // 两边都对上 → 手机与电脑算出的共享密钥必然一致（这是配对能不能通的关键前提）。
            var rfcPrivA = HexToBytes("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            var rfcPubA = HexToBytes("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
            var rfcPrivB = HexToBytes("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
            var rfcPubB = HexToBytes("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
            var rfcShared = HexToBytes("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
            Check(X25519Pub(rfcPrivA).SequenceEqual(rfcPubA) && X25519Pub(rfcPrivB).SequenceEqual(rfcPubB),
                "RFC 7748 §6.1：电脑端 X25519 私钥→公钥与官方向量一致（与手机端同算法）");
            Check(X25519Shared(rfcPrivA, rfcPubB).SequenceEqual(rfcShared) && X25519Shared(rfcPrivB, rfcPubA).SequenceEqual(rfcShared),
                "RFC 7748 §6.1：电脑端共享密钥与官方向量一致 → 与手机端必然算得出同一把密钥");

            // 数据面加密路径（带 -f 的服务器要求密文）：加密 → 解密往返必须一致，
            // 且形状 = 明文 + 4(random) + 16(tag) + 12(finger)，与 Rust 侧 SecretBody 一致。
            {
                string dp = "pw-1234567890ab";
                byte[] dkey = BroadcastSystemNet.VntLite.VntCipher.DeriveKey(dp, out bool use128);
                var vpkt = new BroadcastSystemNet.VntLite.VntPacket
                {
                    GatewayFlag = false,
                    Protocol = BroadcastSystemNet.VntLite.VntOuterProtocol.IpTurn,
                    Transport = BroadcastSystemNet.VntLite.VntIpTurnProtocol.Ipv4,
                    SourceIp = BroadcastSystemNet.VntLite.VntPacket.SelfIp,
                    DestIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.9")
                };
                var inner = Encoding.UTF8.GetBytes("cbs-pair-frame-fake");
                var ipframe = BroadcastSystemNet.VntLite.VntIpFrame.Build(vpkt.SourceIp, vpkt.DestIp, 40000, 40001, inner);
                var nonce = BroadcastSystemNet.VntLite.VntCipher.BuildNonce(vpkt);
                var fh = BroadcastSystemNet.VntLite.VntCipher.FingerHash("test-token");
                var enc = BroadcastSystemNet.VntLite.VntCipher.Encrypt(ipframe, dkey, nonce, fh, true);
                Check(enc.Length == ipframe.Length + 32, "数据面密文形状 = 明文 + 4(random) + 16(tag) + 12(finger)",
                    $"实际 {enc.Length}（明文 {ipframe.Length}）");
                bool okDp = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(enc, dkey, nonce, fh, true, out var back, out string dpErr);
                Check(okDp && back.SequenceEqual(ipframe), "数据面加密→解密往返一致（两端同一把数据密码密钥）", dpErr);
            }

            // vnt 组网开关的"下次启动保持这个状态"靠 VntManager.AutoStart 落盘（用户要求）
            {
                bool oldAuto = VntManager.AutoStart;
                VntManager.AutoStart = true;
                Check(VntManager.AutoStart, "vnt 开关：打开状态已写入配置（下次启动程序会自动组网）");
                VntManager.AutoStart = false;
                Check(!VntManager.AutoStart, "vnt 开关：关闭状态已写入配置（下次启动程序不会自动组网）");
                VntManager.AutoStart = oldAuto;
            }

            // 配对答复的 JSON 转义（真机踩过：base64 里的 + 被转义成 \u002B → 手机端解不出公钥）
            {
                var kpJ = MicProtocol.NewKeyPair();
                var okLine = MicPairing.BuildAuthOk(7, kpJ.PublicKey, MicProtocol.NewNonce());
                Check(!okLine.Contains("\\u002B"), "配对答复：base64 里的 + 不被 JSON 转义（否则手机端公钥变 0 字节）", okLine);
                Check(MicPairing.HasKeyMaterial(okLine), "配对答复：带 ps/ns（手机端能派生会话密钥）");
                string psPart = okLine.Split(new[] { "\"ps\":\"" }, StringSplitOptions.None)[1].Split('"')[0];
                Check(Convert.FromBase64String(psPart).Length == 32, "配对答复：ps 是可解码的 32 字节公钥");
            }

            // 二维码编码器（纯 C# 实现）：必须能"编码 → 反向解码"读回原文，且纠错码伴随式全零
            {
                var (qrOk, qrDetail) = Desktop.QrCode.SelfCheck();
                Check(qrOk, "二维码：纯 C# 编码器自检（往返一致 + RS 伴随式 + 格式信息标准值）", qrDetail);
                var qm = Desktop.QrCode.Encode("测试中文 QR " + new string('A', 200));
                Check(qm.GetLength(0) == qm.GetLength(1) && qm.GetLength(0) >= 21,
                    "二维码：矩阵尺寸合法（(17+4×版本) 的正方形）", $"{qm.GetLength(0)}×{qm.GetLength(1)}");
                // 【外部验证】把"真实连接二维码"导出成 PNG：交给**独立解码器**（OpenCV / 手机相机）验证可扫性。
                // 自检只能证明"自己编→自己解"一致，证明不了别的解码器能认——这一步才是真凭据。
                try
                {
                    string pl = VntManager.BuildQrPayloadFromConfig("192.168.1.10", 29880, 29881, "PC-TEST", 5000);
                    var qbmp = Desktop.QrCode.Render(pl);
                    var qenc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    qenc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(qbmp));
                    string qdist = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist"));
                    System.IO.Directory.CreateDirectory(qdist);
                    using (var qfs = System.IO.File.Create(System.IO.Path.Combine(qdist, "qr-selftest.png"))) qenc.Save(qfs);
                    Check(true, "二维码：已导出 qr-selftest.png（供独立解码器验证可扫性）", pl.Length + " 字节载荷");
                }
                catch (Exception qex) { Check(false, "二维码：导出 PNG 失败", qex.Message); }
            }

            var kpA = MicProtocol.NewKeyPair();            var kpB = MicProtocol.NewKeyPair();
            var nA = MicProtocol.NewNonce();
            var nB = MicProtocol.NewNonce();
            var keyA = MicProtocol.DeriveSessionKey(kpA.PrivateKey, kpB.PublicKey, c0, nA, nB);
            var keyB = MicProtocol.DeriveSessionKey(kpB.PrivateKey, kpA.PublicKey, c0, nA, nB);
            Check(keyA.Length == 32 && keyA.SequenceEqual(keyB), "X25519+HKDF：双方派生出同一把 32 字节会话密钥");
            var keyWrongCode = MicProtocol.DeriveSessionKey(kpA.PrivateKey, kpB.PublicKey, "999999", nA, nB);
            Check(!keyWrongCode.SequenceEqual(keyA), "动态码不同 → 派生出不同密钥（窃听者拿不到密钥）");
            Check(kpA.PublicKey.Length == 32 && !kpA.PublicKey.SequenceEqual(kpB.PublicKey), "每次会话生成不同的临时密钥对");

            Check(VntManager.MaskToken("") == "(未设置)" && VntManager.MaskToken("ab") == "**", "空/极短 token 的脱敏不会泄露");

            // 真实 --info 样例（取自 vnt 官方 README）
            string infoSample = "Name: Ubuntu 18.04 (bionic) [64-bit]\nVirtual ip: 10.26.0.2\nVirtual gateway: 10.26.0.1\n" +
                "Virtual netmask: 255.255.255.0\nConnection status: Connected\nNAT type: Cone\n" +
                "Relay server: 203.0.113.10:29871\nPublic ips: 198.51.100.7\nLocal ip: 172.25.165.58\n";
            var vi = VntManager.ParseInfo(infoSample);
            Check(vi.VirtualIp == "10.26.0.2" && vi.Gateway == "10.26.0.1" && vi.Connected && vi.NatType == "Cone",
                "解析 vnt --info：虚拟IP/网关/连接状态/NAT类型", vi.VirtualIp + " " + vi.Status);
            Check(vi.Relay == "203.0.113.10:29871" && vi.LocalIp == "172.25.165.58", "解析 vnt --info：中继与本地IP");
            Check(!VntManager.ParseInfo("Connection status: Connecting\n").Connected, "未连接时 Connected 为 false");

            // 真实 --list 样例
            string listSample = "Name                                                       Virtual Ip      P2P/Relay      Rt      Status\n" +
                "Windows 10.0.22621 (Windows 11 Professional) [64-bit]      10.26.0.3       p2p            2       Online\n" +
                "CentOS 7.9.2009 (Core) [64-bit]                            10.26.0.4       p2p            35      Online\n" +
                "Android 13 (Xiaomi) [64-bit]                               10.26.0.9       relay          88      Online\n";
            var peers = VntManager.ParseList(listSample);
            Check(peers.Count == 3, "解析 vnt --list：识别 3 台设备（跳过表头）", peers.Count.ToString());
            Check(peers[0].VirtualIp == "10.26.0.3" && peers[0].Kind == "p2p" && peers[0].Rt == 2 && peers[0].Status == "Online",
                "设备行字段正确（虚拟IP/p2p/延迟/在线）", peers.Count > 0 ? peers[0].Name : "-");
            Check(peers[2].Kind == "relay" && peers[2].Rt == 88, "中继连接也能正确识别（relay + 延迟）");
            Check(peers[0].Name.Contains("Windows 10.0.22621"), "设备名含空格也能完整解析", peers[0].Name);

            string qr = VntManager.BuildQrPayload("1.2.3.4:29871", "secret-token", "phone-01", "udp", "aes_gcm",
                "10.26.0.1", VntManager.DefaultMicPort, VntManager.DefaultCtlPort, "教学楼广播主机");
            var qrObj = JsonNode.Parse(qr);
            Check(qrObj?["t"]?.ToString() == "cbs-mic" && qrObj?["v"]?.GetValue<int>() == 1,
                "二维码载荷带类型标记与版本号（安卓端据此识别）", qrObj?["t"]?.ToString());
            Check(qrObj?["vnt"]?["server"]?.ToString() == "1.2.3.4:29871" && qrObj?["vnt"]?["token"]?.ToString() == "secret-token",
                "二维码含 vnt 组网参数（服务器/token）");
            Check(qrObj?["mic"]?["host"]?.ToString() == "10.26.0.1" && qrObj?["mic"]?["port"]?.GetValue<int>() == 29880
                  && qrObj?["mic"]?["ctl_port"]?.GetValue<int>() == 29881,
                "二维码含话筒连接参数（虚拟IP/音频端口/配对端口）");
            // 交接前排错：二维码必须把"能决定手机端能否通信"的字段全带上（漏一个就入网成功但收不到数据）
            VntManager.Server = "1.2.3.4:29871"; VntManager.Token = "tok-x"; VntManager.DeviceId = "";
            VntManager.Encrypt = "aes_gcm"; VntManager.Fingerprint = true; VntManager.DataPassword = "pw-x";
            var qrFromCfg = JsonNode.Parse(VntManager.BuildQrPayloadFromConfig("10.26.0.2", 29880, 29881, "主机", 5000));
            Check(qrFromCfg?["vnt"]?["model"]?.ToString() == "aes_gcm"
                  && qrFromCfg?["vnt"]?["finger"]?.GetValue<bool>() == true
                  && qrFromCfg?["vnt"]?["w_password"]?.ToString() == "pw-x"
                  && qrFromCfg?["mic"]?["http_port"]?.GetValue<int>() == 5000,
                "二维码唯一入口带齐全部关键字段（加密方式/指纹/数据密码/网页端口）");
            VntManager.Server = ""; VntManager.Token = ""; VntManager.Encrypt = "";
            VntManager.Fingerprint = false; VntManager.DataPassword = "";
            Check(qr.Length < 700, "二维码载荷足够小（普通二维码可容纳）", qr.Length + " 字节");
            // 【重写】旧断言写的是 "10.26.0.1 算、192.168.1.2 不算"，那是按 vnet_prefix 的 CIDR 比对的旧语义。
            // 用户把虚拟 IP 改成"由服务端分配"后 vnet_prefix 为空，旧实现必然 false（界面误报"还没拿到虚拟 IP"），
            // 因此 LooksLikeVntAddress 已改为**纯 IP 语义**：合法、非 0、非回环、非 IPv6 的 IPv4 都算。
            Check(VntManager.LooksLikeVntAddress("10.26.0.1") && VntManager.LooksLikeVntAddress("192.168.1.2")
                  && !VntManager.LooksLikeVntAddress("") && !VntManager.LooksLikeVntAddress("0.0.0.0")
                  && !VntManager.LooksLikeVntAddress("127.0.0.1") && !VntManager.LooksLikeVntAddress("不是IP")
                  && !VntManager.LooksLikeVntAddress("2001:db8::1"),
                "虚拟 IP 判定：纯 IP 语义（合法且非 0/非回环/非 IPv6 的 IPv4 都算，不再按网段）");
            Check(!VntManager.VntConfigured, "默认没有配置 vnt（程序不内置服务器/token）");

            // ---------------- 话筒安全状态机（限速锁定 / 一次性动态码 / 每包校验链） ----------------
            var guard = new MicSecurity.PairingGuard();
            var tBase = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < MicSecurity.PairingGuard.MaxFailures - 1; i++) guard.NoteFailure("10.26.0.9", tBase);
            Check(!guard.IsLocked("10.26.0.9", tBase.AddSeconds(1)), "配对失败未达阈值时不锁定",
                guard.FailsInWindow("10.26.0.9", tBase.AddSeconds(1)).ToString());
            Check(guard.NoteFailure("10.26.0.9", tBase.AddSeconds(2)), "第 5 次配对失败触发锁定");
            Check(guard.IsLocked("10.26.0.9", tBase.AddMinutes(5)), "锁定期间继续拒绝（10 分钟）");
            Check(!guard.IsLocked("10.26.0.9", tBase.AddMinutes(11)), "锁定期满后自动解锁");
            Check(!guard.IsLocked("10.26.0.8", tBase), "锁定只针对来源 IP，不影响其他设备");
            guard.NoteSuccess("10.26.0.9");
            Check(guard.FailsInWindow("10.26.0.9", tBase.AddMinutes(11)) == 0, "配对成功后失败计数清零");

            var codeStore = new MicSecurity.CodeStore("mic-guard-seed");
            var tCode = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            string realCode = MicProtocol.CurrentCode("mic-guard-seed", tCode);
            Check(codeStore.TryConsume(realCode, tCode, out long w1), "动态码首次使用通过");
            Check(!codeStore.TryConsume(realCode, tCode, out _), "同一动态码第二次使用被拒绝（一次性）");
            Check(w1 == MicProtocol.WindowIndex(tCode), "消费时返回所属时间窗（便于审计）");
            string nextCode = MicProtocol.CurrentCode("mic-guard-seed", tCode.AddSeconds(61));
            Check(codeStore.TryConsume(nextCode, tCode.AddSeconds(61), out _), "下一个窗口的新动态码可用");
            Check(!codeStore.TryConsume("000000", tCode, out _) || realCode == "000000", "错误动态码被拒绝");

            var reg = new MicSecurity.Registry();
            var sess = reg.Create("10.26.0.9", "mic-phone-01", new byte[32], tBase, maxMinutes: 60);
            Check(sess.Sid > 0 && reg.Count == 1, "会话注册成功（分配 sid）");
            Check(reg.ValidateIncoming(sess.Sid, "10.26.0.9", 1, 100, tBase, out _) == MicSecurity.PacketReject.None, "正常包通过校验链");
            Check(reg.ValidateIncoming(sess.Sid, "10.26.0.9", 1, 100, tBase, out _) == MicSecurity.PacketReject.Replay, "重复序号被拒绝（防重放）");
            Check(reg.ValidateIncoming(sess.Sid, "10.26.0.99", 2, 100, tBase, out _) == MicSecurity.PacketReject.WrongSource,
                "来源 IP 与会话不符被拒绝（白名单绑定虚拟IP）");
            Check(reg.ValidateIncoming(999999, "10.26.0.9", 1, 100, tBase, out _) == MicSecurity.PacketReject.NoSession, "未知 sid 被拒绝");
            Check(reg.ValidateIncoming(sess.Sid, "10.26.0.9", 3, 100, tBase.AddMinutes(61), out _) == MicSecurity.PacketReject.Expired,
                "超过单次时长上限（60 分钟）被拒绝");
            var sess2 = reg.Create("10.26.0.7", "mic-phone-02", new byte[32], tBase, maxMinutes: 0);
            var tRate = tBase.AddSeconds(10);
            bool rateHit = false;
            for (int i = 0; i < MicSecurity.Session.MaxPacketsPerSecond + 5; i++)
            {
                if (reg.ValidateIncoming(sess2.Sid, "10.26.0.7", (uint)(100 + i), 100, tRate, out _) == MicSecurity.PacketReject.RateLimited)
                { rateHit = true; break; }
            }
            Check(rateHit, "包速率超限（>100 包/秒）被拒绝（限流）");
            Check(!sess2.Expired(tBase.AddHours(5)), "时长上限为 0 表示不限制");
            // 注意：上面限速循环里前 100 个包是被接受的，LastPacketAt 已被推到 tBase+10s，
            // 所以这里要用 tBase+26s（26-10=16s > 15s）才算静默超时。
            Check(sess2.Silent(tBase.AddSeconds(26), TimeSpan.FromSeconds(15)), "静默超时判定（15 秒无包视为断线）");
            Check(!sess2.Silent(tBase.AddSeconds(20), TimeSpan.FromSeconds(15)), "有包时不判静默超时");
            uint sidToKill = sess2.Sid;
            byte[] keyRef = sess2.Key;
            reg.Remove(sidToKill);
            Check(reg.Find(sidToKill) == null && reg.Count == 1, "会话结束后从注册表移除");
            Check(keyRef.All(b => b == 0), "会话密钥在结束时被清零销毁");
            Check(new MicSecurity.Stats().Summary().Contains("配对成功"), "审计摘要可生成（供状态页/告警用）");

            // ---------------- 话筒配对协议（HELLO/AUTH_OK/密钥两端一致） ----------------
            string pSeed = "pairing-seed";
            var tP = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            string pCode = MicProtocol.CurrentCode(pSeed, tP);
            var clientKp = MicProtocol.NewKeyPair();
            var clientNonce = MicProtocol.NewNonce();
            string helloLine = new JsonObject
            {
                ["t"] = MicPairing.Type,
                ["v"] = MicPairing.Version,
                ["code"] = pCode,
                ["device_id"] = "mic-phone-01",
                ["pk"] = Convert.ToBase64String(clientKp.PublicKey),
                ["nc"] = Convert.ToBase64String(clientNonce)
            }.ToJsonString();
            Check(MicPairing.TryParseHello(helloLine, out var helloOk, out _) && helloOk.DeviceId == "mic-phone-01"
                  && helloOk.PublicKey.Length == 32 && helloOk.ClientNonce.Length == 16,
                "解析合法 HELLO（设备标识/公钥/随机数）");
            Check(!MicPairing.TryParseHello("{\"t\":\"other\",\"v\":1,\"code\":\"123456\",\"device_id\":\"x\",\"pk\":\"AA==\",\"nc\":\"AA==\"}", out _, out _),
                "类型标记不符的报文被拒绝");
            Check(!MicPairing.TryParseHello("{\"t\":\"cbs-mic\",\"v\":9,\"code\":\"123456\",\"device_id\":\"x\",\"pk\":\"AA==\",\"nc\":\"AA==\"}", out _, out _),
                "协议版本不符的报文被拒绝");
            Check(!MicPairing.TryParseHello("{\"t\":\"cbs-mic\",\"v\":1,\"code\":\"12ab56\",\"device_id\":\"x\",\"pk\":\"AA==\",\"nc\":\"AA==\"}", out _, out _),
                "动态码必须 6 位数字");
            Check(!MicPairing.TryParseHello(new string('x', MicPairing.MaxHelloBytes + 10), out _, out _), "超大报文被拒绝（防滥用）");

            var pGuard = new MicSecurity.PairingGuard();
            var pCodes = new MicSecurity.CodeStore(pSeed);
            var auth1 = MicPairing.Authenticate(helloLine, "10.26.0.9", tP, micEnabled: true, maxSessions: 1, activeSessions: 0,
                guard: pGuard, codes: pCodes, codeSeed: pSeed);
            Check(auth1.Ok && auth1.SessionKey.Length == 32 && auth1.ServerPublicKey.Length == 32,
                "正确动态码 → 握手成功并派生 32 字节会话密钥", auth1.Reason ?? "ok");
            var serverKp = new MicProtocol.KeyPair { PublicKey = auth1.ServerPublicKey };
            var clientKey = MicPairing.ClientDeriveKey(clientKp.PrivateKey, auth1.ServerPublicKey, pCode, clientNonce, auth1.ServerNonce);
            Check(clientKey.SequenceEqual(auth1.SessionKey), "客户端与服务端派生出的会话密钥完全一致（安卓端可照此实现）");
            var authReuse = MicPairing.Authenticate(helloLine, "10.26.0.9", tP, true, 1, 0, pGuard, pCodes, pSeed);
            Check(!authReuse.Ok && authReuse.Reason == MicPairing.Reason.BadCode, "同一动态码再次握手失败（bad_code，一次性）");
            Check(authReuse.Reply.Contains("bad_code"), "AUTH_FAIL 报文带 reason 字段（安卓端据此提示）", authReuse.Reply);
            var authOff = MicPairing.Authenticate(helloLine, "10.26.0.10",
                tP.AddSeconds(61), micEnabled: false, 1, 0, pGuard, new MicSecurity.CodeStore(pSeed), pSeed);
            Check(!authOff.Ok && authOff.Reason == MicPairing.Reason.MicOff, "电脑端没开麦 → mic_off（最高优先级拒绝）");
            var authBusy = MicPairing.Authenticate(helloLine, "10.26.0.10", tP.AddSeconds(122),
                micEnabled: true, maxSessions: 1, activeSessions: 1, guard: pGuard, codes: new MicSecurity.CodeStore(pSeed), codeSeed: pSeed);
            Check(!authBusy.Ok && authBusy.Reason == MicPairing.Reason.Busy, "已有话筒在使用 → busy（默认拒绝而不是顶掉）");
            var lockGuard = new MicSecurity.PairingGuard();
            for (int i = 0; i < MicSecurity.PairingGuard.MaxFailures; i++) lockGuard.NoteFailure("10.26.0.66", tP);
            var authLocked = MicPairing.Authenticate(helloLine, "10.26.0.66", tP, true, 1, 0, lockGuard, new MicSecurity.CodeStore(pSeed), pSeed);
            Check(!authLocked.Ok && authLocked.Reason == MicPairing.Reason.TooMany, "被锁定的来源 IP → too_many（不再消耗动态码）");

            // ---------------- 幂等配对：同一动态码的多通道重复请求必须**重发成功答复**而不是记失败 ----------------
            // 背景（真机踩到）：手机端同时试"经 vnt 中继"和"局域网直发"，同一个 6 位码会到电脑端两次；
            // 动态码一次性，第二次到达被判 bad_code → 界面上出现"配对成功 2 / 失败 11"这种怪异计数。
            {
                string idSeed = "idem-seed";
                VntManager.Set("mic", "code_seed", System.Text.Json.Nodes.JsonValue.Create(idSeed));
                MicServer.Stop();
                Check(MicServer.Start("127.0.0.1", 29893, 29894, idSeed, out string iErr),
                    "幂等配对用例：话筒服务启动", iErr ?? "ok");
                var tI = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
                string iCode = MicProtocol.CurrentCode(idSeed, tI);
                var iKp = MicProtocol.NewKeyPair();
                string iHello = new JsonObject
                {
                    ["t"] = MicPairing.Type, ["v"] = MicPairing.Version, ["code"] = iCode,
                    ["device_id"] = "idem-phone", ["pk"] = Convert.ToBase64String(iKp.PublicKey),
                    ["nc"] = Convert.ToBase64String(MicProtocol.NewNonce())
                }.ToJsonString();
                long failBefore = MicServer.Stats.PairingFail;
                var idemR1 = MicServer.HandlePairLine(iHello, "10.26.0.9", true, 1, idSeed, tI, out uint sidI, out byte[] keyI, "10.26.0.9");
                Check(idemR1.Contains("\"ok\":true") && keyI != null && keyI.Length == 32,
                    "幂等配对：第一次请求成功（拿到会话密钥）", idemR1);
                var idemR2 = MicServer.HandlePairLine(iHello, "10.26.0.9", true, 1, idSeed, tI, out uint sidI2, out byte[] keyI2, "10.26.0.9");
                Check(idemR2.Contains("\"ok\":true") && sidI2 == sidI && keyI2 != null && keyI2.SequenceEqual(keyI),
                    "幂等配对：同一动态码的重复请求重发同样的成功答复（不记失败）", idemR2);
                Check(MicServer.Stats.PairingFail == failBefore,
                    "幂等配对：重复请求不增加\"配对失败\"计数", $"失败计数 ${MicServer.Stats.PairingFail - failBefore}");
                MicServer.Stop();
            }

            // ---------------- 话筒服务：真实 socket 端到端（回环） ----------------
            string eSeed = "e2e-seed";
            VntManager.Set("mic", "code_seed", System.Text.Json.Nodes.JsonValue.Create(eSeed));
            VntManager.MicEnabled = true;
            string bindErr;
            string bindErrAny;
            // vnt-lite 模式（不建虚拟网卡）必须能绑 0.0.0.0，否则手机根本连不上；
            // 安全性靠"来源过滤 + cbs-pair AEAD + 6 位动态码 + 限速 + 审计"，见 MicServer.IsAcceptableSource。
            Check(MicServer.Start("0.0.0.0", 29890, 29891, eSeed, out bindErrAny),
                "vnt-lite 模式：允许监听 0.0.0.0（手机没有虚拟网卡时只能这样连）", bindErrAny ?? "ok");
            Check(MicServer.IsAcceptableSource("10.26.0.5") && MicServer.IsAcceptableSource("192.168.1.50")
                && MicServer.IsAcceptableSource("127.0.0.1")
                && !MicServer.IsAcceptableSource("203.0.113.9") && !MicServer.IsAcceptableSource("8.8.8.8"),
                "来源过滤：虚拟网/私网/回环放行，公网来源拒绝",
                "放行 10.26.0.5/192.168.1.50/127.0.0.1，拒绝 203.0.113.9/8.8.8.8");
            MicServer.Stop();
            Check(MicServer.Start("127.0.0.1", 29890, 29891, eSeed, out bindErr), "话筒服务在回环地址启动成功", bindErr ?? "ok");
            int waitMs = 0;
            while (!MicServer.Running && waitMs < 1000) { System.Threading.Thread.Sleep(20); waitMs += 20; }
            try
            {
                // ① 真实 TCP 配对：先用错误动态码，再用正确动态码
                string codeNow = MicProtocol.CurrentCode(eSeed, DateTime.UtcNow);
                string helloBad = new System.Text.Json.Nodes.JsonObject
                {
                    ["t"] = MicPairing.Type, ["v"] = 1,
                    ["code"] = codeNow == "000000" ? "111111" : "000000",
                    ["device_id"] = "e2e-phone",
                    ["pk"] = Convert.ToBase64String(MicProtocol.NewKeyPair().PublicKey),
                    ["nc"] = Convert.ToBase64String(MicProtocol.NewNonce())
                }.ToJsonString();
                string replyBad = PairOverTcp(29891, helloBad);
                Check(replyBad.Contains("bad_code"), "端到端：错误动态码被拒（bad_code）", replyBad);

                var cliKp = MicProtocol.NewKeyPair();
                var cliNonce = MicProtocol.NewNonce();
                codeNow = MicProtocol.CurrentCode(eSeed, DateTime.UtcNow);   // 重新取（可能跨窗口）
                string e2eHello = new System.Text.Json.Nodes.JsonObject
                {
                    ["t"] = MicPairing.Type, ["v"] = 1, ["code"] = codeNow,
                    ["device_id"] = "e2e-phone",
                    ["pk"] = Convert.ToBase64String(cliKp.PublicKey),
                    ["nc"] = Convert.ToBase64String(cliNonce)
                }.ToJsonString();
                string replyOk = PairOverTcp(29891, e2eHello);
                Check(replyOk.Contains("\"ok\":true"), "端到端：正确动态码握手成功", replyOk);
                var replyObj = System.Text.Json.Nodes.JsonNode.Parse(replyOk);
                uint eSid = replyObj?["sid"]?.GetValue<uint>() ?? 0;
                var srvPub = Convert.FromBase64String(replyObj?["ps"]?.ToString() ?? "");
                var srvNonce = Convert.FromBase64String(replyObj?["ns"]?.ToString() ?? "");
                var e2eSess = MicServer.Registry.Find(eSid);
                Check(eSid > 0 && e2eSess != null, "端到端：会话已登记（sid=" + eSid + "）");
                // 公钥为空说明上一步就没成功：明确报出来并跳过派生，别让自检自己抛异常中断（曾经如此）
                if (srvPub.Length != 32)
                    Check(false, "端到端：AUTH_OK 里的服务端公钥应为 32 字节", "实际 " + srvPub.Length + " 字节（reply=" + replyOk + "）");
                else
                {
                var cliKey = MicPairing.ClientDeriveKey(cliKp.PrivateKey, srvPub, codeNow, cliNonce, srvNonce);
                Check(e2eSess != null && cliKey.SequenceEqual(e2eSess.Key), "端到端：客户端与服务端密钥一致（可正常加密通话）");

                // ② 真实 UDP 音频：发一包 → 接受；重发 → 判重放；篡改 → 判非法
                byte[] key = cliKey;
                var frame = new byte[] { 0xF8, 0xFF, 0xFE, 0x11, 0x22 };
                var pkt = MicProtocol.BuildPacket(MicProtocol.TypeAudio, eSid, 1, 20, frame, key);
                UdpSend("127.0.0.1", 29890, pkt);
                bool accepted = WaitFor(() => MicServer.Registry.Find(eSid)?.Accepted > 0);
                Check(accepted, "端到端：合法音频包被接受（UDP 实收）", (MicServer.Registry.Find(eSid)?.Accepted ?? 0).ToString());
                Check(MicServer.SweepTimerWired, "话筒服务的静默清理/恢复定时器已接线");
                Check(MicDuck.IsDucked, "端到端：收到真实加密音频后自动压低音乐（收包→闪避联动成功）");
                long replayedBefore = MicServer.Registry.Find(eSid)?.Replayed ?? 0;
                UdpSend("127.0.0.1", 29890, pkt);                       // 重发同一包（音频）
                // 设计变更：**音频帧不再参与防重放窗口**（音频与接口共用一套递增序号，交错到达会把
                // 其中一股整批误判成重放 —— 真机就是因此"手机发了上千帧、电脑端 Ogg 0 页"）。
                // 所以这里验证的是"音频重复被接受（无害）"，防重放属性改在接口包上验证（见下）。
                Check(WaitFor(() => (MicServer.Registry.Find(eSid)?.Accepted ?? 0) > 1, 1500),
                    "端到端：音频重复包被接受（音频不参与防重放窗口，属设计）");
                // 防重放仍然生效：接口包（TypeApi）必须走窗口
                var sForWin = MicServer.Registry.Find(eSid);
                if (sForWin != null)
                {
                    var v1 = MicServer.Registry.ValidateIncoming(eSid, sForWin.SourceIp, 777, 64, DateTime.UtcNow, out _, null, false);
                    var v2 = MicServer.Registry.ValidateIncoming(eSid, sForWin.SourceIp, 777, 64, DateTime.UtcNow, out _, null, false);
                    Check(v1 == MicSecurity.PacketReject.None && v2 == MicSecurity.PacketReject.Replay,
                        "防重放：接口包重复序号被拒（窗口仍生效）", $"{v1} / {v2}");
                    var v3 = MicServer.Registry.ValidateIncoming(eSid, sForWin.SourceIp, 777, 64, DateTime.UtcNow, out _, null, true);
                    Check(v3 == MicSecurity.PacketReject.None, "防重放：音频包跳过窗口（设计如此）", v3.ToString());
                }
                long badBefore = MicServer.Stats.BadPackets;
                var e2eTampered = (byte[])pkt.Clone(); e2eTampered[20] ^= 0x5A;
                e2eTampered[8] = 0x5B;                                  // 顺便改一下序号字节，确保它不会被当"重复包"提前分流
                UdpSend("127.0.0.1", 29890, e2eTampered);                // 篡改密文
                Check(WaitFor(() => MicServer.Stats.BadPackets > badBefore, 2500), "端到端：被篡改的包被拒绝（AEAD）");
                Check(MicServer.StatusText().Contains("音频"), "状态摘要可生成（菜单显示用）", MicServer.StatusText().Split('\n')[0]);

                // 数据通道（sid=0）：播控/排程/调休"连上就能用"，不需要 6 位配对码。                // 授权 = 来源可接受 + 能解开 SHA256(数据密码) 那层 AEAD；这里验证它确实走进了接口处理，
                // 而不是被当成"未知会话"丢掉。
                VntManager.DataPassword = "open-channel-pw";
                var openKey = MicPairUdp.DeriveKey(VntManager.DataPassword);
                var openReq = System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"t1\",\"m\":\"GET\",\"p\":\"/api/sys\"}");
                var openPkt = MicProtocol.BuildPacket(MicProtocol.TypeApi, 0, 0x20000001, 0, openReq, openKey);
                MicServer.LastError = "";
                long unknownBefore = MicServer.Stats.BadPackets;
                long handledBefore = MicServer.ApiQueueHandled;
                MicServer.HandleAudioPacket(openPkt, "10.26.0.9", "10.26.0.9");
                // 现在接口请求是**入队后由专用线程串行处理**（接收线程绝不同步等 HTTP —— 那正是"拖动卡死电脑端"的根因），
                // 所以这里要等它被消费，而不是立刻断言。
                Check(WaitFor(() => MicServer.ApiQueueHandled > handledBefore, 3000),
                    "数据通道：sid=0 的请求入队后由专用线程处理（接收线程不被阻塞）",
                    $"已处理 {MicServer.ApiQueueHandled - handledBefore} 条，LastError={MicServer.LastError}");
                // 密码不对时必须拒绝（否则谁都来调接口了）
                var badKey2 = MicPairUdp.DeriveKey("wrong-password-x");
                var badPkt = MicProtocol.BuildPacket(MicProtocol.TypeApi, 0, 0x20000002, 0, openReq, badKey2);
                long badBefore2 = MicServer.Stats.BadPackets;
                MicServer.HandleAudioPacket(badPkt, "10.26.0.9", "10.26.0.9");
                Check(MicServer.Stats.BadPackets > badBefore2, "数据通道：数据密码不对时拒绝（AEAD 挡住）");
                // 重放（同一序号再发一次）必须被拒
                long replayBefore2 = MicServer.Stats.Replays;
                MicServer.HandleAudioPacket(openPkt, "10.26.0.9", "10.26.0.9");
                Check(MicServer.Stats.Replays > replayBefore2, "数据通道：同序号重放被拒绝");

                // cbs-api 桥的方法白名单：手机端「排程」页要改/删任务（PUT/DELETE），
                // 但**只放开 /api/tasks/{数字}**，别的写操作一律拒绝（安全边界不能放大）。
                Check(MicApiBridge.Validate("GET", "/api/sys") == null, "cbs-api 桥：GET /api/sys 允许");
                Check(MicApiBridge.Validate("POST", "/api/player/cmd") == null, "cbs-api 桥：POST /api/player/cmd 允许");
                Check(MicApiBridge.Validate("PUT", "/api/tasks/12") == null,
                    "cbs-api 桥：PUT /api/tasks/12 允许（手机端改排程）", MicApiBridge.Validate("PUT", "/api/tasks/12"));
                Check(MicApiBridge.Validate("DELETE", "/api/tasks/3") == null,
                    "cbs-api 桥：DELETE /api/tasks/3 允许（手机端删排程）", MicApiBridge.Validate("DELETE", "/api/tasks/3"));
                Check(MicApiBridge.Validate("DELETE", "/api/music/1") != null, "cbs-api 桥：DELETE 不许用在 /api/tasks 以外");
                Check(MicApiBridge.Validate("PUT", "/api/tasks/renumber") != null, "cbs-api 桥：非数字 id 的 PUT 被拒");
                Check(MicApiBridge.Validate("GET", "/etc/passwd") != null, "cbs-api 桥：非 /api /res 前缀被拒");
                Check(MicApiBridge.Validate("GET", "/api/../../etc") != null, "cbs-api 桥：含 .. 被拒");

                // ---------------- 安检门（跨网数据防注入/防刷） ----------------
                {
                    string gErr;
                    string okEnv = "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/sys\"}";
                    gErr = MicApiBridge.GateOnly(okEnv, "10.26.0.9", out _, out _, out _);
                    Check(gErr == null, "安检门：正常请求放行", gErr ?? "ok");

                    foreach (var bad in new[]
                    {
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/../etc/passwd\"}",     // 路径穿越
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/%2e%2e/sys\"}",        // 百分号编码绕过
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/sys\\r\\nX: y\"}",     // 请求行注入（CRLF）
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/sys?x=1\"}",           // 查询串
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/etc/passwd\"}",            // 前缀外
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"PATCH\",\"p\":\"/api/sys\"}",             // 方法滥用
                        "{\"v\":1,\"id\":\"坏 id\",\"m\":\"GET\",\"p\":\"/api/sys\"}",              // id 非法字符
                        "{\"v\":2,\"id\":\"a1\",\"m\":\"GET\",\"p\":\"/api/sys\"}",               // 版本不符
                        "{\"v\":1,\"id\":\"a1\",\"m\":\"DELETE\",\"p\":\"/api/music/1\"}"          // 越权删除
                    })
                    {
                        gErr = MicApiBridge.GateOnly(bad, "10.26.0.9", out _, out _, out _);
                        Check(gErr != null, "安检门：拦截 " + bad.Substring(0, Math.Min(58, bad.Length)), gErr ?? "竟然放行了");
                    }

                    // 载荷安检：脏数据不许写进 tasks.jsonl / 调休表
                    // 【管理员校验】改排程/调休必须来自"管理员设备"（用户要求）。先按**非管理员**验证会被拒，
                    // 再把自己加成管理员验证放行；播控不受影响（下面单独验证）。
                    const string devA = "selftest-device-A", devB = "selftest-device-B";
                    VntManager.RemoveAdminDevice(devA); VntManager.AddAdminDevice(devB);
                    var noAdminTask = "{\"v\":1,\"id\":\"a0\",\"m\":\"POST\",\"p\":\"/api/tasks\",\"did\":\"" + devA + "\",\"cred\":\"" + VntManager.DeviceCredential(devA) + "\",\"b\":{\"start_time\":\"08:00:00\",\"days\":[1],\"volume\":80}}";
                    string noAdminErr = MicApiBridge.GateOnly(noAdminTask, "10.26.0.9", out _, out _, out _);
                    Check(noAdminErr != null && noAdminErr.Contains("管理员"), "管理员校验：非管理员设备改排程被拒", noAdminErr ?? "(放行了，不对)");
                    var noAdminCmd = "{\"v\":1,\"id\":\"a0b\",\"m\":\"POST\",\"p\":\"/api/player/cmd\",\"did\":\"" + devA + "\",\"b\":{\"cmd\":\"stop\"}}";
                    Check(MicApiBridge.GateOnly(noAdminCmd, "10.26.0.9", out _, out _, out _) == null, "管理员校验：非管理员设备**播控不受影响**");
                    var badTask = "{\"v\":1,\"id\":\"a2\",\"m\":\"POST\",\"p\":\"/api/tasks\",\"did\":\"" + devB + "\",\"b\":{\"start_time\":\"8点\",\"days\":[9],\"volume\":999}}";
                    Check(MicApiBridge.GateOnly(badTask, "10.26.0.9", out _, out _, out _) != null, "安检门：非法任务字段被拒");
                    var okTask = "{\"v\":1,\"id\":\"a3\",\"m\":\"POST\",\"p\":\"/api/tasks\",\"did\":\"" + devB + "\",\"cred\":\"" + VntManager.DeviceCredential(devB) + "\",\"b\":{\"start_time\":\"08:00:00\",\"days\":[1,2],\"volume\":80,\"music_ids\":[\"1\",\"2\"]}}";
                    Check(MicApiBridge.GateOnly(okTask, "10.26.0.9", out _, out _, out _) == null, "安检门：合法任务放行（管理员设备）");
                    var badCmd = "{\"v\":1,\"id\":\"a4\",\"m\":\"POST\",\"p\":\"/api/player/cmd\",\"b\":{\"cmd\":\"rm -rf\"}}";
                    Check(MicApiBridge.GateOnly(badCmd, "10.26.0.9", out _, out _, out _) != null, "安检门：非法播控指令被拒");
                    // 调休是**数组**载荷（数据结构沿用）：必须能过安检门，否则手机端一直保存失败
                    var tiaoxiu = "{\"v\":1,\"id\":\"a5\",\"m\":\"POST\",\"p\":\"/api/tiaoxiu\",\"did\":\"" + devB + "\",\"cred\":\"" + VntManager.DeviceCredential(devB) + "\",\"b\":[{\"date\":\"2026-05-01\",\"action\":\"disable\",\"marker\":0,\"note\":\"劳动节\"}]}";
                    string tErr = MicApiBridge.GateOnly(tiaoxiu, "10.26.0.9", out _, out string tPath, out _);
                    Check(tErr == null && tPath == "/api/tiaoxiu", "安检门：调休整表（数组载荷）放行", tErr ?? "ok");
                    var badTiaoxiu = "{\"v\":1,\"id\":\"a6\",\"m\":\"POST\",\"p\":\"/api/tiaoxiu\",\"did\":\"" + devB + "\",\"b\":[{\"date\":\"2026/05/01\",\"action\":\"drop\"}]}";
                    Check(MicApiBridge.GateOnly(badTiaoxiu, "10.26.0.9", out _, out _, out _) != null, "安检门：非法调休记录被拒");
                    VntManager.RemoveAdminDevice(devB);
                    // 限速：同一来源连发必须被限（令牌桶）
                    int blocked = 0;
                    for (int i = 0; i < 200; i++)
                        if (MicApiBridge.GateOnly("{\"v\":1,\"id\":\"r" + i + "\",\"m\":\"GET\",\"p\":\"/api/sys\"}", "10.26.0.77", out _, out _, out _) != null) blocked++;
                    Check(blocked > 0, "安检门：同一来源被限速（200 次里拦下 " + blocked + " 次）");
                }

                }
            }
            finally { MicServer.Stop(); }
            Check(!MicServer.Running, "话筒服务可正常停止");

            // ---------------- UDP 配对通道（§15.3：手机不建网卡只能走 UDP） ----------------
            {
                string pwProbe = "selftest-pair-udp-pw";
                string pwWas = VntManager.DataPassword;
                try
                {
                    VntManager.DataPassword = pwProbe;
                    var pKey = MicPairUdp.DeriveKey(pwProbe);
                    Check(pKey.Length == 32, "UDP 配对密钥 = SHA256(数据密码)（32 字节）", pKey.Length + " 字节");
                    Check(MicPairUdp.DeriveKey(pwProbe).SequenceEqual(pKey), "同一数据密码派生出同一把密钥（两端一致）");
                    Check(MicPairUdp.DeriveKey(pwProbe + "-x").SequenceEqual(pKey) == false, "数据密码不同 → 密钥不同");
                    Check(MicPairUdp.DeriveKey("").Length == 0, "数据密码为空 → 不生成密钥（拒绝空密码当密钥）");

                    string line = "{\"t\":\"cbs-mic\",\"v\":1,\"code\":\"135790\",\"device_id\":\"udp-probe\",\"pk\":\"AQ==\",\"nc\":\"Ag==\"}";
                    var fr = MicPairUdp.Build(line, pKey);
                    Check(fr.Length == MicPairUdp.Prefix.Length + MicPairUdp.NonceSize + System.Text.Encoding.UTF8.GetByteCount(line) + MicPairUdp.TagSize
                          && MicPairUdp.LooksLikePairFrame(fr),
                        "配对帧 = cbs-pair:(9) + nonce(12) + 密文 + tag(16)", fr.Length + " 字节");
                    var asText = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(fr);
                    Check(!asText.Contains("135790") && !asText.Contains("udp-probe"),
                        "帧里不含明文动态码/设备号（中继模式下服务端也读不到）");
                    Check(MicPairUdp.TryUnwrap(fr, pKey, out _) == line, "配对帧解封往返一致");
                    Check(MicPairUdp.TryUnwrap(fr, MicPairUdp.DeriveKey(pwProbe + "-x"), out _) == null, "错误密钥解不开配对帧（AEAD）");
                    var tamperedFrame = (byte[])fr.Clone(); tamperedFrame[tamperedFrame.Length - 1] ^= 0x01;
                    Check(MicPairUdp.TryUnwrap(tamperedFrame, pKey, out string tErr) == null && tErr.Contains("AEAD"),
                        "被篡改的配对帧被拒绝（AEAD 校验失败）", tErr);
                    Check(MicPairUdp.TryUnwrap(new byte[] { 1, 2, 3 }, pKey, out _) == null, "过短的帧被拒绝");
                    Check(MicPairUdp.TryUnwrap(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 }, pKey, out _) == null, "前缀不符的帧被拒绝");
                    Check(!MicPairUdp.IsUsableReplyEndpoint(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0)),
                        "0.0.0.0 之类的来源地址不会用来回包");

                    // 中继场景白名单：外层源 = 服务端 IP、内层虚拟 IP = 手机 —— 两个都要认，否则音频被丢
                    var s = MicServer.Registry.Create("203.0.113.9", "udp-probe", pKey, tBase, 60);
                    s.AllowSource("10.26.0.77");
                    var v1 = MicServer.Registry.ValidateIncoming(s.Sid, "203.0.113.9", 1, 100, tBase, out _, "10.26.0.77");
                    var v2 = MicServer.Registry.ValidateIncoming(s.Sid, "203.0.113.9", 2, 100, tBase, out _, "10.26.0.99");
                    var v3 = MicServer.Registry.ValidateIncoming(s.Sid, "198.51.100.7", 3, 100, tBase, out _, "10.26.0.77");
                    Check(v1 == MicSecurity.PacketReject.None, "中继：外层(服务端)+内层(手机虚拟IP)都登记时被接受");
                    Check(v2 == MicSecurity.PacketReject.WrongSource, "内层虚拟 IP 不在白名单 → 来源不符被拒");
                    Check(v3 == MicSecurity.PacketReject.WrongSource, "外层来源不在白名单 → 来源不符被拒");
                    Check(s.AllowedSourcesText.Contains("10.26.0.77"), "允许来源可读（状态页/排错用）", s.AllowedSourcesText);
                    MicServer.Registry.Remove(s.Sid);
                }
                finally { VntManager.DataPassword = pwWas; }
            }

            // ---------------- 话筒闪避（ducking）状态机 ----------------
            MicDuck.Enabled = true;
            MicDuck.DuckPercent = 20;
            MicDuck.RestoreAfterSilenceMs = 800;
            float lastApplied = -1f;
            MicDuck.ApplyLevel = lv => lastApplied = lv;
            MicDuck.ForceRestore();
            var tD = new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);
            Check(Math.Abs(MicDuck.DuckLevel - 0.2f) < 0.001f, "闪避目标音量 = 20%", MicDuck.DuckLevel.ToString("0.00"));
            Check(MicDuck.NoteAudio(tD), "收到话筒音频 → 首次触发压低");
            Check(MicDuck.IsDucked && Math.Abs(lastApplied - 0.2f) < 0.001f, "压低动作已施加（音乐降到 20%）", lastApplied.ToString("0.00"));
            Check(!MicDuck.NoteAudio(tD.AddMilliseconds(100)), "持续说话不会反复触发（状态稳定）");
            Check(!MicDuck.Sweep(tD.AddMilliseconds(500)), "静默不足 800ms 时不恢复");
            Check(MicDuck.Sweep(tD.AddMilliseconds(1000)), "静默超过 800ms → 恢复音量");
            Check(!MicDuck.IsDucked && Math.Abs(lastApplied - 1.0f) < 0.001f, "恢复动作已施加（音乐回到原音量）", lastApplied.ToString("0.00"));
            MicDuck.NoteAudio(tD.AddSeconds(5));
            Check(MicDuck.ForceRestore() && !MicDuck.IsDucked, "会话结束时强制恢复（不会把音乐一直压着）");
            MicDuck.DuckPercent = 150;
            Check(Math.Abs(MicDuck.DuckLevel - 1.0f) < 0.001f, "闪避百分比越界被夹紧（不会超过 100%）");
            MicDuck.DuckPercent = 20;
            MicDuck.Enabled = false;
            Check(!MicDuck.NoteAudio(tD.AddSeconds(10)) && !MicDuck.IsDucked, "关闭闪避后不再压低音乐");
            Check(MicDuck.StatusText().Contains("关闭"), "闪避状态文案可显示（菜单用）", MicDuck.StatusText());
            MicDuck.Enabled = true;
            MicDuck.ApplyLevel = null;
            Check(!AudioFade.SetProcessVolume(null, 0.2f), "给空进程设置音量不抛异常（返回 false）");

            // ---------------- vnt 进程管理（缺文件/未配置时优雅报错，不能崩） ----------------
            string vntErr;
            Check(!VntProcess.IsRunning, "vnt-lite 默认不在运行（内置实现）");
            VntManager.Server = ""; VntManager.Token = "";
            Check(!VntProcess.Start(out vntErr) && vntErr.Contains("vnt 设置"),
                "未配置服务器/token 时拒绝启动并提示去菜单填写", vntErr);
            VntManager.Server = "1.2.3.4:29871"; VntManager.Token = "e2e-token";
            if (string.IsNullOrWhiteSpace(VntProcess.ResolveCliPath()))
            {
                Check(VntProcess.Start(out vntErr) && VntProcess.IsRunning,
                    "vnt-lite 可正常启动（内置实现，异步连接不阻塞界面）", vntErr);
                VntProcess.Stop();
                Check(!VntProcess.IsRunning, "vnt-lite 可正常停止（不再有残留子进程）");
                // W1 发送侧：统一发送入口 SendToPeer 的边界行为（不抛异常、不静默丢包）
                bool txOk2 = !VntProcess.SendToPeer("", new byte[] { 1 }, out string txErr2) && txErr2 != null;
                bool txOk3 = !VntProcess.SendToPeer("10.26.0.9", Array.Empty<byte>(), out string txErr3) && txErr3 != null;
                Check(txOk2 && txOk3, "对端地址为空 / 包为空时返回明确错误", txErr2 + " / " + txErr3);

                Check(!VntProcess.SendToPeer("10.26.0.9", new byte[] { 1, 2, 3 }, out string txErr1)
                      && txErr1 != null && txErr1.Contains("未启动"),

                    "未启动时统一发送入口返回明确错误（提示先启动）", txErr1);
                Check(!VntProcess.SendToPeer("不是IP", new byte[] { 1 }, out string txErr4) && txErr4 != null,
                    "对端虚拟 IP 非法时返回明确错误", txErr4);
            // W4/W5：用户可读状态文案（未配置 / 已配置未启动 / 运行中）——把"用户看到什么"纳入回归保护
            VntManager.Server = ""; VntManager.Token = "";
            Check(VntProcess.StateText().Contains("还没配置组网"),
                "未配置时状态文案给出明确引导（去填服务器与组网令牌）", VntProcess.StateText());
            VntManager.Server = "1.2.3.4:29871"; VntManager.Token = "e2e-token";
            Check(!VntProcess.IsRunning && VntProcess.StateText().Contains("未启动"),
                "已配置但未启动时状态文案提示去点「启动 vnt」", VntProcess.StateText());
            Check(VntProcess.StateText().Contains("不装 vnt") || VntProcess.StateText().Contains("不需要管理员"),
                "状态文案说明这是内置实现（不装 vnt / 不需要管理员），减少用户困惑");
            VntManager.Server = ""; VntManager.Token = "";
                Check(!VntProcess.TryQueryStatus(out _, out _, out string qErr) && qErr != null,
                    "未启动时查询状态优雅返回错误", qErr);
            }
            Check(VntProcess.StatusSummary().Contains("未运行"), "状态摘要在未运行时也能生成（菜单用）");

            // W1：经 vnt-lite 收到的对端包必须带着【内层虚拟 IP】送达业务侧
            // （中继时 UDP 源地址是服务端，若拿它当会话标识会导致话筒会话错乱/静默无声）
            {
                var vcRx = new BroadcastSystemNet.VntLite.VntLiteClient();
                byte[] gotData = null; string gotIp = null;
                vcRx.OnPeerDataFrom = (d, ip) => { gotData = d; gotIp = ip; };
                var vntRxPkt = new BroadcastSystemNet.VntLite.VntPacket
                {
                    Protocol = BroadcastSystemNet.VntLite.VntOuterProtocol.OtherTurn,
                    Transport = BroadcastSystemNet.VntLite.VntLiteClient.TransportMicData,
                    SourceIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.9"),
                    DestIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.8"),
                    Payload = new byte[] { 1, 2, 3, 4 }
                };
                vcRx.DispatchPacket(vntRxPkt.Build(), "203.0.113.7", 9999);   // UDP 源故意给成服务端地址
                Check(gotData != null && gotData.Length == 4 && gotIp == "10.26.0.9",
                    "vnt-lite 收到的对端包用【内层虚拟 IP】作为源（不是 UDP 源），业务侧据此识别设备", gotIp);
                Check(vcRx.PeerPacketsReceived == 1, "该包已计入对端包计数（供界面展示与排错）");
            // W1 验收（组成式，等价于探针的中继 AEAD 场景）：中继路径数据 = IpTurn/Ipv4 + 内层 IP 帧 + MicProtocol AEAD 包
            {
                var rxRelay = new BroadcastSystemNet.VntLite.VntLiteClient();
                byte[] delivered = null; string deliveredFrom = null;
                rxRelay.OnPeerDataFrom = (d, ip) => { delivered = d; deliveredFrom = ip; };
                byte[] relayKey = System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
                var micRelay = BroadcastSystemNet.MicProtocol.BuildPacket(
                    BroadcastSystemNet.MicProtocol.TypeOgg, 1, 7, 20,
                    System.Text.Encoding.UTF8.GetBytes("relay-aead"), relayKey);
                var relayFrame = BroadcastSystemNet.VntLite.VntIpFrame.Build(
                    BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.9"),
                    BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.8"), 40000, 40001, micRelay);
                var relayPkt = new BroadcastSystemNet.VntLite.VntPacket
                {
                    Protocol = BroadcastSystemNet.VntLite.VntOuterProtocol.IpTurn,
                    Transport = BroadcastSystemNet.VntLite.VntIpTurnProtocol.Ipv4,
                    SourceIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.9"),
                    DestIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.26.0.8"),
                    Payload = relayFrame
                };
                rxRelay.DispatchPacket(relayPkt.Build(), "203.0.113.7", 9999);   // UDP 源=服务端地址
                Check(delivered != null && deliveredFrom == "10.26.0.9",
                    "中继路径：IpTurn/Ipv4 的内层载荷被投递给业务层，且源用内层虚拟 IP", deliveredFrom);
                byte[] relayPl2 = null; string relayErr2 = "";
                bool relayAeadOk = delivered != null
                    && BroadcastSystemNet.MicProtocol.TryOpenPacket(delivered, relayKey, out _, out _, out _, out _, out relayPl2, out relayErr2)
                    && relayPl2 != null && System.Text.Encoding.UTF8.GetString(relayPl2) == "relay-aead";
                Check(relayAeadOk,
                    "W1 验收：中继路径投递的载荷就是 MicProtocol AEAD 包且校验通过（中继上的 AEAD 双向可行）", relayErr2);
            }
            }
            Check(VntProcess.StatusSummary().Contains("token") && !VntProcess.StatusSummary().Contains("e2e-token"),
                "状态摘要里的 token 已脱敏（不泄露明文）");
            VntManager.Server = ""; VntManager.Token = "";

            // ---------------- 话筒音频底座（缺 opus.dll 不崩 + WAV 字节级正确） ----------------
            Check(MicAudio.Status().Length > 0, "音频底座状态文案可生成（菜单提示用）", MicAudio.Status());
            if (string.IsNullOrWhiteSpace(MicAudio.ResolveOpusDll()))
            {
                Check(!MicAudio.TryLoadOpus() && MicAudio.LastError.Contains("opus.dll"),
                    "缺少 opus.dll 时给出明确指引（不抛异常）", MicAudio.LastError);
                Check(MicAudio.DecodeFrame(new byte[] { 1, 2, 3 }, new short[960]) == -1,
                    "解码器不可用时解码返回 -1（上层据此退化处理）");
            }
            string wavPath = System.IO.Path.Combine(Program.AppDataDir, "logs", "mic-selftest.wav");
            var pcm = new short[480];                            // 10ms @48kHz 单声道
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i / 20.0) * 8000);
            Check(MicAudio.WriteWav(wavPath, pcm), "PCM 落盘为 WAV 成功", MicAudio.LastError);
            Check(MicAudio.TryReadWavInfo(wavPath, out int wavRate, out int wavCh, out int wavData, out _)
                  && wavRate == MicAudio.SampleRate && wavCh == MicAudio.Channels && wavData == pcm.Length * 2,
                "WAV 头正确（48kHz/单声道/数据长度=样本数×2）", $"{wavRate}Hz {wavCh}ch {wavData}字节");
            Check(new System.IO.FileInfo(wavPath).Length == 44 + pcm.Length * 2, "WAV 文件大小 = 44 + 数据字节", new System.IO.FileInfo(wavPath).Length.ToString());
            try { System.IO.File.Delete(wavPath); } catch { }

            // ---------------- 断线恢复：二维码带 http_port + RESUME 令牌 ----------------
            string qr2 = VntManager.BuildQrPayload("1.2.3.4:29871", "tok", "phone", "udp", "", "10.26.0.1", 29880, 29881, "主机", httpPort: 5000);
            var qr2Obj = System.Text.Json.Nodes.JsonNode.Parse(qr2);
            Check(qr2Obj?["mic"]?["http_port"]?.GetValue<int>() == 5000,
                "二维码带网页/接口端口（手机端据此继承 index.html 功能，不写死 5000）");
            var qrNoHttp = System.Text.Json.Nodes.JsonNode.Parse(
                VntManager.BuildQrPayload("s", "t", "", "udp", "", "10.26.0.1", 1, 2, "h"));
            Check(qrNoHttp?["mic"]?["http_port"] == null, "没传网页端口时不产生空字段");

            var resumeSess = reg.Create("10.26.0.5", "mic-phone-05", new byte[32], tBase, 60);
            var rKey = new byte[32]; for (int i = 0; i < 32; i++) rKey[i] = (byte)(255 - i);
            var rs = reg.Create("10.26.0.6", "mic-phone-06", rKey, tBase, 60);
            string token = MicSecurity.ResumeToken(rs.Key, rs.Sid);
            Check(token.Length > 0 && MicSecurity.VerifyResumeToken(rs.Key, rs.Sid, token), "RESUME 令牌可生成并校验通过");
            Check(!MicSecurity.VerifyResumeToken(rs.Key, rs.Sid, token + "x"), "篡改的 RESUME 令牌被拒绝");
            Check(!MicSecurity.VerifyResumeToken(new byte[32], rs.Sid, token), "用错误密钥算出的令牌被拒绝（别人冒充不了）");
            Check(!MicSecurity.VerifyResumeToken(rs.Key, rs.Sid + 1, token), "换会话号后令牌失效（不能拿别人的会话号恢复）");
            rs.Suspend(tBase.AddSeconds(10));
            Check(rs.Suspended, "静默/断连后会话进入挂起（不立即销毁）");
            Check(rs.TryResume(tBase.AddSeconds(30), graceSeconds: 60), "宽限期内可免动态码恢复");
            Check(!rs.Suspended && rs.StartedAt == tBase, "恢复后沿用原会话与原起始时间（不重置单次时长上限）");
            rs.Suspend(tBase.AddSeconds(100));
            Check(!rs.TryResume(tBase.AddSeconds(200), graceSeconds: 60), "超过宽限期则恢复失败（必须重新输动态码）");

            // 服务端：静默先挂起、宽限期满才销毁（注意用的是服务端自己的注册表）
            MicServer.Registry.Clear();
            var sw1 = MicServer.Registry.Create("10.26.0.77", "sweep-phone", new byte[32], tBase, 60);
            var swSuspend = MicServer.SweepSilentSessions(tBase.AddSeconds(30));     // 30 秒无包 → 挂起
            Check(sw1.Suspended && MicServer.Registry.Find(sw1.Sid) != null && swSuspend.suspended == 1,
                "服务端：静默超时先把会话挂起（不立即销毁）", $"挂起 {swSuspend.suspended} / 销毁 {swSuspend.ended}");
            var swExpire = MicServer.SweepSilentSessions(tBase.AddSeconds(30 + 61)); // 宽限期(60s)已过 → 销毁
            Check(MicServer.Registry.Find(sw1.Sid) == null && swExpire.ended == 1,
                "服务端：宽限期满才真正销毁会话", $"挂起 {swExpire.suspended} / 销毁 {swExpire.ended}");
            MicServer.Registry.Clear();
            Check(MicPairing.TryParseResume(MicPairing.BuildResumeOk(7), out _, out _, out _) == false, "普通应答不会被误判成 RESUME 报文");
            var resumeLine = new System.Text.Json.Nodes.JsonObject
            {
                ["t"] = MicPairing.Type, ["v"] = 1, ["resume"] = true, ["sid"] = 4242, ["token"] = "abc"
            }.ToJsonString();
            Check(MicPairing.TryParseResume(resumeLine, out uint prSid, out string prTok, out _) && prSid == 4242 && prTok == "abc",
                "RESUME 报文可解析（sid + 令牌）");

            // vnt 链路看护：本机没有 10.26.x 虚拟网地址时应返回空且不抛异常；没开麦时不做任何动作
            string localVnt = MicServer.DetectLocalVntIp();
            Check(localVnt == "" || VntManager.LooksLikeVntAddress(localVnt),
                "可检测本机 vnt 虚拟网地址（没有则返回空，不抛异常）", string.IsNullOrEmpty(localVnt) ? "(本机无虚拟网)" : localVnt);
            bool micWasEnabled = VntManager.MicEnabled;
            VntManager.MicEnabled = false;
            bool runningBefore = MicServer.Running;
            MicServer.WatchVntLink();
            Check(MicServer.Running == runningBefore, "未开麦时链路看护不会动话筒服务（不会偷偷开麦）");
            VntManager.MicEnabled = micWasEnabled;

            // ---------------- 播放前 PCM 处理（环形缓冲/电平/限幅） ----------------
            var ring = new MicPcm.Ring(8);
            ring.Write(new short[] { 1, 2, 3, 4, 5 });
            Check(ring.Count == 5, "环形缓冲写入计数正确", ring.Count.ToString());
            var rd = new short[4];
            Check(ring.Read(rd) == 4 && rd[0] == 1 && rd[3] == 4, "环形缓冲按先进先出读出", string.Join(",", rd));
            Check(ring.Read(rd) == 1 && rd[0] == 5, "余量按实际数量读出（不足不补零）");
            Check(ring.Capacity >= 64, "环形缓冲有最小容量保护（不会小到立刻溢出）", ring.Capacity.ToString());
            var big = new short[ring.Capacity + 6];
            for (int i = 0; i < big.Length; i++) big[i] = (short)(100 + i);
            ring.Write(big);                                                        // 超出容量 → 丢最旧
            Check(ring.Count == ring.Capacity, "环形缓冲写满后不会无界增长（容量上限）", ring.Count.ToString());
            var rdAll = new short[ring.Capacity];
            ring.Read(rdAll);
            Check(rdAll[0] == 106, "写满后丢的是最旧数据（保留最后 " + ring.Capacity + " 个）", rdAll[0].ToString());
            ring.Clear();
            Check(ring.Count == 0, "清空缓冲（挂起/结束会话时用）");

            var loud = new short[] { 30000, -30000, 25000 };
            double before = MicPcm.PeakDb(loud);
            double g = MicPcm.ApplyPeakLimit(loud, -3.0);
            Check(before > -3.0 && g < 1.0 && MicPcm.PeakDb(loud) <= -3.0 + 0.2,
                "峰值限幅：过载块被压到 -3dBFS 以下（防啸叫/过载）", $"{before:F1}dB → {MicPcm.PeakDb(loud):F1}dB");
            var quiet = new short[] { 1000, -800, 600 };
            double q0 = MicPcm.PeakDb(quiet);
            Check(Math.Abs(MicPcm.ApplyPeakLimit(quiet, -3.0) - 1.0) < 0.0001 && Math.Abs(MicPcm.PeakDb(quiet) - q0) < 0.01,
                "峰值限幅：不动的块保持原样（不把小声提上来）");
            var pcmG = new short[] { 20000, -20000 };
            MicPcm.ApplyGain(pcmG, 10f);
            Check(pcmG[0] == short.MaxValue && pcmG[1] == short.MinValue, "增益过大时饱和裁切（不会溢出）");
            Check(MicPcm.PeakDb(Array.Empty<short>()) <= -120, "空音频的电平是 -120dB（不会算出 NaN）");

            // ---------------- 话筒播放：只验证不碰声卡的部分 ----------------
            // 注意：WASAPI 真机起流曾在自检里触发过堆损坏（0xC0000374），已从自检移出；
            // 真机出声改由程序运行时验证，避免一个 COM 细节把整份自检带崩。
            Check(MicRender.StatusText().Length > 0, "播放状态文案可生成（菜单用）", MicRender.StatusText());
            var ringSrc = new short[480];
            for (int i = 0; i < ringSrc.Length; i++) ringSrc[i] = (short)(i * 3);
            MicRender.Ring.Write(ringSrc);
            var ringDst = new short[ringSrc.Length];
            Check(MicRender.Feed(ringDst, ringSrc.Length) == ringSrc.Length && ringDst[100] == ringSrc[100],
                "播放队列从话筒环形缓冲取数（收包线程与播放线程互通）", ringDst[100].ToString());
            Check(!MicRender.Running, "未开麦时播放线程未启动");
            MicRender.Ring.Clear();

            // Ogg 直通（安卓端发 Ogg-Opus 页 → 原样喂 ffplay，PC 端零解码）
            Check(MicProtocol.TypeOgg == 3 && MicProtocol.TypeOgg != MicProtocol.TypeAudio, "Ogg 包类型号独立（不会与裸 Opus 帧混淆）");
            Check(!MicPipePlayer.WriteOgg(null) && !MicPipePlayer.WriteOgg(Array.Empty<byte>()),
                "空的 Ogg 载荷被拒绝（不抛异常、不启动播放）");
            Check(MicPipePlayer.StatusText().Length > 0, "Ogg 播放状态文案可生成", MicPipePlayer.StatusText());

            // ---------------- vnt-lite：手写 protobuf 编解码 + 12 字节头（里程碑 1/2 契约） ----------------
            var hsReq = new BroadcastSystemNet.VntLite.HandshakeRequest
            { Version = "1.2.16", Secret = true, KeyFinger = "finger123456" };
            var hsReq2 = BroadcastSystemNet.VntLite.HandshakeRequest.Parse(hsReq.ToBytes());
            Check(hsReq2.Version == "1.2.16" && hsReq2.Secret && hsReq2.KeyFinger == "finger123456",
                "protobuf 往返：HandshakeRequest（version/secret/key_finger）", hsReq.ToBytes().Length + " 字节");
            var hsResp = BroadcastSystemNet.VntLite.HandshakeResponse.Parse(
                new BroadcastSystemNet.VntLite.HandshakeResponse
                { Version = "1.2.16", Secret = true, PublicKey = new byte[] { 1, 2, 3, 4 }, KeyFinger = "abc" }.ToBytes());
            Check(hsResp.PublicKey.Length == 4 && hsResp.KeyFinger == "abc" && hsResp.Secret,
                "protobuf 往返：HandshakeResponse（含 bytes 类型的 public_key）");
            var regReq = new BroadcastSystemNet.VntLite.RegistrationRequest
            {
                Token = "tok-1", DeviceId = "dev-1", Name = "教师机", IsFast = true, Version = "1.2.16",
                VirtualIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.9"), AllowIpChange = true,
                ClientSecret = true, ClientSecretHash = new byte[] { 9, 9, 9 }
            };
            var regReq2 = BroadcastSystemNet.VntLite.RegistrationRequest.Parse(regReq.ToBytes());
            Check(regReq2.Token == "tok-1" && regReq2.DeviceId == "dev-1" && regReq2.Name == "教师机" && regReq2.IsFast
                  && regReq2.ClientSecret && regReq2.ClientSecretHash.Length == 3
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(regReq2.VirtualIp) == "10.99.0.9",
                "protobuf 往返：RegistrationRequest（token/device_id/name/is_fast/fixed32 IP/client_secret）");
            var regResp = new BroadcastSystemNet.VntLite.RegistrationResponse
            {
                VirtualIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.5"),
                VirtualGateway = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.1"),
                VirtualNetmask = BroadcastSystemNet.VntLite.VntPacket.IpToUint("255.255.255.0"),
                Epoch = 7, PublicIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("1.2.3.4"), PublicPort = 29871
            };
            regResp.Devices.Add(new BroadcastSystemNet.VntLite.DeviceInfo
            { Name = "手机", VirtualIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.6"), DeviceStatus = 1 });
            var regResp2 = BroadcastSystemNet.VntLite.RegistrationResponse.Parse(regResp.ToBytes());
            Check(BroadcastSystemNet.VntLite.VntPacket.UintToIp(regResp2.VirtualIp) == "10.99.0.5"
                  && regResp2.Epoch == 7 && regResp2.Devices.Count == 1
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(regResp2.Devices[0].VirtualIp) == "10.99.0.6",
                "protobuf 往返：RegistrationResponse（虚拟IP/网关/掩码/epoch/repeated DeviceInfo）",
                BroadcastSystemNet.VntLite.VntPacket.UintToIp(regResp2.VirtualIp));
            Check(BroadcastSystemNet.VntLite.VntPacket.UintToIp(BroadcastSystemNet.VntLite.VntPacket.GatewayIp) == "0.0.0.1"
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(BroadcastSystemNet.VntLite.VntPacket.SelfIp) == "0.0.0.2",
                "哨兵地址正确：GATEWAY_IP=0.0.0.1、SELF_IP=0.0.0.2");
            var vlPkt = BroadcastSystemNet.VntLite.VntPacket.NewService(
                BroadcastSystemNet.VntLite.VntServiceProtocol.HandshakeRequest, hsReq.ToBytes());
            var vlBytes = vlPkt.Build();
            Check(vlBytes.Length == 12 + hsReq.ToBytes().Length && vlBytes[0] == 0x42 && vlBytes[1] == 1 && vlBytes[2] == 5
                  && vlBytes[3] == 0xFF,
                "12 字节头：版本2+服务包标志=0x42、外层=1(Service)、上层=5(握手请求)、TTL=0xFF",
                vlBytes[0].ToString("X2") + " " + vlBytes[1] + " " + vlBytes[2]);
            Check(BroadcastSystemNet.VntLite.VntPacket.TryParse(vlBytes, out var vlBack, out _)
                  && vlBack.Transport == 5 && vlBack.Version == 2 && vlBack.GatewayFlag
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(vlBack.DestIp) == "0.0.0.1",
                "12 字节头可解析回来（协议号/版本/服务包标志/目的地址）");
            Check(!BroadcastSystemNet.VntLite.VntPacket.TryParse(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _, out string verr)
                  && verr.Contains("STUN"),
                "版本 < 2 的包被识别为 STUN 响应而不是 vnt 报文（与上游一致）", verr);

            // vnt-lite：控制层心跳（Control/Ping→Pong）与错误码（外层协议号按上游源码）
            var vlc = new BroadcastSystemNet.VntLite.VntLiteClient();
            var ping = vlc.NewControlBytes(BroadcastSystemNet.VntLite.VntControlProtocol.Ping);
            Check(ping[1] == 3 && ping[2] == 1 && ping.Length == 12,
                "Control/Ping 报文头正确（外层=3、上层=1、无载荷）", ping[1] + "/" + ping[2]);
            Check(BroadcastSystemNet.VntLite.VntControlProtocol.PunchRequest == 3
                  && BroadcastSystemNet.VntLite.VntOtherTurnProtocol.Punch == 1
                  && BroadcastSystemNet.VntLite.VntErrorProtocol.TokenError == 1,
                "打洞与错误报文号与上游一致（PunchRequest=3、OtherTurn.Punch=1、TokenError=1）");
            vlc.HandleControl(BroadcastSystemNet.VntLite.VntControlProtocol.Ping, Array.Empty<byte>());
            Check(vlc.PongsSent == 1 && vlc.LastControl == 1, "收到 Ping 会回 Pong（否则服务端会判我们掉线）", "PongsSent=" + vlc.PongsSent);
            vlc.HandleError(BroadcastSystemNet.VntLite.VntErrorProtocol.TokenError, Array.Empty<byte>());
            Check(vlc.State == BroadcastSystemNet.VntLite.VntLiteState.Failed && vlc.LastError.Contains("令牌"),
                "令牌错误码会给出人话提示并停止重试", vlc.LastError);
            Check(BroadcastSystemNet.VntLite.VntErrorProtocol.Text(4).Contains("冲突")
                  && BroadcastSystemNet.VntLite.VntErrorProtocol.Text(6).Contains("密钥"),
                "错误码 4/6 有明确解释（IP 冲突 / 缺密钥）");

            // 打洞：PunchInfo 编解码 + PunchRequest 头（目的地址=目标虚拟IP）
            var pi0 = new BroadcastSystemNet.VntLite.PunchInfo
            {
                PublicPort = 41325,
                PublicPortRange = 3,
                NatType = 1,
                Reply = true,
                LocalIp = BroadcastSystemNet.VntLite.VntPacket.IpToUint("192.168.1.20"),
                LocalPort = 51234,
                PunchModel = 1,
                PublicTcpPort = 41326
            };
            pi0.PublicIpList.Add(BroadcastSystemNet.VntLite.VntPacket.IpToUint("203.0.113.10"));
            var pi1 = BroadcastSystemNet.VntLite.PunchInfo.Parse(pi0.ToBytes());
            Check(pi1.PublicPort == 41325 && pi1.PublicPortRange == 3 && pi1.Reply && pi1.NatType == 1
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(pi1.LocalIp) == "192.168.1.20"
                  && pi1.PublicIpList.Count == 1
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(pi1.PublicIpList[0]) == "203.0.113.10",
                "protobuf 往返：PunchInfo（公网端口/范围/NAT类型/reply/本地端点/公网IP列表）", pi1.Describe());
            var punchReq = vlc.BuildPunchRequestBytes(
                BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.3"), 51234);
            bool prOk = BroadcastSystemNet.VntLite.VntPacket.TryParse(punchReq, out var prq, out _);
            Check(punchReq[1] == 3 && punchReq[2] == 3 && prOk
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(prq.DestIp) == "10.99.0.3"
                  && BroadcastSystemNet.VntLite.PunchInfo.Parse(prq.Payload).LocalPort == 51234,
                "打洞请求：外层=Control(3)、上层=PunchRequest(3)、目的=目标虚拟IP、载荷含本机端点");
            vlc.HandleControl(BroadcastSystemNet.VntLite.VntControlProtocol.PunchRequest, pi0.ToBytes());
            Check(vlc.P2PInfo != null && vlc.P2PInfo.PublicPort == 41325,
                "收到对端打洞请求会解析出对端端点（据此互发打洞包）", vlc.P2PInfo?.Describe());

            // IPv4+UDP 封装（自造帧，用于"发给服务端再转"的路径；P2P 直连不需要它但保留可复用）
            var vlFrame = BroadcastSystemNet.VntLite.VntIpFrame.Build(
                BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.2"),
                BroadcastSystemNet.VntLite.VntPacket.IpToUint("10.99.0.3"),
                40000, 40001, System.Text.Encoding.UTF8.GetBytes("hello"));
            Check(vlFrame.Length == 28 + 5 && vlFrame[0] == 0x45 && vlFrame[9] == 17
                  && BroadcastSystemNet.VntLite.VntIpFrame.Checksum(vlFrame, 0, 20) == 0,
                "IPv4+UDP 封装：头部正确且 IP 首部校验和为 0（校验通过）", vlFrame.Length + " 字节");
            bool vlFrOk = BroadcastSystemNet.VntLite.VntIpFrame.TryParse(vlFrame,
                out uint vlSrc, out uint vlDst, out ushort vlSp, out ushort vlDp, out byte[] vlPl, out _);
            Check(vlFrOk && BroadcastSystemNet.VntLite.VntPacket.UintToIp(vlSrc) == "10.99.0.2"
                  && BroadcastSystemNet.VntLite.VntPacket.UintToIp(vlDst) == "10.99.0.3"
                  && vlSp == 40000 && vlDp == 40001 && System.Text.Encoding.UTF8.GetString(vlPl) == "hello",
                "IPv4+UDP 封装可解析回原载荷（源/目的 IP、端口、数据）");
            Check(!BroadcastSystemNet.VntLite.VntIpFrame.TryParse(new byte[] { 0x60, 0, 0, 0 }, out _, out _, out _, out _, out _, out string vlFerr)
                  && !string.IsNullOrEmpty(vlFerr),
                "非 IPv4（版本 6）的帧被拒绝并给出原因", vlFerr);
            var vlNoSock = new BroadcastSystemNet.VntLite.VntLiteClient();
            vlNoSock.SendRawToPeer(new byte[] { 1, 2, 3 }, "127.0.0.1", 12345);
            Check(vlNoSock.P2PRawSent == 0, "未启动时 P2P 直发不计数也不抛异常（优雅失败）");


            // vnt 客户端数据加密（N1）：密钥/指纹/nonce/布局/AAD 全部按上游源码实现
            var vKey = BroadcastSystemNet.VntLite.VntCipher.DeriveKey("0123456789abcdef0123456789abcdef", out bool v128);
            Check(!v128 && vKey.Length == 32, "数据密钥 = SHA256(密码)，长密码走 AES-256（32 字节）");
            var vKeyShort = BroadcastSystemNet.VntLite.VntCipher.DeriveKey("short", out bool v128b);
            Check(v128b && vKeyShort.Length == 16, "密码短于 8 字符时走 AES-128（SHA256 前 16 字节）");
            var vFh = BroadcastSystemNet.VntLite.VntCipher.FingerHash("tok-1");
            var vNP = BroadcastSystemNet.VntLite.VntPacket.NewService(
                BroadcastSystemNet.VntLite.VntServiceProtocol.HandshakeRequest, Array.Empty<byte>());
            var vNonce = BroadcastSystemNet.VntLite.VntCipher.BuildNonce(vNP);
            Check(vNonce.Length == 12 && vNonce[8] == 1 && vNonce[9] == 5,
                "AEAD nonce = 包头字段（源/目的IP + 外层协议 + 上层协议 + 标志 + 源TTL）", BitConverter.ToString(vNonce));
            var vPlain = System.Text.Encoding.UTF8.GetBytes("hello-vnt");
            var vEnc = BroadcastSystemNet.VntLite.VntCipher.Encrypt(vPlain, vKey, vNonce, vFh, true);
            Check(vEnc.Length == vPlain.Length + 32, "密文体长度 = 明文 + 32（tag16 + random4 + finger12）", vEnc.Length.ToString());
            Check(BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(vEnc, vKey, vNonce, vFh, true, out var vDec, out _)
                  && System.Text.Encoding.UTF8.GetString(vDec) == "hello-vnt", "加密→解密往返正确（AAD 为空）");
            var vBadCt = (byte[])vEnc.Clone(); vBadCt[0] ^= 0x5A;
            Check(!BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(vBadCt, vKey, vNonce, vFh, true, out _, out string vErr1)
                  && vErr1.Contains("finger"),
                "篡改密文被 finger 校验拦住（服务端不解密也能挡）", vErr1);
            var vBadFg = (byte[])vEnc.Clone(); vBadFg[vBadFg.Length - 1] ^= 1;
            Check(!BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(vBadFg, vKey, vNonce, vFh, true, out _, out _),
                "伪造 finger 同样被拒绝");
            Check(!BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(new byte[8], vKey, vNonce, vFh, true, out _, out string vErr2)
                  && vErr2.Contains("长度"), "过短的加密体被拒绝并说明原因", vErr2);

            // Ogg 会话校验：缺 OpusHead / 页头异常要能被识别（否则手机端实现有误会"开了麦没声音"）
            var headPage = new byte[19 + 40];
            System.Text.Encoding.ASCII.GetBytes("OpusHead").CopyTo(headPage, 33);
            Check(MicServer.LooksLikeOpusHead(headPage), "能识别 OpusHead 页（Ogg 流开始标志）");
            Check(!MicServer.LooksLikeOpusHead(new byte[] { 1, 2, 3 }), "普通帧不会被误判成 OpusHead");
            var okPage = new byte[40];
            okPage[0] = (byte)'O'; okPage[1] = (byte)'g'; okPage[2] = (byte)'g'; okPage[3] = (byte)'S';
            okPage[26] = 1;
            Check(MicServer.LooksLikeOggPage(okPage), "能识别合法 Ogg 页（OggS + 版本0 + 段数非0）");
            var badPage = (byte[])okPage.Clone(); badPage[0] = (byte)'X';
            Check(!MicServer.LooksLikeOggPage(badPage), "非 Ogg 页的载荷被识别出来（会丢弃并告警一次）");
            Check(!MicServer.LooksLikeOggPage(new byte[10]), "过短的载荷不会越界读取（返回 false）");

            // 设备ID 自动 UUID + 随机令牌
            string dev1 = VntManager.DeviceId;
            Check(dev1.Length >= 32 && dev1.Contains("-"), "设备ID 自动生成 UUID 并写入配置", dev1);
            Check(VntManager.DeviceId == dev1, "设备ID 再次读取保持一致（固定虚拟IP）", dev1);
            string dev2 = VntManager.NewDeviceId();
            Check(dev2 != dev1 && VntManager.DeviceId == dev2, "可重新生成设备ID");
            string tok1 = VntManager.NewToken();
            Check(tok1.Length == 6 && tok1.All(char.IsLetterOrDigit) && VntManager.Token == tok1, "组网令牌可一键随机生成（a-z/A-Z/0-9 六位，好念好输入）", tok1);
            Check(VntManager.NewToken() != tok1, "每次随机令牌都不同");
            string _pw0 = VntManager.DataPassword; string _pw1 = VntManager.NewDataPassword();
            Check(_pw1.Length == 16 && _pw1.All(char.IsLetterOrDigit) && VntManager.DataPassword == _pw1, "数据密码可一键随机生成（a-z/A-Z/0-9 十六位）", _pw1.Length + " 位");
            VntManager.DataPassword = _pw0;
            VntManager.Token = ""; VntManager.DeviceId = "";

            // 组网连接状态（一眼看懂：绿=已连接 / 黄=没启动或没拿到IP / 红=没配置或掉线）
            var st1 = VntManager.ConnectionState(false, false, "", "");
            Check(st1.colorKey == "red" && st1.label.Contains("未配置"), "未配置时状态是红色并提示去填服务器/token", st1.label);
            var st2 = VntManager.ConnectionState(true, false, "", "");
            Check(st2.colorKey == "amber" && st2.label.Contains("未启动"), "已配置但没启动时是黄色", st2.label);
            var st3 = VntManager.ConnectionState(true, true, "Connected", "10.26.0.2");
            Check(st3.colorKey == "green" && st3.label.Contains("10.26.0.2"), "已连接且拿到虚拟IP时是绿色（显示IP）", st3.label);
            // 已入网后短暂 Connecting = 服务端静默触发的**重新握手**（数据面仍通）→ 保持绿色，不能报红
            // （历史用户反馈：每 40 秒红一次）。这一条与下面"从未入网就 Connecting"配对，避免把"红色"这条语义丢掉。
            var st4 = VntManager.ConnectionState(true, true, "Connecting", "10.26.0.2");
            Check(st4.colorKey == "green" && st4.label.Contains("重新握手"), "已入网后短暂 Connecting 不报红（只是重新握手，数据面仍通）", st4.label);
            var st4b = VntManager.ConnectionState(true, true, "Connecting", "");
            Check(st4b.colorKey == "red" && st4b.label.Contains("Connecting"), "从未入网过就处于 Connecting → 红色并带上原因", st4b.label);
            var st5 = VntManager.ConnectionState(true, true, "Connected", "");
            Check(st5.colorKey == "amber" && st5.label.Contains("虚拟 IP"), "连上了但没虚拟IP时是黄色（提示还没拿到IP）", st5.label);
            Check(!AudioFade.StopNow(null), "传空进程不抛异常（返回 false）");
            Check(AudioFade.StopNowAll(null) == 0, "传空集合不抛异常");

            // ⑤ 任务的"到达时间后"：默认 immediate（立即终止，与历史一致），非法值一律回落到 immediate
            Check(Program.StopModeOf(new System.Text.Json.Nodes.JsonObject { ["stop_mode"] = "finish_track" }) == "finish_track",
                "任务可设为「播完当前这首」");
            Check(Program.StopModeOf(new System.Text.Json.Nodes.JsonObject { ["stop_mode"] = "immediate" }) == "immediate",
                "任务可设为「立即终止」");
            Check(Program.StopModeOf(new System.Text.Json.Nodes.JsonObject()) == "immediate"
                  && Program.StopModeOf(null) == "immediate"
                  && Program.StopModeOf(new System.Text.Json.Nodes.JsonObject { ["stop_mode"] = "乱填的值" }) == "immediate",
                "缺省/空/非法值都回落到「立即终止」（行为与升级前一致）");

            // 迁移真的会把老任务补上 stop_mode（升级安装后老配置也能用）
            Program.WriteJsonl("tasks.jsonl", new JsonArray {
                new JsonObject { ["id"] = 901, ["start_time"] = "07:30:00", ["music_ids"] = new JsonArray { "1" } }
            });
            Program.MigrateConfigIfNeeded();
            string migrated = null;
            foreach (var n in Program.ReadJsonl("tasks.jsonl"))
                if (n is JsonObject o && (o["id"]?.GetValue<int>() ?? 0) == 901) migrated = o["stop_mode"]?.ToString();
            Check(migrated == "immediate", "老任务经迁移会补上 stop_mode=immediate", migrated ?? "（没补上）");
            Check(WeatherWarn.FallbackListUrl() == "", "没填地区名时兜底列表地址为空（程序不内置地名）");
            string filled = WeatherWarn.BuildDetailUrl("https://x/yj.php?id=1&key=2&number=" + WeatherWarn.NumberToken, "12345678901234_20260923142952");
            Check(filled.EndsWith("number=12345678901234_20260923142952"), "详情地址里的 {number} 被替换成预警编号", filled);
            string filled2 = WeatherWarn.BuildDetailUrl("https://x/yj.php?id=1&key=2", "ABC");
            Check(filled2.EndsWith("&number=ABC"), "没有占位符时自动补 number 参数", filled2);

            // 中央气象台列表：区划码精确筛本地 + 从标题解析类型/等级
            Check(WeatherWarn.AreaCodeOf("12345678901234_20260924105216") == "123456", "从预警编号取区划码");
            string nmcJson = "{\"msg\":\"success\",\"code\":0,\"data\":{\"page\":{\"pageNo\":1,\"pageSize\":100,\"count\":356,\"totalPage\":4,\"list\":["
                + "{\"alertid\":\"52052441600000_20260924105216\",\"issuetime\":\"2026/09/24 10:52\",\"title\":\"贵州省毕节市织金县气象台发布大风蓝色预警信号\"},"
                + "{\"alertid\":\"12345678901234_20260924143000\",\"issuetime\":\"2026/09/24 14:30\",\"title\":\"示例县气象台发布雷电黄色预警信号[III级/较重]\"}]}}}";
            var nmc = WeatherWarn.ParseNmcList(nmcJson, "123456", out int nmcPages, out int nmcCount);
            Check(nmc.Count == 1 && nmc[0].Id.StartsWith("123456"), "中央气象台列表按区划码筛出示例县那一条", nmc.Count + " 条");
            Check(nmc[0].SignalType == "雷电" && nmc[0].SignalLevel == "黄色", "从标题解析出 类型+等级", nmc[0].SignalType + nmc[0].SignalLevel);
            Check(nmc[0].EffectiveTime == new DateTime(2026, 9, 24, 14, 30, 0), "生效时间用 issuetime", nmc[0].EffectiveTime.ToString("yyyy-MM-dd HH:mm"));
            Check(nmcPages == 4 && nmcCount == 356, "识别出总页数/总条数（用于翻页）", nmcPages + "/" + nmcCount);
            Check(WeatherWarn.TryParseTitleSignal("海口市气象台发布雷电黄色预警信号", out string tt, out string tl) && tt == "雷电" && tl == "黄色",
                "标题解析（海口样例）", tt + tl);

            // 带分钟的区间写法："我市24日10时42分到19时可能发生雷电活动"
            Check(WeatherWarn.ParseExpect("受对流云团影响，我市24日10时42分到19时可能发生雷电活动。", new DateTime(2026, 9, 24, 10, 42, 0), out _, out _, out _)
                  == new DateTime(2026, 9, 24, 19, 0, 0), "带分钟的区间 24日10时42分到19时 → 19:00");

            // 乡镇精度：点名别的乡镇 → 不播；点名本地 → 播；没点名（全域）→ 播
            var cOther = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "x1", ["name"] = "示例", ["signaltype"] = "雷电", ["signallevel"] = "黄色",
                ["effective"] = "2026-09-24 14:30", ["expect"] = "2026-09-24 20:30",
                ["title"] = "示例县气象台发布雷电黄色预警信号",
                ["text"] = "示例县气象台发布雷电黄色预警信号：预计未来6小时，板桥乡、五城镇、月潭湖镇、海阳镇等乡镇将出现雷电活动，请注意防范。"
            };
            // 乡镇精度：地名关键词默认留空（= 不过滤），所以这里先在配置里填上再测
            WeatherWarn.SaveSetting("town", System.Text.Json.Nodes.JsonValue.Create("双河镇"));
            Check(WeatherWarn.GetTowns().Count == 1, "填了地名关键词后才做乡镇过滤", string.Join("/", WeatherWarn.GetTowns()));
            Check(!string.IsNullOrEmpty(WeatherWarn.TownScopeReason(cOther)), "点名的是别的乡镇 → 不作为本地预警", WeatherWarn.TownScopeReason(cOther));
            var cHit = cOther.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cHit["text"] = "示例县气象台发布雷电黄色预警信号：预计未来6小时，双河镇、青石镇等乡镇将出现雷电活动，请注意防范。";
            Check(WeatherWarn.TownScopeReason(cHit) == "", "点名了本地乡镇 → 作为本地预警");
            var cAll = cOther.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cAll["text"] = "示例县气象台发布雷电黄色预警信号：预计未来6小时，示例县大部将自东向西出现雷电，请注意防范。";
            Check(WeatherWarn.TownScopeReason(cAll) == "", "没点名具体乡镇（全域预警）→ 作为本地预警");

            // 解除/失效：不在当前生效列表里 → 不播
            var cResolved = cHit.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cResolved["resolved"] = true;
            cResolved["resolved_at"] = "2026-09-24 18:00:00";
            bool okResolved = WeatherWarn.Evaluate(cResolved, WeatherWarn.DefaultWindows(), new DateTime(2026, 9, 24, 15, 0, 0), out string rRes, out _);
            Check(!okResolved && rRes.Contains("已解除"), "已解除/失效的预警不播报", rRes);

            // 地名关键词支持多个（逗号/顿号/空格分隔）：命中邻近乡镇也算影响本地
            var cNb = cOther.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cNb["text"] = "示例县气象台发布雷电黄色预警信号：预计未来6小时，邻水镇、山河镇等乡镇将出现雷电活动，请注意防范。";
            Check(!string.IsNullOrEmpty(WeatherWarn.TownScopeReason(cNb)), "没填邻镇时，点名邻镇算不命中", WeatherWarn.TownScopeReason(cNb));

            // 填入邻近乡镇（多关键词）后：点名邻镇也算影响本地，未填的乡镇仍然跳过
            WeatherWarn.SaveSetting("town", System.Text.Json.Nodes.JsonValue.Create("双河镇, 邻水镇、山河镇"));
            Check(WeatherWarn.GetTowns().Count == 3, "地名筛选支持多关键词（逗号/顿号分隔）", string.Join("/", WeatherWarn.GetTowns()));
            Check(WeatherWarn.TownScopeReason(cNb) == "", "填入邻镇后，点名邻镇的预警也作为本地预警", WeatherWarn.TownScopeReason(cNb));
            Check(WeatherWarn.TownScopeReason(cOther) != "", "没填的乡镇（板桥乡等）仍然跳过", WeatherWarn.TownScopeReason(cOther));
            WeatherWarn.RemoveSetting("town");     // 清掉，后面要验证"默认留空"

            // 中央气象台详情页是 HTML：能抽出正文
            string html = "<html><head><title>中央气象台官方网站-预警信号</title><style>x{}</style></head><body><div class='nav'>北京</div>"
                        + "<div class='txt'>海口市气象台2026年09月24日10时42分发布雷电黄色预警信号：受对流云团影响，我市24日10时42分到19时可能发生雷电活动，请注意防范。</div>"
                        + "<div>防御指南：</div></body></html>";
            string body = WeatherWarn.ExtractHtmlWarningBody(html);
            Check(body != null && body.Contains("预警信号：") && body.Contains("防御指南") == false, "从详情页 HTML 抽出预警正文", body == null ? "(null)" : body.Substring(0, Math.Min(40, body.Length)));

            // 隐私：地区名/区划码/地名筛选一律**没有默认值**（只有两行接口地址有默认），避免把使用者的所在地写进程序
            WeatherWarn.RemoveSetting("place");
            WeatherWarn.RemoveSetting("area_code");
            WeatherWarn.RemoveSetting("town");
            Check(WeatherWarn.GetAreaCode() == "", "区划码默认留空（程序不带地区信息）", "[" + WeatherWarn.GetAreaCode() + "]");
            Check(WeatherWarn.GetPlace() == "", "地区名默认留空", "[" + WeatherWarn.GetPlace() + "]");
            Check(WeatherWarn.GetTown() == "" && WeatherWarn.GetTowns().Count == 0, "地名筛选默认留空（= 不做这层过滤）", "[" + WeatherWarn.GetTown() + "]");
            Check(WeatherWarn.FallbackListUrl() == "", "没填地区名时兜底源地址为空（不内置任何地名）");
            Check(!WeatherWarn.IsLegacyApihzListUrl(WeatherWarn.DefaultListUrl()), "默认天气接口不会被当成历史 apihz 地址");
            Check(WeatherWarn.IsLegacyApihzListUrl("https://cn.apihz.cn/api/tianqi/tqyb.php?id=1&key=2&place=x"), "历史 apihz 地址按接口路径识别（无需写任何地名）");

            // 填了地区后，兜底源才拼地址
            WeatherWarn.SaveSetting("place", System.Text.Json.Nodes.JsonValue.Create("示例"));
            Check(WeatherWarn.FallbackListUrl().Contains("place=" + Uri.EscapeDataString("示例")), "填了地区名后兜底源按它拼地址");
            Check(WeatherWarn.StationOf(new System.Text.Json.Nodes.JsonObject()) == "", "台名取不到就不念（不再用配置的地区名兜底）");
            WeatherWarn.RemoveSetting("place");

            // 预报节点判断：周一~周五 07:30-12:00、13:30-18:00
            var windows = WeatherWarn.DefaultWindows();
            Check(WeatherWarn.InWindows(new DateTime(2026, 9, 23, 15, 0, 0), windows, out string hit1) && hit1.Contains("13:30-18:00"),
                "周三 15:00 落在下午节点内", hit1);
            Check(!WeatherWarn.InWindows(new DateTime(2026, 9, 23, 20, 27, 0), windows, out _), "周三 20:27 不在任何节点内");
            Check(!WeatherWarn.InWindows(new DateTime(2026, 9, 26, 9, 0, 0), windows, out _), "周六 09:00 不在节点内（节点只设了周一~周五）");
            Check(WeatherWarn.DayNo(new DateTime(2026, 9, 27)) == 7, "周日按约定记作 7");
            Check(WeatherWarn.InWindows(new DateTime(2026, 9, 24, 9, 0, 0), windows, out _), "周四 09:00 在上午节点内");

            // 判断口径：**排程到点那一刻**是否落在节点内（节点 = 允许播报的时段），预警何时发布不参与判断
            bool okPlay = WeatherWarn.Evaluate(cache, windows, new DateTime(2026, 9, 23, 15, 0, 0), out string r1, out _);
            Check(okPlay && r1.Contains("当前时刻") && r1.Contains("落在预报节点"), "播报时刻 15:00 落在下午节点内 → 播报", r1);

            // 早上 06:00 发布、有效期到 12:00：只要到点播报的时刻在节点内就播（不再要求"发布时间在节点内"）
            var cacheEarly = cache.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cacheEarly["effective"] = "2026-09-23 06:00";
            cacheEarly["expect"] = "2026-09-23 12:00";
            bool okEarly930 = WeatherWarn.Evaluate(cacheEarly, windows, new DateTime(2026, 9, 23, 9, 30, 0), out string r930, out _);
            Check(okEarly930, "早上 06:00 发布的预警，09:30（节点内）到点 → 播报", r930);

            // 到点时刻在节点外（07:00，节点从 07:30 开始）→ 不播，理由说得明白是"当前时刻不在节点内"
            bool okPlay0 = WeatherWarn.Evaluate(cacheEarly, windows, new DateTime(2026, 9, 23, 7, 0, 0), out string r0, out _);
            Check(!okPlay0 && r0.Contains("当前时刻") && r0.Contains("不在任何预报节点内"), "播报时刻 07:00 不在节点内 → 不播报", r0);

            var cacheExpired = cache.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cacheExpired["effective"] = "2026-09-23 09:00";
            cacheExpired["expect"] = "2026-09-23 10:00";
            bool okPlay3 = WeatherWarn.Evaluate(cacheExpired, windows, new DateTime(2026, 9, 23, 16, 0, 0), out string r3, out _);
            Check(!okPlay3 && r3.Contains("有效期已过"), "有效期已过 → 不播报", r3);
            Check(!WeatherWarn.Evaluate(null, windows, DateTime.Now, out _, out _), "还没有预警数据时不播报");

            // 编号 1000：排程展开时不当作"引用失效"，而是给出跳过原因
            var all = Program.ReadJsonl("music.jsonl");
            var task = new System.Text.Json.Nodes.JsonObject { ["id"] = "998", ["music_ids"] = new System.Text.Json.Nodes.JsonArray { "1000" } };
            var q = Program.ExpandTaskQueue(task, all, new System.Collections.Generic.List<string> { "1000" },
                out var probs, out _, out var warnNotes);
            Check(q.Count == 0 && probs.Count == 0, "编号 1000 不会被判成引用失效", "problems=" + probs.Count);
            Check(warnNotes.Count == 1, "编号 1000 给出跳过原因（写日志）", string.Join("；", warnNotes));

            // 配置自检也不能把保留编号 1000 当成"已删除的音乐编号"（用户实测遇到的误报）
            var taskArr = Program.ReadJsonl("tasks.jsonl");
            taskArr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "999",
                ["start_time"] = "08:00",
                ["duration_m"] = 1,
                ["duration_s"] = 0,
                ["volume"] = 100,
                ["days"] = new System.Text.Json.Nodes.JsonArray { 1, 2, 3, 4, 5 },
                ["specific_dates"] = new System.Text.Json.Nodes.JsonArray(),
                ["music_ids"] = new System.Text.Json.Nodes.JsonArray { "1000" }
            });
            Program.WriteJsonl("tasks.jsonl", taskArr);
            bool falseAlarm = false; string warnItem = null;
            foreach (var it in Desktop.ConfigSelfCheck.Run())
            {
                if ((it.Text ?? "").Contains("已删除的音乐编号")) falseAlarm = true;
                if ((it.Text ?? "").Contains("1000")) warnItem = it.Text;
            }
            Check(!falseAlarm, "引用了编号 1000 的任务不会被自检报成“引用了已删除的音乐编号”");
            Check(warnItem != null, "自检里给出了编号 1000 的专项状态", warnItem ?? "(没有)");

            // 离线语音：系统里有中文语音，且能合成出有效 WAV
            // 【环境依赖 → SKIP】没装中文语音包的机器上这几项必然失败，计 SKIP 而不是 FAIL。
            var zh = WeatherWarn.InstalledChineseVoices();
            bool hasTts = zh.Count >= 1;
            // 【CI 可声明环境缺失】学校机器必须装中文语音包（气象预警播报要用），所以默认是硬断言；
            // 但 GitHub Actions 的 runner 是英文系统、没有中文语音包，那属于"环境不满足"而不是"代码坏了"。
            // 因此允许用环境变量 CBS_SELFTEST_ALLOW_NO_TTS=1 把这条降级为 SKIP（CI 里会设置它）。
            bool allowNoTts = (Environment.GetEnvironmentVariable("CBS_SELFTEST_ALLOW_NO_TTS") ?? "") == "1";
            // 【不再用 CheckOrSkip】它的第一个参数是"环境满足(envOk)"，写成表达式极易搞反（我已错两次）。
            // 这里用显式分支，真值表一目了然：
            //   有中文语音                      -> 硬断言（PASS/FAIL）
            //   没有 + CI 声明 ALLOW_NO_TTS=1   -> SKIP（runner 是英文系统，不是代码问题）
            //   没有 + 没声明                    -> 硬断言失败（学校机器漏装语音包必须报错）
            if (!hasTts && allowNoTts)
            {
                Skip("系统装了中文离线语音（SAPI）", "环境变量 CBS_SELFTEST_ALLOW_NO_TTS=1（CI 上没有中文语音包）");
            }
            else
            {
                Check(hasTts, "系统装了中文离线语音（SAPI）", hasTts ? string.Join("；", zh) : "没有中文语音");
            }
            string wav = Path.Combine(Program.AppDataDir, "warn", "selftest.wav");
            string err = null;
            bool syn = hasTts && WeatherWarn.Synthesize("请注意，示例雷电黄色预警，预计9月23日14点27分至20点27分影响本地。", wav, 0, null, out err);
            CheckOrSkipEnv(hasTts, "本机没有中文语音包", syn, err, "离线合成预警语音成功（不联网）", err ?? "");
            CheckOrSkipEnv(hasTts, "本机没有中文语音包",
                File.Exists(wav) && new FileInfo(wav).Length > 1024, syn ? null : err, "合成出的 WAV 有实际内容",
                File.Exists(wav) ? new FileInfo(wav).Length + " 字节" : "文件不存在");

            // 音色 / 语速 / 重复次数 / 响度归一
            CheckOrSkip(hasTts, "本机没有中文语音包", WeatherWarn.InstalledVoices().Count >= 1,
                "能列出系统里的音色", string.Join("；", WeatherWarn.InstalledVoices()));
            Check(WeatherWarn.GetRepeat() == 2, "默认重复 2 遍", WeatherWarn.GetRepeat().ToString());
            Check(WeatherWarn.GetGain(), "默认开启响度归一");
            Check(WeatherWarn.GetVoice() == "", "默认音色 = 自动", WeatherWarn.GetVoice());

            // ffmpeg：响度归一 + 重复 N 遍（实跑，用 ffprobe/volumedetect 量结果）
            // 【环境依赖 → SKIP】ffmpeg 缺失或被安全软件拦住时必然失败，同样计 SKIP。
            string proc = Path.Combine(Program.AppDataDir, "warn", "selftest-g3.wav");
            string perr = null;
            // ffmpeg 是否可用必须**独立探测**：不能拿 okProc 本身当环境判据，
            // 否则"处理真的失败了"会被误判成"环境不满足"而跳过，把真实回归藏起来。
            bool hasFfmpeg = File.Exists(Program.FfmpegPath);
            // 【环境判据必须完整】除了"有语音包 / 有 ffmpeg"，还必须"合成真的产出了可用 WAV"。
            // 否则安全软件拦掉 SAPI 写入时会得到一个 **0 字节**的 wav，拿它去喂 ffmpeg 会直接把 ffmpeg 跑崩
            // （退出码 0xC0000005），于是被记成 FAIL —— 但这明明是环境问题，应该 SKIP。
            bool wavOk = File.Exists(wav) && new FileInfo(wav).Length > 1024;
            bool ffEnv = hasTts && hasFfmpeg && syn && wavOk;
            bool okProc = ffEnv && WeatherWarn.ProcessAudio(wav, proc, 3, out perr);
            string ffWhy = !hasTts ? "本机没有中文语音包"
                         : (!syn || !wavOk) ? "离线语音合成没产出可用 WAV（写入被安全软件拦截）"
                         : "本机 ffmpeg 不可用（缺失或被安全软件拦截）";
            CheckOrSkipEnv(ffEnv, ffWhy, okProc, perr, "ffmpeg 响度归一 + 重复处理成功", perr ?? "");
            double rawSec = hasTts ? WeatherWarn.ProbeSeconds(wav) : 0.0;
            double procSec = okProc ? WeatherWarn.ProbeSeconds(proc) : 0.0;
            CheckOrSkipEnv(ffEnv, ffWhy, procSec > rawSec * 2.5 && procSec < rawSec * 3.6, perr, "重复 3 遍后时长约为原来 3 倍",
                Math.Round(rawSec, 1) + "s → " + Math.Round(procSec, 1) + "s");
            double rawDb = hasTts ? WeatherWarn.ProbeMaxVolumeDb(wav) : 0.0;
            double procDb = okProc ? WeatherWarn.ProbeMaxVolumeDb(proc) : 0.0;
            double rawMean = hasTts ? WeatherWarn.ProbeMeanVolumeDb(wav) : 0.0;
            double procMean = okProc ? WeatherWarn.ProbeMeanVolumeDb(proc) : 0.0;
            // 注意：TTS 原声的**峰值**本来就接近满刻度（实测约 -2.8dB），"比音乐小声"是因为语音的平均响度低，
            // 所以这里必须比平均响度（mean_volume），比峰值会得出"已经够响"的错误结论。
            CheckOrSkipEnv(ffEnv, ffWhy, procMean > rawMean + 3, perr, "响度归一后平均响度明显提高（这才是「比音乐小声」的根因）",
                "平均 " + Math.Round(rawMean, 1) + " dB → " + Math.Round(procMean, 1) + " dB；峰值 "
                + Math.Round(rawDb, 1) + " → " + Math.Round(procDb, 1) + " dB");

            // 端到端：排程真正调用的入口（判断通过 → 合成 → 增益 + 重复 → 返回可播文件）
            var nowCache = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "12345678901234_20260924105216",
                ["name"] = "示例",
                ["signaltype"] = "雷电",
                ["signallevel"] = "黄色",
                ["effective"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ["expect"] = DateTime.Now.AddHours(6).ToString("yyyy-MM-dd HH:mm"),
                ["text"] = "示例县气象台发布雷电黄色预警信号：预计未来6小时，双河镇等乡镇将出现雷电活动，请注意防范。",
                ["duration"] = "未来6小时",
                ["speech"] = ""
            };
            WeatherWarn.SaveCaches(new System.Collections.Generic.List<System.Text.Json.Nodes.JsonObject> { nowCache });
            WeatherWarn.SaveWindows(new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["days"] = new System.Text.Json.Nodes.JsonArray { 1, 2, 3, 4, 5, 6, 7 }, ["start"] = "00:00", ["end"] = "23:59" }
            });
            string prepReason = "";
            bool envOk = ffEnv;   // 环境判据用"TTS + ffmpeg 都在"，而不是"处理成功了" —— 否则真实失败会被跳过
            var prep = new System.Collections.Generic.List<string>();
            if (envOk) prep = WeatherWarn.PrepareForPlayback(out prepReason);
            else prepReason = ffWhy;
            CheckOrSkipEnv(envOk, ffWhy, prep.Count == 1 && File.Exists(prep[0]), prepReason, "准备播报：返回处理好的音频文件",
                prepReason + " → " + (prep.Count > 0 ? System.IO.Path.GetFileName(prep[0]) : "(空)"));
            if (prep.Count > 0)
                Check(WeatherWarn.ProbeSeconds(prep[0]) > WeatherWarn.ProbeSeconds(wav) * 1.4,
                    "准备播报的音频已按重复次数加长（默认 2 遍）",
                    Math.Round(WeatherWarn.ProbeSeconds(wav), 1) + "s → " + Math.Round(WeatherWarn.ProbeSeconds(prep[0]), 1) + "s");

            // 预报节点"跟随调休规则"
            Check(WeatherWarn.GetFollowTiaoxiu(), "默认跟随调休规则 = 开");
            Check(WeatherWarn.TiaoxiuVerdict(new DateTime(2026, 9, 24)) == null, "没写调休规则的日子：判定为空（回落到星期几）");
            var sat = new DateTime(2026, 9, 26);      // 周六
            var monWin = WeatherWarn.DefaultWindows();
            Check(!WeatherWarn.InWindows(sat.AddHours(9), monWin, out _), "周六 09:00 默认不落在「周一~周五」节点内");
            Check(WeatherWarn.InWindows(sat.AddHours(9), monWin, true, out string hitSat), "调休补班日：周六 09:00 按工作日节点算命中", hitSat);

            var cfgTx = Program.ReadSettingMerged();
            cfgTx.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["_type"] = "tiaoxiu",
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["marker"] = 0,
                ["action"] = "disable",
                ["note"] = "自检临时规则"
            });
            Program.WriteSettingMerged( cfgTx);
            Check(WeatherWarn.TiaoxiuVerdict(DateTime.Now) == false, "写入全局放假规则后：判定为放假日");
            bool okHoliday = WeatherWarn.Evaluate(nowCache, WeatherWarn.GetWindows(), DateTime.Now, out string rHoliday, out _);
            Check(!okHoliday && rHoliday.Contains("调休放假日"), "调休放假日 → 预警不播报", rHoliday);

            // 清掉自检临时写进去的放假规则，别影响后面的用例（曾经因此误判 3 条）
            var cfgTxKeep = new System.Text.Json.Nodes.JsonArray();
            foreach (var n in Program.ReadSettingMerged())
            {
                if (n is System.Text.Json.Nodes.JsonObject o && o["_type"]?.ToString() == "tiaoxiu" && o["note"]?.ToString() == "自检临时规则") continue;
                cfgTxKeep.Add(n.DeepClone());
            }
            Program.WriteSettingMerged( cfgTxKeep);
            Check(WeatherWarn.TiaoxiuVerdict(DateTime.Now) == null, "临时调休规则已清理（后续用例不受影响）");

            // 多条预警：按等级从高到低依次都播 + 预警音量不随任务音量
            var cOrange = nowCache.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cOrange["id"] = "12345678901234_20260924110000";
            cOrange["signaltype"] = "暴雨";
            cOrange["signallevel"] = "橙色";
            cOrange["speech"] = "";
            WeatherWarn.SaveCaches(new System.Collections.Generic.List<System.Text.Json.Nodes.JsonObject> { nowCache, cOrange });
            Check(WeatherWarn.GetCaches().Count == 2, "缓存能同时存多条预警", WeatherWarn.GetCaches().Count.ToString());
            string multiReason;
            var multiFiles = new System.Collections.Generic.List<string>();
            if (envOk) multiFiles = WeatherWarn.PrepareForPlayback(out multiReason);
            else multiReason = ffWhy;
            CheckOrSkipEnv(envOk, ffWhy, multiFiles.Count == 2, multiReason, "两条都满足条件 → 依次播两段", multiReason);
            CheckOrSkipEnv(envOk, ffWhy, multiReason.Contains("暴雨橙色、雷电黄色"), multiReason, "按等级从高到低排序（橙色在前）", multiReason);
            Check(WeatherWarn.LevelRank("红色") > WeatherWarn.LevelRank("橙色")
                  && WeatherWarn.LevelRank("橙色") > WeatherWarn.LevelRank("黄色")
                  && WeatherWarn.LevelRank("黄色") > WeatherWarn.LevelRank("蓝色"), "等级权重 红>橙>黄>蓝");

            var warnTask = new System.Text.Json.Nodes.JsonObject { ["id"] = "997", ["music_ids"] = new System.Text.Json.Nodes.JsonArray { "1000" } };
            var warnQueue = Program.ExpandTaskQueue(warnTask, all, new System.Collections.Generic.List<string> { "1000" },
                out _, out _, out _);
            CheckOrSkipEnv(envOk, ffWhy,
                warnQueue.Count >= 1 && warnQueue[0]["_volume"] != null && warnQueue[0]["_volume"].GetValue<int>() == 100,
                multiReason, "气象预警不受任务音量削减（队列项自带 _volume=100）",
                warnQueue.Count > 0 ? (warnQueue[0]["_volume"]?.ToString() ?? "(无)") : "(空队列)");

            // 台名：优先预警标题里的官方全名
            var cTitle = nowCache.DeepClone() as System.Text.Json.Nodes.JsonObject;
            cTitle["title"] = "示例县气象台发布雷电黄色预警信号[III级/较重]";
            Check(WeatherWarn.StationOf(cTitle) == "示例县气象台", "台名优先用标题里的官方全名", WeatherWarn.StationOf(cTitle));
            Check(WeatherWarn.StationOf(nowCache) == "示例气象台", "没有标题时退回「地区名+气象台」", WeatherWarn.StationOf(nowCache));

            // 迁移：把旧版存在 setting.jsonl 里的一条/一组缓存导入 warn.jsonl
            WeatherWarn.SaveCaches(new System.Collections.Generic.List<System.Text.Json.Nodes.JsonObject>());
            Check(WeatherWarn.GetCaches().Count == 0, "清空缓存后读出来是 0 条");
            var legacy = new System.Text.Json.Nodes.JsonArray { nowCache.DeepClone() };
            WeatherWarn.ImportLegacyCaches(legacy);
            Check(WeatherWarn.GetCaches().Count == 1 && WeatherWarn.GetCaches()[0]["_type"]?.ToString() == "warn_cache",
                "旧版缓存能被迁移导入 warn.jsonl（并补上 _type）", WeatherWarn.GetCaches().Count.ToString());

            // 预警播报页：无界面状态下把布局完整搭一遍（能抓出"同一个控件挂了两个父节点"这类布局异常）
            try
            {
                var page = new Desktop.WarnPage(null).RenderPage();
                var sv = page as System.Windows.Controls.ScrollViewer;
                var rootPanel = sv?.Content as System.Windows.Controls.Panel;
                int cards = 0;
                if (rootPanel != null)
                    foreach (var c in rootPanel.Children) if (c is System.Windows.Controls.Border) cards++;
                Check(page != null && cards >= 3, "预警播报页能构建出来（订阅设置 / 最近预警 / 预报节点 三张卡片）", "卡片 " + cards + " 张");
            }
            catch (Exception ex) { Check(false, "预警播报页布局构建异常", ex.GetType().Name + "：" + ex.Message); }

            Line("");
        }

        // ---- RFC 7748 自检用的三个小工具（BouncyCastle X25519，与 MicProtocol 同一套实现）----
        static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        static byte[] X25519Pub(byte[] priv)
        {
            var p = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(priv, 0);
            return p.GeneratePublicKey().GetEncoded();
        }

        static byte[] X25519Shared(byte[] priv, byte[] peerPub)
        {
            var p = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(priv, 0);
            var q = new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(peerPub, 0);
            var agree = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            agree.Init(p);
            var shared = new byte[agree.AgreementSize];
            agree.CalculateAgreement(q, shared, 0);
            return shared;
        }
    }
}
