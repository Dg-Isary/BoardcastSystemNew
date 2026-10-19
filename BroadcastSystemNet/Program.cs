#nullable disable

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Net;
using System.Net.Sockets;

namespace BroadcastSystemNet
{
    public static class LogHelper
    {
        private static readonly object _lock = new object();
        public static void Log(string message, Exception ex = null)
        {
            Task.Run(() => {
                try
                {
                    lock (_lock)
                    {
                        string logPath = Program.ErrorLogPath;
                        string logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                        if (ex != null) logMsg += $"\r\n   细节: {ex.Message}\r\n   追踪: {ex.StackTrace}";
                        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                        File.AppendAllText(logPath, logMsg + "\r\n----------------------------------\r\n");
                    }
                }
                catch { }
            });
            PlayLog.Write("系统", "异常", "error", detail: message + (ex != null ? " | " + ex.Message : ""));
        }
    }

    internal static class Program
    {
        /// <summary>
        /// 程序目录。注意不能用 AppDomain.BaseDirectory —— 发布版是 PublishSingleFile，
        /// 那个值指向 %TEMP%\.net\... 的临时解压目录，res（网页的 FA/CSS/JS、字体）其实在 exe 旁边。
        /// 单文件发布时 AppContext.BaseDirectory 也会指到解压目录，所以优先取 exe 所在目录。
        /// </summary>
        /// <summary>静默启动（--silent）：不显示主界面，只在托盘与悬浮球运行。</summary>
        public static bool SilentStart;

        public static string BaseDir = ResolveBaseDir();

        static string ResolveBaseDir()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "res"))) return dir;
                }
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
        public static string ResDir = Path.Combine(BaseDir, "res");
        public static string BinDir = Path.Combine(ResDir, "bin");
        public static string FfplayPath = Path.Combine(BinDir, "ffplay.exe");
        public static string FfmpegPath = Path.Combine(BinDir, "ffmpeg.exe");

        public static string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusBroadcastSystem");
        public static string ConfigDir = Path.Combine(AppDataDir, "config");
        public static string RingDir = Path.Combine(AppDataDir, "ring");
        public static string LogDir = Path.Combine(AppDataDir, "logs");
        /// <summary>
        /// 清理超过「日志保留天数」的日志文件（用户要求）。默认 30 天，0 = 永久保留。
        /// **只删 logs 目录下的日志类文件**（play-*.jsonl / warn-*.jsonl / *.log / *.txt），
        /// 业务数据（tasks.jsonl / resume.json / music.jsonl / 配置）**一律不碰**。
        /// </summary>
        public static void CleanupOldLogs()
        {
            int days = VntManager.LogKeepDays;
            if (days <= 0) return;                                   // 0 = 永久保留
            if (!Directory.Exists(LogDir)) return;
            var limit = DateTime.Now.AddDays(-days);
            int deleted = 0; long freed = 0;
            foreach (var f in new DirectoryInfo(LogDir).GetFiles())
            {
                string n = f.Name.ToLowerInvariant();
                bool isLog = n.EndsWith(".jsonl") || n.EndsWith(".log") || n.EndsWith(".txt");
                if (!isLog) continue;                                // 非日志文件不碰
                if (f.LastWriteTime >= limit) continue;
                try { long len = f.Length; f.Delete(); deleted++; freed += len; } catch { }
            }
            if (deleted > 0)
                PlayLog.Write("系统", "日志清理", "info",
                    detail: $"已删除 {deleted} 个超过 {days} 天的日志文件，释放 {freed / 1024.0 / 1024.0:0.0} MB（保留天数可在「系统设置」里改，0 = 永久保留）");
        }
        public static string ErrorLogPath = Path.Combine(AppDataDir, "system_error.log");

        public static int ListenPort = 5000;

        public static ConcurrentDictionary<string, DateTime> ValidTokens = new ConcurrentDictionary<string, DateTime>();
        public static ConcurrentDictionary<string, bool> TriggeredTasks = new ConcurrentDictionary<string, bool>();

        /// <summary>配置文件读写总闸：所有 jsonl 的读-改-写都走它，避免多定时器与 HTTP 并发写坏文件。</summary>
        private static readonly object ConfigIoLock = new object();

        public static object StatusLock = new object();
        public static bool IsPlaying = false;
        public static string CurrentPlayingName = "";

        public static DateTime CurrentAudioStartTime = DateTime.Now;
        public static double CurrentAudioOffset = 0;

        public static object WaveformLock = new object();
        public static short[] CurrentWaveformSamples = new short[0];

        public static object ManualLock = new object();
        public static string ManualStatus = "stopped";
        public static string ManualMode = "sequential";
        public static int ManualVolume = 100;
        public static int ManualCurrentIndex = 0;
        public static double ManualPauseOffset = 0;
        /// <summary>当前播放文件的真实时长（毫秒）：用随包的 ffprobe 读，结果按路径缓存。</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _durCache = new();
        static double ProbeDurationMs(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return 0;
                if (_durCache.TryGetValue(path, out var c)) return c;
                string ffprobe = System.IO.Path.Combine(AppContext.BaseDirectory, "res", "bin", "ffprobe.exe");
                if (!System.IO.File.Exists(ffprobe)) return 0;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = "-v error -show_entries format=duration -of default=nw=1:nk=1 \"" + path + "\"",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return 0;
                string outText = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (double.TryParse(outText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec > 0)
                {
                    double ms = sec * 1000.0;
                    _durCache[path] = ms;
                    return ms;
                }
            }
            catch { }
            return 0;
        }

        public static double ManualCurrentDuration = 0.0;
        public static DateTime MpStartTime = DateTime.Now;
        public static JsonArray ManualQueue = new JsonArray();
        public static Process MpProcess = null;
        /// <summary>当前定时任务的 ffplay 进程，用于抢占与全局停用时的精确终止。</summary>
        public static Process ScheduleProcess = null;
        /// <summary>定时任务序号：每次开始新的定时播放递增，被抢占的旧任务据此退出循环。</summary>
        public static int SequenceGeneration = 0;

        /// <summary>当前正在播放的定时任务编号（0 = 没有）。"播完当前这首"的温柔抢占要据此通知旧任务。</summary>
        public static volatile int CurrentScheduledTaskId = 0;
        /// <summary>"请播完当前这首就停"的请求，键 = 任务编号（温柔抢占用，不切断正在放的曲子）。</summary>
        static readonly ConcurrentDictionary<int, bool> FinishTrackRequested = new ConcurrentDictionary<int, bool>();
        /// <summary>定时任务票号：每触发一次 +1；正在等待的任务发现票号变了就让位给新任务。</summary>
        static int ScheduledTicket = 0;

        /// <summary>统一进程台账：所有音频输出与波形解码进程都登记在案，停止时精确终止。</summary>
        public sealed class AudioProcessInfo
        {
            public int Pid;
            public string Purpose = "";
            public string Name = "";
            public string Path = "";
            public DateTime StartTime;
            public Process Proc;
        }

        public static ConcurrentDictionary<int, AudioProcessInfo> ActiveAudioProcesses = new ConcurrentDictionary<int, AudioProcessInfo>();

        public static System.Threading.Timer SchedulerTimer;
        public static System.Threading.Timer ManualPlayerTimer;
        public static System.Threading.Timer SubscriptionTimer;
        public static System.Threading.Timer WarnTimer;

        public static Icon AppIcon;

        // --- 底层物理 CPU / RAM 探针 ---
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime; }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual; }

        private static ulong _lastIdleTime = 0;
        private static ulong _lastSystemTime = 0;

        public static double GetCpuUsage()
        {
            try
            {
                if (GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
                {
                    ulong currentIdle = idleTime.ToUInt64();
                    ulong currentSystem = kernelTime.ToUInt64() + userTime.ToUInt64();
                    if (_lastSystemTime != 0)
                    {
                        ulong idleDiff = currentIdle - _lastIdleTime;
                        ulong sysDiff = currentSystem - _lastSystemTime;
                        if (sysDiff > 0)
                        {
                            double cpu = (1.0 - ((double)idleDiff / sysDiff)) * 100.0;
                            _lastIdleTime = currentIdle; _lastSystemTime = currentSystem;
                            return Math.Min(100.0, Math.Max(0.0, cpu));
                        }
                    }
                    _lastIdleTime = currentIdle; _lastSystemTime = currentSystem;
                }
            }
            catch (Exception ex) { LogHelper.Log("GetCpuUsage Error", ex); }
            return 0.0;
        }

        public static double GetRamUsage()
        {
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus)) return memStatus.dwMemoryLoad;
            }
            catch (Exception ex) { LogHelper.Log("GetRamUsage Error", ex); }
            return 0.0;
        }

        static Mutex _singleInstanceMutex;
        static bool _createdSingleInstanceMutex = true;

        public static string StartupUrl => $"http://127.0.0.1:{ListenPort}";

        /// <summary>端口可用性探测（不实际占用端口，仅判断能否绑定）。</summary>
        public static bool IsPortFree(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                return true;
            }
            catch { return false; }
            finally { try { listener?.Stop(); } catch { } }
        }

        /// <summary>
        /// 读取配置端口；若已被占用（例如另一个实例残留、或其它软件占用），
        /// 临时向上探测可用端口并继续启动 —— 只改内存，不写回配置文件。
        /// </summary>
        public static void ResolveListenPort()
        {
            int configured = 5000;
            try
            {
                foreach (var item in ReadSettingMerged())
                {
                    if (item is JsonObject o && o["_type"]?.ToString() == "global" && o["web_port"] != null)
                    {
                        if (int.TryParse(o["web_port"].ToString(), out int p) && p >= 1 && p <= 65535) configured = p;
                        break;
                    }
                }
            }
            catch { }
            if (configured < 1 || configured > 65535) configured = 5000;

            if (IsPortFree(configured)) { ListenPort = configured; return; }

            for (int p = configured + 1; p <= Math.Min(65535, configured + 50); p++)
            {
                if (IsPortFree(p))
                {
                    ListenPort = p;
                    PlayLog.Write("系统", "端口切换", "warn",
                        detail: $"配置端口 {configured} 已被占用，本次临时改用 {p}（不写回配置，下次启动仍以配置为准）");
                    AlertCenter.Publish("系统", $"端口 {configured} 被占用，本次已临时改用 {p}。此变更不会保存，下次启动仍会尝试 {configured}。", "warn",
                        dedupKey: $"port-{configured}-{p}-{DateTime.Now:yyyyMMddHHmm}");
                    return;
                }
            }
            PlayLog.Write("系统", "端口冲突", "error", detail: $"配置端口 {configured} 及其后 50 个端口均被占用，本次仍尝试使用 {configured}");
            ListenPort = configured;
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 离线自检模式：不启动界面、不联网、不触碰真实数据目录
            SilentStart = args != null && Array.IndexOf(args, "--silent") >= 0;
            // --data-dir <目录>：把数据目录改到指定位置（探针/排查用，不影响任何默认值）。
            // 典型用途：数据目录不可写时（例如受控环境），换到可写目录后再验证 -W 所需的 RSA 密钥生成。
            if (args != null)
            {
                int di = Array.IndexOf(args, "--data-dir");
                if (di >= 0 && di + 1 < args.Length && !args[di + 1].StartsWith("--"))
                {
                    try
                    {
                        AppDataDir = Path.GetFullPath(args[di + 1]);
                        ConfigDir = Path.Combine(AppDataDir, "config");
                        RingDir = Path.Combine(AppDataDir, "ring");
                        LogDir = Path.Combine(AppDataDir, "logs");
                        ErrorLogPath = Path.Combine(AppDataDir, "system_error.log");
                        Directory.CreateDirectory(AppDataDir); Directory.CreateDirectory(ConfigDir);
                        Console.WriteLine("数据目录已切换到 " + AppDataDir);
                    }
                    catch (Exception ex) { Console.WriteLine("切换数据目录失败：" + ex.Message); }
                }
            }
        if (args != null && Array.IndexOf(args, "--self-test") >= 0)
            {
                
                                // 【自检不许污染用户配置】以前这里给虚拟 IP 塞了个前置值，结果写进了真实配置文件（用户已多次反馈）。
                // 现在：保存原值 → 跑自检 → 立刻还原。
                string _vpBackup = ""; bool _vpHad = false;
                try { _vpBackup = VntManager.VnetPrefix; _vpHad = true; } catch { }
                try
                {
                    Environment.ExitCode = SelfTest.Run(args);
                }
                finally
                {
                    try { if (_vpHad) VntManager.VnetPrefix = _vpBackup; } catch { }
                }
                return;
            }
            // 托盘图标三态预览：把"红/蓝/绿"状态圆点画成一张图，方便人工核对（不启动界面、不联网）
            if (args != null && Array.IndexOf(args, "--tray-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--tray-preview");
                    string outPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
                    string iconPath0 = Path.Combine(ResDir, "favicon.ico");
                    if (File.Exists(iconPath0)) { try { AppIcon = new Icon(iconPath0); } catch { } }
                    string saved = Desktop.TrayIcons.WriteStatePreview(outPath);
                    Console.WriteLine("托盘图标预览已生成：" + saved);
                    // 顺带把三种状态下的右键菜单也渲染出来（走真实菜单代码）
                    string basePath = System.IO.Path.ChangeExtension(saved, null);
                    Console.WriteLine("托盘菜单预览：" + Desktop.TrayMenuWindow.RenderPreview(Desktop.TrayState.Disabled, true, basePath + "-menu-disabled.png"));
                    Console.WriteLine("托盘菜单预览：" + Desktop.TrayMenuWindow.RenderPreview(Desktop.TrayState.Scheduled, true, basePath + "-menu-running.png"));
                    Console.WriteLine("托盘菜单预览：" + Desktop.TrayMenuWindow.RenderPreview(Desktop.TrayState.Disabled, false, basePath + "-menu-stopped.png"));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成托盘图标预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // vnt-lite 中继收发测试（里程碑 3 验收）：--vntlite-relay-test <host:port> <token> [秒数] [password] [model]
            if (args != null && Array.IndexOf(args, "--vntlite-relay-test") >= 0)
            {
                try
                {
                    try { PlayLog.Start(LogDir); } catch { }
                    // 日志保留天数（用户要求）：启动时清掉过期日志（**只动 logs 目录里的日志文件**，业务数据不碰）
                    try { CleanupOldLogs(); } catch { }
                    try { PlayLog.Write("系统", "程序启动", "info", detail: "STARTED BUILD=1004-0326-ADMINAPI (/api/mic/code|devices|kick, cred-warn-only)"); } catch { }
                    int i = Array.IndexOf(args, "--vntlite-relay-test");
                    string srv = (i + 1 < args.Length) ? args[i + 1] : "";
                    string tok = (i + 2 < args.Length) ? args[i + 2] : "test-token";
                    int secs = 20; if (i + 3 < args.Length) int.TryParse(args[i + 3], out secs);
                    string pw = (i + 4 < args.Length) ? args[i + 4] : "";
                    string mdl = (i + 5 < args.Length) ? args[i + 5] : "aes_gcm";

                    var a = new BroadcastSystemNet.VntLite.VntLiteClient { Password = pw, Model = mdl, ServerEncrypt = false };
                    var b = new BroadcastSystemNet.VntLite.VntLiteClient { Password = pw, Model = mdl, ServerEncrypt = false };
                    int relayAeadA = 0, relayAeadB = 0;
                    byte[] rk = System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
                    a.OnPeerData = d => { Console.WriteLine($"    [A 收到] {d.Length} 字节"); if (BroadcastSystemNet.MicProtocol.TryOpenPacket(d, rk, out _, out _, out _, out _, out _, out string ra)) { relayAeadA++; Console.WriteLine("    [A] 中继 AEAD 校验通过"); } };
                    a.OnMicPacket = (d, h, p) => { if (BroadcastSystemNet.MicProtocol.TryOpenPacket(d, System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef"), out _, out _, out _, out _, out var pl, out string errA)) Console.WriteLine("    [A] AEAD 校验通过"); else Console.WriteLine("    [A] AEAD 失败：" + errA); };
                    b.OnPeerData = d => { Console.WriteLine($"    [B 收到] {d.Length} 字节"); if (BroadcastSystemNet.MicProtocol.TryOpenPacket(d, rk, out _, out _, out _, out _, out _, out string rb)) { relayAeadB++; Console.WriteLine("    [B] 中继 AEAD 校验通过"); } };
                    byte[] aeadKeyEarly = new byte[32]; for (int k = 0; k < 32; k++) aeadKeyEarly[k] = (byte)(k + 1);
                    b.OnMicPacket = (d, h, p) => { if (BroadcastSystemNet.MicProtocol.TryOpenPacket(d, System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef"), out _, out _, out _, out _, out _, out string errB)) Console.WriteLine("    [B] AEAD 校验通过"); else Console.WriteLine("    [B] AEAD 失败：" + errB); };
                    if (!a.Start(srv, tok, Guid.NewGuid().ToString(), "vlite-A", out string ea)) { Console.WriteLine("A 启动失败：" + ea); Environment.ExitCode = 3; return; }
                    if (!b.Start(srv, tok, Guid.NewGuid().ToString(), "vlite-B", out string eb)) { Console.WriteLine("B 启动失败：" + eb); a.Stop(); Environment.ExitCode = 3; return; }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < secs && (a.State != BroadcastSystemNet.VntLite.VntLiteState.Registered || b.State != BroadcastSystemNet.VntLite.VntLiteState.Registered))
                        System.Threading.Thread.Sleep(200);
                    Console.WriteLine($"A: {a.StatusText()}  IP={BroadcastSystemNet.VntLite.VntPacket.UintToIp(a.VirtualIp)}  公网={a.PublicIp}:{a.PublicPort}");
                    Console.WriteLine($"B: {b.StatusText()}  IP={BroadcastSystemNet.VntLite.VntPacket.UintToIp(b.VirtualIp)}  公网={b.PublicIp}:{b.PublicPort}");
                    foreach (var d in a.Devices) Console.WriteLine($"    A 看到的设备: {d.Name}  {BroadcastSystemNet.VntLite.VntPacket.UintToIp(d.VirtualIp)}  device_status={d.DeviceStatus}");
                    foreach (var d in b.Devices) Console.WriteLine($"    B 看到的设备: {d.Name}  {BroadcastSystemNet.VntLite.VntPacket.UintToIp(d.VirtualIp)}  device_status={d.DeviceStatus}");

                    // ① 先试着直连打洞（同机同 NAT 可能不通，失败不算错）
                    a.SendPunchBurst(b.PublicIp, (int)b.PublicPort, 5);
                    b.SendPunchBurst(a.PublicIp, (int)a.PublicPort, 5);
                    Console.WriteLine($"打洞直发完成（A→{b.PublicIp}:{b.PublicPort}，B→{a.PublicIp}:{a.PublicPort}）");
                    a.PunchTo(b.VirtualIp, 40000); b.PunchTo(a.VirtualIp, 40000);
                    Console.WriteLine("已向对端虚拟 IP 发出 PunchRequest（服务端会转给对方，用于互换端点）");

                    // 2) 中继路径互发自定义包（这条通就算里程碑 3 达成）
                    int round = 0;
                    while (sw.Elapsed.TotalSeconds < secs && (a.PeerPacketsReceived == 0 || b.PeerPacketsReceived == 0 || relayAeadA == 0 || relayAeadB == 0))
                    {
                        round++;
                        a.SendPeerViaIp(System.Text.Encoding.UTF8.GetBytes($"from A #{round}"), b.VirtualIp);
                        b.SendPeerViaIp(System.Text.Encoding.UTF8.GetBytes($"from B #{round}"), a.VirtualIp);
                        // W1 验收：中继路径上发 MicProtocol AEAD 包（不只是普通载荷），并在对端校验
                        if (round % 2 == 0)
                        {
                            var micRelayA = BroadcastSystemNet.MicProtocol.BuildPacket(BroadcastSystemNet.MicProtocol.TypeOgg, 1, (uint)round, 20, System.Text.Encoding.UTF8.GetBytes("aead-relay #" + round), rk);
                            a.SendPeerViaIp(micRelayA, b.VirtualIp);
                            b.SendPeerViaIp(micRelayA, a.VirtualIp);
                        }
                        byte[] aeadKey = System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
                        if (a.P2PEstablished && b.P2PEstablished && round % 2 == 0)
                        {
                            var mic = BroadcastSystemNet.MicProtocol.BuildPacket(BroadcastSystemNet.MicProtocol.TypeOgg, 1, (uint)round, 20, System.Text.Encoding.UTF8.GetBytes("aead #" + round), aeadKey);
                            a.SendRawToPeer(mic, b.PublicIp, (int)b.PublicPort);
                            b.SendRawToPeer(mic, a.PublicIp, (int)a.PublicPort);
                        }
                        a.SendPunchBurst(b.PublicIp, (int)b.PublicPort, 2);
                        b.SendPunchBurst(a.PublicIp, (int)a.PublicPort, 2);
                        System.Threading.Thread.Sleep(400);
                    }
                    Console.WriteLine($"结果：A 发 {a.PeerPacketsSent}/收 {a.PeerPacketsReceived}；B 发 {b.PeerPacketsSent}/收 {b.PeerPacketsReceived}；轮次 {round}" + $"｜收包总数 A={a.RecvPackets}/B={b.RecvPackets}｜错 A={a.LastError}/B={b.LastError}｜IpTurn A={a.PeerIpTurnReceived}/B={b.PeerIpTurnReceived}");
                    Console.WriteLine($"打洞探针：A 收 {a.PunchPacketsReceived}（P2P={a.P2PEstablished}）；B 收 {b.PunchPacketsReceived}（P2P={b.P2PEstablished}）");
                    Console.WriteLine($"P2P 原始包：A 发 {a.P2PRawSent}/收 {a.P2PRawReceived}；B 发 {b.P2PRawSent}/收 {b.P2PRawReceived}");
                    Console.WriteLine($"中继上的 AEAD：A 通过 {relayAeadA}，B 通过 {relayAeadB}");
                    bool ok = a.PeerPacketsReceived > 0 && b.PeerPacketsReceived > 0 && relayAeadA > 0 && relayAeadB > 0;
                    a.Stop(); b.Stop();
                    Environment.ExitCode = ok ? 0 : 4;
                    Console.WriteLine(ok ? "里程碑 3：中继通道收发成功" : "里程碑 3：未收到对端包（可能被服务端策略/密钥组拦截）");
                    Console.WriteLine("PHASE-DONE");
                }
                catch (Exception ex) { Console.WriteLine("中继测试异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // vnt-lite 探针：--vntlite-probe <host:port> <token> [秒数]  （里程碑 2 的真机验收）
            if (args != null && Array.IndexOf(args, "--vntlite-probe") >= 0)
            {
                try
                {
                    try { PlayLog.Start(LogDir); } catch { }
                    int i = Array.IndexOf(args, "--vntlite-probe");
                    string srv = (i + 1 < args.Length) ? args[i + 1] : "";
                    string tok = (i + 2 < args.Length) ? args[i + 2] : "test-token";
                    int secs = 20;
                    if (i + 3 < args.Length) int.TryParse(args[i + 3], out secs);
                    var vc = new BroadcastSystemNet.VntLite.VntLiteClient();
                    if (i + 4 < args.Length) vc.Password = args[i + 4];        // 节点间密码 -w
                    if (i + 5 < args.Length) vc.Model = args[i + 5];           // 加密模式 --model
                    if (!vc.Start(srv, tok, Guid.NewGuid().ToString(), "vntlite-probe", out string verr))
                    { Console.WriteLine("启动失败：" + verr); Environment.ExitCode = 3; return; }
                    var vsw = System.Diagnostics.Stopwatch.StartNew();
                    while (vsw.Elapsed.TotalSeconds < secs)
                    {
                        System.Threading.Thread.Sleep(500);
                        Console.WriteLine($"[{vsw.Elapsed.TotalSeconds,5:F1}s] 状态={vc.State} 收={vc.RecvPackets} 发={vc.SentPackets} 设备={vc.Devices.Count} 最近包={vc.LastPacketInfo}");
                        foreach (var d in vc.Devices)
                            Console.WriteLine($"      设备: {d.Name}  {BroadcastSystemNet.VntLite.VntPacket.UintToIp(d.VirtualIp)}");
                        if (vc.State == BroadcastSystemNet.VntLite.VntLiteState.Registered) break;
                    }
                    Console.WriteLine("最终：" + vc.StatusText());
                    bool ok = vc.State == BroadcastSystemNet.VntLite.VntLiteState.Registered;
                    if (ok)
                        Console.WriteLine("虚拟IP=" + BroadcastSystemNet.VntLite.VntPacket.UintToIp(vc.VirtualIp)
                            + " 网关=" + BroadcastSystemNet.VntLite.VntPacket.UintToIp(vc.VirtualGateway)
                            + " 掩码=" + BroadcastSystemNet.VntLite.VntPacket.UintToIp(vc.VirtualNetmask)
                            + " 公网=" + vc.PublicIp + ":" + vc.PublicPort + " 同网设备=" + vc.Devices.Count);
                    vc.Stop();
                    Environment.ExitCode = ok ? 0 : 4;
                    Console.WriteLine("PHASE-DONE");
                }
                catch (Exception ex) { Console.WriteLine("vnt-lite 探针异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // UDP 配对通道探针（§15.3 验收）：在回环上真起话筒服务 → 用 cbs-pair 加密帧走一次
            //   完整配对（含错误密码被拒、合法密码派生出两端一致的会话密钥），无需手机即可回归。
            //   用法：--mic-pair-udp-probe ["数据密码"] ["动态码"] [端口]
            if (args != null && Array.IndexOf(args, "--mic-pair-udp-probe") >= 0)
            {
                int rc;
                try { rc = MicPairUdpProbe(args); }
                catch (Exception ex) { Console.WriteLine("UDP 配对探针异常：" + ex.Message); rc = 5; }
                Environment.ExitCode = rc;
                Console.WriteLine("PHASE-DONE");
                return;
            }
            // 配对守候模式（真机/模拟器联调用）：起话筒服务并等待手机来配对，实时打印结果与动态码。
            //   用法：--mic-pair-udp-serve [绑定地址] [ctl端口] [code_seed] [守候秒数]
            //   绑定地址默认 0.0.0.0（模拟器走 10.0.2.2 或 adb reverse 都能连；真机用本机局域网 IP）
            if (args != null && Array.IndexOf(args, "--mic-pair-udp-serve") >= 0)
            {
                int rc;
                try { rc = MicPairUdpServe(args); }
                catch (Exception ex) { Console.WriteLine("配对守候异常：" + ex.Message); rc = 5; }
                Environment.ExitCode = rc;
                Console.WriteLine("PHASE-DONE");
                return;
            }
            // 服务端加密（-W）握手探针：只验证"能不能在要求 -W 的 vnts 上完成密钥握手并注册成功"。
            //   与 --vntlite-relay-test 的区别：这里开 ServerEncrypt，跑的是加密通道；
            //   而 vnts 只中继明文，所以加密通道下"收包"本来就该是 0，判据是**注册成功**。
            //   用法：--vntlite-wtest <host:port> <token> [秒数]
            if (args != null && Array.IndexOf(args, "--vntlite-wtest") >= 0)
            {
                try
                {
                    try { PlayLog.Start(LogDir); } catch { }
                    int i = Array.IndexOf(args, "--vntlite-wtest");
                    string srv = (i + 1 < args.Length) ? args[i + 1] : "";
                    string tok = (i + 2 < args.Length) ? args[i + 2] : "test-token";
                    int secs = 12; if (i + 3 < args.Length) int.TryParse(args[i + 3], out secs);
                    var a = new BroadcastSystemNet.VntLite.VntLiteClient
                    {
                        ServerEncrypt = true,
                        RelayPlaintext = true,          // 不发客户端加密体，让 -W 的 e 位语义保持干净
                        Name = "wtest-A"
                    };
                    // 可选第 5 个参数：上报的客户端版本号（用来排除"服务端按版本号区别对待"）
                    if (i + 4 < args.Length && args[i + 4].StartsWith("ver="))
                        a.ClientVersion = args[i + 4].Substring(4);
                    // 可选：数据密码（pw=...）。配了它时对称密钥改用 SHA256(密码) 派生（与官方客户端一致）
                    int pwIdx = Array.FindIndex(args, s => s != null && s.StartsWith("pw="));
                    if (pwIdx >= 0) a.DataPassword = args[pwIdx].Substring(3);
                    Console.WriteLine($"A: 连接 {srv}（要求服务端加密 -W）…");
                    // 诊断：把**真实线上字节**里"第二次握手"那一包 dump 出来（外层=Service、上层=SecretHandshakeRequest=7）
                    var dumped = new List<string>();
                    a.OnOutgoingPacket = pkt =>
                    {
                        try
                        {
                            if (dumped.Count < 8)
                            {
                                dumped.Add($"出站包#{dumped.Count + 1}：{pkt.Length} 字节，"
                                    + $"头={Convert.ToHexString(pkt, 0, Math.Min(12, pkt.Length))}（e位={(pkt[0] & 0x80) != 0}）");
                            }
                        }
                        catch { }
                    };
                    if (!a.Start(srv, tok, Guid.NewGuid().ToString(), "wtest-A", out string err))
                    {
                        Console.WriteLine("启动失败：" + err);
                        Environment.ExitCode = 3;
                        Console.WriteLine("PHASE-DONE");
                        return;
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < secs && a.State != BroadcastSystemNet.VntLite.VntLiteState.Registered)
                        System.Threading.Thread.Sleep(200);
                    bool ok = a.State == BroadcastSystemNet.VntLite.VntLiteState.Registered;
                    Console.WriteLine($"A 状态={a.State} 虚拟IP={(a.VirtualIp != 0 ? BroadcastSystemNet.VntLite.VntPacket.UintToIp(a.VirtualIp) : "-")}"
                        + $" 服务端加密={a.ServerEncrypted} 收 {a.RecvPackets}/发 {a.SentPackets} 包");
                    Console.WriteLine("加密握手诊断：" + (string.IsNullOrEmpty(a.SecretHandshakeDiag) ? "(未发生——说明没走到第二次握手)" : a.SecretHandshakeDiag));
                    Console.WriteLine("加密发送诊断：" + (string.IsNullOrEmpty(a.EncryptedSendDiag) ? "(未发生——没有包被加密发出)" : a.EncryptedSendDiag));
                    Console.WriteLine($"收包解密：成功 {a.ServerDecryptOk} 次，失败 {a.ServerDecryptFailures} 次 "
                        + $"（加密态下收到带 e 位 {a.ServerRecvEncrypted} 个 / 明文 {a.ServerRecvPlain} 个）{a.ServerDecryptDiag}");
                    // 诊断：把服务端实际发回的加密响应原始字节也落盘，便于离线硬解
                    try
                    {
                        if (a.LastServerEncryptedPacket != null)
                        {
                            string dd2 = Path.Combine(Path.GetTempPath(), "cbs-wreplay");
                            Directory.CreateDirectory(dd2);
                            File.WriteAllBytes(Path.Combine(dd2, "resp.bin"), a.LastServerEncryptedPacket);
                            File.WriteAllBytes(Path.Combine(dd2, "key.bin"), a.ServerKeyBytes ?? Array.Empty<byte>());
                            Console.WriteLine($"服务端加密响应已落盘：{a.LastServerEncryptedPacket.Length} 字节 → {dd2}\\resp.bin");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine("落盘响应失败：" + ex.Message); }
                    // 与真实服务器日志对拍用：我们发给服务端的"客户端公钥指纹"
                    Console.WriteLine("客户端 key_finger（第一次握手携带）= "
                        + (string.IsNullOrEmpty(BroadcastSystemNet.VntLite.VntRsa.LocalFinger()) ? "(空)" : BroadcastSystemNet.VntLite.VntRsa.LocalFinger()));
                    Console.WriteLine("收到的握手响应：" + (string.IsNullOrEmpty(a.HandshakeDiag) ? "(未收到)" : a.HandshakeDiag));
                    if (string.IsNullOrEmpty(BroadcastSystemNet.VntLite.VntRsa.LocalFinger()))
                        Console.WriteLine("  └ 密钥不可用原因：" + BroadcastSystemNet.VntLite.VntRsa.LastError
                            + "；配置目录=" + BroadcastSystemNet.Program.ConfigDir);
                    Console.WriteLine($"注册结果：{(ok ? "成功，虚拟 IP=" + BroadcastSystemNet.VntLite.VntPacket.UintToIp(a.VirtualIp) : "失败")}"
                        + $"，状态={a.State}，收 {a.RecvPackets}/发 {a.SentPackets} 包");
                    foreach (var l in a.EncryptedSendLog) Console.WriteLine("加密出站：" + l);
                    // 把真实发出的包落盘，便于用 Java 原样重放判定"是我们的字节问题还是服务端不兼容"
                    try
                    {
                        string dumpDir = Path.Combine(Path.GetTempPath(), "cbs-wreplay");
                        Directory.CreateDirectory(dumpDir);
                        for (int pi = 0; pi < a.OutgoingPackets.Count; pi++)
                            File.WriteAllBytes(Path.Combine(dumpDir, $"p{pi + 1}.bin"), a.OutgoingPackets[pi]);
                        Console.WriteLine($"已落盘 {a.OutgoingPackets.Count} 个出站包到 {dumpDir}（可用 Java 重放）");
                    }
                    catch (Exception ex) { Console.WriteLine("落盘失败：" + ex.Message); }
                    foreach (var d in dumped) Console.WriteLine(d);
                    // 离线往返：拿服务端**真实私钥**解我们 C# 加出来的密文。
                    // 这一步能把问题一刀切开：解不开 = 我们加密侧的问题；解得开 = 服务端解密侧的问题。
                    try
                    {
                        string keyDir = ResolveVntsKeyDir();
                        string privPath = Path.Combine(keyDir, "private_key.pem");
                        string pubPath = Path.Combine(keyDir, "public_key.pem");
                        if (File.Exists(privPath) && File.Exists(pubPath))
                        {
                            using var priv = System.Security.Cryptography.RSA.Create();
                            priv.ImportFromPem(File.ReadAllText(privPath));
                            using var pub = System.Security.Cryptography.RSA.Create();
                            pub.ImportFromPem(File.ReadAllText(pubPath));
                            Console.WriteLine("本机读到的服务端公钥指纹：" + BroadcastSystemNet.VntLite.VntRsa.FingerOfSpki(pub.ExportSubjectPublicKeyInfo()));
                            Console.WriteLine($"服务端密钥长度：{priv.KeySize} 位（RSA 密文应为 {priv.KeySize / 8} 字节）");
                            var nonce = new byte[] { 0, 0, 0, 2, 0, 0, 0, 1, 1, 7, 1, 15 };
                            var payload = BroadcastSystemNet.VntLite.VntRsa.BuildSecretHandshakePayload(pub, tok, new byte[32], nonce);
                            bool opened = BroadcastSystemNet.VntLite.VntRsa.TryOpenSecretBody(priv, payload, nonce,
                                out byte[] msg, out string oerr);
                            Console.WriteLine($"离线往返（我方加密 → 服务端私钥解密）：{(opened ? "成功" : "失败：" + oerr)}"
                                + (opened ? $"，报文 {msg.Length} 字节，可解析={BroadcastSystemNet.VntLite.SecretHandshakeRequest.Parse(msg).Key.Length} 字节密钥" : ""));
                        }
                        else Console.WriteLine("离线往返：找不到本机测试服务端的密钥（third_party\\vnts-runtime\\key 或 %USERPROFILE%\\CampusBroadcastKeys\\vnts-key-本机测试服务端），跳过");
                    }
                    catch (Exception ex) { Console.WriteLine("离线往返异常：" + ex.Message); }

                    // 密钥一致性对照（最后一步定位）：
                    //   用服务端私钥解开我们发出的第二次握手(p2) → 得到"我们交给服务端的 32 字节 key"；
                    //   再分别用它解密 A) 服务端回的 SecretHandshakeResponse(pkt8)、B) 我们自己的 p3。
                    //   A 失败 = 服务端用的不是这个 key；B 失败 = 我们本地 AES 有问题。
                    try
                    {
                        string privPath2 = Path.Combine(ResolveVntsKeyDir(), "private_key.pem");
                        if (a.OutgoingPackets.Count >= 2 && File.Exists(privPath2))
                        {
                            using var priv2 = System.Security.Cryptography.RSA.Create();
                            priv2.ImportFromPem(File.ReadAllText(privPath2));
                            var p2 = a.OutgoingPackets[1];
                            var head2 = new byte[] { 0, 0, 0, 2, 0, 0, 0, 1, 1, 7, 1, 15 };
                            if (BroadcastSystemNet.VntLite.VntRsa.TryOpenSecretBody(priv2, p2.AsSpan(12).ToArray(),
                                    head2, out byte[] msg, out string oerr))
                            {
                                var shr = BroadcastSystemNet.VntLite.SecretHandshakeRequest.Parse(msg);
                                Console.WriteLine($"从 p2 解出我们交给服务端的 key：{shr.Key.Length} 字节，SHA256前16="
                                    + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(shr.Key))[..16]);
                                Console.WriteLine($"客户端自己说它用的 key SHA256前16="
                                    + (a.SecretHandshakeKeyHash.Length >= 16 ? a.SecretHandshakeKeyHash[..16] : "(空)"));
                                if (a.LastServerEncryptedPacket != null)
                                {
                                    var sp = a.LastServerEncryptedPacket;
                                    Console.WriteLine($"服务端加密响应包：{sp.Length} 字节，头={Convert.ToHexString(sp, 0, 12)}，载荷={sp.Length - 12}");
                                    if (BroadcastSystemNet.VntLite.VntPacket.TryParse(sp, out var spkt, out _))
                                    {
                                        bool ok8 = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(spkt.Payload, shr.Key,
                                            BroadcastSystemNet.VntLite.VntCipher.BuildNonce(spkt),
                                            BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true,
                                            out byte[] plain8, out string e8);
                                        Console.WriteLine("① 用该 key 解服务端 SecretHandshakeResponse(transport=" + spkt.Transport + ")："
                                            + (ok8 ? "成功" : "失败：" + e8));
                                        Console.WriteLine("解密细节：" + BroadcastSystemNet.VntLite.VntCipher.LastDecryptDiag);
                                        // 试几种 nonce 变体，判定服务端实际用的 12 字节
                                        var h = sp.AsSpan(0, 12).ToArray();
                                        var variants = new (string name, byte[] n)[]
                                        {
                                            ("源/目的原样", new byte[]{ h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],(byte)(((h[0]&0x40)!=0)?1:0),(byte)(h[3]>>4) }),
                                            ("源/目的互换", new byte[]{ h[8],h[9],h[10],h[11],h[4],h[5],h[6],h[7],h[1],h[2],(byte)(((h[0]&0x40)!=0)?1:0),(byte)(h[3]>>4) }),
                                            ("TTL用低4位", new byte[]{ h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],(byte)(((h[0]&0x40)!=0)?1:0),(byte)(h[3]&0x0F) }),
                                            ("网关位=1(强制)", new byte[]{ h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],1,(byte)(h[3]>>4) }),
                                            ("TTL=15(强制)", new byte[]{ h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],(byte)(((h[0]&0x40)!=0)?1:0),15 }),
                                            ("全 0 nonce", new byte[12])
                                        };
                                        foreach (var (vn, vnt) in variants)
                                        {
                                            bool vok = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(spkt.Payload, shr.Key,
                                                vnt, BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true, out _, out _);
                                            Console.WriteLine($"   nonce[{vn}] = {BitConverter.ToString(vnt)} → {(vok ? "成功(命中)" : "失败")}");
                                        }
                                        // 扫描 source_ttl 的 16 种取值，判定服务端加密时真正读到的值
                                        for (int t = 0; t < 16; t++)
                                        {
                                            var vn2 = new byte[] { h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],
                                                (byte)(((h[0]&0x40)!=0)?1:0), (byte)t };
                                            bool tok2 = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(spkt.Payload, shr.Key,
                                                vn2, BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true, out _, out _);
                                            if (tok2) Console.WriteLine($"   source_ttl={t} 时解密成功(命中)（头里是 {h[3] >> 4}）");
                                        }
                                        Console.WriteLine("   source_ttl 扫描完成（没有“命中”行说明 0-15 都不匹配）");
                                        // 再试 nonce 第 12 字节直接取 h[3] 原值（0x0F）——之前漏掉的一种
                                        foreach (var (dn, dv) in new (string, byte)[] { ("h[3]原值", h[3]), ("h[3]低4位", (byte)(h[3]&0x0F)), ("网关位+ttl=h[3]", h[3]) })
                                        {
                                            var vn3 = new byte[] { h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],
                                                (byte)(((h[0]&0x40)!=0)?1:0), dv };
                                            bool t3 = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(spkt.Payload, shr.Key,
                                                vn3, BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true, out _, out _);
                                            Console.WriteLine($"   nonce[{dn}={dv:X2}] = {BitConverter.ToString(vn3)} → {(t3 ? "成功(命中)" : "失败")}");
                                        }
                                        var nonceStd = new byte[]{ h[4],h[5],h[6],h[7],h[8],h[9],h[10],h[11],h[1],h[2],(byte)(((h[0]&0x40)!=0)?1:0),(byte)(h[3]>>4) };
                                        // 再试几种"密钥候选"，判定服务端响应用的是不是别的东西
                                        var keyCands = new (string name, byte[] k)[]
                                        {
                                            ("发送的 key(对照)", shr.Key),
                                            ("SHA256(token)", System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tok))),
                                            ("SHA256(key)", System.Security.Cryptography.SHA256.HashData(shr.Key)),
                                            ("空 key 32 字节", new byte[32])
                                        };
                                        foreach (var (kn, kk) in keyCands)
                                        {
                                            bool kok = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(spkt.Payload, kk,
                                                nonceStd, BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true, out _, out _);
                                            Console.WriteLine($"   key[{kn}] → {(kok ? "成功(命中)" : "失败")}");
                                        }
                                        // 自算 tag：空明文 + 同一 key/nonce 下 GCM 的 tag 应该等于包里前 16 字节
                                        try
                                        {
                                            using var gcm = new System.Security.Cryptography.AesGcm(shr.Key, 16);
                                            var tagOut = new byte[16];
                                            gcm.Encrypt(nonceStd, Array.Empty<byte>(), Array.Empty<byte>(), tagOut, null);
                                            var wireTag = spkt.Payload.AsSpan(0, 16).ToArray();
                                            Console.WriteLine($"B) 自算空明文 tag={Convert.ToHexString(tagOut)}");
                                            Console.WriteLine($"   包里前 16 字节 ={Convert.ToHexString(wireTag)} → {(tagOut.AsSpan().SequenceEqual(wireTag) ? "一致" : "不一致")}");
                                        }
                                        catch (Exception ex) { Console.WriteLine("B) 自算 tag 异常：" + ex.Message); }
                                    }
                                }
                                else Console.WriteLine("① 没有抓到服务端加密响应包");
                                if (a.OutgoingPackets.Count >= 3)
                                {
                                    var p3 = a.OutgoingPackets[2];
                                    if (BroadcastSystemNet.VntLite.VntPacket.TryParse(p3, out var pkt3, out _))
                                    {
                                        bool ok3 = BroadcastSystemNet.VntLite.VntCipher.TryDecrypt(pkt3.Payload, shr.Key,
                                            BroadcastSystemNet.VntLite.VntCipher.BuildNonce(pkt3),
                                            BroadcastSystemNet.VntLite.VntCipher.FingerHash(tok), true,
                                            out _, out string e3);
                                        Console.WriteLine("② 用该 key 解我们自己的 p3(注册包)：" + (ok3 ? "成功" : "失败：" + e3));
                                    }
                                }
                            }
                            else Console.WriteLine("解 p2 失败：" + oerr);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine("密钥一致性对照异常：" + ex.Message); }
                    if (!string.IsNullOrEmpty(a.LastError)) Console.WriteLine("最近错误：" + a.LastError);
                    Console.WriteLine(ok ? "服务端加密(-W)握手 + 注册：成功" : "服务端加密(-W)握手 + 注册：失败");
                    a.Stop();
                    Environment.ExitCode = ok ? 0 : 4;
                    Console.WriteLine("PHASE-DONE");
                }
                catch (Exception ex) { Console.WriteLine("wtest 异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // cbs-api 桥探针：不发真实 UDP，直接验证"请求 JSON → 本机 HTTP 转发 → 响应 JSON"，
            //   以及参数校验/拒绝规则。本探针会自己起网页服务（StartWebServer）。
            //   用法：--mic-api-bridge-probe [网页端口]
            if (args != null && Array.IndexOf(args, "--mic-api-bridge-probe") >= 0)
            {
                int rc;
                try { rc = MicApiBridgeProbe(args); }
                catch (Exception ex) { Console.WriteLine("cbs-api 桥探针异常：" + ex.Message); rc = 5; }
                Environment.ExitCode = rc;
                Console.WriteLine("PHASE-DONE");
                return;
            }
            // Ogg 直通探针：--mic-ogg-probe <ogg文件>  验证"收到 Ogg-Opus 页 → 原样喂 ffplay → 出声"
            if (args != null && Array.IndexOf(args, "--mic-ogg-probe") >= 0)
            {
                try
                {
                    try { PlayLog.Start(LogDir); } catch { }
                    int i = Array.IndexOf(args, "--mic-ogg-probe");
                    string ogg = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "";
                    if (!System.IO.File.Exists(ogg)) { Console.WriteLine("找不到 Ogg 文件：" + ogg); Environment.ExitCode = 6; return; }
                    MicPipePlayer.OggMode = true;
                    if (!MicPipePlayer.Start(out string perr)) { Console.WriteLine("启动失败：" + perr); Environment.ExitCode = 3; return; }
                    var bytes = System.IO.File.ReadAllBytes(ogg);
                    int chunk = 512;                                     // 模拟 UDP 一包一页地推
                    for (int off = 0; off < bytes.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, bytes.Length - off);
                        var page = new byte[len];
                        Array.Copy(bytes, off, page, 0, len);
                        MicPipePlayer.WriteOgg(page);
                        System.Threading.Thread.Sleep(8);
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1500 && MicPipePlayer.OggBytes == 0) System.Threading.Thread.Sleep(20);
                    Console.WriteLine("写入 ffplay 的 Ogg 字节数：" + MicPipePlayer.OggBytes + " / 源文件 " + bytes.Length);
                    Console.WriteLine("播放状态：" + MicPipePlayer.StatusText());
                    System.Threading.Thread.Sleep(600);
                    MicPipePlayer.Stop();
                    Console.WriteLine("已停止；清空后状态：" + MicPipePlayer.StatusText());
                    Environment.ExitCode = MicPipePlayer.OggBytes > 0 ? 0 : 4;
                    Console.WriteLine("PHASE-DONE");
                }
                catch (Exception ex) { Console.WriteLine("Ogg 探针异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // 话筒管道播放探针（独立进程）：起 ffplay 管道 → 喂 500ms 正弦 → 看写出字节 → 正常停止
            if (args != null && Array.IndexOf(args, "--mic-pipe-probe") >= 0)
            {
                try
                {
                    try { PlayLog.Start(LogDir); } catch { }
                    Console.WriteLine("播放状态（起始）：" + MicPipePlayer.StatusText());
                    if (!MicPipePlayer.Start(out string perr)) { Console.WriteLine("启动失败：" + perr); Environment.ExitCode = 3; return; }
                    var sine = new short[MicAudio.SampleRate / 2];              // 500ms
                    for (int i = 0; i < sine.Length; i++) sine[i] = (short)(Math.Sin(i / 15.0) * 3000);
                    MicPipePlayer.Ring.Write(sine);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2500 && MicPipePlayer.BytesWritten == 0) System.Threading.Thread.Sleep(30);
                    Console.WriteLine("已写入 ffplay 的字节数：" + MicPipePlayer.BytesWritten);
                    Console.WriteLine("播放状态（运行中）：" + MicPipePlayer.StatusText());
                    MicPipePlayer.Stop();
                    Console.WriteLine("播放状态（已停止）：" + MicPipePlayer.StatusText());
                    Environment.ExitCode = MicPipePlayer.BytesWritten > 0 ? 0 : 4;
                    Console.WriteLine("PHASE-DONE");
                }
                catch (Exception ex) { Console.WriteLine("管道探针异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // 话筒播放探针（独立进程跑，崩了也不影响自检）：真机起流 → 喂 300ms 正弦 → 看是否送出声卡 → 正常停止
            if (args != null && Array.IndexOf(args, "--mic-play-probe") >= 0)
            {
                try
                {
                    Console.WriteLine("播放状态（起始）：" + MicRender.StatusText());
                    // 探针走的是"没启动主流程"的路径，日志器还没初始化过；MicRender.Start 会写日志，
                    // 这里必须先把它起起来（否则写日志本身就会 AV 崩掉，看起来像播放崩了）。
                    try { PlayLog.Start(LogDir); } catch { }
                    if (!MicRender.Start(out string perr)) { Console.WriteLine("启动失败：" + perr); Environment.ExitCode = 3; return; }
                    var sine = new short[MicAudio.SampleRate / 10 * 3];
                    for (int i = 0; i < sine.Length; i++) sine[i] = (short)(Math.Sin(i / 15.0) * 4000);
                    MicRender.Ring.Write(sine);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1500 && MicRender.SamplesRendered == 0) System.Threading.Thread.Sleep(20);
                    Console.WriteLine("已送出声卡样本数：" + MicRender.SamplesRendered);
                    Console.WriteLine("播放状态（运行中）：" + MicRender.StatusText());
                    MicRender.Stop();
                    Console.WriteLine("播放状态（已停止）：" + MicRender.StatusText() + "，线程已退出=" + !MicRender.Running);
                    Environment.ExitCode = MicRender.SamplesRendered > 0 ? 0 : 4;
                    Console.WriteLine("PHASE-DONE");     // 标记：能走到这里说明 Stop() 没有释放后使用
                }
                catch (Exception ex) { Console.WriteLine("播放探针异常：" + ex.Message); Environment.ExitCode = 5; }
                return;
            }
            // 话筒相关窗口预览：--mic-preview vnt|mic <输出.png> [宽度]
            if (args != null && Array.IndexOf(args, "--mic-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--mic-preview");
                    string which = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "vnt";
                    string outPath = (i + 2 < args.Length && !args[i + 2].StartsWith("--")) ? args[i + 2] : null;
                    int width = 620;
                    if (i + 3 < args.Length) int.TryParse(args[i + 3], out width);
                    Console.WriteLine("话筒窗口预览已生成：" + Desktop.MicPreview.Render(which, outPath, width));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成话筒窗口预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 关于窗版式预览：离屏渲染成 PNG，方便核对对齐（不进屏、不联网、不改数据）
            if (args != null && Array.IndexOf(args, "--about-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--about-preview");
                    string outPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
                    int width = 520;
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out width);
                    Console.WriteLine("关于窗预览已生成：" + Desktop.AboutDialog.RenderPreview(outPath, width));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成关于窗预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 任务编辑窗版式预览：离屏把「新增/编辑任务排程」渲染成 PNG，方便核对控件对齐（不进屏、不联网、不改数据）
            if (args != null && Array.IndexOf(args, "--task-editor-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--task-editor-preview");
                    string outPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
                    int width = 660;
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out width);
                    int height = 0;   // 第 3 个参数：受限高度（模拟高缩放/矮屏幕），0 = 按内容自然高度
                    if (i + 3 < args.Length) int.TryParse(args[i + 3], out height);
                    Console.WriteLine("任务编辑窗预览已生成：" + Desktop.TaskEditorWindow.RenderPreview(outPath, width, height));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成任务编辑窗预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 停止探针：起一个真实 ffplay 播放 1.5 秒，然后立即结束它，打印耗时与进程是否真的死了。
            // 历史：这里原来叫 --fade-probe，用来验证"渐弱"这套 COM 互操作；渐弱已于 2026-10-15 移除，
            // 探针保留下来继续验证"停止播放确实能立刻结束进程"（参数名不变，免得旧脚本失效）。
            if (args != null && Array.IndexOf(args, "--fade-probe") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--fade-probe");
                    string file = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        ? args[i + 1]
                        : Path.Combine(ResDir, "ring", "03 下课铃.mp3");
                    Console.WriteLine($"停止探针：文件 {file}");
                    Console.WriteLine("渐弱已移除（2026-10-15）：停止播放改为直接结束进程，不再碰会话音量");
                    var psi = new ProcessStartInfo
                    {
                        FileName = Path.Combine(ResDir, "bin", "ffplay.exe"),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (var a in new[] { "-nodisp", "-autoexit", "-af", "volume=0.06", file }) psi.ArgumentList.Add(a);
                    var proc = Process.Start(psi);
                    System.Threading.Thread.Sleep(1500);
                    bool running = proc != null && !proc.HasExited;
                    var pids = AudioFade.ListSessionPids(out string detail);
                    Console.WriteLine("音频会话枚举：" + detail);
                    Console.WriteLine("ffplay PID = " + (proc?.Id ?? -1) + "；会话 PID 列表里是否包含它：" + (proc != null && pids.Contains(proc.Id)));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = AudioFade.StopNow(proc);
                    sw.Stop();
                    bool dead = proc == null || proc.HasExited;
                    Console.WriteLine($"播放中：{running}；已结束进程：{ok}；耗时 {sw.ElapsedMilliseconds} 毫秒；进程已结束：{dead}");
                    Environment.ExitCode = dead ? 0 : 3;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("渐弱探针失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 悬浮球扇形倒计时预览：把不同剩余时间的球并排画成一张图，方便人工核对扇形长度（不启动界面、不联网、不改数据）
            if (args != null && Array.IndexOf(args, "--ball-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--ball-preview");
                    string outPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
                    Console.WriteLine("悬浮球预览已生成：" + Desktop.FloatingBall.WriteStatePreview(outPath));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成悬浮球预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 预警播报页版式预览：无界面状态下把页面渲染成 PNG，方便人工核对布局（不进屏、不联网、不改数据）
            if (args != null && Array.IndexOf(args, "--warn-page-preview") >= 0)
            {
                try
                {
                    int i = Array.IndexOf(args, "--warn-page-preview");
                    string outPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : null;
                    int width = 1000;
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out width);
                    Console.WriteLine("预警播报页预览已生成：" + Desktop.WarnPage.RenderPreview(outPath, width));
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("生成预警播报页预览失败：" + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }
            // 启动期兜底：无论在哪一步失败，都要在磁盘上留下可追溯的痕迹（历史上曾出现"双击后静默退出、无任何日志"）
            try { MainCore(args); }
            catch (Exception ex) { TryWriteStartupCrash(ex); throw; }
            finally { try { _singleInstanceMutex?.Dispose(); } catch { } }
        }

        /// <summary>
        /// UDP 配对通道探针（《话筒功能接口文档.md》§15.3 验收）：在回环上真起话筒服务，
        /// 用 "cbs-pair:" + AES-GCM 帧走一次**完整配对**，并顺手验证三件事：
        ///   1) 错误密码的帧被静默丢弃（不回包）；2) 明文/坏帧被拒；3) 配对成功后两端派生同一把会话密钥。
        /// 全程只用回环、只写临时目录（结束即删），不联网、不动真实数据。
        /// </summary>
        static int MicPairUdpProbe(string[] args)
        {
            int i = Array.IndexOf(args, "--mic-pair-udp-probe");
            string password = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "udp-pair-probe-pw";
            string code = (i + 2 < args.Length && !args[i + 2].StartsWith("--")) ? args[i + 2] : "";
            int port = 29895; if (i + 3 < args.Length) int.TryParse(args[i + 3], out port);
            int audioPort = port + 1;
            string tmpRoot = Path.Combine(Path.GetTempPath(), "CBS_PairUdpProbe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var oldData = AppDataDir; var oldCfg = ConfigDir; var oldLog = LogDir; var oldErr = ErrorLogPath;
            try
            {
                // 只写临时目录：探针的日志与配置绝不落到真实 %LOCALAPPDATA%
                AppDataDir = tmpRoot;
                ConfigDir = Path.Combine(tmpRoot, "config");
                LogDir = Path.Combine(tmpRoot, "logs");
                ErrorLogPath = Path.Combine(tmpRoot, "system_error.log");
                Directory.CreateDirectory(ConfigDir);
                Directory.CreateDirectory(LogDir);
                try { PlayLog.Start(LogDir); } catch { }

                string seed = "pair-udp-probe-seed";
                VntManager.DataPassword = password;
                VntManager.MicEnabled = true;
                // 注意：本进程会自己起一个话筒服务（seed = 上面这个）。当第 2 个参数给了动态码时，
                // 说明是打**外部**守候服务端（--mic-pair-udp-serve），那个服务端的 seed 是它自己的，
                // 所以这里必须**原样使用调用方给的码**，不能再用本进程的 seed 去重算（否则必然 bad_code）。
                bool isLocalPairing = string.IsNullOrWhiteSpace(code);
                if (isLocalPairing)
                {
                    VntManager.Set("mic", "code_seed", System.Text.Json.Nodes.JsonValue.Create(seed));
                }
                string nowCode = isLocalPairing ? MicProtocol.CurrentCode(seed, DateTime.UtcNow) : code.Trim();
                Console.WriteLine("配对目标：" + (isLocalPairing ? "本进程自带的回环服务" : $"外部服务端（使用给定动态码 {nowCode}）"));

                // 密钥派生：两端都必须是 SHA256(数据密码)，长度 32
                var key = MicPairUdp.DeriveKey(password);
                Console.WriteLine("① 配对密钥 = SHA256(数据密码)：长度 " + key.Length + " 字节"
                    + (key.SequenceEqual(MicPairUdp.DeriveKey(password)) ? "（同密码可复现）" : "（不一致）"));
                bool key32 = key.Length == 32;
                bool emptyKeyRefused;
                try { MicPairUdp.Build("{}", MicPairUdp.DeriveKey("")); emptyKeyRefused = false; }
                catch { emptyKeyRefused = true; }
                Console.WriteLine("2) 没配数据密码时不生成帧（拒绝用空密码）：" + (emptyKeyRefused ? "通过" : "失败"));

                if (!MicServer.Start("127.0.0.1", audioPort, port, seed, out string startErr))
                {
                    Console.WriteLine("话筒服务启动失败：" + startErr);
                    return 3;
                }
                int wait = 0;
                while (!MicServer.Running && wait < 1500) { System.Threading.Thread.Sleep(20); wait += 20; }
                Console.WriteLine($"③ 话筒服务已启动：音频 UDP 127.0.0.1:{audioPort}，配对 TCP/UDP 127.0.0.1:{port}，UDP 配对通道="
                    + (MicServer.PairUdpReady ? "已就绪" : "未启用"));

                // ④ 错误密码：服务端应静默丢弃（不回包）
                var wrongKeyFrame = MicPairUdp.Build("{\"t\":\"cbs-mic\"}", MicPairUdp.DeriveKey(password + "-wrong"));
                bool wrongReplied = PairUdpExchange(wrongKeyFrame, port, 500, out _);
                Console.WriteLine("④ 错误密码的帧：收到回复=" + wrongReplied + "（应为 False，静默丢弃）"
                    + (wrongReplied ? "（错误密码竟然收到回复）" : "（错误密码被静默丢弃）"));

                // ⑤ 真配对：HELLO → AUTH_OK（都是加密帧），并核对两端会话密钥一致
                var kp = MicProtocol.NewKeyPair();
                var nc = MicProtocol.NewNonce();
                string deviceId = "probe-phone-01";
                string hello = MicPairingAuthHello(nowCode, deviceId, kp.PublicKey, nc);
                var frame = MicPairUdp.Build(hello, key);
                bool replied = PairUdpExchange(frame, port, 2000, out byte[] replyFrame);
                int rcOpen = 0;
                if (!replied) { Console.WriteLine("⑤ 配对失败：没有收到 UDP 回复"); rcOpen = 4; }
                else
                {
                    var replyLine = MicPairUdp.TryUnwrap(replyFrame, key, out string unwrapErr);
                    if (replyLine == null) { Console.WriteLine("⑤ 回复帧解不开：" + unwrapErr); rcOpen = 4; }
                    else
                    {
                        var obj = JsonNode.Parse(replyLine) as JsonObject;
                        bool ok = obj?["ok"]?.GetValue<bool>() ?? false;
                        uint sid = obj?["sid"]?.GetValue<uint>() ?? 0;
                        var ps = Convert.FromBase64String(obj?["ps"]?.ToString() ?? "");
                        var ns = Convert.FromBase64String(obj?["ns"]?.ToString() ?? "");
                        Console.WriteLine("   [手机端收到] " + replyLine);
                        if (!ok) { Console.WriteLine("⑤ 服务端拒绝配对，reason=" + obj?["reason"]); rcOpen = 4; }
                        else
                        {
                            var cliKey = MicPairing.ClientDeriveKey(kp.PrivateKey, ps, nowCode, nc, ns);
                            var sess = MicServer.Registry.Find(sid);
                            bool same = sess != null && sess.Key != null && cliKey.Length == 32 && cliKey.SequenceEqual(sess.Key);
                            Console.WriteLine("   [电脑端日志] 配对成功：设备 " + (sess?.DeviceId ?? "?") + "（来源 " + (sess?.SourceIp ?? "?")
                                + "）会话 " + sid);
                            Console.WriteLine("⑤ 配对成功：sid=" + sid + "，两端会话密钥一致=" + same
                                + "（客户端 " + cliKey.Length + " 字节 / 服务端 " + (sess?.Key?.Length ?? 0) + " 字节）");
                            if (!same) rcOpen = 4;
                        }
                    }
                }

                // 6) 合法密钥但明文/垃圾载荷：服务端必须拒绝并且**不回包**（不给探测者反馈）
                bool junkReplied = PairUdpExchange(System.Text.Encoding.UTF8.GetBytes("cbs-pair:0123456789abcdef"), port, 500, out _);
                Console.WriteLine("6) 伪造/截断的 cbs-pair 帧：收到回复=" + junkReplied + "（应为 False）"
                    + (junkReplied ? "（不应有回复）" : "（已按预期拒绝）"));

                Console.WriteLine("统计：收到 " + MicServer.PairUdpReceived + " 帧 / 回包 " + MicServer.PairUdpReplied
                    + " / 拒绝 " + MicServer.PairUdpRejected + "；" + MicServer.Stats.Summary());
                bool pass = key32 && emptyKeyRefused && MicServer.PairUdpReady && !wrongReplied && rcOpen == 0 && !junkReplied;
                Console.WriteLine(pass ? "UDP 配对通道探针：全部通过" : "UDP 配对通道探针：存在失败项");
                return pass ? 0 : 4;
            }
            finally
            {
                try { MicServer.Stop(); } catch { }
                AppDataDir = oldData; ConfigDir = oldCfg; LogDir = oldLog; ErrorLogPath = oldErr;
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }
        }

        /// <summary>
        /// 配对守候模式（真机/模拟器联调用）：起话筒服务 → 实时打印动态码与配对结果 → 守候若干秒。
        /// 与 `--mic-pair-udp-probe`（自问自答式回归）的区别：本模式**只被动等待手机来配对**，
        /// 因此可以用模拟器/真机把"手机 App → 电脑端"这一跳真实验证一遍。
        ///   用法：--mic-pair-udp-serve [数据密码@绑定地址:ctl端口/动态码或seed/守候秒数]
        ///   例：  --mic-pair-udp-serve mumu-pw@0.0.0.0:29881/246810/120
        ///   也兼容旧写法：--mic-pair-udp-serve 绑定地址 ctl端口 code_seed 秒数（此时数据密码取自环境变量 CBS_PAIR_PW）
        /// 第 3 段若是 6 位纯数字 → 当作**固定动态码**（方便脚本/自动化输入）；否则按 code_seed 每分钟轮换。
        /// </summary>
        static int MicPairUdpServe(string[] args)
        {
            int i = Array.IndexOf(args, "--mic-pair-udp-serve");
            string password = "";
            string bind = "0.0.0.0";
            int ctl = 29881;
            string seed = "serve-seed";
            int seconds = 180;

            string first = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "";
            if (first.Contains("@"))
            {
                // 紧凑写法：密码@地址:端口/码/秒数
                int at = first.IndexOf('@');
                password = first.Substring(0, at);
                string rest = first.Substring(at + 1);
                string[] parts = rest.Split('/');
                string hostPort = parts[0];
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) seed = parts[1].Trim();
                if (parts.Length > 2 && int.TryParse(parts[2], out int sec2)) seconds = sec2;
                int colon = hostPort.LastIndexOf(':');
                if (colon > 0)
                {
                    bind = hostPort.Substring(0, colon);
                    if (int.TryParse(hostPort.Substring(colon + 1), out int cp)) ctl = cp;
                }
                else bind = hostPort;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(first)) bind = first;
                if (i + 2 < args.Length && int.TryParse(args[i + 2], out int c1)) ctl = c1;
                if (i + 3 < args.Length && !args[i + 3].StartsWith("--")) seed = args[i + 3];
                if (i + 4 < args.Length && int.TryParse(args[i + 4], out int s1)) seconds = s1;
                password = Environment.GetEnvironmentVariable("CBS_PAIR_PW") ?? "";
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                Console.WriteLine("缺少【数据密码】：用紧凑写法 --mic-pair-udp-serve 密码@0.0.0.0:29881/246810/120，"
                    + "或设置环境变量 CBS_PAIR_PW（UDP 配对通道的加密钥匙，手机端要填同一个）");
                return 3;
            }
            if (string.IsNullOrWhiteSpace(bind)) bind = "0.0.0.0";
            string tmpRoot = Path.Combine(Path.GetTempPath(), "CBS_PairServe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var oldData = AppDataDir; var oldCfg = ConfigDir; var oldLog = LogDir; var oldErr = ErrorLogPath;
            try
            {
                AppDataDir = tmpRoot; ConfigDir = Path.Combine(tmpRoot, "config"); LogDir = Path.Combine(tmpRoot, "logs");
                ErrorLogPath = Path.Combine(tmpRoot, "system_error.log");
                Directory.CreateDirectory(ConfigDir); Directory.CreateDirectory(LogDir);
                try { PlayLog.Start(LogDir); } catch { }

                VntManager.DataPassword = password;
                VntManager.MicEnabled = true;
                // 自动化联调（真机/模拟器）需要"一个不会变的码"：第 3 个参数若给的是 6 位数字，
                // 就当成**固定动态码**使用（方便脚本输入），否则按 code_seed 正常轮换。
                bool fixedCode = seed.Length == 6 && seed.All(char.IsDigit);
                var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
                MicServer.OnPairResult = (device, from, ok, reason, sid) =>
                    events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + (ok
                        ? $"  配对成功：设备 {device}（来源 {from}）会话 {sid}"
                        : $"  配对失败：设备 {device}（来源 {from}）原因 {reason}"));
                if (!MicServer.Start(bind, ctl + 100, ctl, seed, out string err))
                {
                    Console.WriteLine("话筒服务启动失败：" + err);
                    return 4;
                }
                if (fixedCode)
                {
                    // 必须在 Start **之后**设置：Start 内部会用 code_seed 重建码仓库，先设会被覆盖。
                    MicServer.Codes = new MicSecurity.CodeStore(seed);
                    MicServer.Guard = new MicSecurity.PairingGuard();
                }
                Console.WriteLine($"配对守候中：绑定 {bind}，ctl_port={ctl}，数据密码长度 {password.Length}，UDP 配对="
                    + (MicServer.PairUdpReady ? "已就绪" : "未启用"));
                Console.WriteLine("手机端填：对端虚拟 IP = 本机地址（模拟器可用 10.0.2.2 或 adb reverse 后的 127.0.0.1），配对端口 = " + ctl);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string lastCode = "";
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    string code = fixedCode ? seed : MicProtocol.CurrentCode(seed, DateTime.UtcNow);
                    if (code != lastCode)
                    {
                        lastCode = code;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 当前 6 位动态码：{code}"
                            + (fixedCode ? "（固定码模式，不轮换）" : "（剩余 "
                                + (MicProtocol.CodePeriodSeconds - DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % MicProtocol.CodePeriodSeconds) + " 秒）"));
                    }
                    while (events.TryDequeue(out string line)) Console.WriteLine(line);
                    System.Threading.Thread.Sleep(300);
                }
                Console.WriteLine("守候结束。收帧 " + MicServer.PairUdpReceived + " / 回包 " + MicServer.PairUdpReplied
                    + " / 拒绝 " + MicServer.PairUdpRejected + "；会话数 " + MicServer.Registry.Count);
                foreach (var s in MicServer.Registry.All)
                    Console.WriteLine($"  活动会话 sid={s.Sid} 设备={s.DeviceId} 允许来源={s.AllowedSourcesText} 已收音频包={s.Accepted}");
                return MicServer.Registry.Count > 0 ? 0 : 6;
            }
            finally
            {
                try { MicServer.OnPairResult = null; } catch { }
                try { MicServer.Stop(); } catch { }
                AppDataDir = oldData; ConfigDir = oldCfg; LogDir = oldLog; ErrorLogPath = oldErr;
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }
        }

        /// <summary>
        /// cbs-api 桥探针：验证"手机发来的请求 JSON → 电脑端转发到本机 HTTP 接口 → 结构化响应 JSON"。
        ///
        /// 为什么要有它：这条桥是手机端"播控/排程/调休"三个页面的数据来源，一旦坏了表现为"页面一直空"，
        /// 很难从界面上看出是哪一环。本探针把每一环单独打出来（校验规则、转发、响应结构、错误路径），
        /// 不需要手机、不需要配对即可回归。
        /// </summary>
        static int MicApiBridgeProbe(string[] args)
        {
            int i = Array.IndexOf(args, "--mic-api-bridge-probe");
            int port = 5000; if (i + 1 < args.Length) int.TryParse(args[i + 1], out port);
            string tmpRoot = Path.Combine(Path.GetTempPath(), "CBS_ApiBridge_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var oldData = AppDataDir; var oldCfg = ConfigDir; var oldLog = LogDir; var oldErr = ErrorLogPath;
            int pass = 0, fail = 0;
            void Check(bool ok, string name, string detail = "")
            {
                if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
                else { fail++; Console.WriteLine("  [FAIL] " + name + "  " + detail); }
            }
            try
            {
                AppDataDir = tmpRoot; ConfigDir = Path.Combine(tmpRoot, "config"); LogDir = Path.Combine(tmpRoot, "logs");
                ErrorLogPath = Path.Combine(tmpRoot, "system_error.log");
                Directory.CreateDirectory(ConfigDir); Directory.CreateDirectory(LogDir);
                try { PlayLog.Start(LogDir); } catch { }

                ListenPort = port;
                var web = Task.Run(() => { try { StartWebServer(new string[0]); } catch { } });
                System.Threading.Thread.Sleep(1500);   // 等 Kestrel 起监听
                Console.WriteLine($"cbs-api 桥探针：网页端口 {port}，本机回环转发");

                // ① 正常路径：/api/sys（免鉴权接口，永远可用）
                string req = new System.Text.Json.Nodes.JsonObject
                {
                    ["v"] = MicApiBridge.ApiVersion,
                    ["id"] = "probe-1",
                    ["m"] = "GET",
                    ["p"] = "/api/sys"
                }.ToJsonString();
                string resp = MicApiBridge.Handle(req);
                var o = System.Text.Json.Nodes.JsonNode.Parse(resp) as System.Text.Json.Nodes.JsonObject;
                string data = o?["d"]?.ToString() ?? "";
                int status = 0; try { status = o?["s"]?.GetValue<int>() ?? 0; } catch { }
                Check(o != null && string.Equals(o["id"]?.ToString(), "probe-1"), "响应带回请求 id（手机端据此配对请求/响应）", resp.Substring(0, Math.Min(120, resp.Length)));
                Check(status == 200 && data.Length > 0, "GET /api/sys 转发成功（状态 " + status + "，正文 " + data.Length + " 字节）", data.Substring(0, Math.Min(120, data.Length)));
                Check(data.TrimStart().StartsWith("{"), "/api/sys 返回的是 JSON", data.Substring(0, Math.Min(60, data.Length)));

                // 2) 另一个常用接口：/api/tasks
                string resp2 = MicApiBridge.Handle(new System.Text.Json.Nodes.JsonObject
                { ["v"] = 1, ["id"] = "probe-2", ["m"] = "GET", ["p"] = "/api/tasks" }.ToJsonString());
                var o2 = System.Text.Json.Nodes.JsonNode.Parse(resp2) as System.Text.Json.Nodes.JsonObject;
                Check((o2?["s"]?.GetValue<int>() ?? 0) == 200, "GET /api/tasks 转发成功", resp2.Substring(0, Math.Min(120, resp2.Length)));

                // ③ 拒绝规则：不允许的路径/方法/绝对地址/穿越
                foreach (var (m, p, why) in new[]
                {
                    ("GET", "/etc/passwd", "非 /api/ 前缀"),
                    ("GET", "http://127.0.0.1:5000/api/sys", "绝对地址"),
                    ("GET", "/api/../../windows/win.ini", "路径穿越"),
                    ("DELETE", "/api/tasks", "不允许的方法"),
                    ("GET", "//127.0.0.1/api/sys", "双斜杠")
                })
                {
                    string r = MicApiBridge.Handle(new System.Text.Json.Nodes.JsonObject
                    { ["v"] = 1, ["id"] = "deny", ["m"] = m, ["p"] = p }.ToJsonString());
                    var od = System.Text.Json.Nodes.JsonNode.Parse(r) as System.Text.Json.Nodes.JsonObject;
                    bool denied = od?["e"] != null;
                    Check(denied, "拒绝：" + why + "（" + m + " " + p + "）", r.Substring(0, Math.Min(100, r.Length)));
                }

                // ④ 坏请求：版本不符 / 非 JSON / 空
                var ob = System.Text.Json.Nodes.JsonNode.Parse(MicApiBridge.Handle("{\"v\":99,\"m\":\"GET\",\"p\":\"/api/sys\"}")) as System.Text.Json.Nodes.JsonObject;
                Check(ob?["e"] != null, "协议版本不符被拒绝");
                var ob2 = System.Text.Json.Nodes.JsonNode.Parse(MicApiBridge.Handle("not-json")) as System.Text.Json.Nodes.JsonObject;
                Check(ob2?["e"] != null, "非 JSON 请求被拒绝");
                var ob3 = System.Text.Json.Nodes.JsonNode.Parse(MicApiBridge.Handle("")) as System.Text.Json.Nodes.JsonObject;
                Check(ob3?["e"] != null, "空请求被拒绝");

                Console.WriteLine("统计：" + MicApiBridge.Summary());
                Console.WriteLine((fail == 0 ? "cbs-api 桥探针：全部通过" : "cbs-api 桥探针：存在失败项") + $"（通过 {pass} / 失败 {fail}）");
                return fail == 0 ? 0 : 4;
            }
            finally
            {
                AppDataDir = oldData; ConfigDir = oldCfg; LogDir = oldLog; ErrorLogPath = oldErr;
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }
        }

        /// <summary>构造一行探针用 HELLO（与安卓端 MicPairing.buildHello 字段完全一致）。</summary>
        static string MicPairingAuthHello(string code, string deviceId, byte[] pk, byte[] nc)
            => "{\"t\":\"cbs-mic\",\"v\":1,\"code\":\"" + code + "\",\"device_id\":\"" + deviceId
               + "\",\"pk\":\"" + Convert.ToBase64String(pk) + "\",\"nc\":\"" + Convert.ToBase64String(nc) + "\"}";

        /// <summary>向配对 UDP 口发一帧并等回复（探针用；超时/无回复返回 false）。</summary>
        static bool PairUdpExchange(byte[] frame, int port, int timeoutMs, out byte[] reply)
        {
            reply = null;
            try
            {
                using var sock = new System.Net.Sockets.UdpClient(0);
                sock.Client.ReceiveTimeout = timeoutMs;
                sock.Send(frame, frame.Length, "127.0.0.1", port);
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                reply = sock.Receive(ref ep);
                return reply != null && reply.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>把启动期异常写到启动目录与 AppData，作为最后一道可追溯保障。</summary>
        static void TryWriteStartupCrash(Exception ex)
        {
            try
            {
                string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动失败: {ex}\r\n";
                try { File.AppendAllText(Path.Combine(BaseDir, "startup_crash.log"), msg); } catch { }
                try { Directory.CreateDirectory(AppDataDir); File.AppendAllText(Path.Combine(AppDataDir, "startup_crash.log"), msg); } catch { }
            }
            catch { }
        }

        static void MainCore(string[] args)
        {
            // ===== 单实例检测：已有实例在运行时直接提示并退出，避免两个实例抢同一份配置与音频设备 =====
            // 说明：Global\ 命名互斥体需要特权，非管理员运行时可能抛异常；失败则退回 Local 互斥体，
            // 绝不因为"检测单实例"这件事本身把整个程序搞崩（历史教训：异常发生在日志初始化之前，表现为静默退出）。
            string mutexName = Environment.GetEnvironmentVariable("CBS_INSTANCE_NAME");
            if (string.IsNullOrWhiteSpace(mutexName)) mutexName = "Global\\CampusBroadcastSystem_SingleInstance_9F2A";
            bool createdNew = false;
            bool mutexOk = true;
            try
            {
                _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            }
            catch
            {
                try
                {
                    string fallback = mutexName.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase)
                        ? mutexName.Substring("Global\\".Length) : mutexName;
                    _singleInstanceMutex = new Mutex(true, fallback, out createdNew);
                }
                catch { mutexOk = false; createdNew = true; }
            }
            if (mutexOk && !createdNew)
            {
                // CBS_ALLOW_MULTI=1 用于并排调试/自动化测试：跳过单实例拦截（不弹窗、不退出）
                if (Environment.GetEnvironmentVariable("CBS_ALLOW_MULTI") == "1")
                {
                    try { PlayLog.Write("系统", "单实例跳过", "warn", detail: "检测到已有实例，但因 CBS_ALLOW_MULTI=1 继续启动（调试模式）"); } catch { }
                }
                else
                {
                    // 用与主界面统一的对话框提示（此时 WPF Application 还没建，AppDialog.ShowDialog 可独立工作）
                    const string msg = "智能校园广播系统已经在运行中。\n\n请在任务栏托盘区找到它的图标（可能被折叠在隐藏图标里），双击即可打开控制界面。";
                    try { Desktop.AppDialog.Info(null, "程序已在运行", msg); }
                    catch { MessageBox.Show(msg, "程序已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    return;
                }
            }
            _createdSingleInstanceMutex = createdNew;

            ApplicationConfiguration.Initialize();

            Directory.CreateDirectory(AppDataDir); Directory.CreateDirectory(ConfigDir); Directory.CreateDirectory(RingDir); Directory.CreateDirectory(LogDir);
            MigrateConfigIfNeeded();   // 覆盖安装后自动把旧版配置文件升级到新格式

            PlayLog.Start(LogDir);
            PlayLog.Write("系统", "启动", "info", detail: $"程序启动，数据目录 {AppDataDir}");

            string iconPath = Path.Combine(ResDir, "favicon.ico");
            if (File.Exists(iconPath)) { try { AppIcon = new Icon(iconPath); } catch { AppIcon = SystemIcons.Information; } }
            else { AppIcon = SystemIcons.Information; }

            GetSystemTimes(out FILETIME i, out FILETIME k, out FILETIME u);
            _lastIdleTime = i.ToUInt64(); _lastSystemTime = k.ToUInt64() + u.ToUInt64();

            // ===== 端口：优先使用配置值；被占用则临时向上探测可用端口，且不写回配置 =====
            ResolveListenPort();

            Task.Run(() => StartWebServer(args));

            SchedulerTimer = new System.Threading.Timer(SchedulerWorker, null, 1000, 1000);
            ManualPlayerTimer = new System.Threading.Timer(ManualPlayerWorker, null, 200, 200);

            int intervalHours = 12;
            var all = ReadSettingMerged();
            foreach (var item in all) { if (item["_type"]?.ToString() == "global" && item["subscription_interval"] != null) { intervalHours = item["subscription_interval"].GetValue<int>(); break; } }
            if (intervalHours <= 0) intervalHours = 12;
            SubscriptionTimer = new System.Threading.Timer(SubscriptionWorker, null, 5000, intervalHours * 3600 * 1000);

            // 气象预警：首拉等 25 秒（避开启动高峰），之后每分钟检查一次是否到点
            WarnTimer = new System.Threading.Timer(WarnWorker, null, 25000, 60000);

            // 默认启动 WPF 桌面客户端（单窗口 + 状态条 + 托盘）；传 --winforms 可回退到旧界面
            // vnt-lite 组网：按配置在启动时自动拉起（内置实现，无需 vnt-cli、不建网卡、不需管理员；可在「vnt 设置」里关）
            //
            // 【为什么要带重试】用户反馈"电脑端 vnt 不能自动启动"。真机常见原因：程序是**开机自启**的，
            // 启动时网卡/网络/DNS 还没就绪，`VntProcess.Start` 失败 —— 而老代码只试一次、失败了就再也不试，
            // 于是表现就是"开机后 vnt 一直没起"。现在改成后台线程重试 6 次（5 秒一次、约 30 秒窗口），
            // 每次成功即停；期间失败只写日志，不弹窗打扰。
            if (VntManager.AutoStart && VntManager.VntConfigured)
            {
                var autoVnt = new System.Threading.Thread(() =>
                {
                    for (int attempt = 1; attempt <= 6; attempt++)
                    {
                        try
                        {
                            if (VntProcess.IsRunning) return;
                            if (VntProcess.Start(out string vntStartErr))
                            {
                                PlayLog.Write("vnt-lite", "自动组网", "info",
                                    detail: $"第 {attempt} 次尝试成功。" + VntProcess.StatusSummary());
                                return;
                            }
                            PlayLog.Write("vnt-lite", "自动组网失败", "warn",
                                detail: $"第 {attempt}/6 次：{vntStartErr ?? "未知原因"}（{(attempt < 6 ? "5 秒后重试" : "不再重试，可在「vnt 设置」里手动打开开关")}）");
                        }
                        catch (Exception ex)
                        {
                            PlayLog.Write("vnt-lite", "自动组网异常", "error", detail: $"第 {attempt}/6 次：{ex.Message}");
                        }
                        try { System.Threading.Thread.Sleep(5000); } catch { }
                    }
                }) { IsBackground = true, Name = "vnt-auto-start" };
                autoVnt.Start();
            }
            else if (!VntManager.AutoStart)
            {
                PlayLog.Write("vnt-lite", "自动组网已关闭", "info",
                    detail: "「vnt 设置」里的组网开关是「关」：本次启动不会自动组网（打开开关即可，设置会记住）");
            }
            else
            {
                PlayLog.Write("vnt-lite", "未配置", "warn",
                    detail: "服务器或组网令牌为空，无法自动组网：请打开「vnt 设置」填写后打开开关");
            }

            // 统一用内置 vnt-lite（用户确认：另一套"自己装"的 vnt 不再需要，配置里若还留着旧值也强制纠正）
            try { VntManager.ManagedByApp = true; } catch { }
            // 【一次性迁移】虚拟 IP 字段的语义已改为"纯 IP"（填了=固定，留空=服务器分配，与官方 App 一致）。
            // 历史配置里如果还留着 CIDR 写法（例如 10.26.0.0/24），在新语义下是无效值 —— 启动时清空一次，
            // 否则界面会永远显示那个旧值、也永远不会用"服务器自动分配"（用户反馈：永远显示 10.26.0.0/24）。
            try
            {
                string vpOld = VntManager.VnetPrefix;
                if (!string.IsNullOrWhiteSpace(vpOld) && vpOld.Contains("/"))
                {
                    VntManager.VnetPrefix = "";
                    PlayLog.Write("vnt", "配置迁移", "info",
                        detail: $"虚拟 IP 字段的历史 CIDR 值「{vpOld}」已清空（新语义：填纯 IP = 固定，留空 = 服务器自动分配）");
                }
            }
            catch { }

            // 【话筒服务也要按配置自动拉起】用户反馈"话筒开关是开的、但电脑端无声"：
            // 以前只有手动点开关才会 MicServer.Start，程序重启后开关显示"开"、服务其实没跑
            //（表现：手机配对得上、在发帧，电脑端 `话筒服务未启动 / 播放器未启动 / Ogg 0 页`）。
            // 这里在启动时按配置拉起，并交给状态定时器做看门狗（崩了就自动重启）。
            if (VntManager.MicEnabled)
            {
                try
                {
                    if (!MicServer.Running)
                    {
                        if (MicServer.Start("0.0.0.0", VntManager.MicPort, VntManager.MicCtlPort, VntManager.MicCodeSeed, out string micErr))
                            PlayLog.Write("话筒", "自动开麦", "info", detail: "按上次的开关状态自动启动话筒服务（" + MicServer.BoundAddress + "）");
                        else
                            PlayLog.Write("话筒", "自动开麦失败", "warn", detail: micErr ?? "未知原因");
                    }
                }
                catch (Exception ex) { PlayLog.Write("话筒", "自动开麦异常", "error", detail: ex.Message); }
            }
            if (args != null && Array.IndexOf(args, "--winforms") >= 0) Application.Run(new MainForm());
            else Desktop.DesktopBootstrap.Run();
        }

        /// <summary>退出时释放单实例互斥体。</summary>
        public static void ReleaseSingleInstance()
        {
            try { if (_singleInstanceMutex != null && _createdSingleInstanceMutex) _singleInstanceMutex.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
        }

        public static string HashPassword(string pwd)
        {
            if (string.IsNullOrEmpty(pwd)) return "";
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pwd));
                return BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }

        public static string ResolveAudioPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
                string relPath = path.TrimStart('\\', '/');
                string finalPath = Path.GetFullPath(Path.Combine(AppDataDir, relPath));
                if (!File.Exists(finalPath) && !Directory.Exists(finalPath))
                {
                    string oldPath = Path.GetFullPath(Path.Combine(BaseDir, relPath));
                    if (File.Exists(oldPath) || Directory.Exists(oldPath)) return oldPath;
                }
                if (!finalPath.StartsWith(AppDataDir, StringComparison.OrdinalIgnoreCase)) return "";
                return finalPath;
            }
            catch (Exception ex) { LogHelper.Log($"Path Resolve Error: {path}", ex); return ""; }
        }

        public static bool IsSafeAudioPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("http://") || path.StartsWith("https://")) return true;
            try
            {
                string fullPath = ResolveAudioPath(path);
                if (string.IsNullOrEmpty(fullPath)) return false;
                var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" };
                return exts.Contains(Path.GetExtension(fullPath).ToLower());
            }
            catch { return false; }
        }

        /// <summary>
        /// 自动升级旧版本留下的配置文件（覆盖安装后不用手工改文件）：
        ///  - setting.jsonl：老版只有单条 subscription_url，没有订阅组/标记/动作映射等新键 → 补齐并换算
        ///  - music.jsonl：内置铃声若被写成绝对路径（老版写在安装目录 ring\ 下）→ 换算成 ring/xxx
        ///  - tasks.jsonl：补齐新字段（调休标记、日期数组、模式、时长等）
        /// 只在确实有改动时写回，并写日志 + 推一条横幅告知。
        /// </summary>
        public static void MigrateConfigIfNeeded()
        {
            try
            {
                var notes = new List<string>();

                // ---------------- setting.jsonl ----------------
                var all = ReadSettingMerged();
                JsonObject g = null;
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "global") { g = o; break; }
                bool dirty = false;
                if (g == null)
                {
                    g = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" };
                    all.Insert(0, g);
                    dirty = true;
                    notes.Add("补建全局设置记录");
                }

                if (!(g["subscription_urls"] is JsonArray))
                {
                    var na = new JsonArray();
                    string single = g["subscription_url"]?.ToString() ?? "";
                    foreach (var line in single.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        if (!string.IsNullOrWhiteSpace(line)) na.Add(line.Trim());
                    g["subscription_urls"] = na;
                    dirty = true;
                    notes.Add(na.Count > 0 ? $"订阅组由旧字段 subscription_url 补齐 {na.Count} 条" : "补上订阅组字段");
                }
                var urlsNow = g["subscription_urls"] as JsonArray;
                if (g["subscription_url"] == null || (urlsNow != null && urlsNow.Count > 0 && (g["subscription_url"]?.ToString() ?? "") != (urlsNow[0]?.ToString() ?? "")))
                {
                    g["subscription_url"] = urlsNow != null && urlsNow.Count > 0 ? (urlsNow[0]?.ToString() ?? "") : "";
                    dirty = true;
                }
                if (g["subscription_format"] == null) { g["subscription_format"] = "auto"; dirty = true; notes.Add("订阅格式=auto"); }
                if (g["subscription_interval"] == null) { g["subscription_interval"] = 12; dirty = true; notes.Add("轮询周期=12 小时"); }
                if (g["subscription_marker"] == null) { g["subscription_marker"] = 0; dirty = true; }
                if (g["subscription_marker_disable"] == null) { g["subscription_marker_disable"] = 0; dirty = true; notes.Add("云端标记=0（全局）"); }
                if (g["subscription_marker_enable"] == null) { g["subscription_marker_enable"] = 0; dirty = true; }
                if (g["subscription_action_off"] == null) { g["subscription_action_off"] = "disable"; dirty = true; notes.Add("放假日=禁用、上班日=启用"); }
                if (g["subscription_action_on"] == null) { g["subscription_action_on"] = "enable"; dirty = true; }
                if (g["sys_volume"] != null) { g.Remove("sys_volume"); dirty = true; notes.Add("清除历史字段 sys_volume"); }
                // 渐弱功能已于 2026-10-15 移除（停止播放改为直接结束进程，不再碰会话音量）。
                // 旧配置里若还有 fade_ms，顺手清掉，免得以后有人以为它还在生效。
                if (g["fade_ms"] != null) { g.Remove("fade_ms"); dirty = true; notes.Add("清除已废弃的 fade_ms（渐弱已移除）"); }
                // 调休订阅数据拆到独立文件 tiaoxiu.jsonl：旧配置里混着 tiaoxiu/sub_invalid 记录或 subscription_* 字段时，
                // 强制落盘一次（写入走合并视图，会自动把这几类内容拆到新文件），并留一行日志。
                {
                    bool legacyMixed = false;
                    foreach (var n in ReadJsonl("setting.jsonl"))
                    {
                        string tp = (n as JsonObject)?["_type"]?.ToString();
                        if (tp == "tiaoxiu" || tp == "sub_invalid") { legacyMixed = true; break; }
                    }
                    if (!legacyMixed) foreach (var k in SubscriptionKeys) if (g[k] != null) { legacyMixed = true; break; }
                    if (legacyMixed || ReadJsonl(TiaoxiuFile).Count == 0)
                    {
                        dirty = true;
                        notes.Add("调休订阅数据已拆到独立文件 " + TiaoxiuFile);
                    }
                }
                // ---------------- 气象预警（保留编号 1000）：配置放独立的 warn.jsonl ----------------
                // 隐私：只给"两行接口地址"和播报参数默认值；地区名/区划码/地名筛选一律留空、由你自己填，
                // 免得把使用者的所在地写进程序、安装包、配置或日志里。
                // 把早前版本写在 setting.jsonl 里的预警配置整体搬到 warn.jsonl（一次性）。
                if (g["warn_moved_to_file"] == null)
                {
                    var warnKeys = new[] { "warn_enabled", "warn_place", "warn_area_code", "warn_town",
                                           "warn_list_url", "warn_detail_url", "warn_interval_min", "warn_prefetch_min",
                                           "warn_rate", "warn_voice", "warn_repeat", "warn_gain", "warn_follow_tiaoxiu",
                                           "warn_windows", "warn_last_ok", "warn_last_try", "warn_last_error" };
                    int moved = 0;
                    foreach (var k in warnKeys)
                    {
                        if (g[k] == null) continue;
                        WeatherWarn.SaveSetting(k.Substring(5), g[k].DeepClone());
                        g.Remove(k);
                        moved++;
                    }
                    // 旧预警缓存（可能是一组 warn_caches，也可能是单条 warn_cache）
                    if (g["warn_caches"] is JsonArray oldCaches) { WeatherWarn.ImportLegacyCaches(oldCaches); g.Remove("warn_caches"); moved++; }
                    if (g["warn_cache"] is JsonObject oldOne) { WeatherWarn.ImportLegacyCaches(new JsonArray { oldOne.DeepClone() }); g.Remove("warn_cache"); moved++; }
                    g["warn_moved_to_file"] = true;
                    dirty = true;
                    if (moved > 0) notes.Add("气象预警配置已挪到独立文件 warn.jsonl（" + moved + " 项）");
                }
                // 清掉早前版本自动写入的地区信息（那时候把默认地区写进了配置）
                if (g["warn_privacy_v2"] == null)
                {
                    bool removed = false;
                    foreach (var k in new[] { "warn_place", "warn_area_code", "warn_town" })
                        if (WeatherWarn.ReadSetting(k.Substring(5)) != null) { WeatherWarn.RemoveSetting(k.Substring(5)); removed = true; }
                    g["warn_privacy_v2"] = true;
                    if (g["warn_addr_cleared"] != null) g.Remove("warn_addr_cleared");
                    dirty = true;
                    notes.Add(removed ? "已清除早前写入的地区信息（地区名/区划码/地名筛选改由你自己在「预警播报」页填写）"
                                      : "地区信息一律不留默认值（由你自己填写）");
                }
                // 补齐 warn.jsonl 的默认值 + 升级历史地址（apihz→中央气象台、pageSize 100→500、兜底轮询 30→关）
                foreach (var wn in WeatherWarn.Migrate()) notes.Add(wn);
                // 代理与"忽略证书校验"这两个功能已移除，配置里的残留键一并清掉
                if (g["subscription_proxy"] != null) { g.Remove("subscription_proxy"); dirty = true; notes.Add("清除已废弃的 subscription_proxy"); }
                if (g["subscription_ignore_cert"] != null) { g.Remove("subscription_ignore_cert"); dirty = true; notes.Add("清除已废弃的 subscription_ignore_cert"); }
                if (g["subscription_format"] != null) { g.Remove("subscription_format"); dirty = true; notes.Add("清除已废弃的 subscription_format（格式已改为自动识别）"); }
                if (g["silent_start"] == null) { g["silent_start"] = false; dirty = true; }
                if (g["show_ball"] == null) { g["show_ball"] = true; dirty = true; }
                if (dirty) WriteSettingMerged( all);

                // ---------------- music.jsonl ----------------
                var music = ReadJsonl("music.jsonl");
                int fixedPaths = 0;
                foreach (var n in music)
                {
                    if (n is not JsonObject m) continue;
                    string raw = m["path"]?.ToString() ?? "";
                    string np = NormalizeMusicPath(raw);
                    if (!string.Equals(np, raw, StringComparison.Ordinal)) { m["path"] = np; fixedPaths++; }
                }
                if (fixedPaths > 0) { WriteJsonl("music.jsonl", music); notes.Add($"音乐库 {fixedPaths} 条路径换成相对形式"); }

                // ---------------- tasks.jsonl ----------------
                var tasks = ReadJsonl("tasks.jsonl");
                int fixedTasks = 0;
                foreach (var n in tasks)
                {
                    if (n is not JsonObject tk) continue;
                    // 字段补齐与「按项导入」共用同一份默认值（ConfigTransfer.MigrateTaskFields），避免两处漂移。
                    // 默认值语义：标记 1（0 只留给「调休管理」页）、数组为空、模式 sequential、stop_mode immediate。
                    if (ConfigTransfer.MigrateTaskFields(tk)) fixedTasks++;
                }
                if (fixedTasks > 0) { WriteJsonl("tasks.jsonl", tasks); notes.Add($"排程 {fixedTasks} 条补齐新字段"); }

                if (notes.Count > 0)
                {
                    string summary = string.Join("；", notes);
                    PlayLog.Write("系统", "配置迁移", "info", detail: summary);
                }
            }
            catch (Exception ex) { PlayLog.Write("系统", "配置迁移", "warn", detail: "迁移失败（不影响使用）：" + ex.Message); }
        }
        /// <summary>
        /// 订阅数据下载，超时按 5 个级别逐级放宽：
        ///   级别 1 (连接5s/读取10s) → 2 (8/20) → 3 (15/35) → 4 (25/60) → 5 (40/120)
        /// 没有或失败则退回内置 HttpClient。失败信息会带上两个通道各自的原因，便于定位。
        /// </summary>
        public static async Task<string> DownloadTextAsync(string url, int level, string what = "订阅数据")
        {
            int[] connectSec = { 5, 8, 15, 25, 40 };
            int[] readSec = { 10, 20, 35, 60, 120 };
            int idx = Math.Min(Math.Max(level, 1), 5) - 1;
            string lastError = null;

            // ---------- 通道 1：系统自带的 curl.exe ----------
            // 有些单位的上网行为管理设备按"客户端身份"放行：浏览器能过、系统自带的 curl.exe 能过，
            // 而 .NET 自己的 TLS 客户端会被直接 RST（表现为 "远程主机强迫关闭了一个现有的连接"）。
            // 这里优先用系统自带的 curl.exe（微软签名，Win10 1803+ 自带），失败再退回内置 HttpClient。
            string curlPath = Path.Combine(Environment.SystemDirectory ?? @"C:\Windows\System32", "curl.exe");
            if (File.Exists(curlPath))
            {
                string cdir = Path.Combine(Path.GetTempPath(), "cbs-curl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(cdir);
                    string outFile = Path.Combine(cdir, "sub.json");
                    var psi = new ProcessStartInfo
                    {
                        FileName = curlPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (var a in new[]
                    {
                        "-sS", "-L",   // 注意：不要加 --compressed，精简版 libcurl 不支持会直接报用法错误
                        "--connect-timeout", connectSec[idx].ToString(),
                        "--max-time", readSec[idx].ToString(),
                        "-o", outFile,
                        "--http1.1",
                        "-H", "accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7",
                        "-H", "accept-language: zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6",
                        "-H", "cache-control: no-cache",
                        "-H", "pragma: no-cache",
                        "-H", "priority: u=0, i",
                        "-H", "sec-ch-ua: \"Microsoft Edge\";v=\"153\", \"Not_A Brand\";v=\"8\", \"Chromium\";v=\"153\"",
                        "-H", "sec-ch-ua-mobile: ?0",
                        "-H", "sec-ch-ua-platform: \"Windows\"",
                        "-H", "sec-fetch-dest: document",
                        "-H", "sec-fetch-mode: navigate",
                        "-H", "sec-fetch-site: cross-site",
                        "-H", "sec-fetch-user: ?1",
                        "-H", "upgrade-insecure-requests: 1",
                        "-H", "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36 Edg/153.0.0.0",
                        "--url", url
                    }) psi.ArgumentList.Add(a);
                    using var proc = Process.Start(psi);
                    var errTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit((readSec[idx] + 15) * 1000))
                    {
                        try { proc.Kill(true); } catch { }
                        throw new TimeoutException($"curl 超时（上限 {readSec[idx]} 秒）");
                    }
                    string cerr = await errTask;
                    if (proc.ExitCode == 0 && File.Exists(outFile))
                    {
                        // 有些服务器（例如苹果 iCloud 日历）不问也会返回 gzip 压缩内容，
                        // 直接按文本读会变成乱码，所以先看魔数再决定是否解压。
                        byte[] rawBytes = await File.ReadAllBytesAsync(outFile);
                        string ctext = DecodeDownloadedBytes(rawBytes);
                        if (!string.IsNullOrWhiteSpace(ctext))
                        {
                            PlayLog.Write("订阅", "下载", "info", detail: $"curl 下载成功（级别 {level}，{ctext.Length} 字符）：{url}");
                            return ctext;
                        }
                    }
                    // 退出码 2 = 参数不被这个 curl 支持（例如精简构建缺 --compressed）：
                    // 换最小参数集再试一次，保证不是"命令写错"导致的假失败
                    if (proc.ExitCode == 2)
                    {
                        string outFile2 = Path.Combine(cdir, "sub2.json");
                        var psi2 = new ProcessStartInfo { FileName = curlPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var a in new[] { "-sS", "-L", "--connect-timeout", connectSec[idx].ToString(), "--max-time", readSec[idx].ToString(), "-o", outFile2, "--url", url }) psi2.ArgumentList.Add(a);
                        using var proc2 = Process.Start(psi2);
                        var errTask2 = proc2.StandardError.ReadToEndAsync();
                        if (proc2.WaitForExit((readSec[idx] + 15) * 1000))
                        {
                            string cerr2 = await errTask2;
                            if (proc2.ExitCode == 0 && File.Exists(outFile2))
                            {
                                string t2 = await File.ReadAllTextAsync(outFile2);
                                if (!string.IsNullOrWhiteSpace(t2))
                                {
                                    PlayLog.Write("订阅", "下载", "info", detail: $"curl 下载成功（级别 {level}，最小参数，{t2.Length} 字符）：{url}");
                                    return t2;
                                }
                            }
                            PlayLog.Write("订阅", "下载", "warn", detail: $"curl 最小参数也失败：退出码 {proc2.ExitCode} {(cerr2 ?? "").Trim()}（{url}）");
                        }
                        else { try { proc2.Kill(true); } catch { } }
                    }
                    PlayLog.Write("订阅", "下载", "warn", detail: $"curl 失败（级别 {level}）：退出码 {proc.ExitCode} {(cerr ?? "").Trim()}（{url}）");
                }
                catch (Exception ex)
                {
                    PlayLog.Write("订阅", "下载", "warn", detail: $"curl 异常（级别 {level}）：{ex.Message}（{url}）");
                }
                finally { try { Directory.Delete(cdir, true); } catch { } }
            }
            // ---------- 通道：内置 HttpClient ----------
            // TLS 版本按「系统默认 → 1.2/1.3 → 仅 1.2」依次尝试：
            // 老系统上强制 TLS1.3 会直接握手失败（Chrome 自带 TLS 栈不受影响，所以会出现"浏览器能开、程序报 SSL"）；
            // 反过来某些老服务器只认 1.2，所以留一个只走 1.2 的兜底。
            var tlsPlans = new (string Name, System.Security.Authentication.SslProtocols Proto)[]
            {
                ("系统默认", (System.Security.Authentication.SslProtocols)0),
                ("TLS1.2/1.3", System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13),
                ("仅TLS1.2", System.Security.Authentication.SslProtocols.Tls12)
            };
            Exception lastEx = null;
            foreach (var plan in tlsPlans)
            {
                try
                {
                    using var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(connectSec[idx]),
                    // 自动解压：iCloud 等服务器会直接回 gzip，不开启就会拿到乱码
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
                };
                    if (plan.Proto != (System.Security.Authentication.SslProtocols)0)
                    {
                        try { handler.SslOptions.EnabledSslProtocols = plan.Proto; } catch { }
                    }
                    using var nc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(readSec[idx]) };
                    nc.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Microsoft Edge\";v=\"153\", \"Not_A Brand\";v=\"8\", \"Chromium\";v=\"153\"");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                    nc.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "cross-site");
                    string text = await nc.GetStringAsync(url);
                    PlayLog.Write("订阅", "下载", "info", detail: $"内置下载成功（级别 {level}，TLS {plan.Name}，{text.Length} 字符）：{url}");
                    return text;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    string m = ex is TaskCanceledException ? $"请求超时（读取上限 {readSec[idx]} 秒）" : ex.Message;
                    var inner = ex.InnerException;
                    for (int lv = 0; lv < 3 && inner != null; lv++) { m += " ← " + inner.GetType().Name + ": " + inner.Message; inner = inner.InnerException; }
                    m += "  [TLS " + plan.Name + ", HResult=0x" + ex.HResult.ToString("X8") + "]";
                    PlayLog.Write("订阅", "下载", "warn", detail: $"内置下载失败（级别 {level}，TLS {plan.Name}）：{m}（{url}）");
                }
            }
            {
                string msg = lastEx is TaskCanceledException ? $"请求超时（读取上限 {readSec[idx]} 秒）" : (lastEx?.Message ?? "下载失败");
                var inner2 = lastEx?.InnerException;
                for (int lv = 0; lv < 3 && inner2 != null; lv++) { msg += " ← " + inner2.GetType().Name + ": " + inner2.Message; inner2 = inner2.InnerException; }
                throw new Exception(msg);
            }

        }
        /// <summary>入库时把路径规范化：AppData 内的写成相对路径，外部/网络源保持绝对路径或 URL。</summary>
        /// <summary>
        /// 入库/保存时规范化音乐路径。规则刻意保守——**绝不改写当前能用的路径**：
        ///   1) 网络源（http/https）原样返回；
        ///   2) 位于数据目录（%LOCALAPPDATA%\CampusBroadcastSystem）内 → 换成相对数据目录（等价换算，指向同一文件）；
        ///   3) 位于程序目录（含 res\ring）内 → 换成相对程序目录（等价换算）；
        ///   4) 只有当原路径**确实不存在**、且数据目录 ring 下存在同名文件时，才修成 ring/xxx（修老版本遗留的坏路径）；
        ///   5) 其余一律原样返回（外部目录/共享盘/移动硬盘上的音乐永远不动）。
        /// 起因：早期版本按"文件名相同就改写"，把用户导入的同名音乐改成了内置库路径，导致"找不到音乐"。
        /// </summary>
        public static string NormalizeMusicPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                string abs = Path.GetFullPath(path);
                string fileName = Path.GetFileName(abs);

                // 2) 数据目录内 → 相对数据目录（等价）
                string dataBase = AppDataDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (abs.StartsWith(dataBase, StringComparison.OrdinalIgnoreCase))
                    return abs.Substring(dataBase.Length).Replace('\\', '/');

                // 3) 程序目录内 → 相对程序目录（等价）
                string appBase = BaseDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (abs.StartsWith(appBase, StringComparison.OrdinalIgnoreCase))
                    return abs.Substring(appBase.Length).Replace('\\', '/');

                // 4) 只在"原路径失效 + 内置库里有同名文件"时才修复
                bool exists = false;
                try { exists = File.Exists(abs) || Directory.Exists(abs); } catch { }
                if (!exists && !string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        if (File.Exists(Path.Combine(Path.Combine(AppDataDir, "ring"), fileName))) return "ring/" + fileName;
                    }
                    catch { }
                }

                // 5) 其它原样保留
                return path.Replace('\\', '/');
            }
            catch { return path.Replace('\\', '/'); }
        }

        /// <summary>新增音乐条目：自动分配最小空闲编号（稳定 ID 策略），并写回 music.jsonl。</summary>
        public static JsonObject AddMusicEntry(string name, string path, string type)
        {
            lock (ConfigIoLock)
            {
                var arr = ReadJsonl("music.jsonl");
                var ids = AllocateMusicIds(arr, 1);
                var obj = new JsonObject { ["id"] = ids[0], ["name"] = name ?? "", ["path"] = NormalizeMusicPath(path), ["type"] = type ?? "file" };
                arr.Add(obj);
                WriteJsonl("music.jsonl", arr);
                PlayLog.Write("音乐库", "新增", "info", name: obj["name"]?.ToString(), path: obj["path"]?.ToString(), detail: $"分配编号 {ids[0]}");
                return obj;
            }
        }

        /// <summary>删除音乐条目（只删指定编号，其余编号一律不动）。</summary>
        public static bool RemoveMusicEntry(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (ConfigIoLock)
            {
                var arr = ReadJsonl("music.jsonl");
                var keep = new JsonArray();
                bool removed = false;
                foreach (var m in arr)
                {
                    if (m is JsonObject o && string.Equals(o["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase))
                    {
                        removed = true;
                        PlayLog.Write("音乐库", "删除", "warn", name: o["name"]?.ToString(), detail: $"释放编号 {id}");
                        continue;
                    }
                    keep.Add(m.DeepClone());
                }
                if (removed) WriteJsonl("music.jsonl", keep);
                return removed;
            }
        }

        /// <summary>
        /// 云端调休同步。合并策略为「手动优先」：
        ///   1) 本地手动记录优先，云端记录与手动记录同一天（或同标记）时被压住，不写入；
        ///   2) 云端记录只在本地没有同期手动记录时生效；
        ///   3) 旧的云端记录每次同步整体重建，避免同一日期出现互相矛盾的重复行；
        ///   4) 云端非法数据不静默丢弃，存为 _type=sub_invalid 供界面标红删除线提示。
        /// </summary>
        /// <summary>同步结果明细（供界面显示"生效/覆盖/非法"条数）。</summary>
        public sealed class SyncResult
        {
            public bool Ok;
            public int Applied;
            public int Overridden;
            public int Invalid;
            public string Error;
            /// <summary>实际识别到的云端格式（native / holiday-cn）。</summary>
            public string Format;
            /// <summary>云端生效的天数（连续日期会合并成区间，所以记录数可能更少）。</summary>
            public int AppliedDays;
            /// <summary>被手动记录压住的天数。</summary>
            public int OverriddenDays;
            /// <summary>本次实际用到的订阅地址（订阅组里第一个成功的）。</summary>
            public string UsedUrl;
            /// <summary>跨年窗口里提前取到的下一年记录条数（0 = 没预取）。</summary>
            public int Prefetched;
            /// <summary>预取的年份。</summary>
            public int PrefetchYear;
        }

        public static async Task<bool> ForceSyncSubscription() => (await ForceSyncSubscriptionDetailed()).Ok;

        public static async Task<SyncResult> ForceSyncSubscriptionDetailed()
        {
            var result = new SyncResult();
            try
            {
                var urls = GetSubscriptionUrls();
                if (urls.Count == 0)
                {
                    result.Error = "未配置订阅地址";
                    return result;
                }
                // 订阅组：按顺序逐个尝试，任意一条"拉得到且解析得出记录"就算成功，
                // 这样 GitHub raw 不通时还能落到 jsDelivr 之类的镜像上。
                JsonArray fetched = null;
                string detected = null, usedUrl = null;
                var failures = new List<string>();
                int idx = 0;
                foreach (var rawUrl in urls)
                {
                    idx++;
                    string subUrl = ExpandSubscriptionUrl(rawUrl);
                    if (string.IsNullOrWhiteSpace(subUrl) || !subUrl.StartsWith("http"))
                    {
                        failures.Add($"第 {idx} 条不是 http(s) 地址：{rawUrl}");
                        continue;
                    }
                    try
                    {
                        string response = await DownloadTextAsync(subUrl, idx);   // 超时按 1→5 级递进
                        JsonNode root = null;
                            string det, adaptError;
                            var items = AdaptPayloadText(
response
, out det, out adaptError);
                        if (items == null)
                        {
                            failures.Add($"第 {idx} 条解析失败：{adaptError}（{subUrl}）");
                            PlayLog.Write("订阅", "订阅组", "warn", detail: $"第 {idx} 条解析失败，继续试下一条：{adaptError}");
                            continue;
                        }
                        fetched = items; detected = det; usedUrl = subUrl;
                        if (idx > 1) PlayLog.Write("订阅", "订阅组", "info", detail: $"已启用第 {idx} 条备用订阅源：{subUrl}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"第 {idx} 条拉取失败：{ex.Message}（{subUrl}）");
                        PlayLog.Write("订阅", "订阅组", "warn", detail: $"第 {idx} 条拉取失败，继续试下一条：{ex.Message}");
                    }
                }

                if (fetched == null)
                {
                    result.Error = failures.Count == 0 ? "订阅组里没有可用地址" : string.Join("；", failures);
                    PlayLog.Write("订阅", "同步失败", "error", detail: result.Error + $"（订阅格式设置：{GetSubscriptionFormat()}）");
                    return result;
                }
                result.UsedUrl = usedUrl;
                result.Format = detected;

                // 跨年窗口（12-25 起）：顺手把"下一年"的数据也拉一次。
                // 否则 1 月 1 日零点 {year} 才跳到新年，而数据往往还没发布，会出现几天没有节假日可用的空窗。
                if (IsYearTurnWindow(DateTime.Now))
                {
                    int nextYear = DateTime.Now.Year + 1;
                    int urlIndex = 0;
                    foreach (var rawUrl in urls)
                    {
                        urlIndex++;
                        try
                        {
                            string nextUrl = ExpandSubscriptionUrl(rawUrl, nextYear);
                            if (!nextUrl.StartsWith("http") || nextUrl == usedUrl) continue;
                            string ntext = await DownloadTextAsync(nextUrl, 3, "下一年数据");
                            JsonNode nroot = null;
                            string ndet, nerr;
                            var nitems = AdaptPayloadText(
ntext
, out ndet, out nerr);
                            if (nitems == null)
                            {
                                PlayLog.Write("订阅", "跨年预取", "warn", detail: $"{nextYear} 年第 {urlIndex} 条解析失败：{nerr}");
                                continue;
                            }
                            foreach (var it in nitems) fetched.Add(it.DeepClone());
                            result.Prefetched = nitems.Count;
                            result.PrefetchYear = nextYear;
                            PlayLog.Write("订阅", "跨年预取", "info", detail: $"已提前取到 {nextYear} 年数据 {nitems.Count} 条并一起写入：{nextUrl}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            PlayLog.Write("订阅", "跨年预取", "warn", detail: $"{nextYear} 年第 {urlIndex} 条暂时取不到（{ex.Message}），跨年后会自动重试");
                        }
                    }
                }
                // 跨年前没取到下一年的数据 —— 这是必须提示的情况（放假/调休会不准），按天去重
                if (IsYearTurnWindow(DateTime.Now) && result.Prefetched == 0)
                {
                    PlayLog.Write("订阅", "跨年预取失败", "error", detail: $"跨年窗口内未能取到 {DateTime.Now.Year + 1} 年的节假日数据，跨年后放假/调休可能不准");
                    AlertCenter.Publish("订阅", $"跨年前未能取到 {DateTime.Now.Year + 1} 年的节假日数据，跨年后放假/调休可能不准；可在「订阅 → 本地导入调休数据」手动导入。", "error", dedupKey: "sub-prefetch-fail-" + DateTime.Now.ToString("yyyyMMdd"));
                }

                int manualCount, subCount, overridden;
                var merged = MergeSubscription(fetched, out manualCount, out subCount, out overridden);
                int invalidCount = LastInvalidItems?.Count ?? 0;
                int appliedDays = LastAppliedDays, overriddenDays = LastOverriddenDays;

                if (invalidCount > 0)
                {
                    merged.Add(new JsonObject
                    {
                        ["_type"] = "sub_invalid",
                        ["synced_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["items"] = TrimInvalid(LastInvalidItems)
                    });
                    PlayLog.Write("订阅", "非法数据", "error", detail: $"云端返回 {invalidCount} 条无法解析的记录，已保存待人工处理");
                }

                // 记下本次成功的源：下次同步优先试它（失败会自动继续试后面的备用源）
                foreach (var n2 in merged) if (n2 is JsonObject g2 && g2["_type"]?.ToString() == "global")
                    {
                        g2["subscription_last_ok"] = usedUrl;
                        g2["subscription_last_sync"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");   // 供"距上次同步多久"判断
                        break;
                    }
                WriteSettingMerged( merged);
                PlayLog.Write("订阅", "同步成功", "info",
                    detail: $"格式 {detected}；云端生效 {subCount} 条（共 {appliedDays} 天），被手动记录优先覆盖 {overriddenDays} 天，非法 {invalidCount} 条");
                result.Ok = true; result.Applied = subCount; result.Overridden = overridden;
                result.Invalid = invalidCount; result.AppliedDays = appliedDays; result.OverriddenDays = overriddenDays;
                return result;
            }
            catch (Exception ex)
            {
                LogHelper.Log("ForceSyncSubscription Failed", ex);
                result.Error = ex.Message;
                PlayLog.Write("订阅", "同步失败", "error", detail: ex.Message);
                return result;
            }
        }

        public static string GetSubscriptionUrl()
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is JsonObject o && o["_type"]?.ToString() == "global") return o["subscription_url"]?.ToString() ?? "";
            }
            return "";
        }

        /// <summary>
        /// 内置默认订阅组：holiday-cn（国务院节假日数据）。第 1 条是 jsDelivr 镜像（国内可直连），
        /// 第 2 条是 GitHub 原始地址（权威但国内常超时）。{year} 会替换成当年年份。
        /// 配置里没有 subscription_urls / subscription_url 时自动使用这组默认值；
        /// **用户在订阅设置里把地址全部删空并保存**才算"主动删除"，此后不再自动回填。
        /// </summary>
        public static readonly string[] DefaultSubscriptionUrls =
        {
            // 国内域名，优先试：不含 {year}，一次性返回多年数据（当年 + 下一年会被写入）
            "https://api.comm.miui.com/holiday/holiday.jsp",
            "https://p10-calendars.icloud.com/holidays/CN_ZH.ics",        // 苹果 iCloud 日历（ICS，国内一般可访问）
            "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json",
            "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json"
        };

        /// <summary>
        /// 订阅组：可以配置多条地址（一条主源 + 若干备用），按顺序尝试，任意一条成功即算成功。
        /// - 配置里有 subscription_urls（哪怕是空数组）→ 以此为准（空数组 = 用户主动删除，不再自动同步）
        /// - 否则退回单条的 subscription_url（兼容老配置）
        /// - 两者都没有 → 使用内置默认组
        /// </summary>
        /// <summary>是否静默启动（开机自启时用，勾选后注册表项会带 --silent）。</summary>
        public static bool GetSilentStart() => GetGlobalBool("silent_start");

        /// <summary>是否显示桌面悬浮球（默认显示）。</summary>
        public static bool GetShowBall()
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                string v = o["show_ball"]?.ToString();
                return !(v == "false" || v == "False" || v == "0");   // 默认 true
            }
            return true;
        }

        static bool GetGlobalBool(string key)
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                string v = o[key]?.ToString();
                return v == "true" || v == "True" || v == "1";
            }
            return false;
        }

        /// <summary>读取"上次成功用过的订阅地址"（写在 global 记录的 subscription_last_ok 里）。</summary>
        public static string GetSubscriptionLastOk()
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                return o["subscription_last_ok"]?.ToString() ?? "";
            }
            return "";
        }

        /// <summary>
        /// 把"上次成功的订阅地址"提到最前面：同步是按顺序试、成功即停，
        /// 这样日常就不会每次都在已知失败的源上白等十几秒到几十秒；它失败了也会自动继续试后面的备用源。
        /// </summary>
        public static List<string> PrioritizeLastOk(List<string> urls)
        {
            if (urls == null || urls.Count < 2) return urls;
            string last = GetSubscriptionLastOk();
            if (string.IsNullOrWhiteSpace(last)) return urls;
            int at = urls.FindIndex(u => string.Equals(u, last, StringComparison.OrdinalIgnoreCase));
            if (at <= 0) return urls;
            string moved = urls[at];
            urls.RemoveAt(at);
            urls.Insert(0, moved);
            PlayLog.Write("订阅", "订阅组", "info", detail: "优先使用上次成功的订阅源：" + moved);
            return urls;
        }

        public static List<string> GetSubscriptionUrls()
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;

                if (o.ContainsKey("subscription_urls"))
                {
                    var res = new List<string>();
                    if (o["subscription_urls"] is JsonArray arr)
                    {
                        foreach (var u in arr)
                        {
                            string s = u?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) res.Add(s.Trim());
                        }
                    }
                    return PrioritizeLastOk(res);   // 可能是空列表：那是用户主动清空，尊重它
                }

                string single = o["subscription_url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(single)) return PrioritizeLastOk(new List<string> { single.Trim() });
                break;
            }
            return PrioritizeLastOk(new List<string>(DefaultSubscriptionUrls));
        }

        /// <summary>当前是否在用内置默认订阅组（界面用来提示"这是默认值"）。</summary>
        public static bool IsUsingDefaultSubscription()
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                if (o.ContainsKey("subscription_urls")) return false;
                if (!string.IsNullOrWhiteSpace(o["subscription_url"]?.ToString())) return false;
                return true;
            }
            return true;
        }

        /// <summary>
        /// 是否处于"跨年前后"的危险窗口：12-25 ~ 12-31（新一年的数据可能还没发布）
        /// 以及 1-1 ~ 1-3（配置里的 {year} 刚跳到新年，数据可能还没跟上）。
        /// 这段时间里订阅拉取失败要额外用横幅提醒，否则会静默地没有节假日数据。
        /// </summary>
        public static bool IsYearTurnWindow(DateTime now)
            => (now.Month == 12 && now.Day >= 25) || (now.Month == 1 && now.Day <= 3);

        /// <summary>跨年窗口里拉取失败时追加的提示语（不在窗口内返回空串）。</summary>
        public static string YearTurnWarning(DateTime? now = null)
        {
            var t = now ?? DateTime.Now;
            if (!IsYearTurnWindow(t)) return "";
            return $"注意：现在处于跨年前后（{t:MM-dd}），{t.Year} 年的节假日数据可能还没发布或订阅源暂时不可用，" +
                   "建议检查订阅组里的备用地址，或先手动维护调休规则。";
        }

        /// <summary>
        /// 订阅地址里的 {year} / {yyyy} 占位符替换成当前年份，方便"一次配置、年年可用"，
        /// 例如 https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json
        /// </summary>
        public static string ExpandSubscriptionUrl(string url) => ExpandSubscriptionUrl(url, null);

        /// <summary>同上，可指定年份（跨年前预取下一年的数据时用）。</summary>
        public static string ExpandSubscriptionUrl(string url, int? year)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string y = (year ?? DateTime.Now.Year).ToString();
            return url.Replace("{year}", y).Replace("{YYYY}", y).Replace("{yyyy}", y);
        }


        /// <summary>
        /// 把云端返回的 JSON 适配成内部"日期 + enable/disable"记录数组。支持两种格式：
        ///   1) 原生格式：直接就是一个数组 [{date|start_date, action: enable|disable, marker?, note?}, ...]
        ///   2) holiday-cn 格式（github.com/NateScarlet/holiday-cn）：
        ///      { "year": 2026, "days": [ { "name": "元旦", "date": "2026-01-01", "isOffDay": true }, ... ] }
        ///      其中 isOffDay=true 表示法定放假（不打铃 → disable），false 表示调休上班（打铃 → enable），
        ///      标记统一用 0（全局规则），这样对当天所有任务都生效。
        /// format 可传 "auto" / "native" / "holiday-cn"；detected 回报实际使用的格式。
        /// 解析失败时通过 error 返回人话原因（会原样显示在界面与日志里）。
        /// </summary>
        /// <summary>
        /// 小米节假日接口（api.comm.miui.com/holiday/holiday.jsp）适配：
        ///   { "versioncode":24, "holiday":[ { "year":2026, "workday":[4,45,...], "freeday":[1,2,3,...] } ] }
        /// workday/freeday 是"年内第几天"（1 起算）。只转换当年与下一年，避免把十几年数据全写进调休表。
        /// 放假日按订阅设置里的"放假日动作/标记"，调休上班日按"上班日动作/标记"。该接口没有节日名，故备注为空。
        /// </summary>
        /// <summary>
        /// 统一的「文本 → 记录数组」入口：先看是不是 ICS 日历，否则按 JSON 走原有逻辑。
        /// 这样订阅源可以是 holiday-cn JSON、程序原生 JSON、小米接口，或苹果 iCloud 的 ICS 日历。
        /// </summary>
        // 把下载到的字节转成文本：识别 gzip / zlib 魔数并解压，否则按 UTF-8 解码（含 BOM 处理）。
        // 起因：苹果 iCloud 日历接口不问也会返回 gzip，直接按文本读会变成乱码。
        static string DecodeDownloadedBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try
            {
                if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                {
                    using var input = new MemoryStream(data);
                    using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gz.CopyTo(output);
                    data = output.ToArray();
                }
                else if (data.Length > 2 && data[0] == 0x78)
                {
                    using var input = new MemoryStream(data);
                    using var zs = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zs.CopyTo(output);
                    data = output.ToArray();
                }
            }
            catch { }
            string text = System.Text.Encoding.UTF8.GetString(data);
            return text.Length > 0 && text[0] == 0xFEFF ? text.Substring(1) : text;
        }

        public static JsonArray AdaptPayloadText(string text, out string detected, out string error)
        {
            detected = null; error = null;
            if (!string.IsNullOrWhiteSpace(text) && text.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var icsArr = IcsHoliday.Parse(text, out error);
                if (icsArr != null) detected = "iCloud 日历(ICS)";
                return HolidayMerge.MergeRanges(icsArr);   // 统一合并连续日期
            }
            JsonNode root = null;
            try { root = JsonNode.Parse(text); } catch { root = null; }
            var arr = AdaptCloudPayload(root, GetSubscriptionFormat(), out detected, out error);
            return HolidayMerge.MergeRanges(arr);   // 统一合并连续日期（小米/holiday-cn/原生 都走这里）
        }

        public static JsonArray AdaptMiuiHoliday(JsonNode root, out string error)
        {
            error = null;
            var obj = root as JsonObject;
            var list = obj?["holiday"] as JsonArray;
            if (list == null) { error = "小米接口格式不符：找不到 holiday 数组"; return null; }
            int thisYear = DateTime.Now.Year;
            var wantYears = new HashSet<int> { thisYear, thisYear + 1 };
            var outArr = new JsonArray();
            int used = 0;
            foreach (var n in list)
            {
                if (n is not JsonObject y) continue;
                int year;
                if (!int.TryParse(y["year"]?.ToString(), out year)) continue;
                if (!wantYears.Contains(year)) continue;
                used++;
                AppendMiuiDays(outArr, year, y["freeday"] as JsonArray, false);
                AppendMiuiDays(outArr, year, y["workday"] as JsonArray, true);
            }
            if (used == 0)
            {
                error = "小米接口里没有 " + thisYear + " / " + (thisYear + 1) + " 年的数据（可能还没更新）";
                return null;
            }
            if (outArr.Count == 0) { error = "小米接口解析后没有任何日期"; return null; }
            return outArr;
        }

        /// <summary>把"年内第几天"数组追加成日期记录；isWorkday=true 表示调休上班日。</summary>
        static void AppendMiuiDays(JsonArray target, int year, JsonArray days, bool isWorkday)
        {
            if (days == null) return;
            DateTime first = new DateTime(year, 1, 1);
            string action = isWorkday ? GetSubscriptionOnAction() : GetSubscriptionOffAction();
            int marker = GetSubscriptionMarker(action);
            foreach (var d in days)
            {
                int doy;
                if (!int.TryParse(d?.ToString(), out doy)) continue;
                if (doy < 1 || doy > 366) continue;
                DateTime when = first.AddDays(doy - 1);
                if (when.Year != year) continue;   // 防闰年越界
                target.Add(new JsonObject
                {
                    ["date"] = when.ToString("yyyy-MM-dd"),
                    ["start_date"] = when.ToString("yyyy-MM-dd"),
                    ["end_date"] = when.ToString("yyyy-MM-dd"),
                    ["action"] = action,
                    ["marker"] = marker,
                    ["note"] = isWorkday ? "调休上班" : "放假"
                });
            }
        }

        public static JsonArray AdaptCloudPayload(JsonNode root, string format, out string detected, out string error)
        {
            detected = null; error = null;
            if (root == null) { error = "云端返回内容不是合法 JSON"; return null; }

            string want = string.IsNullOrWhiteSpace(format) ? "auto" : format.Trim().ToLowerInvariant();
            var arr = root as JsonArray;
            var obj = root as JsonObject;

            // 手动指定"原生记录数组"：必须真的是数组，否则明确报错而不是猜
            if (want == "native")
            {
                if (arr == null) { error = "订阅格式已指定为「原生记录数组」，但云端返回的不是 JSON 数组"; return null; }
                detected = "native";
                return arr;
            }

            // 手动指定"holiday-cn"：对象取 days/data/holidays，数组也接受（有的镜像只给 days 列表）
            if (want == "holiday-cn")
            {
                var list = arr ?? (obj?["days"] as JsonArray) ?? (obj?["data"] as JsonArray) ?? (obj?["holidays"] as JsonArray);
                var conv = ConvertDayList(list, out error);
                if (conv != null) detected = "holiday-cn";
                return conv;
            }

            // 手动指定「小米(miui)」接口格式
            if (want == "miui" || want == "xiaomi")
            {
                string merr;
                var mconv = AdaptMiuiHoliday(root, out merr);
                if (mconv == null) { error = merr; return null; }
                detected = "miui（小米节假日接口）";
                return mconv;
            }

            // auto：按内容特征判断
            if (arr != null)
            {
                bool hasAction = false, hasOffFlag = false;
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    if (o["action"] != null) hasAction = true;
                    if (o["isOffDay"] != null || o["offDay"] != null || o["isHoliday"] != null) hasOffFlag = true;
                    if (hasAction || hasOffFlag) break;
                }
                if (hasOffFlag && !hasAction)
                {
                    var conv = ConvertDayList(arr, out error);
                    if (conv != null) detected = "holiday-cn（数组形式）";
                    return conv;
                }
                detected = "native";
                return arr;
            }
            if (obj != null)
            {
                // 小米接口特征：holiday 数组里带 freeday / workday
                    if (obj["holiday"] is JsonArray mh && (mh.Count == 0 || mh[0]?["freeday"] != null || mh[0]?["workday"] != null))
                    {
                        string merr2;
                        var mconv2 = AdaptMiuiHoliday(root, out merr2);
                        if (mconv2 != null) { detected = "miui（小米节假日接口）"; return mconv2; }
                    }
                    var list = (obj["days"] ?? obj["data"] ?? obj["holidays"]) as JsonArray;
                if (list != null)
                {
                    var conv = ConvertDayList(list, out error);
                    if (conv != null) detected = "holiday-cn";
                    return conv;
                }
                error = "JSON 对象里找不到日期列表（支持 days / data / holidays 字段）；若订阅源格式特殊，请在订阅设置里手动指定类型";
                return null;
            }
            error = "云端内容既不是 JSON 数组，也不是 JSON 对象";
            return null;
        }

        /// <summary>兼容旧调用（自检与早期代码）：默认自动识别。</summary>
        public static JsonArray AdaptCloudPayload(JsonNode root, out string error)
            => AdaptCloudPayload(root, "auto", out _, out error);

        /// <summary>
        /// 把"逐日列表"转成内部记录：带 action 的原样透传；否则按 isOffDay/offDay/isHoliday 判断，
        /// 放假日 → disable（不打铃），调休上班日 → enable（打铃），节日名写进 note。
        /// 最后把**连续且动作/备注相同**的日期合并成一条区间记录 —— holiday-cn 是逐天下发的，
        /// 一天写一条会变成几十条记录，界面又乱又难维护。
        /// </summary>
        static JsonArray ConvertDayList(JsonArray days, out string error)
        {
            error = null;
            if (days == null) { error = "找不到日期列表（holiday-cn 需要 days 字段）"; return null; }

            // 云端记录的动作与标记由订阅设置决定（默认：放假→禁用、调休上班→启用、标记 0 全局）
            string offAction = GetSubscriptionOffAction();
            string onAction = GetSubscriptionOnAction();
            int markerDisable = GetSubscriptionMarker("disable");
            int markerEnable = GetSubscriptionMarker("enable");

            var res = new JsonArray();
            foreach (var d in days)
            {
                if (d is not JsonObject o) continue;
                string date = o["date"]?.ToString() ?? o["start_date"]?.ToString();
                if (string.IsNullOrWhiteSpace(date)) continue;

                string action = o["action"]?.ToString();
                string note = o["note"]?.ToString() ?? o["name"]?.ToString();
                if (action != "enable" && action != "disable")
                {
                    bool offDay = true;
                    var flag = o["isOffDay"] ?? o["offDay"] ?? o["isHoliday"] ?? o["holiday"];
                    if (flag != null)
                    {
                        if (flag is JsonValue jv && jv.TryGetValue<bool>(out var b)) offDay = b;
                        else { bool parsed; offDay = bool.TryParse(flag.ToString(), out parsed) ? parsed : true; }
                    }
                    action = offDay ? offAction : onAction;
                }

                var item = new JsonObject
                {
                    ["date"] = date,
                    ["action"] = action,
                    ["marker"] = o["marker"]?.ToString() ?? (action == "disable" ? markerDisable : markerEnable).ToString()
                };
                if (!string.IsNullOrWhiteSpace(note)) item["note"] = note;
                res.Add(item);
            }
            if (res.Count == 0) { error = "日期列表里没有可用记录（每条至少要有一个可解析的 date 字段）"; return null; }
            return MergeConsecutiveDays(res);
        }

        /// <summary>
        /// 把"一天一条"的记录合并成区间：日期连续、动作相同、标记与备注都相同的合成一条 start_date~end_date。
        /// 输入需按日期升序（holiday-cn 本身就是升序）。
        /// </summary>
        public static JsonArray MergeConsecutiveDays(JsonArray dayItems)
        {
            var grouped = new JsonArray();
            JsonObject cur = null;
            foreach (var n in dayItems)
            {
                if (n is not JsonObject o) continue;
                string ds = o["date"]?.ToString() ?? o["start_date"]?.ToString();
                if (!DateTime.TryParse(ds, out var d)) continue;
                string action = o["action"]?.ToString();
                string note = o["note"]?.ToString() ?? "";
                string marker = o["marker"]?.ToString() ?? "0";

                if (cur != null
                    && cur["action"]?.ToString() == action
                    && cur["marker"]?.ToString() == marker
                    && (cur["note"]?.ToString() ?? "") == note
                    && DateTime.TryParse(cur["end_date"]?.ToString(), out var prevEnd)
                    && (d - prevEnd).TotalDays == 1.0)
                {
                    cur["end_date"] = d.ToString("yyyy-MM-dd");
                    continue;
                }

                cur = new JsonObject
                {
                    ["start_date"] = d.ToString("yyyy-MM-dd"),
                    ["end_date"] = d.ToString("yyyy-MM-dd"),
                    ["action"] = action,
                    ["marker"] = marker
                };
                if (!string.IsNullOrEmpty(note)) cur["note"] = note;
                grouped.Add(cur);
            }
            return grouped;
        }

        /// <summary>把升序日期序列折成连续区间（用于"被手动记录切成几段后"重新合并）。</summary>
        static List<KeyValuePair<DateTime, DateTime>> GroupConsecutiveDays(List<DateTime> days)
        {
            var res = new List<KeyValuePair<DateTime, DateTime>>();
            if (days == null || days.Count == 0) return res;
            days.Sort();
            var start = days[0]; var prev = days[0];
            for (int i = 1; i < days.Count; i++)
            {
                if ((days[i] - prev).TotalDays == 1.0) { prev = days[i]; continue; }
                res.Add(new KeyValuePair<DateTime, DateTime>(start, prev));
                start = prev = days[i];
            }
            res.Add(new KeyValuePair<DateTime, DateTime>(start, prev));
            return res;
        }

        /// <summary>当前配置的订阅格式：auto / native / holiday-cn。</summary>
        /// <summary>订阅格式已改为「始终自动识别」：native / holiday-cn / 小米(miui) 三种都能按内容认出，不再需要用户设置。</summary>
        public static string GetSubscriptionFormat() => "auto";


        public static int GetSubscriptionMarker() => GetSubscriptionMarker("disable");

        public static int GetSubscriptionMarker(string action)
        {
            string key = action == "enable" ? "subscription_marker_enable" : "subscription_marker_disable";
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                int m;
                if (int.TryParse(o[key]?.ToString(), out m) && m >= 0 && m <= 10) return m;
                if (int.TryParse(o["subscription_marker"]?.ToString(), out m) && m >= 0 && m <= 10) return m;
                return 0;
            }
            return 0;
        }

        /// <summary>云端"放假日"写成什么动作（默认 disable = 不播音）。</summary>
        public static string GetSubscriptionOffAction()
        {
            string a = ReadGlobalString("subscription_action_off");
            return (a == "enable" || a == "disable") ? a : "disable";
        }

        /// <summary>云端"调休上班日"写成什么动作（默认 enable = 照常打铃）。</summary>
        public static string GetSubscriptionOnAction()
        {
            string a = ReadGlobalString("subscription_action_on");
            return (a == "enable" || a == "disable") ? a : "enable";
        }

        static string ReadGlobalString(string key)
        {
            foreach (var item in ReadSettingMerged())
            {
                if (item is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                return o[key]?.ToString();
            }
            return null;
        }

        /// <summary>本次合并中发现的云端非法数据（供界面展示与自检断言）。</summary>
        public static JsonArray LastInvalidItems { get; private set; } = new JsonArray();

        static JsonArray TrimInvalid(JsonArray invalid)
        {
            const int maxInvalid = 200;
            var trimmed = new JsonArray();
            if (invalid == null) return trimmed;
            int start = Math.Max(0, invalid.Count - maxInvalid);
            for (int i = start; i < invalid.Count; i++) trimmed.Add(invalid[(int)i]?.DeepClone());
            return trimmed;
        }

        /// <summary>
        /// 纯逻辑的合并过程（不联网、不落盘），便于自检与单元验证。
        /// 返回合并后的完整 setting.jsonl 记录集合。
        /// </summary>
        public static JsonArray MergeSubscription(JsonArray cloudItems, out int manualRules, out int subApplied, out int overridden)
        {
            var all = ReadSettingMerged();
            var manualKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);        // 日期|标记|动作
            var manualDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 日期
            var manualDateMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 日期|标记
            var newAll = new JsonArray();
            overridden = 0; manualRules = 0;
            var invalid = new JsonArray();

            // 第一遍：登记所有手动规则；旧的云端记录一律丢弃（稍后整体重建）
            foreach (var item in all)
            {
                if (item is not JsonObject o) continue;
                string t = o["_type"]?.ToString();
                if (t == "sub_invalid") continue;
                if (t == "tiaoxiu" && o["source"]?.ToString() == "sub") continue;
                if (t == "tiaoxiu" && o["source"]?.ToString() == "manual")
                {
                    string sd = o["start_date"]?.ToString() ?? o["date"]?.ToString() ?? "";
                    string ed = o["end_date"]?.ToString() ?? sd;
                    string mk = o["marker"]?.ToString() ?? "0";
                    string ac = o["action"]?.ToString() ?? "";
                    manualKeys.Add($"{sd}|{mk}|{ac}");
                    manualDates.Add(sd);
                    manualDateMarkers.Add($"{sd}|{mk}");
                    manualRules++;

                    // 手动记录的"优先"要覆盖它占用的每一天，而不是只有第一天：
                    // 否则手动的 09-25~09-27 假期区间挡不住云端逐日下发的 09-26、09-27，界面上会出现重复行。
                    if (DateTime.TryParse(sd, out var dFrom) && DateTime.TryParse(ed, out var dTo)
                        && dTo >= dFrom && (dTo - dFrom).TotalDays <= 400)
                    {
                        for (var d = dFrom; d <= dTo; d = d.AddDays(1))
                        {
                            string ds = d.ToString("yyyy-MM-dd");
                            manualDates.Add(ds);
                            manualDateMarkers.Add($"{ds}|{mk}");
                            manualKeys.Add($"{ds}|{mk}|{ac}");
                        }
                    }
                }
                newAll.Add(o.DeepClone());
            }

            // 第二遍：逐条处理云端记录（云端可能是"一天一条"，也可能是合并后的区间）
            subApplied = 0;
            int appliedDays = 0, overriddenDays = 0;
            if (cloudItems != null)
            {
                foreach (var item in cloudItems)
                {
                    try
                    {
                        var obj = item?.AsObject();
                        if (obj == null) { invalid.Add(BuildInvalidRecord(item, "元素不是 JSON 对象")); continue; }
                        string startRaw = obj["start_date"]?.ToString() ?? obj["date"]?.ToString();
                        string endRaw = obj["end_date"]?.ToString() ?? startRaw;
                        if (string.IsNullOrWhiteSpace(startRaw)) { invalid.Add(BuildInvalidRecord(item, "缺少 date / start_date 字段")); continue; }
                        if (!DateTime.TryParse(startRaw, out var dFrom)) { invalid.Add(BuildInvalidRecord(item, "date 无法解析为日期")); continue; }
                        if (!DateTime.TryParse(endRaw, out var dTo)) { invalid.Add(BuildInvalidRecord(item, "end_date 无法解析为日期")); continue; }
                        if (dTo < dFrom) { invalid.Add(BuildInvalidRecord(item, "end_date 早于 start_date")); continue; }
                        if ((dTo - dFrom).TotalDays > 400) { invalid.Add(BuildInvalidRecord(item, "日期区间超过 400 天，疑似数据错误")); continue; }
                        string action = obj["action"]?.ToString();
                        if (action != "enable" && action != "disable") { invalid.Add(BuildInvalidRecord(item, "action 必须是 enable 或 disable")); continue; }
                        if (obj["marker"] != null && !int.TryParse(obj["marker"].ToString(), out _)) { invalid.Add(BuildInvalidRecord(item, "marker 不是整数")); continue; }

                        int markerInt = 0;
                        if (obj["marker"] != null) int.TryParse(obj["marker"].ToString(), out markerInt);
                        string note = obj["note"]?.ToString();

                        // 逐天判断手动优先：手动的规则能挡住区间里对应的那一天，剩下的再重新合并成区间
                        var kept = new List<DateTime>();
                        for (var d = dFrom; d <= dTo; d = d.AddDays(1))
                        {
                            string ds = d.ToString("yyyy-MM-dd");
                            if (manualKeys.Contains($"{ds}|{markerInt}|{action}") || manualDates.Contains(ds) || manualDateMarkers.Contains($"{ds}|{markerInt}"))
                            {
                                overridden++; overriddenDays++;
                                continue;
                            }
                            kept.Add(d);
                        }
                        foreach (var seg in GroupConsecutiveDays(kept))
                        {
                            var clean = new JsonObject
                            {
                                ["_type"] = "tiaoxiu",
                                ["source"] = "sub",
                                ["start_date"] = seg.Key.ToString("yyyy-MM-dd"),
                                ["end_date"] = seg.Value.ToString("yyyy-MM-dd"),
                                ["action"] = action,
                                ["marker"] = markerInt
                            };
                            if (!string.IsNullOrEmpty(note)) clean["note"] = note;
                            newAll.Add(clean);
                            subApplied++;
                            appliedDays += (int)(seg.Value - seg.Key).TotalDays + 1;
                        }
                    }
                    catch (Exception ex) { invalid.Add(BuildInvalidRecord(item, "解析异常：" + ex.Message)); }
                }
            }

            LastInvalidItems = invalid;
            LastAppliedDays = appliedDays;
            LastOverriddenDays = overriddenDays;
            return newAll;
        }

        /// <summary>最近一次合并里云端实际生效的天数（记录数可能更少，因为连续日期会合并成区间）。</summary>
        public static int LastAppliedDays { get; private set; }

        /// <summary>最近一次合并里被手动记录压住的天数。</summary>
        public static int LastOverriddenDays { get; private set; }

        static JsonObject BuildInvalidRecord(JsonNode raw, string reason)
        {
            var o = new JsonObject();
            try { o["raw"] = raw == null ? "" : raw.ToJsonString(); } catch { o["raw"] = "无法序列化"; }
            o["reason"] = reason;
            return o;
        }

        /// <summary>供自检使用：暴露调休判定逻辑。</summary>
        internal static bool ShouldPlayTodayForTest(JsonObject task) => ShouldPlayToday(task);

        /// <summary>供桌面界面使用：与调度器完全同源的"今天该不该播"判定。</summary>
        public static bool ShouldPlayTodayForUi(JsonObject task) => ShouldPlayToday(task);

        static void SubscriptionWorker(object state)
        {
            try
            {
                // 定时/启动触发时先看"距上次同步过了多久"：没到配置周期就不拉。
                // 否则每次重启程序都会把同一个订阅链接同步一遍（历史日志里 27 次启动 = 26 次同步）。
                int intervalHours = 12;
                foreach (var item in ReadSettingMerged())
                {
                    if (item is JsonObject g0 && g0["_type"]?.ToString() == "global" && g0["subscription_interval"] != null)
                    { try { intervalHours = g0["subscription_interval"].GetValue<int>(); } catch { } break; }
                }
                if (intervalHours <= 0) intervalHours = 12;
                DateTime last;
                if (TryGetGlobalTime("subscription_last_sync", out last) && (DateTime.Now - last).TotalHours < intervalHours) return;
                _ = ForceSyncSubscription();
            }
            catch (Exception ex) { LogHelper.Log("SubscriptionWorker Failed", ex); }
        }

        /// <summary>读 global 记录里的时间戳字段（用于"距上次多久"这类判断）。</summary>
        static bool TryGetGlobalTime(string key, out DateTime t)
        {
            t = DateTime.MinValue;
            try
            {
                foreach (var n in ReadSettingMerged())
                    if (n is JsonObject o && o["_type"]?.ToString() == "global")
                        return DateTime.TryParse(o[key]?.ToString(), out t);
            }
            catch { }
            return false;
        }

        // ---------------- 气象预警订阅（保留编号 1000） ----------------
        // **按需拉取**：默认只在"引用编号 1000 的排程时刻前 N 分钟（默认 10）"拉一次，
        // 到点前数据就是新的，不用整天定时轮询。兜底轮询默认关闭（warn_interval_min=0）。
        // 线程每分钟醒一次做判断，所以界面上改参数立刻生效，不用重启。
        static DateTime _warnLastFetch = DateTime.MinValue;

        /// <summary>当天已经拉过的排程点 → 尝试次数（同一个点最多试 2 次，失败重试一次）。</summary>
        static readonly Dictionary<string, int> _warnPrefetched = new Dictionary<string, int>();

        static void WarnWorker(object state)
        {
            try
            {
                if (!WeatherWarn.GetEnabled()) return;
                DateTime now = DateTime.Now;
                string today = now.ToString("yyyy-MM-dd");
                int lead = WeatherWarn.GetPrefetchMin();
                int interval = WeatherWarn.GetIntervalMin();

                // ① 按需拉取：找到"今天会播 + 引用 1000 + 已进入提前窗口"的排程点
                string dueKey = null; int attempts = 0;
                foreach (var n in ReadJsonl("tasks.jsonl"))
                {
                    if (n is not JsonObject task) continue;
                    if (!WeatherWarn.TaskUsesWarn(task)) continue;
                    if (!ShouldPlayToday(task)) continue;
                    string st = task["start_time"]?.ToString() ?? "";
                    if (st.Length == 5) st += ":00";
                    if (!TimeSpan.TryParse(st, out TimeSpan ts)) continue;
                    DateTime at = now.Date.Add(ts);
                    if (!WeatherWarn.InPrefetchWindow(now, at, lead)) continue;
                    string key = today + "#" + (task["id"]?.ToString() ?? "0") + "#" + st;
                    int done = 0;
                    lock (_warnPrefetched) { _warnPrefetched.TryGetValue(key, out done); }
                    if (done >= 2) continue;                      // 这个点已经拉过（最多两次）
                    dueKey = key; attempts = done;
                    break;
                }

                // 2) 兜底轮询（默认关闭）：没有进入窗口、但距上次拉取超过周期时拉一次
                bool fallback = false;
                if (dueKey == null && interval > 0)
                {
                    DateTime lastTry;
                    if (!WeatherWarn.TryGetLastTry(out lastTry) || (now - lastTry).TotalMinutes >= interval) fallback = true;
                }
                if (dueKey == null && !fallback) return;

                if (dueKey != null)
                {
                    lock (_warnPrefetched)
                    {
                        // 只保留今天的记录，避免长期运行越攒越多
                        foreach (var k in _warnPrefetched.Keys.Where(k => !k.StartsWith(today, StringComparison.Ordinal)).ToList())
                            _warnPrefetched.Remove(k);
                        _warnPrefetched[dueKey] = attempts + 1;
                    }
                    PlayLog.Write("预警", "按需拉取", "info", detail: $"排程点 {dueKey.Split('#')[2]} 前 {lead} 分钟，开始拉取（第 {attempts + 1} 次）");
                }

                _warnLastFetch = now;
                WeatherWarn.FetchAndCacheAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { LogHelper.Log("WarnWorker Failed", ex); }
        }

        /// <summary>界面点了"立即拉取"后重置计时，避免下一次定时器又马上拉一遍。</summary>
        public static void MarkWarnFetched() => _warnLastFetch = DateTime.Now;

        public static void UpdateSubscriptionTimer()
        {
            int intervalHours = 12; var all = ReadSettingMerged();
            foreach (var item in all) { if (item["_type"]?.ToString() == "global" && item["subscription_interval"] != null) { intervalHours = item["subscription_interval"].GetValue<int>(); break; } }
            if (intervalHours <= 0) intervalHours = 12;
            SubscriptionTimer?.Change(2000, intervalHours * 3600 * 1000);
        }

        /// <summary>
        /// 解码整曲波形。注意：网络直播流（http/https）不做整曲解码——
        /// 直连流永远不会结束，早期版本会留下一个无限拉流解码的 ffmpeg 进程（这就是 HLS「停止后仍在播」的根因）。
        /// </summary>
        static void BackgroundLoadWave(string filepath, bool isManual = false)
        {
            try
            {
                lock (WaveformLock) { CurrentWaveformSamples = new short[0]; }
                if (!File.Exists(FfmpegPath) || string.IsNullOrEmpty(filepath)) return;

                bool isRemote = filepath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || filepath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!isRemote && !File.Exists(filepath)) return;

                Task.Run(() =>
                {
                    Process proc = null;
                    int pid = -1;
                    try
                    {
                        proc = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = FfmpegPath,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            }
                        };
                        proc.StartInfo.ArgumentList.Add("-nostdin");
                        if (isRemote)
                        {
                            // 直播流只取前 20 秒，并加 25 秒硬超时，确保进程一定退出
                            proc.StartInfo.ArgumentList.Add("-t");
                            proc.StartInfo.ArgumentList.Add("20");
                        }
                        proc.StartInfo.ArgumentList.Add("-i"); proc.StartInfo.ArgumentList.Add(filepath);
                        proc.StartInfo.ArgumentList.Add("-ac"); proc.StartInfo.ArgumentList.Add("1");
                        proc.StartInfo.ArgumentList.Add("-ar"); proc.StartInfo.ArgumentList.Add("200");
                        proc.StartInfo.ArgumentList.Add("-f"); proc.StartInfo.ArgumentList.Add("s16le");
                        proc.StartInfo.ArgumentList.Add("-");
                        proc.Start();
                        pid = proc.Id;
                        ActiveAudioProcesses[pid] = new AudioProcessInfo
                        {
                            Pid = pid, Purpose = "波形解码", Name = Path.GetFileName(filepath) ?? "",
                            Path = filepath, StartTime = DateTime.Now, Proc = proc
                        };
                        try { proc.BeginErrorReadLine(); } catch { }

                        const int maxSamples = 200 * 60 * 20; // 最多保留 20 分钟波形（约 4.8MB）
                        var samples = new List<short>(200 * 60);
                        byte[] buffer = new byte[8192];
                        var deadline = DateTime.Now.AddSeconds(isRemote ? 25 : 180);
                        int read;
                        while (samples.Count < maxSamples && DateTime.Now < deadline &&
                               (read = proc.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            int usable = read - (read % 2);
                            for (int i = 0; i + 1 < usable; i += 2)
                            {
                                samples.Add((short)(buffer[i] | (buffer[i + 1] << 8)));
                                if (samples.Count >= maxSamples) break;
                            }
                        }
                        try { if (!proc.HasExited) proc.Kill(); } catch { }

                        var arr = samples.ToArray();
                        lock (WaveformLock) { CurrentWaveformSamples = arr; }
                        if (isManual) { lock (ManualLock) { ManualCurrentDuration = ProbeDurationMs(filepath) / 1000.0; } }   // 用 ffprobe 读真实时长（原来按缓冲区长度估算，恰好卡在 30 秒）
                    }
                    catch (Exception ex) { LogHelper.Log($"Waveform Load Error: {filepath}", ex); }
                    finally
                    {
                        if (pid > 0) ActiveAudioProcesses.TryRemove(pid, out _);
                        try { proc?.Dispose(); } catch { }
                    }
                });
            }
            catch (Exception ex) { LogHelper.Log($"Waveform Schedule Error: {filepath}", ex); }
        }

        public static JsonArray ReadJsonl(string filename)
        {
            var arr = new JsonArray(); string path = Path.Combine(ConfigDir, filename);
            lock (ConfigIoLock)
            {
                try
                {
                    if (!File.Exists(path)) return arr;
                    foreach (var line in File.ReadAllLines(path)) { if (string.IsNullOrWhiteSpace(line)) continue; try { arr.Add(JsonNode.Parse(line)); } catch { } }
                }
                catch (Exception ex) { LogHelper.Log($"ReadJsonl Failed: {filename}", ex); }
            }
            return arr;
        }

        /// <summary>
        /// 落盘前的自动备份：把当前 config 文件复制到 config\_backup\xxx-yyyyMMdd-HHmmss.jsonl，最多保留 30 份。
        /// 起因：调休规则/音乐库的删除是立即落盘的，一旦误触（例如回车连发）会把规则一条条删光，
        /// 有这份滚动备份就能直接从 config\_backup 里翻回来。
        /// </summary>
        /// <summary>
        /// 【导出隐私数据 · 默认不导出】把备份包里的敏感字段清空：网页口令、组网令牌、数据密码、凭证密钥。
        /// 不导出也能正常还原排程/调休/预警配置/音乐库/铃声；vnt 只保留服务器地址（跨电脑导入正好只需要它）。
        /// 返回被清空的字段个数，便于日志核对。
        /// </summary>
        public static int StripPrivacyFromBackup(string zipPath)
        {
            int cleared = 0;
            try
            {
                using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update);
                foreach (var entry in zip.Entries.ToList())
                {
                    string nm = (entry.FullName ?? "").Replace('\\', '/');
                    bool isVnt = nm.EndsWith("vnt.jsonl", StringComparison.OrdinalIgnoreCase);
                    bool isSetting = nm.EndsWith("setting.jsonl", StringComparison.OrdinalIgnoreCase);
                    if (!isVnt && !isSetting) continue;
                    string text;
                    using (var rs = entry.Open()) using (var sr = new StreamReader(rs, System.Text.Encoding.UTF8)) text = sr.ReadToEnd();
                    var outLines = new List<string>();
                    foreach (var line in text.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) { outLines.Add(line); continue; }
                        try
                        {
                            var o = System.Text.Json.Nodes.JsonNode.Parse(t) as System.Text.Json.Nodes.JsonObject;
                            if (o == null) { outLines.Add(line); continue; }
                            if (isVnt)
                            {
                                // 与新的「导出」路径共用同一份字段清单（ConfigTransfer.PrivacyVntFields）。
                                // 老实现只清 token/data_password/cred_secret，漏了 device_id（本机身份）
                                // 与 admin_devices（管理员设备名单）—— 那两条同样是隐私。
                                foreach (var k in ConfigTransfer.PrivacyVntFields)
                                    if (o[k] != null && o[k].ToString().Length > 0) { o[k] = ""; cleared++; }
                            }
                            else
                            {
                                if (o["password"] != null && o["password"].ToString().Length > 0) { o["password"] = ""; cleared++; }
                            }
                            outLines.Add(o.ToJsonString());
                        }
                        catch { outLines.Add(line); }
                    }
                    string full = entry.FullName;
                    entry.Delete();
                    var ne = zip.CreateEntry(full, System.IO.Compression.CompressionLevel.Optimal);
                    using var ws = ne.Open();
                    using var sw = new StreamWriter(ws, new System.Text.UTF8Encoding(false));
                    sw.Write(string.Join("\n", outLines));
                }
            }
            catch (Exception ex) { LogHelper.Log("StripPrivacyFromBackup Failed", ex); }
            return cleared;
        }
        static void BackupConfigBeforeWrite(string fileName)
        {
            try
            {
                string path = Path.Combine(ConfigDir, fileName);
                if (!File.Exists(path)) return;
                string dir = Path.Combine(ConfigDir, "_backup");
                Directory.CreateDirectory(dir);
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string dest = Path.Combine(dir, baseName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                if (!File.Exists(dest)) File.Copy(path, dest, true);   // 同一秒内的连续写入只留一份
                var files = new DirectoryInfo(dir).GetFiles(baseName + "-*.jsonl");
                Array.Sort(files, (a, b) => string.Compare(b.Name, a.Name, StringComparison.Ordinal));
                for (int i = 30; i < files.Length; i++) { try { files[i].Delete(); } catch { } }
            }
            catch { }
        }
        /// <summary>
        /// 写配置用的序列化选项：一次性建好，之后不再修改（避免被冻结后再设属性）。
        /// TypeInfoResolver 必须显式指定，否则单文件/裁剪发布下会抛"must specify a TypeInfoResolver"。
        /// </summary>
        public static readonly JsonSerializerOptions JsonWriteOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
        public static void WriteJsonl(string filename, JsonArray data)
        {
            string path = Path.Combine(ConfigDir, filename);
                    BackupConfigBeforeWrite(filename);
            lock (ConfigIoLock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    var tmp = path + ".tmp";
                    File.WriteAllLines(tmp, data.Select(n => n?.ToJsonString(JsonWriteOptions) ?? "{}"));
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (Exception ex) { LogHelper.Log($"WriteJsonl Failed: {filename}", ex); }
            }
        }

        // ================= 调休 / 订阅配置：独立文件 tiaoxiu.jsonl =================
        // 原先这些内容都混在 setting.jsonl 里（订阅地址组、周期、云端动作与标记、调休规则、无效云端数据）。
        // 现在物理上拆到 tiaoxiu.jsonl：
        //   第 1 行 { "_type": "subscription", urls/interval/marker*/action*/last_ok/last_sync ... }
        //   其余行 { "_type": "tiaoxiu" | "sub_invalid", ... }
        // 为了让 40 多处调用方一行不用改，这里提供"合并视图"读写：
        //   ReadSettingMerged() —— setting.jsonl + tiaoxiu.jsonl 的调休/无效记录，且把订阅配置**并进 global 记录**；
        //   WriteSettingMerged() —— 再把调休/无效记录与订阅键**拆回去**。
        public const string TiaoxiuFile = "tiaoxiu.jsonl";

        /// <summary>属于"订阅配置"的键（存在 tiaoxiu.jsonl 的 subscription 记录里，不再放 global）。</summary>
        static readonly string[] SubscriptionKeys =
        {
            "subscription_url", "subscription_urls", "subscription_interval",
            "subscription_marker", "subscription_marker_disable", "subscription_marker_enable",
            "subscription_action_off", "subscription_action_on",
            "subscription_last_ok", "subscription_last_sync"
        };

        /// <summary>读订阅配置记录（_type=subscription）。</summary>
        public static JsonObject ReadSubscriptionConfig()
        {
            try
            {
                foreach (var n in ReadJsonl(TiaoxiuFile))
                    if (n is JsonObject o && o["_type"]?.ToString() == "subscription") return o;
            }
            catch { }
            return null;
        }

        /// <summary>改订阅配置并写回（没有记录就新建一条放在文件开头）。</summary>
        public static void SaveSubscriptionConfig(Action<JsonObject> mutate)
        {
            try
            {
                var all = ReadJsonl(TiaoxiuFile);
                JsonObject s = null;
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "subscription") { s = o; break; }
                if (s == null) { s = new JsonObject { ["_type"] = "subscription" }; all.Insert(0, s); }
                mutate?.Invoke(s);
                WriteJsonl(TiaoxiuFile, all);
            }
            catch (Exception ex) { LogHelper.Log("SaveSubscriptionConfig Failed", ex); }
        }

        /// <summary>读全部的调休记录（tiaoxiu + 无效云端 sub_invalid）。</summary>
        public static JsonArray ReadTiaoxiuRecords()
        {
            var res = new JsonArray();
            try
            {
                foreach (var n in ReadJsonl(TiaoxiuFile))
                {
                    string tp = (n as JsonObject)?["_type"]?.ToString();
                    if (tp == "tiaoxiu" || tp == "sub_invalid") res.Add(n.DeepClone());
                }
            }
            catch { }
            return res;
        }

        /// <summary>覆盖写入调休记录（订阅配置行原样保留）。</summary>
        public static void WriteTiaoxiuRecords(JsonArray records)
        {
            try
            {
                var keep = new JsonArray();
                foreach (var n in ReadJsonl(TiaoxiuFile))
                {
                    string tp = (n as JsonObject)?["_type"]?.ToString();
                    if (tp == "tiaoxiu" || tp == "sub_invalid") continue;
                    keep.Add(n.DeepClone());
                }
                foreach (var r in records ?? new JsonArray()) keep.Add(r.DeepClone());
                WriteJsonl(TiaoxiuFile, keep);
            }
            catch (Exception ex) { LogHelper.Log("WriteTiaoxiuRecords Failed", ex); }
        }

        /// <summary>原始读写：只碰 setting.jsonl 本身（薄适配层内部用，不要拿它当通用读写）。</summary>
        static JsonArray ReadSettingRaw() => ReadJsonl("setting.jsonl");
        static void WriteSettingRaw(JsonArray data) => WriteJsonl("setting.jsonl", data);
        /// <summary>读取"合并视图"：setting.jsonl 的全部记录 + tiaoxiu.jsonl 的调休/无效记录，
        /// 并把订阅配置并进 global 记录（老代码按 _type=global 取 subscription_* 照样能用）。</summary>
        public static JsonArray ReadSettingMerged()
        {
            var res = new JsonArray();
            try
            {
                JsonObject sub = ReadSubscriptionConfig();
                foreach (var n in ReadSettingRaw())
                {
                    if (n is JsonObject g && g["_type"]?.ToString() == "global" && sub != null)
                        foreach (var kv in sub)
                        {
                            if (kv.Key == "_type") continue;
                            g[kv.Key] = kv.Value?.DeepClone();
                        }
                    res.Add(n.DeepClone());
                }
                foreach (var r in ReadTiaoxiuRecords()) res.Add(r.DeepClone());
            }
            catch (Exception ex) { LogHelper.Log("ReadSettingMerged Failed", ex); }
            return res;
        }

        /// <summary>写入"合并视图"：调休/无效记录写进 tiaoxiu.jsonl，订阅键从 global 记录里拆到 subscription 记录，其余写回 setting.jsonl。</summary>
        public static void WriteSettingMerged(JsonArray all)
        {
            try
            {
                var setting = new JsonArray();
                var records = new JsonArray();
                JsonObject sub = new JsonObject { ["_type"] = "subscription" };
                bool hasSub = false;
                foreach (var n in all ?? new JsonArray())
                {
                    string tp = (n as JsonObject)?["_type"]?.ToString();
                    if (tp == "tiaoxiu" || tp == "sub_invalid") { records.Add(n.DeepClone()); continue; }
                    if (tp == "subscription")
                    {
                        sub = n.DeepClone() as JsonObject;
                        hasSub = true;
                        continue;
                    }
                    var copy = n.DeepClone() as JsonObject;
                    if (copy != null && tp == "global")
                    {
                        foreach (var key in SubscriptionKeys)
                        {
                            if (copy[key] == null) continue;
                            sub[key] = copy[key].DeepClone();
                            copy.Remove(key);
                            hasSub = true;
                        }
                    }
                    setting.Add(copy);
                }
                WriteJsonl("setting.jsonl", setting);
                var outT = new JsonArray();
                if (hasSub) outT.Add(sub);
                // 必须 DeepClone：records 里的节点已经挂在 records 这个数组上了，直接 Add 会抛
                // "The node already has a parent"（拆分后踩过一次：调休记录整个写不进文件）
                foreach (var r in records) outT.Add(r.DeepClone());
                WriteJsonl(TiaoxiuFile, outT);
            }
            catch (Exception ex) { LogHelper.Log("WriteSettingMerged Failed", ex); }
        }

        // ================= 目录断点续播（统一使用 resume.json） =================
        // 结构: { "<目录绝对路径>": { "index": 4, "elapsed": 0.0 } }
        // index 语义: 下一次该从目录里第几首开始（0 基）。为兼容历史数据，读取时也接受旧的扁平写法 { "路径": 4 }。

        private static JsonObject ReadResumeObject()
        {
            var all = ReadJsonl("resume.json");
            return all.FirstOrDefault()?.AsObject() ?? new JsonObject();
        }

        public static int GetFolderResumeIndex(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath)) return 0;
                var obj = ReadResumeObject();
                var node = obj[folderPath];
                if (node == null) return 0;
                if (node is JsonObject o && o["index"] != null) return o["index"].GetValue<int>();
                if (node is JsonValue v && v.TryGetValue<int>(out int flat)) return flat;
            }
            catch (Exception ex) { LogHelper.Log("Read Resume Error", ex); }
            return 0;
        }

        public static void SaveFolderResumeIndex(string folderPath, int index, double elapsed = 0)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            try
            {
                lock (ConfigIoLock)
                {
                    // 关键点（历史 bug）：ReadJsonl 返回的 JsonNode 仍然挂在它所属的数组之下，
                    // 直接把同一个节点 Add 到新数组会抛 "The node already has a parent"（异常被吞掉，
                    // 表现为"断点除第一次外永远写不进去"）。正确做法是把节点从原数组摘下来再放进去。
                    var all = ReadJsonl("resume.json");
                    var arr = new JsonArray();
                    JsonObject target = null;
                    for (int i = all.Count - 1; i >= 0; i--)
                    {
                        if (all[i] is JsonObject o)
                        {
                            all.RemoveAt(i);   // 先脱离原父节点
                            if (target == null) target = o;
                            arr.Insert(0, o);  // 再放入目标数组
                        }
                    }
                    if (target == null)
                    {
                        target = new JsonObject();
                        arr.Add(target);
                    }
                    JsonObject entry = target[folderPath] as JsonObject;
                    if (entry == null) { entry = new JsonObject(); target[folderPath] = entry; }
                    entry["index"] = index;
                    if (entry["elapsed"] == null) entry["elapsed"] = Math.Round(elapsed, 2);
                    WriteJsonl("resume.json", arr);

                    // 历史遗留的错误文件名（代码早期版本写的是 resume.jsonl），发现即清理，避免两份断点数据并存
                    var legacy = Path.Combine(ConfigDir, "resume.jsonl");
                    if (File.Exists(legacy)) { try { File.Delete(legacy); PlayLog.Write("系统", "清理", "warn", detail: "已删除历史遗留的 resume.jsonl（断点数据统一到 resume.json）"); } catch { } }
                }
            }
            catch (Exception ex) { LogHelper.Log("Save Resume Error", ex); PlayLog.Write("系统", "断点保存失败", "error", detail: ex.Message); }
        }

        // ================= 音乐库：只读辅助 + 稳定 ID 分配 =================

        public static JsonObject FindMusic(JsonArray allMusic, string id)
        {
            if (allMusic == null || string.IsNullOrEmpty(id)) return null;
            foreach (var m in allMusic)
            {
                if (m is JsonObject o && string.Equals(o["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase)) return o;
            }
            return null;
        }

        /// <summary>取当前记录里最小的空闲正整数编号，保证「删除后新条目顶替空缺、既有编号永不重排」。</summary>
        public static string AllocateMusicId(JsonArray allMusic)
        {
            var used = new HashSet<int>();
            if (allMusic != null)
            {
                foreach (var m in allMusic)
                {
                    if (m is JsonObject o && int.TryParse(o["id"]?.ToString(), out int id) && id > 0) used.Add(id);
                }
            }
            // 【保留编号】1000 = 气象预警播报（虚拟条目，由 GET /api/music 实时合成，永远不在 music.jsonl 里）。
            // 不预置它的话，音乐库编号连续排到 999 时新条目会被分到 1000，与虚拟条目重号 ——
            // 重号后调度侧永远走预警分支，那条真实音乐就永远播不到（静默失效）。
            used.Add(WeatherWarn.MusicId);
            int candidate = 1;
            while (used.Contains(candidate)) candidate++;
            return candidate.ToString();
        }

        /// <summary>为一批新条目分配编号：优先填最小空缺，按从小到大依次占用。</summary>
        public static List<string> AllocateMusicIds(JsonArray existing, int count)
        {
            var used = new HashSet<int>();
            if (existing != null)
            {
                foreach (var m in existing)
                {
                    if (m is JsonObject o && int.TryParse(o["id"]?.ToString(), out int id) && id > 0) used.Add(id);
                }
            }
            used.Add(WeatherWarn.MusicId);   // 同上：保留编号 1000 不参与分配
            var result = new List<string>();
            int candidate = 1;
            while (result.Count < count)
            {
                while (used.Contains(candidate)) candidate++;
                used.Add(candidate);
                result.Add(candidate.ToString());
                candidate++;
            }
            return result;
        }

        static void ChangePlayingStatus(bool isPlaying, string name) { lock (StatusLock) { IsPlaying = isPlaying; CurrentPlayingName = isPlaying ? name : ""; } }

        public static bool IsGlobalSwitchOn()
        {
            var all = ReadSettingMerged();
            foreach (var item in all) { if (item["_type"]?.ToString() == "global") return item["global_switch"]?.GetValue<bool>() ?? true; }
            return true;
        }

        public static void ToggleGlobalSwitch()
        {
            var all = ReadSettingMerged(); bool found = false;
            foreach (var item in all)
            {
                if (item["_type"]?.ToString() == "global")
                {
                    bool current = item["global_switch"]?.GetValue<bool>() ?? true; item["global_switch"] = !current; found = true;
                    if (current) StopAllAudio("全局开关：停用广播系统"); else RestoreAudioAfterEnable();
                    PlayLog.Write("系统", "开关", "warn", detail: current ? "广播系统已停用（全局静音）" : "广播系统已恢复运行");
                    break;
                }
            }
            if (!found) { all.Insert(0, new JsonObject { ["_type"] = "global", ["global_switch"] = false, ["password"] = "" }); PlayLog.Write("系统", "开关", "warn", detail: "配置缺少 global 记录，已补建并置为停用"); }
            WriteSettingMerged( all);
        }

        static void StartWebServer(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.Use(async (context, next) => {
                string method = context.Request.Method; string path = context.Request.Path.Value;
                // 放行两个新架构接口 api/wave 和 api/sys
                if (path == "/" || path == "/m" || path.StartsWith("/res/") || path == "/api/login" || path == "/api/wave" || path == "/api/sys" || (method == "GET" && path == "/api/settings")) { await next.Invoke(); return; }

                string sysPwd = ""; var settings = ReadSettingMerged();
                foreach (var item in settings) { if (item["_type"]?.ToString() == "global") { sysPwd = item["password"]?.ToString() ?? ""; break; } }
                if (!string.IsNullOrEmpty(sysPwd))
                {
                    bool authValid = false; var authHeader = context.Request.Headers["Authorization"].ToString();
                    if (authHeader.StartsWith("Bearer ")) { string token = authHeader.Substring(7); if (ValidTokens.TryGetValue(token, out var exp) && exp > DateTime.Now) { authValid = true; } }
                    if (!authValid) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { status = "error", message = "Token Invalid" }); return; }
                }
                await next.Invoke();
            });

            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(ResDir), RequestPath = "/res" });
            app.MapGet("/", async context => await context.Response.WriteAsync(await File.ReadAllTextAsync(Path.Combine(ResDir, "index.html"))));
            app.MapGet("/m", async context => await context.Response.WriteAsync(await File.ReadAllTextAsync(Path.Combine(ResDir, "mobile.html"))));

            app.MapPost("/api/login", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); string inputPwd = json?["password"]?.ToString() ?? ""; string inputHash = HashPassword(inputPwd); string sysPwd = "";
                    foreach (var item in ReadSettingMerged()) { if (item["_type"]?.ToString() == "global") { sysPwd = item["password"]?.ToString() ?? ""; break; } }
                    if (string.IsNullOrEmpty(sysPwd) || sysPwd == inputPwd || sysPwd == inputHash) { string token = Guid.NewGuid().ToString("N"); ValidTokens[token] = DateTime.Now.AddHours(24); await context.Response.WriteAsJsonAsync(new { status = "success", token = token }); }
                    else { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { status = "error", message = "Password Error" }); }
                }
            });

            app.MapGet("/api/music", () => {
                var all = ReadJsonl("music.jsonl");
                var res = new JsonArray();
                foreach (var item in all) { if (item["_type"]?.ToString() != "settings") res.Add(item.DeepClone()); }
                // 保留编号 1000：气象预警播报（虚拟条目，不在音乐库里，删不掉也改不动；排程里选中它即可定时播报）
                res.Add(new JsonObject
                {
                    ["id"] = WeatherWarn.MusicId,
                    ["name"] = "气象预警播报（自动生成）",
                    ["path"] = "",
                    ["type"] = "warn",
                    ["_virtual"] = true
                });
                return res;
            });

            // 新增音乐条目：编号由后端按「最小空闲」自动分配，既有编号永不重排
            app.MapPost("/api/music", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject();
                    if (json == null) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { status = "error", message = "body" }); return; }
                    var obj = AddMusicEntry(json["name"]?.ToString() ?? "", json["path"]?.ToString() ?? "", json["type"]?.ToString() ?? "file");
                    await context.Response.WriteAsJsonAsync(new { status = "success", id = obj["id"]?.ToString() });
                }
            });

            // 删除音乐条目：只释放该编号，其余编号保持不动
            app.MapDelete("/api/music/{id}", (string id) =>
            {
                bool ok = RemoveMusicEntry(id);
                return new JsonObject { ["status"] = ok ? "success" : "notfound", ["id"] = id };
            });

            app.MapGet("/api/tasks", () => ReadJsonl("tasks.jsonl"));

            app.MapPost("/api/tasks", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var newTask = JsonNode.Parse(await reader.ReadToEndAsync()).AsObject(); var tasks = ReadJsonl("tasks.jsonl"); int maxId = 0;
                    foreach (var t in tasks) { int id = t["id"]?.GetValue<int>() ?? 0; if (id > maxId) maxId = id; }
                    newTask["id"] = maxId + 1; tasks.Add(newTask); WriteJsonl("tasks.jsonl", tasks);
                    PlayLog.Write("排程", "新增任务", "info", task: "ID " + (maxId + 1),
                        detail: $"时间 {newTask["start_time"]?.ToString()}，内容编号 {newTask["music_ids"]?.ToJsonString()}");
                    await context.Response.WriteAsJsonAsync(new { status = "success", id = maxId + 1 });
                }
            });

            app.MapPut("/api/tasks/{id:int}", async (int id, HttpContext context) => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var updatedData = JsonNode.Parse(await reader.ReadToEndAsync()).AsObject(); updatedData["id"] = id; var tasks = ReadJsonl("tasks.jsonl");
                    for (int i = 0; i < tasks.Count; i++) { if (tasks[i]["id"]?.GetValue<int>() == id) { tasks[i] = updatedData; break; } }
                    WriteJsonl("tasks.jsonl", tasks);
                    PlayLog.Write("排程", "修改任务", "info", task: "ID " + id,
                        detail: $"时间 {updatedData["start_time"]?.ToString()}，内容编号 {updatedData["music_ids"]?.ToJsonString()}");
                    await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.MapDelete("/api/tasks/{id:int}", async (int id, HttpContext context) => {
                var tasks = ReadJsonl("tasks.jsonl"); var newTasks = new JsonArray();
                foreach (var t in tasks) { if (t["id"]?.GetValue<int>() != id) newTasks.Add(t.DeepClone()); }
                WriteJsonl("tasks.jsonl", newTasks);
                PlayLog.Write("排程", "删除任务", "warn", task: "ID " + id);
                await context.Response.WriteAsJsonAsync(new { status = "success" });
            });

            // 按开始时间重排任务编号（1..N）并写回 tasks.jsonl。
            // 注意：编号是任务的对外标识，重排后网页/手机端排程页需要重新加载，否则会拿着旧编号去改/删。
            app.MapPost("/api/tasks/renumber", async context => {
                var tasks = ReadJsonl("tasks.jsonl");
                var list = new List<JsonObject>();
                foreach (var t in tasks) { if (t is JsonObject o) list.Add(o); }
                list.Sort((a, b) =>
                {
                    int c = string.Compare(a["start_time"]?.ToString() ?? "", b["start_time"]?.ToString() ?? "", StringComparison.Ordinal);
                    if (c != 0) return c;
                    int ia = 0, ib = 0;
                    int.TryParse(a["id"]?.ToString(), out ia); int.TryParse(b["id"]?.ToString(), out ib);
                    return ia.CompareTo(ib);   // 同一时间的保持原有先后
                });
                var arr = new JsonArray(); int n = 0;
                foreach (var o in list) { var clone = (JsonObject)o.DeepClone(); clone["id"] = ++n; arr.Add(clone); }
                WriteJsonl("tasks.jsonl", arr);
                PlayLog.Write("排程", "重排编号", "warn", detail: $"共 {n} 条任务按开始时间重新编号为 1-{n}");
                await context.Response.WriteAsJsonAsync(new { status = "success", count = n });
            });

            app.MapGet("/api/tiaoxiu", () => { var all = ReadSettingMerged(); var res = new JsonArray(); foreach (var item in all) { if (item["_type"]?.ToString() == "tiaoxiu") res.Add(item.DeepClone()); } return res; });

            app.MapPost("/api/tiaoxiu", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); var newTiaoxiuList = json?.AsArray(); if (newTiaoxiuList == null) return;
                    var all = ReadSettingMerged(); var newAll = new JsonArray();
                    foreach (var item in all) { if (item["_type"]?.ToString() != "tiaoxiu") { newAll.Add(item.DeepClone()); } }
                    foreach (var item in newTiaoxiuList) { var obj = item.DeepClone().AsObject(); obj["_type"] = "tiaoxiu"; newAll.Add(obj); }
                    WriteSettingMerged( newAll);
                    PlayLog.Write("调休", "保存", "info", detail: $"共 {newTiaoxiuList.Count} 条调休记录");
                    await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.MapPost("/api/tiaoxiu/sync", async context => {
                var result = await ForceSyncSubscriptionDetailed();
                if (result.Ok)
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "success",
                        applied = result.Applied,
                        overridden = result.Overridden,
                        invalid = result.Invalid,
                        format = result.Format,
                        used_url = result.UsedUrl,
                        prefetched = result.Prefetched,
                        prefetch_year = result.PrefetchYear,
                        applied_days = result.AppliedDays,
                        overridden_days = result.OverriddenDays,
                        message = $"同步成功（格式：{result.Format}）：云端生效 {result.Applied} 条（共 {result.AppliedDays} 天），被手动记录优先覆盖 {result.OverriddenDays} 天，非法 {result.Invalid} 条"
                    });
                }
                else
                {
                      // 失败也返回 200：网络/远端故障不是"服务器内部错误"。
                      // 用 500 的话界面只能显示笼统的"同步失败"，看不到到底是没填地址还是拉取失败。
                      bool noUrl = GetSubscriptionUrls().Count == 0;
                      await context.Response.WriteAsJsonAsync(new
                      {
                          status = "failed",
                          no_url = noUrl,
                          message = noUrl
                              ? "没有订阅地址：请在订阅设置里填至少一条订阅链接"
                              : ("同步失败：" + (result.Error ?? "请检查订阅地址与网络"))
                      });
                }
            });

            // 云端订阅「只预览不写入」：拉取 + 按指定（或自动识别）格式转写，返回条数与样例，
            // 让用户先看清楚会被写成什么，再决定要不要真的同步。
        // 本地导入：把"已经拿到的 JSON 文本"直接转写进调休文件（网络走不通时用）。
        // 文件导入和粘贴导入都走这一个接口：桌面端读文件后把文本发过来，网页端也一样。
        app.MapPost("/api/tiaoxiu/import", async context =>
        {
            string text = null, fmt = null;
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var json = JsonNode.Parse(body) as JsonObject;
                    text = json?["text"]?.ToString();
                    fmt = json?["format"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                await context.Response.WriteAsJsonAsync(new { status = "failed", message = "请求解析失败：" + ex.Message });
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                await context.Response.WriteAsJsonAsync(new { status = "failed", message = "没有收到要导入的内容" });
                return;
            }
            string adaptError, detected;
            var items = AdaptPayloadText(text, out detected, out adaptError);
            if (items == null)
            {
                PlayLog.Write("订阅", "本地导入", "warn", detail: "解析失败：" + adaptError);
                await context.Response.WriteAsJsonAsync(new { status = "failed", message = "解析失败：" + adaptError });
                return;
            }
            int manualCount, subCount, overridden;
            // 导入是"增量"的：先把已有的云端记录(clone)一起交给合并逻辑，
            // 否则导入 2027 的数据会把 2026 的云端记录整批冲掉（合并语义是"丢弃所有 sub 记录后重建"）。
            var importItems = new JsonArray();
            foreach (var n0 in ReadSettingMerged())
            {
                if (n0 is not JsonObject o0) continue;
                if (o0["_type"]?.ToString() != "tiaoxiu" || o0["source"]?.ToString() != "sub") continue;
                string sd0 = o0["start_date"]?.ToString() ?? o0["date"]?.ToString() ?? "";
                if (sd0.Length == 0) continue;
                importItems.Add(new JsonObject
                {
                    ["date"] = sd0,                      // 两个键都写上：合并逻辑读任一都行
                    ["start_date"] = sd0,
                    ["end_date"] = o0["end_date"]?.ToString() ?? sd0,
                    ["action"] = o0["action"]?.ToString() ?? "disable",
                    ["marker"] = o0["marker"]?.ToString() ?? "0",
                    ["note"] = o0["note"]?.ToString() ?? ""
                });
            }
            foreach (var it0 in items) importItems.Add(it0?.DeepClone());   // JsonNode 不能有两个父节点，必须克隆
            var mergedImport = MergeSubscription(importItems, out manualCount, out subCount, out overridden);
            int appliedDays = LastAppliedDays, overriddenDays = LastOverriddenDays;
            int invalidCount = LastInvalidItems?.Count ?? 0;
            // 与云端同步一致：非法数据也记下来，并把合并结果真正写回文件
            // （之前漏了这一步，所以会出现"导入成功但没保存"）
            if (invalidCount > 0)
            {
                mergedImport.Add(new JsonObject
                {
                    ["_type"] = "sub_invalid",
                    ["synced_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["items"] = TrimInvalid(LastInvalidItems)
                });
            }
            WriteSettingMerged( mergedImport);
            string summary = $"本地导入成功（格式：{detected}）：写入 {subCount} 条（共 {appliedDays} 天），被手动记录优先覆盖 {overriddenDays} 天，非法 {invalidCount} 条";
            PlayLog.Write("订阅", "本地导入", "info", detail: summary);
            await context.Response.WriteAsJsonAsync(new
            {
                status = "success",
                applied = subCount,
                overridden = overriddenDays,
                invalid = invalidCount,
                format = detected,
                applied_days = appliedDays,
                overridden_days = overriddenDays,
                message = summary
            });
        });
            app.MapPost("/api/tiaoxiu/preview", async context => {
                string url = null, format = null;
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var json = JsonNode.Parse(body) as JsonObject;
                        url = json?["url"]?.ToString();
                        format = json?["format"]?.ToString();
                    }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(format)) format = GetSubscriptionFormat();

                // 预览同样支持订阅组：指定 url 就只用它，否则按组顺序逐个试
                var urlList = new List<string>();
                if (!string.IsNullOrWhiteSpace(url)) urlList.Add(url.Trim());
                else urlList.AddRange(GetSubscriptionUrls());
                if (urlList.Count == 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { status = "error", message = "未配置订阅地址" });
                    return;
                }
                try
                {
                    JsonArray items = null; string detected = null, usedUrl = null;
                    var failures = new List<string>();
                    int idx = 0;
                    foreach (var rawUrl in urlList)
                    {
                        idx++;
                        string u = ExpandSubscriptionUrl(rawUrl);
                        if (string.IsNullOrWhiteSpace(u) || !u.StartsWith("http")) { failures.Add($"第 {idx} 条不是 http(s) 地址"); continue; }
                        try
                        {
                            using var client = new HttpClient(); client.Timeout = TimeSpan.FromSeconds(30);   // GitHub raw 在国内经常十几秒
                            string text = await client.GetStringAsync(u);
                            JsonNode root = null;
                            string det2, err2;
                            var parsed = AdaptPayloadText(text, out det2, out err2);
                            if (parsed == null) { failures.Add($"第 {idx} 条解析失败：{err2}"); continue; }
                            items = parsed; detected = det2; usedUrl = u;
                            break;
                        }
                        catch (Exception ex) { failures.Add($"第 {idx} 条拉取失败：{ex.Message}"); }
                    }
                    if (items == null)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { status = "error", message = string.Join("；", failures) });
                        return;
                    }
                    int offDays = 0, onDays = 0;
                    foreach (var n in items)
                    {
                        if (n is not JsonObject o) continue;
                        int days = 1;
                        if (DateTime.TryParse(o["start_date"]?.ToString(), out var ds0) && DateTime.TryParse(o["end_date"]?.ToString(), out var de0))
                            days = Math.Max(1, (int)(de0 - ds0).TotalDays + 1);
                        if (o["action"]?.ToString() == "disable") offDays += days; else onDays += days;
                    }
                    var sample = new JsonArray();
                    for (int i = 0; i < items.Count && i < 12; i++) sample.Add(items[i]?.DeepClone());
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "success",
                        format = detected,
                        requested_format = format,
                        url = usedUrl,
                        total = items.Count,
                        total_days = offDays + onDays,
                        off_days = offDays,
                        on_days = onDays,
                        sample
                    });
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { status = "error", message = ex.Message });
                }
            });

            // 云端非法数据：供调休设置界面做「红字 + 删除线」提示，可选择清除
            app.MapGet("/api/tiaoxiu/invalid", () => {
                var res = new JsonArray();
                foreach (var item in ReadSettingMerged())
                {
                    if (item is JsonObject o && o["_type"]?.ToString() == "sub_invalid")
                    {
                        res.Add(new JsonObject
                        {
                            ["synced_at"] = o["synced_at"]?.ToString() ?? "",
                            ["items"] = o["items"]?.DeepClone() ?? new JsonArray()
                        });
                    }
                }
                return res;
            });

            app.MapDelete("/api/tiaoxiu/invalid", () => {
                var all = ReadSettingMerged(); var keep = new JsonArray();
                int removed = 0;
                foreach (var item in all)
                {
                    if (item is JsonObject o && o["_type"]?.ToString() == "sub_invalid") { removed++; continue; }
                    keep.Add(item.DeepClone());
                }
                if (removed > 0) WriteSettingMerged( keep);
                PlayLog.Write("订阅", "清除非法数据", "warn", detail: $"清除 {removed} 组云端非法记录");
                return new JsonObject { ["status"] = "success", ["removed"] = removed };
            });

            // ================= 按项导入 / 导出（桌面窗口、拖入、整包还原共用同一批导入器） =================
            // 四个入口一律走 ConfigTransfer；校验规则复用 MicApiBridge.ValidateBody，只保留一份。
            // 请求体统一为 { path, items:[...], overwrite|privacy, kind? }。
            app.MapPost("/api/transfer/export", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
                string path = body?["path"]?.ToString() ?? "";
                bool privacy = false;
                try { if (body?["privacy"] is JsonValue pv) privacy = pv.GetValue<bool>(); } catch { }
                var items = new List<string>();
                if (body?["items"] is JsonArray ia) foreach (var n in ia) { string s = n?.ToString(); if (!string.IsNullOrEmpty(s)) items.Add(s); }
                if (string.IsNullOrWhiteSpace(path))
                { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { status = "error", message = "缺少导出文件路径" }); return; }
                await context.Response.WriteAsJsonAsync(ConfigTransfer.Export(path, items, privacy));
            });

            app.MapPost("/api/transfer/preview", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
                string path = body?["path"]?.ToString() ?? "";
                bool overwrite = false;
                try { if (body?["overwrite"] is JsonValue ov) overwrite = ov.GetValue<bool>(); } catch { }
                var items = new List<string>();
                if (body?["items"] is JsonArray ia) foreach (var n in ia) { string s = n?.ToString(); if (!string.IsNullOrEmpty(s)) items.Add(s); }
                if (string.IsNullOrWhiteSpace(path))
                { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { status = "error", message = "缺少来源文件路径" }); return; }
                await context.Response.WriteAsJsonAsync(ConfigTransfer.Preview(path, items, overwrite, body?["kind"]?.ToString()));
            });

            app.MapPost("/api/transfer/import", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
                string path = body?["path"]?.ToString() ?? "";
                bool overwrite = false;
                try { if (body?["overwrite"] is JsonValue ov) overwrite = ov.GetValue<bool>(); } catch { }
                var items = new List<string>();
                if (body?["items"] is JsonArray ia) foreach (var n in ia) { string s = n?.ToString(); if (!string.IsNullOrEmpty(s)) items.Add(s); }
                if (string.IsNullOrWhiteSpace(path))
                { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { status = "error", message = "缺少来源文件路径" }); return; }
                await context.Response.WriteAsJsonAsync(ConfigTransfer.Import(path, items, overwrite, body?["kind"]?.ToString()));
            });

            // 整包还原（「从备份中恢复…」入口）：不再盲目解压覆盖，而是走同一批导入器的合法性校验与旧格式迁移。
            app.MapPost("/api/transfer/restore", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
                string path = body?["path"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(path))
                { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { status = "error", message = "缺少备份包路径" }); return; }
                await context.Response.WriteAsJsonAsync(ConfigTransfer.ImportWholePackage(path));
            });

            // 执行日志与待提示消息（桌面端横幅、网页端日志页共用）
            app.MapGet("/api/log", (HttpContext context) =>
            {
                int limit = 200;
                if (int.TryParse(context.Request.Query["limit"], out int q) && q > 0) limit = q;
                return PlayLog.RecentJson(limit);
            });

            app.MapGet("/api/alerts", (HttpContext context) =>
            {
                long afterId = 0;
                long.TryParse(context.Request.Query["after"], out afterId);
                return AlertCenter.PendingJson(afterId);
            });

            app.MapGet("/api/settings", () => {
                foreach (var item in ReadSettingMerged())
                {
                    if (item["_type"]?.ToString() == "global")
                    {
                        bool gs = item["global_switch"]?.GetValue<bool>() ?? true; bool hasPwd = !string.IsNullOrEmpty(item["password"]?.ToString()); string subUrl = item["subscription_url"]?.ToString() ?? "";
                        var urlArr0 = new JsonArray();
                        foreach (var u in GetSubscriptionUrls()) urlArr0.Add(u);
                        return new JsonObject
                        {
                            ["global_switch"] = gs,
                            ["has_password"] = hasPwd,
                            ["subscription_url"] = subUrl,
                            ["subscription_urls"] = urlArr0,
                            ["subscription_is_default"] = IsUsingDefaultSubscription(),
                            ["subscription_marker"] = GetSubscriptionMarker(),
                            ["subscription_marker_disable"] = GetSubscriptionMarker("disable"),
                            ["subscription_marker_enable"] = GetSubscriptionMarker("enable"),
                            ["silent_start"] = GetSilentStart(),
                            ["show_ball"] = GetShowBall(),
                            ["subscription_action_off"] = GetSubscriptionOffAction(),
                            ["subscription_action_on"] = GetSubscriptionOnAction(),
                            ["subscription_interval"] = item["subscription_interval"]?.GetValue<int>() ?? 12
                        };
                    }
                }
                return new JsonObject { ["global_switch"] = true, ["has_password"] = false, ["subscription_url"] = "", ["subscription_urls"] = new JsonArray(), ["subscription_format"] = "auto", ["subscription_marker"] = 0, ["subscription_marker_disable"] = 0, ["subscription_marker_enable"] = 0, ["subscription_action_off"] = "disable", ["subscription_action_on"] = "enable", ["subscription_interval"] = 12 };
            });

            app.MapPost("/api/settings", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); var all = ReadSettingMerged(); JsonObject globalItem = null;
                    foreach (var item in all) { if (item["_type"]?.ToString() == "global") { globalItem = item.AsObject(); break; } }
                    if (globalItem == null) { globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" }; all.Insert(0, globalItem); }
                    if (json["global_switch"] != null)
                    {
                        bool newState = json["global_switch"].GetValue<bool>(); globalItem["global_switch"] = newState;
                        if (!newState) StopAllAudio("网页端停用广播系统"); else RestoreAudioAfterEnable();
                        PlayLog.Write("系统", "开关", "warn", detail: newState ? "网页端恢复广播系统运行" : "网页端停用广播系统");
                    }
                    // 历史遗留字段 sys_volume 未被任何逻辑使用，写入时顺手清除
                    if (globalItem["sys_volume"] != null) globalItem.Remove("sys_volume");
                    if (json["password"] != null) { string rawPwd = json["password"].ToString(); globalItem["password"] = string.IsNullOrEmpty(rawPwd) ? "" : HashPassword(rawPwd); }
                    if (json["subscription_url"] != null) globalItem["subscription_url"] = json["subscription_url"].ToString();
                    if (json["subscription_urls"] != null)
                    {
                        var arr = new JsonArray();
                        if (json["subscription_urls"] is JsonArray inArr)
                        {
                            foreach (var u in inArr)
                            {
                                string s = u?.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) arr.Add(s.Trim());
                            }
                        }
                        else
                        {
                            // 兼容：也可以传换行分隔的字符串
                            foreach (var line in (json["subscription_urls"].ToString() ?? "").Split('\n'))
                            {
                                string s = line.Trim().TrimEnd('\r');
                                if (!string.IsNullOrWhiteSpace(s)) arr.Add(s);
                            }
                        }
                        globalItem["subscription_urls"] = arr;
                        // 单条字段同步成第一条，老界面/老逻辑仍然能用
                        globalItem["subscription_url"] = arr.Count > 0 ? arr[0]?.ToString() : "";
                    }
                    foreach (var bk in new[] { "silent_start", "show_ball" })
                    {
                        if (json[bk] == null) continue;
                        string bv = json[bk].ToString().Trim().ToLowerInvariant();
                        globalItem[bk] = (bv == "true" || bv == "1");
                    }
                    if (json["subscription_interval"] != null && int.TryParse(json["subscription_interval"].ToString(), out int iv) && iv > 0)
                        globalItem["subscription_interval"] = iv;
                    // 云端记录使用的调休标记与动作映射
                    foreach (var mkKey in new[] { "subscription_marker", "subscription_marker_disable", "subscription_marker_enable" })
                    {
                        if (json[mkKey] == null) continue;
                        int smk;
                        if (int.TryParse(json[mkKey].ToString(), out smk) && smk >= 0 && smk <= 10) globalItem[mkKey] = smk;
                    }
                    foreach (var actKey in new[] { "subscription_action_off", "subscription_action_on" })
                    {
                        if (json[actKey] == null) continue;
                        string av = json[actKey].ToString().Trim().ToLowerInvariant();
                        if (av != "enable" && av != "disable") continue;
                        globalItem[actKey] = av;
                    }
                    WriteSettingMerged( all); await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            // ================= 高速波形轻量化通道 (60ms) =================
            app.MapGet("/api/wave", () => {
                lock (StatusLock)
                {
                    int[] frameData = new int[200];
                    if (IsPlaying)
                    {
                        double actualElapsed = (DateTime.Now - CurrentAudioStartTime).TotalSeconds + CurrentAudioOffset;
                        int startIdx = (int)((actualElapsed + 0.05) * 200); short[] samples; lock (WaveformLock) { samples = CurrentWaveformSamples; }
                        if (samples.Length > 0)
                        {
                            int maxAbs = 0;
                            for (int i = 0; i < 200; i++) { int idx = startIdx + i; frameData[i] = (idx >= 0 && idx < samples.Length) ? samples[idx] : (short)0; if (Math.Abs(frameData[i]) > maxAbs) maxAbs = Math.Abs(frameData[i]); }
                            if (maxAbs > 0) { for (int i = 0; i < 200; i++) frameData[i] = (int)((frameData[i] / (double)maxAbs) * 100); }
                        }
                        else { for (int i = 0; i < 200; i++) { frameData[i] = (int)((Math.Sin((actualElapsed + 0.05 + (i / 200.0)) * 15) * 0.5 + Math.Sin((actualElapsed + 0.05 + (i / 200.0)) * 43) * 0.5) * 20); } }
                    }
                    return new JsonObject { ["is_playing"] = IsPlaying, ["frame_data"] = JsonSerializer.SerializeToNode(frameData) };
                }
            });

            // ================= 低速系统重载通道 (500ms) =================
            // 二维码载荷：本地页面 /res/mic-qr.html 拉它来画二维码（页面用随包的 qrcode.min.js 渲染）
            app.MapGet("/api/mic/qr", () =>
            {
                // 【务必】这里只能用"手机能直接发到的地址"（局域网 IPv4），**绝不能回退成虚拟网地址**：
                // 手机没有虚拟网卡，裸 UDP 发不到 10.x，把 10.x 当对端地址等于让手机白发一轮。
                // 拿不到局域网地址就留空 —— 手机端会只走 vnt 中继（mic.vnt_ip）。
                string ip = MicServer.AdvertisedHost;
                var o = new JsonObject
                {
                    // 走唯一入口：服务器/token/设备ID/协议/加密方式/数据密码/指纹 全部从配置取，避免漏字段
                    ["payload"] = VntManager.BuildQrPayloadFromConfig(ip, VntManager.MicPort, VntManager.MicCtlPort,
                        Environment.MachineName, ListenPort),
                    ["http_port"] = ListenPort,
                    ["mic_host"] = ip,
                    // 三个地址都给出来，省得"二维码里的 IP 和 vnt 里看到的不是一个"这种困惑：
                    //   vnt_lite_ip = 程序内置 vnt-lite 的地址；os_vnet_ip = 你自己那份 vnt 的地址（网卡上）
                    ["vnt_lite_ip"] = VntManager.LocalVirtualIp,
                    ["os_vnet_ip"] = VntManager.OsVnetIp,
                    // 排错：最近收到的数据面报文 / 话筒认不出的包（判断"手机发的包到底有没有到电脑"）
                    ["data_plane"] = VntProcess.DataPlaneDiag(),
                    ["vnt_configured"] = VntManager.VntConfigured
                };
                return o;
            });
            // 话筒状态（只读）：菜单与网页用它显示"开麦没有/几路会话/vnt 通没通"。
            // 刻意**不返回动态配对码**：那玩意只能在电脑本机的菜单里看，避免被远程读走。
            app.MapGet("/api/mic/status", () =>
            {
                var o = new JsonObject
                {
                    // 音频链路体检一行话：手机端会拉过去写进日志（"话筒没声音"靠它定位，不用翻电脑端）
                    ["audio_text"] = MicServer.AudioHealthText(),
                    ["enabled"] = VntManager.MicEnabled,
                    ["running"] = MicServer.Running,
                    ["bound"] = MicServer.BoundAddress,
                    ["audio_port"] = MicServer.Running ? MicServer.AudioPort : VntManager.MicPort,
                    ["ctl_port"] = MicServer.Running ? MicServer.CtlPort : VntManager.MicCtlPort,
                    // 网页/接口端口：被占用时会自动顺延（如 5000→5001），客户端必须读这里而不是写死 5000
                    ["http_port"] = ListenPort,
                    ["mic_host"] = MicServer.AdvertisedHost,
                    // 三个地址分别是什么，见 /api/mic/qr 的说明；网页/诊断看这里一眼就懂
                    ["advertised"] = MicServer.AdvertisedHost,
                    ["vnt_lite_ip"] = VntManager.LocalVirtualIp,
                    ["os_vnet_ip"] = VntManager.OsVnetIp,
                    ["sessions"] = MicServer.Registry.Count,
                    ["ducking"] = MicDuck.StatusText(),
                    ["max_minutes"] = VntManager.MicMaxMinutes,
                    ["resume_grace_s"] = VntManager.MicResumeGraceSeconds,
                    ["duck_percent"] = MicDuck.DuckPercent,
                    ["vnt_configured"] = VntManager.VntConfigured,
                    ["vnt_running"] = VntProcess.IsRunning,
                    ["stats"] = MicServer.Stats.Summary()
                };
                return o;
            });
            app.MapGet("/api/sys", () => {
                // 【手机端要用】把"全局总开关"一并返回，手机端才能在「设置」里显示并切换停用/启用。
                // 必须在下面的锁**之外**取：IsGlobalSwitchOn 会拿 ConfigIoLock，
                // 与 StatusLock/ManualLock 交叉持锁会有死锁风险。
                bool globalOn = true;
                try { globalOn = IsGlobalSwitchOn(); } catch { }
                lock (StatusLock) lock (ManualLock)
                {
                    double elapsed = ManualStatus == "playing" ? (DateTime.Now - MpStartTime).TotalSeconds + ManualPauseOffset : ManualPauseOffset;
                    return new JsonObject
                    {
                        ["is_playing"] = IsPlaying,
                        ["current_playing_name"] = CurrentPlayingName,
                        ["global_switch"] = globalOn,
                        ["sys_stats"] = new JsonObject { ["cpu"] = GetCpuUsage(), ["ram"] = GetRamUsage() },
                        ["manual_player"] = new JsonObject
                        {
                            // 拖动回执：数字变化 = 电脑端已按新位置起播（手机据此结束"定位中"状态）
                            ["seek_ack"] = ManualSeekAck,
                            ["seek_offset"] = ManualSeekAckOffset,
                            ["status"] = ManualStatus,
                            ["mode"] = ManualMode,
                            ["volume"] = ManualVolume,
                            ["current_index"] = ManualCurrentIndex,
                            ["elapsed"] = elapsed,
                            ["current_duration"] = ManualCurrentDuration,
                            ["queue"] = ManualQueue.DeepClone() // 降频到 500ms，极大降低内存 GC 压力
                        }
                    };
                }
            });

            // ================= 管理员专属接口（手机端「设置」顶部的管理员面板）=================
            // 安全：这三个都在 MicApiBridge 的**管理员门禁**后面 —— 普通设备拿不到码、也踢不了人。
            app.MapGet("/api/mic/code", () => {
                try
                {
                    var now = DateTime.Now;
                    string code = MicProtocol.CurrentCode(VntManager.MicCodeSeed, now);
                    double period = MicProtocol.CodePeriodSeconds;
                    double left = period - (DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond % period);
                    return Results.Json(new JsonObject
                    {
                        ["code"] = code,
                        ["seconds_left"] = (int)Math.Max(0, Math.Ceiling(left)),
                        ["period"] = (int)period
                    });
                }
                catch (Exception ex) { return Results.Json(new JsonObject { ["error"] = ex.Message }); }
            });

            app.MapGet("/api/mic/devices", () => {
                var arr = new JsonArray();
                try
                {
                    foreach (var s in MicServer.Registry.All)
                        arr.Add(new JsonObject
                        {
                            ["sid"] = s.Sid,
                            ["device"] = s.DeviceId ?? "",
                            ["ip"] = s.SourceIp ?? "",
                            ["admin"] = !string.IsNullOrWhiteSpace(s.DeviceId) && VntManager.IsAdminDevice(s.DeviceId)
                        });
                }
                catch { }
                return Results.Json(arr);
            });

            app.MapPost("/api/mic/kick", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync());
                    int sid = -1;
                    try { if (json?["sid"] != null) sid = json["sid"].GetValue<int>(); } catch { }
                    string dev = json?["device"]?.ToString() ?? "";
                    int kicked = 0;
                    try
                    {
                        foreach (var s in MicServer.Registry.All.ToList())
                        {
                            bool hit = (sid >= 0 && s.Sid == sid) ||
                                       (!string.IsNullOrWhiteSpace(dev) && string.Equals(s.DeviceId ?? "", dev, StringComparison.OrdinalIgnoreCase));
                            if (!hit) continue;
                            MicServer.Registry.Remove(s.Sid);
                            if (!string.IsNullOrWhiteSpace(s.DeviceId)) VntManager.RemoveTrustedDevice(s.DeviceId);   // 踢出 = 断开 + 解除信任
                            if (!string.IsNullOrWhiteSpace(s.DeviceId)) VntManager.RemoveAdminDevice(s.DeviceId);     // 也解除管理员身份
                            kicked++;
                        }
                    }
                    catch { }
                    PlayLog.Write("远程", "手机端踢出", "warn", detail: $"管理员在手机上踢出设备（sid={sid} device={dev}），实际断开 {kicked} 个会话");
                    // 注意：MapPost 的 RequestDelegate 是 Task，不能 return IResult —— 直接写响应
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(new JsonObject { ["ok"] = true, ["kicked"] = kicked }.ToJsonString());
                }
            });
            app.MapPost("/api/player/cmd", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); if (json == null) return; string cmd = json["cmd"]?.ToString();
                    // 【绝不在锁里结束进程】Kill 会等目标进程真正退出（以前还要额外渐弱 800ms）。
                    // 老写法在 lock (ManualLock) 里直接 KillManualProcess()：手机"停止 → 删队列"两连击
                    // 就把锁攥住 1.6 秒以上，而 /api/sys 轮询与界面刷新都要这把锁 → 整个程序卡死（真机复现）。
                    // 现在：锁内只把进程**摘出来**（纳秒级），结束与清理放到锁外（见 StopOutsideLock）。
                    Process toKill = null;
                    lock (ManualLock)
                    {
                        if (cmd == "play") { if (ManualStatus == "paused" || (ManualStatus == "stopped" && ManualQueue.Count > 0)) { if (ManualCurrentIndex < 0 || ManualCurrentIndex >= ManualQueue.Count) ManualCurrentIndex = 0; ManualStatus = "playing"; PlayLog.Write("播控", "播放", "info", detail: $"从第 {ManualCurrentIndex + 1} 首继续（队列 {ManualQueue.Count} 首）"); } }   // 【修复】索引越界时回到第一首（以前不纠正 → 点了播放毫无反应）
                        else if (cmd == "pause") { if (ManualStatus == "playing") { try { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; } catch { } ManualStatus = "paused"; toKill = TakeManualProcessLocked(); PlayLog.Write("播控", "暂停", "info", detail: $"已暂停于 {Math.Round(ManualPauseOffset, 1)} 秒"); } }
                        else if (cmd == "stop") { ManualStatus = "stopped"; ManualCurrentIndex = 0; ManualPauseOffset = 0; toKill = TakeManualProcessLocked(); PlayLog.Write("播控", "停止", "info"); }
                        else if (cmd == "next" || cmd == "prev") { toKill = TakeManualProcessLocked(); ManualPauseOffset = 0; if (ManualQueue.Count > 0) { if (cmd == "next") ManualCurrentIndex = (ManualCurrentIndex + 1) % ManualQueue.Count; else ManualCurrentIndex = (ManualCurrentIndex - 1 + ManualQueue.Count) % ManualQueue.Count; } ManualStatus = "playing"; PlayLog.Write("播控", cmd == "next" ? "下一首" : "上一首", "info", detail: $"切到第 {ManualCurrentIndex + 1} 首"); }
                        else if (cmd == "play_idx") { toKill = TakeManualProcessLocked(); ManualPauseOffset = 0; ManualCurrentIndex = json["val"]?.GetValue<int>() ?? 0; ManualStatus = "playing"; PlayLog.Write("播控", "选播", "info", detail: $"指定第 {ManualCurrentIndex + 1} 首"); }
                        else if (cmd == "seek")
                        {
                            // 【拖动进度条不能把电脑端顶死】手机拖动会连续发多个 seek。
                            // 老写法在 ManualLock 里做"同步渐弱 + 杀进程"（最长几百毫秒），
                            // 而播放线程每 200ms 也要拿同一把锁 → 两下就互相顶死，整个电脑端卡住。
                            // 现在只记下最新目标位置 + 挂一个待处理标记，由播放线程在下一次 tick 里统一处理：
                            // 多个 seek 自动合并成一个，锁内只做异步 Kill，不做任何同步等待。
                            ManualPauseOffset = Math.Max(0, json["val"]?.GetValue<double>() ?? 0);
                            ManualSeekPending = true;
                            _seekAckPending = true;      // 起播成功后补一次回执（见 ManualPlayerWorker）
                            PlayLog.Write("播控", "拖动", "info", detail: $"跳到 {Math.Round(ManualPauseOffset, 1)} 秒（已交给播放线程处理）");
                        }
                        else if (cmd == "mode") { var modes = new[] { "sequential", "loop", "single" }; ManualMode = modes[(Array.IndexOf(modes, ManualMode) + 1) % 3]; PlayLog.Write("播控", "切换模式", "info", detail: "当前模式 " + ManualMode); }
                        else if (cmd == "volume")
                        {
                            ManualVolume = json["val"]?.GetValue<int>() ?? 100;
                            // 同 seek：音量变化需要重启进程，但**绝不能在锁里同步渐弱**（会顶死播放线程）。
                            // 记下已播进度 + 挂待处理标记，交给播放线程统一重启。
                            if (ManualStatus == "playing" && MpProcess != null && !MpProcess.HasExited)
                            {
                                try { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; } catch { }
                                ManualVolumePending = true;
                            }
                            PlayLog.Write("播控", "调音量", "info", detail: $"播控音量 {ManualVolume}%");
                        }
                        else if (cmd == "add")
                        {
                            var val = json["val"];
                            if (val != null)
                            {
                                string p = val["path"]?.ToString() ?? ""; string type = val["type"]?.ToString() ?? "";
                                if (!IsSafeAudioPath(p) && type != "folder") return;
                                p = ResolveAudioPath(p);
                                if (type == "folder" && Directory.Exists(p)) { var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" }; int added = 0; foreach (var f in Directory.GetFiles(p).Where(f => exts.Contains(Path.GetExtension(f).ToLower())).OrderBy(f => f)) { var item = new JsonObject(); item["name"] = Path.GetFileName(f); item["path"] = f.StartsWith(AppDataDir) ? f.Substring(AppDataDir.Length).TrimStart('\\', '/').Replace('\\', '/') : f; item["type"] = "file"; ManualQueue.Add(item); added++; } PlayLog.Write("播控", "加队", "info", name: val["name"]?.ToString(), detail: $"目录展开 {added} 首加入播控队列"); }
                                else { ManualQueue.Add(val.DeepClone()); PlayLog.Write("播控", "加队", "info", name: val["name"]?.ToString(), detail: "加入播控队列"); }
                            }
                        }
                        else if (cmd == "remove") { int idx = json["val"]?.GetValue<int>() ?? -1; if (idx >= 0 && idx < ManualQueue.Count) { string removedName = ""; try { removedName = ManualQueue[idx]?["name"]?.ToString() ?? ""; } catch { } ManualQueue.RemoveAt(idx); if (ManualCurrentIndex > idx) ManualCurrentIndex--; else if (ManualCurrentIndex == idx) { toKill = TakeManualProcessLocked(); ManualPauseOffset = 0; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; } } PlayLog.Write("播控", "移出队列", "info", name: removedName); } }
                    }
                    // 锁外立即结束旧进程（渐弱已移除；Kill 会等进程退出，不能拿锁等）
                    if (toKill != null) StopOutsideLock(toKill);
                    // 切歌/停止/删队列：**立即**回执一次，让手机端把"定位中"的钉住位置清掉（否则进度条会定在原处）
                    if (cmd == "stop" || cmd == "next" || cmd == "prev" || cmd == "play_idx" || cmd == "remove" || cmd == "pause")
                    {
                        _seekAckPending = false;
                        ManualSeekAck++;
                        ManualSeekAckOffset = ManualPauseOffset;
                    }
                    await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.Run($"http://0.0.0.0:{ListenPort}");
        }

        /// <summary>
        /// 锁内调用：把当前手动播放进程**摘出来**交给调用方（纳秒级，绝不做渐弱/等待）。
        /// 渐弱是同步 Sleep + COM 音频会话枚举，放在 ManualLock 里会把 /api/sys 轮询与界面刷新一起顶死。
        /// </summary>
        static Process TakeManualProcessLocked()
        {
            var p = MpProcess;
            MpProcess = null;
            return p;
        }

        /// <summary>
        /// 锁外结束进程并清理台账。
        /// 【2026-10-15】渐弱已移除 → 这里改成**立即结束**，所以原来的"1.5 秒看门狗"也一并删掉了
        /// （那个看门狗是为了防"音频会话 COM 调用长时间不返回"，而现在根本不再碰 COM）。
        /// "在锁外做"这一点仍然重要：Kill 会等进程真正退出，不能拿着 ManualLock 等。
        /// </summary>
        static void StopOutsideLock(Process proc)
        {
            if (proc == null) return;
            Interlocked.Increment(ref _pendingKillCount);
            try
            {
                AudioFade.StopNow(proc);
            }
            finally
            {
                UnregisterAudioProcess(proc);
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                try { proc.Dispose(); } catch { }
                Interlocked.Decrement(ref _pendingKillCount);
            }
        }

        // ================= 统一进程台账：登记 / 注销 / 精确终止 =================

        /// <summary>
        /// 【起播后把该进程的会话音量拉回 100%，带重试】
        /// 为什么必须重试：音频会话是 ffplay **真正打开音频设备时**才由 Windows 创建的（启动后几十~几百毫秒），
        /// 紧跟 proc.Start() 单次调用几乎必然拿不到会话 → SetProcessVolume 返回 false → 静默失败，
        /// 于是"停用广播后按应用音量被写成 0%"的问题依旧存在（话筒那版就是靠 5 秒心跳重试才生效的）。
        /// 这里用后台线程按递增间隔重试，成功即退出；进程已退出则直接放弃（不刷日志）。
        /// </summary>
        public static void ForceProcessVolumeSoon(Process proc, string tag)
        {
            if (proc == null) return;
            try
            {
                var th = new Thread(() =>
                {
                    foreach (var wait in new[] { 200, 400, 800, 1500, 3000 })
                    {
                        try { Thread.Sleep(wait); } catch { }
                        try
                        {
                            bool exited; try { exited = proc.HasExited; } catch { exited = true; }
                            if (exited) return;
                            // 若此刻正处于"话筒闪避"状态（音乐被压到 20%），就不能强行拉回 100%，
                            // 否则喊话期间的压低会被这条起播逻辑立刻撤销（等于闪避失效）。
                            float target = MicDuck.IsDucked ? MicDuck.DuckLevel : 1.0f;
                            if (AudioFade.SetProcessVolume(proc, target))
                            {
                                try { PlayLog.Write("系统", "会话音量", "info", detail: $"{tag}：已把 ffplay 的会话音量设为 {target * 100:F0}%（{(MicDuck.IsDucked ? "话筒说话中，保持压低" : "重试成功，避免'停用后没声音'")}）"); } catch { }
                                return;
                            }
                        }
                        catch { }
                    }
                    try { PlayLog.Write("系统", "会话音量", "warn", detail: $"{tag}：多次重试仍未能设置会话音量（进程可能已结束或该设备没有音频会话）"); } catch { }
                }) { IsBackground = true, Name = "vol-fix" };
                th.Start();
            }
            catch { }
        }
        public static Process RegisterAudioProcess(string purpose, string name, string path)
        {
            Process proc = null;
            try
            {
                proc = new Process { StartInfo = new ProcessStartInfo { FileName = FfplayPath, CreateNoWindow = true, UseShellExecute = false } };
                proc.StartInfo.ArgumentList.Add("-nodisp");
                proc.StartInfo.ArgumentList.Add("-autoexit");
                proc.Start();
                ForceProcessVolumeSoon(proc, purpose);   // 防将来启用本函数时踩"按应用音量=0"的坑
                var info = new AudioProcessInfo
                {
                    Pid = proc.Id,
                    Purpose = purpose,
                    Name = name ?? "",
                    Path = path ?? "",
                    StartTime = DateTime.Now,
                    Proc = proc
                };
                ActiveAudioProcesses[proc.Id] = info;
                PlayLog.Write("进程", "启动", "info", name: info.Name, path: info.Path, detail: $"{purpose} pid={proc.Id}");
                return proc;
            }
            catch (Exception ex)
            {
                LogHelper.Log("RegisterAudioProcess Failed", ex);
                try { proc?.Dispose(); } catch { }
                return null;
            }
        }

        public static void UnregisterAudioProcess(Process proc)
        {
            if (proc == null) return;
            try { ActiveAudioProcesses.TryRemove(proc.Id, out _); } catch { }
        }

        public static int KillLedgerProcesses(string purpose = null)
        {
            int killed = 0;
            foreach (var kv in ActiveAudioProcesses.ToArray())
            {
                var info = kv.Value;
                if (purpose != null && info.Purpose != purpose) continue;
                try
                {
                    if (info.Proc != null && !info.Proc.HasExited)
                    {
                        // 渐弱已移除（2026-10-15）：一律立即结束，不再按用途区分（波形解码本来也是直接杀）。
                        AudioFade.StopNow(info.Proc);
                        killed++;
                    }
                }
                catch { }
                ActiveAudioProcesses.TryRemove(kv.Key, out _);
                try
                {
                    PlayLog.Write("进程", "终止", "warn", name: info.Name,
                        detail: $"{info.Purpose} pid={kv.Key} 已运行 {Math.Round((DateTime.Now - info.StartTime).TotalSeconds, 1)} 秒");
                }
                catch { }
            }
            return killed;
        }

        /// <summary>
        /// 统一的"停干净"流程：手动播放、定时播放、波形解码 —— 所有音频输出进程一并终止。
        /// 顺序固定为「先改状态、再杀进程、后复位标记」，避免 200ms 轮询把进程从原地复活。
        /// </summary>
        /// <summary>
        /// 【恢复广播时仍要做的一件事】把"按应用音量"从 0 拉回来。
        /// 原因：**旧版本**（4.0.0 及更早）的渐弱停止会把 ffplay 的会话音量写成 0，而 Windows 按应用
        /// 持久记住该值 → 恢复广播后新起的播放会以 0% 出声，表现为"停用再恢复就没声音，只能重启电脑"。
        /// 渐弱已于 2026-10-15 移除、不再产生这种状态，但**旧版本留在注册表里的 0 还在**，
        /// 所以这条"恢复时拉满"必须保留（而且 SetProcessVolume 现在还会顺手解除会话静音）。
        /// 这里做三件事：①清掉话筒闪避状态；②把当前还活着的定时/手动播放进程音量拉回 100%；
        /// ③写一条日志便于事后核对（起播时的"拉满"由 RegisterAudioProcess / 手动播控起播点负责）。
        /// </summary>
        public static void RestoreAudioAfterEnable()
        {
            // 【非阻塞】这里原来是同步调 COM（MicDuck.ForceRestore 与 SetProcessVolume 都会枚举
            // 所有输出设备的音频会话）。COM 调用在音频服务异常时可能长时间不返回 ——
            // 那样"恢复广播系统运行"的 HTTP 请求就会一直挂着、前端一直转圈。
            // 这个动作只是"兜底把音量拉回 100%"，调用方不需要它的结果，所以直接丢到线程池。
            var sp = ScheduleProcess; var mp = MpProcess;
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    int fixedCount = 0;
                    try { MicDuck.ForceRestore(); } catch { }
                    try { if (sp != null && !sp.HasExited) { if (AudioFade.SetProcessVolume(sp, 1.0f)) fixedCount++; } } catch { }
                    try { if (mp != null && !mp.HasExited) { if (AudioFade.SetProcessVolume(mp, 1.0f)) fixedCount++; } } catch { }
                    try
                    {
                        PlayLog.Write("系统", "恢复音量", "info",
                            detail: fixedCount > 0
                                ? $"已把 {fixedCount} 个正在播放的进程音量拉回 100%"
                                : "已清掉静音/闪避状态（起播时也会强制拉回 100%）");
                    }
                    catch { }
                });
            }
            catch { }
        }
        public static void StopAllAudio(string reason, bool pauseManual = true)
        {
            try
            {
                Process manualProc = null;
                lock (ManualLock)
                {
                    if (pauseManual && ManualStatus == "playing" && MpProcess != null)
                    {
                        try { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; } catch { }
                        ManualStatus = "paused";
                        // 手动播控被抢占必须留独立日志：原来只有一行无条件的"系统/停止"，
                        // 事后无法确认到底有没有音乐被切断（用户专门问过这件事）。
                        try
                        {
                            string nm = "";
                            try { nm = ManualQueue.Count > ManualCurrentIndex ? (ManualQueue[ManualCurrentIndex]?["name"]?.ToString() ?? "") : ""; } catch { }
                            PlayLog.Write("播控", "被抢占", "warn", name: nm,
                                detail: $"{reason}：手动播控《{nm}》已暂停（已播 {Math.Round(ManualPauseOffset, 1)} 秒，进度保留），队列与进度都在，点播放可续播");
                        }
                        catch { }
                    }
                    // 状态与台账在锁内立刻改掉（界面/日志马上反映），进程结束放到锁外：
                    // Kill 会等进程真正退出，在锁里做会把 /api/sys 的轮询一起卡住。
                    manualProc = MpProcess;
                    MpProcess = null;
                    UnregisterAudioProcess(manualProc);
                    ChangePlayingStatus(false, "");
                }
                if (manualProc != null)
                {
                    AudioFade.StopNow(manualProc);
                    try { PlayLog.Write("播控", "停止", "info", detail: "已结束手动播放进程（渐弱已移除，改为立即停止）"); } catch { }
                }
                Interlocked.Increment(ref SequenceGeneration);
                var sp = Interlocked.Exchange(ref ScheduleProcess, null);
                if (sp != null)
                {
                    AudioFade.StopNow(sp);
                    try { PlayLog.Write("播放", "抢占", "warn", detail: $"{reason}：已结束正在播放的定时任务"); } catch { }
                }
                KillLedgerProcesses();
                ChangePlayingStatus(false, "");
                PlayLog.Write("系统", "停止", "info", detail: reason);
            }
            catch (Exception ex) { LogHelper.Log("StopAllAudio Failed", ex); }
        }

        /// <summary>用于播放中的暂停/拖动/改音量：保留进度，暂存已播秒数后立即结束进程（马上会续播）。</summary>
        public static void PauseManualForRestart()
        {
            if (ManualStatus == "playing" && MpProcess != null)
            {
                try { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; } catch { }
                try { ManualStatus = "paused"; } catch { }
            }
            KillManualProcess();
        }

        /// <summary>结束手动播放进程（立即结束，不渐弱）。返回 true 表示确实结束了一个还活着的进程。</summary>
        public static bool KillManualProcess()
        {
            var proc = MpProcess;
            bool killed = false;
            if (proc != null && !proc.HasExited)
            {
                killed = AudioFade.StopNow(proc);
                ChangePlayingStatus(false, "");
            }
            UnregisterAudioProcess(proc);
            MpProcess = null;
            return killed;
        }

        /// <summary>
        /// 调休判定（与前端 shouldPlayToday 保持同一语义）：
        ///   1) 收集所有命中今天的调休记录；
        ///   2) 「分标记规则(1-10)」优先于「全局规则(0)」——只要本任务标记有专属规则，全局规则一律让位；
        ///   3) 同一优先级内，后写入的记录覆盖先写入的（按文件顺序取最后一条）；
        ///   4) 一条都没有命中时，才回落到 specific_dates / days。
        /// </summary>
        /// <summary>
        /// 任务的"到达时间后"行为：immediate = 立即终止正在播放的音乐（默认，与历史行为一致）；
        /// finish_track = 播完当前这首（只对上一个定时任务生效；手动播控永远立即抢断）。
        /// </summary>
        public static string StopModeOf(JsonObject task)
        {
            string m = task?["stop_mode"]?.ToString();
            return string.Equals(m, "finish_track", StringComparison.OrdinalIgnoreCase) ? "finish_track" : "immediate";
        }

        static bool ShouldPlayToday(JsonObject task)
        {
            var now = DateTime.Now; string todayDate = now.ToString("yyyy-MM-dd"); int todayWeekday = (int)now.DayOfWeek; if (todayWeekday == 0) todayWeekday = 7;
            int taskMarker = 1; if (task["tiaoxiu_marker"] != null) int.TryParse(task["tiaoxiu_marker"].ToString(), out taskMarker);
            var settings = ReadSettingMerged();

            var matches = new List<Tuple<int, int, string>>(); // 优先级(0=分标记,1=全局) / 顺序 / 动作
            int order = 0;
            foreach (var item in settings)
            {
                if (item is not JsonObject o) continue;
                if (o["_type"]?.ToString() != "tiaoxiu") continue;
                string tStart = o["start_date"]?.ToString() ?? o["date"]?.ToString();
                string tEnd = o["end_date"]?.ToString() ?? tStart;
                int tMarker = 0; if (o["marker"] != null) int.TryParse(o["marker"].ToString(), out tMarker);
                string tAction = o["action"]?.ToString();
                if (string.IsNullOrEmpty(tStart) || string.IsNullOrEmpty(tEnd)) continue;
                if (string.Compare(todayDate, tStart) < 0 || string.Compare(todayDate, tEnd) > 0) continue;

                if (tMarker == 0) matches.Add(Tuple.Create(1, order++, tAction));
                else if (tMarker == taskMarker) matches.Add(Tuple.Create(0, order++, tAction));
            }

            if (matches.Count > 0)
            {
                // 分标记规则(优先级0) 高于全局规则(优先级1)；同优先级内「后写入的覆盖先写入的」
                matches.Sort((a, b) =>
                {
                    if (a.Item1 != b.Item1) return a.Item1.CompareTo(b.Item1);
                    return b.Item2.CompareTo(a.Item2);
                });
                return matches[0].Item3 == "enable";
            }

            var specificDates = task["specific_dates"]?.AsArray(); if (specificDates != null) { foreach (var d in specificDates) { if (d?.ToString() == todayDate) return true; } }
            var days = task["days"]?.AsArray(); if (days != null) { foreach (var d in days) { if (int.TryParse(d?.ToString(), out int dayInt) && dayInt == todayWeekday) return true; } }
            return false;
        }

        /// <summary>正在执行的定时任务 id 集合，避免同一任务被并发拉起。</summary>
        static readonly HashSet<int> RunningTaskIds = new HashSet<int>();

        /// <summary>上一次调度心跳的时刻：用来发现"调度线程被卡住"（两次间隔正常是 1 秒）。</summary>
        static DateTime _lastSchedulerTick = DateTime.MinValue;

        static void SchedulerWorker(object state)
        {
            if (!IsGlobalSwitchOn()) return;
            try
            {
                var now = DateTime.Now; string todayDate = now.ToString("yyyy-MM-dd"); int nowSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
                // 【绝不静默】两次调度间隔正常是 1 秒。若这次隔了 >5 秒，说明线程池/磁盘/系统休眠把它卡住了，
                // 这期间到点的铃可能被触发窗口漏掉 —— 记一条 warn，保证事后一定查得到原因。
                try
                {
                    if (_lastSchedulerTick != DateTime.MinValue)
                    {
                        double gap = (now - _lastSchedulerTick).TotalSeconds;
                        if (gap > 5)
                            PlayLog.Write("排程", "调度卡顿", "warn",
                                detail: $"上次调度 {_lastSchedulerTick:HH:mm:ss}，本次 {now:HH:mm:ss}，间隔 {gap:F1} 秒（正常应为 1 秒）；"
                                      + "这期间到点的任务可能被漏掉，请检查系统休眠、杀毒软件扫描或磁盘占用");
                    }
                }
                catch { }
                _lastSchedulerTick = now;
                foreach (var t in ReadJsonl("tasks.jsonl"))
                {
                    var taskObj = t as JsonObject; if (taskObj == null) continue; if (!ShouldPlayToday(taskObj)) continue;
                    string taskTime = taskObj["start_time"]?.ToString() ?? "00:00:00"; if (taskTime.Length == 5) taskTime += ":00";
                    var parts = taskTime.Split(':'); if (parts.Length != 3) continue;
                    int tSec;
                    if (!int.TryParse(parts[0], out int hh) || !int.TryParse(parts[1], out int mm) || !int.TryParse(parts[2], out int ss)) continue;
                    tSec = hh * 3600 + mm * 60 + ss;
                    int diff = (nowSec - tSec) % 86400;
                    // 触发窗口见 TriggerWindowSeconds（默认 60 秒）。原来只有 5 秒：
                    // 只要调度晚执行超过 5 秒（休眠唤醒、杀软扫描、线程池繁忙），这个铃当天就会被**永久跳过且不留任何日志**。
                    // 防重仍由 TriggeredTasks 保证（同一任务同一天只触发一次），所以放宽窗口不会重复播。
                    if (IsTriggerWindow(nowSec, tSec))
                    {
                        string taskKey = $"{(taskObj["id"]?.ToString() ?? "0")}_{taskTime}_{todayDate}";
                        if (TriggeredTasks.TryAdd(taskKey, true))
                        {
                            // 延迟 >5 秒单独记 warn：铃还是响了，但能看出系统当时卡过。
                            bool late = diff > 5;
                            PlayLog.Write("排程", late ? "延迟触发" : "触发", late ? "warn" : "info",
                                task: "ID " + (taskObj["id"]?.ToString() ?? "?"),
                                detail: late
                                    ? $"任务时间 {taskTime}，实际触发 {now:HH:mm:ss}（延迟 {diff} 秒 —— 系统当时可能卡顿或刚从休眠唤醒）"
                                    : $"任务时间 {taskTime}，触发时刻 {now:HH:mm:ss}");
                            var captured = taskObj;
                            Task.Run(() => PlayScheduledTask(captured, "[定时]"));
                        }
                    }
                }
            }
            catch (Exception ex) { LogHelper.Log("Scheduler Exception", ex); }
        }

        // ================= 定时播放（含抢占、日志、断点续播、引用失效提示） =================

        /// <summary>播放单个曲目，返回实际播放秒数；启动失败返回负数。</summary>
        static double PlayOneTrack(string path, string name, int volume, double maxSeconds, string taskLabel)
        {
            Process proc = null;
            DateTime started = DateTime.Now;
            try
            {
                proc = StartScheduledFfplay(path, name, volume, maxSeconds);
                if (proc == null) return -1;
                Interlocked.Exchange(ref ScheduleProcess, proc);
                proc.WaitForExit();
                double played = (DateTime.Now - started).TotalSeconds;
                int exitCode = -1; try { exitCode = proc.ExitCode; } catch { }
                PlayLog.Write("播放", "结束", "info", task: taskLabel, name: name, path: path,
                    detail: $"实际播放 {Math.Round(played, 1)} 秒，退出码 {exitCode}");
                return played;
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Play One Track Failed: {path}", ex);
                return -1;
            }
            finally
            {
                UnregisterAudioProcess(proc);
                Interlocked.CompareExchange(ref ScheduleProcess, null, proc);
                try { proc?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 构建 ffplay 参数（纯函数，便于自检断言）。
        /// 约定：音量以 volume=0.00 形式传给 -af；maxSeconds&gt;0 时追加 -t 限制播放时长。
        /// </summary>
        public static List<string> BuildFfplayArgs(string path, int volume, double maxSeconds)
        {
            var args = new List<string> { "-nodisp", "-autoexit" };
            args.Add("-af");
            args.Add($"volume={(volume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
            if (maxSeconds > 0)
            {
                args.Add("-t");
                args.Add(maxSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            args.Add(path ?? "");
            return args;
        }

        /// <summary>按正确顺序构建参数并启动定时播放进程（登记入台账）。</summary>
        static Process StartScheduledFfplay(string path, string name, int volume, double maxSeconds)        {
            Process proc = null;
            try
            {
                proc = new Process { StartInfo = new ProcessStartInfo { FileName = FfplayPath, CreateNoWindow = true, UseShellExecute = false } };
                foreach (var a in BuildFfplayArgs(path, volume, maxSeconds)) proc.StartInfo.ArgumentList.Add(a);
                proc.Start();
                // 【关键修复】定时打铃起播后立刻把该应用的会话音量拉回 100%，并解除会话静音。
                // 背景：**旧版本**的渐弱会把 ffplay 的会话音量写成 0，Windows 把会话音量当作"按应用音量"
                // 持久记住 → 之后打铃的 ffplay 会以 0% 出声：进程在跑、日志一切正常、却完全没声音
                //（真机复现，只能重启电脑）。渐弱虽已移除，但注册表里的旧 0、以及"会话被静音"仍会发生，
                // 所以起播点这次强制拉满是必须的。
                ForceProcessVolumeSoon(proc, "定时播放");   // 带重试：会话要等 ffplay 打开设备后才存在
                ActiveAudioProcesses[proc.Id] = new AudioProcessInfo
                {
                    Pid = proc.Id, Purpose = "定时播放", Name = name ?? "", Path = path ?? "",
                    StartTime = DateTime.Now, Proc = proc
                };
                PlayLog.Write("进程", "启动", "info", name: name, path: path, detail: $"定时播放 pid={proc.Id}");
                return proc;
            }
            catch (Exception ex)
            {
                LogHelper.Log("StartScheduledFfplay Failed", ex);
                try { proc?.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// 触发窗口（秒）：任务到点后多少秒内仍算"该触发"。
        /// 原为 5 秒 —— 太窄：调度线程只要被卡住超过 5 秒（休眠唤醒/杀软扫描/线程池繁忙），
        /// 这个铃当天就会被永久跳过、而且**一条日志都不留**。改为 60 秒，
        /// 并且延迟 >5 秒时记"延迟触发"warn、调度本身卡顿记"调度卡顿"warn（见 SchedulerWorker）。
        /// </summary>
        public const int TriggerWindowSeconds = 60;

        /// <summary>供自检使用：暴露调度触发窗口判定（与 SchedulerWorker 完全同源）。</summary>
        public static bool IsTriggerWindow(int nowSec, int taskSec)
        {
            int diff = (nowSec - taskSec) % 86400;
            return diff >= 0 && diff <= TriggerWindowSeconds;
        }

        /// <summary>供自检使用：计算下一次排程（与桌面端/网页端同源）。</summary>
        public static int SecondsToNextTask(IEnumerable<JsonObject> tasks, int nowSec, DateTime today)
        {
            int best = int.MaxValue;
            foreach (var o in tasks)
            {
                if (o == null) continue;
                string s = o["start_time"]?.ToString() ?? ""; if (s.Length == 5) s += ":00";
                var p = s.Split(':'); if (p.Length != 3) continue;
                if (!int.TryParse(p[0], out int h) || !int.TryParse(p[1], out int m) || !int.TryParse(p[2], out int sec)) continue;
                int at = h * 3600 + m * 60 + sec;
                int delta = at - nowSec; if (delta < 0) delta += 86400;
                if (delta < best) best = delta;
            }
            return best == int.MaxValue ? -1 : best;
        }

        static void PlayScheduledTask(JsonObject task, string prefix)
        {
            int taskId = 0; try { taskId = task["id"]?.GetValue<int>() ?? 0; } catch { }
            bool locked = false;
            int myGeneration = 0;
            int myTicket = 0;          // 票号在"确认要播"时再领：被跳过的任务不该让等待中的任务白白让位
            var playedIds = new HashSet<string>();
            try
            {
                lock (RunningTaskIds)
                {
                    if (!RunningTaskIds.Add(taskId)) return;
                    locked = true;
                }
                FinishTrackRequested.TryRemove(taskId, out _);   // 清掉上一轮留给本任务的陈旧请求

                // 注意：真正的"抢占"放在下面——先确认这个任务确实有内容可播（全局开关打开、队列非空），
                // 再去停正在播放的音频。原来的顺序是无条件先停，结果"全局停用"或"引用全失效"的任务
                // 什么都没播，却已经把正在放的音乐掐断了（用户报过这个）。
                PlayLog.Write("排程", "执行", "info", task: "ID " + taskId, detail: "开始执行定时任务");

                if (!IsGlobalSwitchOn())
                {
                    PlayLog.Write("排程", "跳过", "warn", task: "ID " + taskId, detail: "全局开关处于停用状态");
                    return;
                }

                var musicIds = task["music_ids"]?.AsArray();
                if (musicIds == null || musicIds.Count == 0)
                {
                    string msg = $"任务 ID {taskId} 未配置任何播放内容，已跳过";
                    PlayLog.Write("排程", "跳过", "warn", task: "ID " + taskId, detail: msg);
                    AlertCenter.Publish("排程", msg, "warn", dedupKey: $"empty-{taskId}");
                    return;
                }

                int durationM = task["duration_m"]?.GetValue<int>() ?? 0;
                int durationS = task["duration_s"]?.GetValue<int>() ?? 0;
                double totalDurationSec = durationM * 60 + durationS;
                DateTime taskStart = DateTime.Now;
                int volume = task["volume"]?.GetValue<int>() ?? 100;

                var allMusic = ReadJsonl("music.jsonl");
                string seqMode = task["sequence_mode"]?.ToString() ?? "sequential";
                List<string> playList = new List<string>(); foreach (var m in musicIds) playList.Add(m?.ToString() ?? "");
                if (seqMode == "random") { var rnd = new Random(); playList = playList.OrderBy(x => rnd.Next()).ToList(); }

                // 把"音乐库引用 → 实际播放队列"的展开逻辑抽成纯函数（可自检），这里只负责推送提示
                var expandedQueue = ExpandTaskQueue(task, allMusic, playList, out var problemIds, out var resumeNotes, out var warnNotes);
                foreach (var w in warnNotes)
                    PlayLog.Write("预警", "跳过", "info", task: "ID " + taskId, detail: w);
                if (problemIds.Count > 0)
                {
                    string idList = string.Join("、", problemIds.Distinct());
                    AlertCenter.Publish("排程", $"任务 ID {taskId} 引用的编号 {idList} 已失效（音乐被删除或文件丢失），已自动跳过；请到音乐库重新指定。", "error",
                        dedupKey: $"task-ref-{taskId}-{idList}-{DateTime.Now:yyyyMMdd}");
                }

                if (expandedQueue.Count == 0)
                {
                    string why = warnNotes.Count > 0
                        ? "编号 1000（气象预警）本次不播报：" + string.Join("；", warnNotes)
                        : (problemIds.Count > 0 ? "所有引用均失效，本次无内容可播" : "播放队列为空，本次无内容可播");
                    PlayLog.Write("排程", "跳过", "warn", task: "ID " + taskId, detail: why);
                    return;
                }

                // 到这里才确认"真的要播"。
                // 【语义（用户 2026-10-12 确认）】stop_mode 只描述"**本任务到达自己的结束时间时怎么停**"：
                //   immediate（默认）= 到点立即切掉正在播的那一首；
                //   finish_track     = 等当前这一首自然放完再结束任务（第 n 首播完即停）。
                // **任务与任务之间一律立即抢断**（原"温柔抢占"已按用户要求移除）：新任务到点直接接管、
                // 不等待旧任务，这也天然满足"若下一个任务有冲突则抢断"。
                string stopMode = StopModeOf(task);
                myTicket = Interlocked.Increment(ref ScheduledTicket);   // 真的要播了才领票号
                StopAllAudio($"定时任务 ID {taskId} 抢占");
                myGeneration = Volatile.Read(ref SequenceGeneration);
                CurrentScheduledTaskId = taskId;
                taskStart = DateTime.Now;      // 抢占（含渐弱/等待）花掉的时间不计入本任务的时长上限

                foreach (var m in expandedQueue)
                {
                    if (Volatile.Read(ref SequenceGeneration) != myGeneration)
                    {
                        PlayLog.Write("排程", "中止", "warn", task: "ID " + taskId, detail: "任务已被新的定时任务抢占，剩余曲目不再播放");
                        return;
                    }
                    if (!IsGlobalSwitchOn())
                    {
                        PlayLog.Write("排程", "中止", "warn", task: "ID " + taskId, detail: "全局开关被停用，任务中止");
                        return;
                    }
                    if (totalDurationSec > 0 && (DateTime.Now - taskStart).TotalSeconds >= totalDurationSec) break;

                    string rawPath = m["path"]?.ToString() ?? "";
                    string name = m["name"]?.ToString() ?? "";
                    string path = ResolveAudioPath(rawPath);
                    bool isRemote = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    if (string.IsNullOrEmpty(path) || (!isRemote && !File.Exists(path)))
                    {
                        PlayLog.Write("播放", "失败", "error", task: "ID " + taskId, name: name, path: rawPath, detail: "文件不存在或路径无法解析，已跳过");
                        AlertCenter.Publish("播放", $"任务 ID {taskId} 的《{name}》文件不存在或路径无效，已跳过。", "error",
                            dedupKey: $"file-missing-{taskId}-{rawPath}");
                        continue;
                    }

                    double remaining = totalDurationSec > 0 ? totalDurationSec - (DateTime.Now - taskStart).TotalSeconds : 0;
                    if (totalDurationSec > 0 && remaining <= 0) break;
                    // 【播完当前】用户确认的语义：任务到达自己的结束时间时，若是"播完当前"，
                    // 就**不给 ffplay 传 -t**，让正在播的这一首自然放完；放完后循环会因为"已超时长"而 break，
                    // 于是"第 n 首播完即停"。若是"立即终止"，仍把剩余时长作为 -t 传下去 → 到点立即切掉。
                    double trackLimit = (stopMode == "finish_track") ? 0 : remaining;

                    ChangePlayingStatus(true, $"{prefix} {name}");
                    CurrentAudioOffset = 0; CurrentAudioStartTime = DateTime.Now;
                    BackgroundLoadWave(path, false);
                    PlayLog.Write("播放", "开始", "info", task: "ID " + taskId, name: name, path: path,
                        detail: totalDurationSec > 0 ? (stopMode == "finish_track" ? $"上限剩余 {Math.Round(remaining, 1)} 秒，但本任务选的是「播完当前」→ 本首放完再停" : $"上限剩余 {Math.Round(remaining, 1)} 秒（到点立即切）") : "不限时长");

                    // 队列项可以自带音量（_volume）：气象预警用 100，不受任务音量削减
                    int trackVolume = volume;
                    try { if (m["_volume"] != null) trackVolume = m["_volume"].GetValue<int>(); } catch { }
                    if (trackVolume < 0) trackVolume = 0;
                    if (trackVolume > 100) trackVolume = 100;

                    double played = PlayOneTrack(path, name, trackVolume, trackLimit, "ID " + taskId);   // 播完当前：trackLimit=0=不限时长
                    ChangePlayingStatus(false, "");
                    if (played < 0)
                    {
                        AlertCenter.Publish("播放", $"任务 ID {taskId} 的《{name}》启动播放失败，详见执行日志。", "error",
                            dedupKey: $"play-fail-{taskId}-{rawPath}");
                    }
                    System.Threading.Thread.Sleep(200);

                    // 温柔抢占：新任务要求"播完当前这首" → 本首刚放完，就不再播下一首了。
                    // 用独立的标记而不是代际号/直接 Kill，就是为了让它自然放完、不切断。
                    if (FinishTrackRequested.TryRemove(taskId, out _))
                    {
                        PlayLog.Write("排程", "让位", "info", task: "ID " + taskId,
                            detail: "已按新任务的要求播完当前这首，停止后续曲目");
                        return;
                    }
                }

                // 播放真正结束后，才推进目录断点
                foreach (var note in resumeNotes)
                {
                    SaveFolderResumeIndex(note.Item1, note.Item2, 0);
                    PlayLog.Write("音乐库", "断点推进", "info",
                        detail: $"目录 {note.Item1} 下一首索引 {note.Item2 + 1}/{note.Item3}");
                }
                PlayLog.Write("排程", "完成", "info", task: "ID " + taskId, detail: "定时任务播放结束");
            }
            catch (Exception ex)
            {
                LogHelper.Log("PlayScheduledTask Error", ex);
                AlertCenter.Publish("排程", $"任务 ID {taskId} 执行异常：{ex.Message}", "error", dedupKey: $"task-ex-{taskId}-{DateTime.Now:yyyyMMddHHmm}");
            }
            finally
            {
                if (locked) { lock (RunningTaskIds) RunningTaskIds.Remove(taskId); }
                if (CurrentScheduledTaskId == taskId) CurrentScheduledTaskId = 0;   // 不再"正在播放"，温柔抢占要据此判断
                FinishTrackRequested.TryRemove(taskId, out _);
            }
        }

        /// <summary>
        /// 定位"本机测试服务端"的 RSA 密钥目录。
        /// vnts 运行件已移到 third_party\vnts-runtime\ 且**不入版本库**；密钥本身也早已从仓库移出。
        /// 所以按候选位置依次找；都不存在时返回第一个候选，调用方用 File.Exists 判断即可
        /// （找不到就跳过这项诊断，不影响其它检查）。
        /// </summary>
        static string ResolveVntsKeyDir()
        {
            var cands = new[]
            {
                Path.Combine("third_party", "vnts-runtime", "key"),
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                             "CampusBroadcastKeys", "vnts-key-本机测试服务端"),
                Path.Combine("vnts", "key"),
            };
            foreach (var c in cands)
            {
                if (File.Exists(Path.Combine(c, "private_key.pem")) && File.Exists(Path.Combine(c, "public_key.pem")))
                    return c;
            }
            return cands[0];
        }

        /// <summary>
        /// 把任务的音乐编号展开成实际播放队列（纯逻辑，不启动进程、不写盘，便于自检）。
        /// 目录条目的行为：
        ///   folder_mode=sequential —— 从 resume.json 的断点接续，取 folder_play_count 首，播完后把断点推进 count 位；
        ///   folder_mode=random     —— 随机取 count 首，不推进断点。
        /// 引用失效（编号不存在/目录不存在/目录里没有音频）只记录到 problems，不影响其余曲目。
        /// </summary>
        public static List<JsonObject> ExpandTaskQueue(JsonObject task, JsonArray allMusic, List<string> playList,
                                                       out List<string> problems, out List<Tuple<string, int, int>> resumeNotes)
        {
            return ExpandTaskQueue(task, allMusic, playList, out problems, out resumeNotes, out _);
        }

        /// <summary><paramref name="warnNotes"/> 收集"保留编号 1000 本次为什么不播"的原因，供排程日志使用。</summary>
        public static List<JsonObject> ExpandTaskQueue(JsonObject task, JsonArray allMusic, List<string> playList,
                                                       out List<string> problems, out List<Tuple<string, int, int>> resumeNotes,
                                                       out List<string> warnNotes)
        {
            problems = new List<string>();
            resumeNotes = new List<Tuple<string, int, int>>();
            warnNotes = new List<string>();
            var queue = new List<JsonObject>();
            var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" };

            foreach (var idStr in playList ?? new List<string>())
            {
                // 保留编号 1000：气象预警播报。不是音乐库里的文件，而是"当前最新预警"的离线语音合成结果，
                // 播报与否由预报节点判断决定（见 WeatherWarn.Evaluate），判断不通过只记日志、不算"引用失效"。
                if (string.Equals((idStr ?? "").Trim(), WeatherWarn.MusicId.ToString(), StringComparison.Ordinal))
                {
                    string why;
                    var warnFiles = WeatherWarn.PrepareForPlayback(out why);
                    if (warnFiles == null || warnFiles.Count == 0) warnNotes.Add(why);
                    else foreach (var f in warnFiles)
                        // _volume=100：气象预警不受任务音量削减（任务音量 50% 时预警也照样满音量播，否则学生听不见）
                        queue.Add(new JsonObject { ["name"] = "气象预警播报", ["path"] = f, ["_musicId"] = idStr, ["_volume"] = 100 });
                    continue;
                }

                var m = FindMusic(allMusic, idStr);
                if (m == null) { problems.Add(idStr); continue; }

                if (m["type"]?.ToString() == "folder")
                {
                    string resolved = ResolveAudioPath(m["path"]?.ToString());
                    if (!Directory.Exists(resolved)) { problems.Add(idStr); continue; }

                    var files = Directory.GetFiles(resolved)
                        .Where(f => exts.Contains(Path.GetExtension(f).ToLower()))
                        .OrderBy(f => f).ToList();
                    if (files.Count == 0) { problems.Add(idStr); continue; }

                    int count = task?["folder_play_count"]?.GetValue<int>() ?? 1;
                    if (count < 1) count = 1;
                    if (count > files.Count) count = files.Count;

                    if (task?["folder_mode"]?.ToString() == "random")
                    {
                        foreach (var f in files.OrderBy(x => Guid.NewGuid()).Take(count))
                            queue.Add(new JsonObject { ["name"] = Path.GetFileName(f), ["path"] = f, ["_musicId"] = idStr });
                    }
                    else
                    {
                        int startIndex = ((GetFolderResumeIndex(resolved) % files.Count) + files.Count) % files.Count;
                        for (int i = 0; i < count; i++)
                        {
                            string f = files[(startIndex + i) % files.Count];
                            queue.Add(new JsonObject { ["name"] = Path.GetFileName(f), ["path"] = f, ["_musicId"] = idStr });
                        }
                        int nextIndex = (startIndex + count) % files.Count;
                        resumeNotes.Add(Tuple.Create(resolved, nextIndex, files.Count));
                    }
                }
                else
                {
                    queue.Add(new JsonObject { ["name"] = m["name"]?.ToString(), ["path"] = m["path"]?.ToString(), ["_musicId"] = idStr });
                }
            }
            return queue;
        }

        /// <summary>
        /// 待应用的拖动位置（由播放线程统一处理）。
        /// 【为什么必须这样】手机拖进度条会连发多个 seek；若在 HTTP 处理器里、且拿着 ManualLock 做
        /// "同步渐弱 + 杀进程"，就会和每 200ms 取同一把锁的播放线程互相顶死 → 电脑端整体卡死（真机踩到）。
        /// 现在 seek 只设标记，播放线程每个 tick 合并处理一次，锁内只做**异步** Kill。
        /// </summary>
        /// <summary>
        /// 正在"锁外结束进程"的数量。手动播控每 200ms 一轮，若它在上一次 Kill 还没收尾时就起新进程，
        /// 新旧两个 ffplay 属于同一个音频会话组，音量会互相覆盖（切歌后忽大忽小）。用它做护栏。
        /// </summary>
        static int _pendingKillCount;
        public static volatile bool ManualSeekPending = false;
        /// <summary>待应用的音量变化（同上：交给播放线程重启进程，避免锁内同步渐弱）。</summary>
        public static volatile bool ManualVolumePending = false;

        /// <summary>
        /// 拖动回执：电脑端**真正按新位置起播**时自增，并把实际位置放进 ManualSeekAckOffset。
        /// 手机端进度条据此结束"定位中"（用户要求：调整结束后两边都要知道操作完成了）。
        /// </summary>
        public static long ManualSeekAck = 0;
        public static double ManualSeekAckOffset = 0;
        /// <summary>本次 seek 是否还等着回执（play 起来时补一次 ack）。</summary>
        static volatile bool _seekAckPending = false;

        static void ManualPlayerWorker(object state)
        {
            // 【兜底】以前这里一旦抛异常，定时器回调就静默中断：界面"点了播放没反应、日志也没有"。
            // 现在任何异常都记一条，保证下次一定查得到原因。
            try { ManualPlayerWorkerCore(state); }
            catch (Exception ex) { try { PlayLog.Write("播放", "线程异常", "error", detail: ex.GetType().Name + "：" + ex.Message); } catch { } }
        }
        static void ManualPlayerWorkerCore(object state)
        {
            Process pendingKill = null;
            lock (ManualLock)
            {
                if (ManualStatus != "playing") { ManualSeekPending = false; ManualVolumePending = false; return; }

                // ① 拖动/调音量的待处理请求：只摘出旧进程，**不在锁内做渐弱**（渐弱是同步等待）
                if (ManualSeekPending || ManualVolumePending)
                {
                    ManualSeekPending = false; ManualVolumePending = false;
                    if (MpProcess != null)
                    {
                        pendingKill = MpProcess; MpProcess = null;
                        ChangePlayingStatus(false, "");
                    }
                    // 本 tick 只负责停；下一个 tick（≤200ms）会用新的 ManualPauseOffset 重新起播
                    goto afterKill;
                }
                bool isExited = true; if (MpProcess != null) { try { isExited = MpProcess.HasExited; } catch { isExited = true; } }

                if (isExited)
                {
                    if (MpProcess != null)
                    {
                        // 一曲自然结束：记录实际时长、注销台账，再按模式前进
                        string finishedName = "";
                        try { finishedName = ManualQueue.Count > ManualCurrentIndex ? (ManualQueue[ManualCurrentIndex]?["name"]?.ToString() ?? "") : ""; } catch { }
                        double playedSec = 0;
                        try { playedSec = (DateTime.Now - MpStartTime).TotalSeconds + ManualPauseOffset; } catch { }
                        int exitCode = -1; try { exitCode = MpProcess.ExitCode; } catch { }
                        PlayLog.Write("播放", "结束", "info", name: finishedName,
                            detail: $"手动播控播放 {Math.Round(playedSec, 1)} 秒，退出码 {exitCode}");
                        ChangePlayingStatus(false, ""); try { MpProcess.Dispose(); } catch { }
                        UnregisterAudioProcess(MpProcess);
                        MpProcess = null; ManualPauseOffset = 0; ManualCurrentDuration = 0;
                        if (ManualMode == "single") { } else if (ManualMode == "loop") { if (ManualQueue.Count > 0) ManualCurrentIndex = (ManualCurrentIndex + 1) % ManualQueue.Count; } else { ManualCurrentIndex++; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; return; } }
                        return;
                    }

                    // 【不再静默】状态是 playing 但索引越界或队列为空时：
                    // 以前这里什么都不做 → 用户"点了播放没反应、日志也没记录"（真机踩到）。
                    // 现在：能纠正就纠正，不能就记一条原因。
                    if (ManualQueue.Count == 0)
                    {
                        ManualStatus = "stopped";
                        PlayLog.Write("播放", "无法播放", "warn", detail: "播控队列为空，已回到停止状态");
                        return;
                    }
                    if (ManualCurrentIndex < 0 || ManualCurrentIndex >= ManualQueue.Count)
                    {
                        PlayLog.Write("播放", "序号纠正", "warn",
                            detail: $"当前序号越界（第 {ManualCurrentIndex + 1} 首 / 队列 {ManualQueue.Count} 首），已回到第 1 首");
                        ManualCurrentIndex = 0;
                    }

                    // 【竞态护栏】上一次"锁外结束进程"可能还没收尾（HTTP 线程正在 Kill 旧进程）。
                    // 这时若立刻起新进程，新旧两个 ffplay 属于同一个音频会话组，音量会互相覆盖
                    //（表现为"切歌后音量忽大忽小、或突然很轻"）。旧进程结束是毫秒级，跳过本 tick 即可。
                    if (Volatile.Read(ref _pendingKillCount) > 0)
                    {
                        PlayLog.WriteOnce("manual-wait-kill", "播控", "等待旧进程退出", "info",
                            detail: "上一次停止还在收尾，本轮先不起播，避免新旧 ffplay 抢同一个音频会话的音量");
                        return;   // 在 lock 内 return：锁会正常释放
                    }

                    while (ManualStatus == "playing" && ManualCurrentIndex < ManualQueue.Count)
                    {
                        var music = ManualQueue[ManualCurrentIndex]; string path = music["path"]?.ToString() ?? ""; path = ResolveAudioPath(path);
                        PlayLog.Write("播放", "准备起播", "info", name: music["name"]?.ToString(), path: path, detail: $"第 {ManualCurrentIndex + 1}/{ManualQueue.Count} 首，文件存在={File.Exists(path)}，ffplay={FfplayPath}");
                        if (File.Exists(path) || path.StartsWith("http"))
                        {
                            ChangePlayingStatus(true, $"[手动] {music["name"]}"); if (ManualPauseOffset == 0) ManualCurrentDuration = 0.0; CurrentAudioOffset = ManualPauseOffset; CurrentAudioStartTime = DateTime.Now; MpStartTime = DateTime.Now; try { BackgroundLoadWave(path, true); } catch (Exception wex) { PlayLog.Write("播放", "波形加载失败", "warn", name: music["name"]?.ToString(), path: path, detail: "不影响播放，已忽略：" + wex.Message); }
                            MpProcess = new Process(); MpProcess.StartInfo.FileName = FfplayPath; MpProcess.StartInfo.CreateNoWindow = true; MpProcess.StartInfo.UseShellExecute = false; MpProcess.StartInfo.ArgumentList.Add("-nodisp"); MpProcess.StartInfo.ArgumentList.Add("-autoexit");
                            MpProcess.StartInfo.ArgumentList.Add("-af"); MpProcess.StartInfo.ArgumentList.Add($"volume={(ManualVolume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
                            if (ManualPauseOffset > 0) { MpProcess.StartInfo.ArgumentList.Add("-ss"); MpProcess.StartInfo.ArgumentList.Add(ManualPauseOffset.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                            MpProcess.StartInfo.ArgumentList.Add(path);
                            try
                            {
                                MpProcess.Start();
                                // 【关键修复】同①：手动播控起播也要把音量拉回 100%（否则会继承上次渐弱写下的 0%）
                                ForceProcessVolumeSoon(MpProcess, "手动播控");   // 带重试
                                ActiveAudioProcesses[MpProcess.Id] = new AudioProcessInfo
                                {
                                    Pid = MpProcess.Id, Purpose = "手动播放",
                                    Name = music["name"]?.ToString() ?? "", Path = path,
                                    StartTime = DateTime.Now, Proc = MpProcess
                                };
                                // 拖动回执：真正按新位置起播了才自增 —— 手机端据此解除"定位中"并恢复跟随播放进度
                                if (_seekAckPending)
                                {
                                    _seekAckPending = false;
                                    ManualSeekAck++;
                                    ManualSeekAckOffset = ManualPauseOffset;
                                }
                                PlayLog.Write("播放", "开始", "info", name: music["name"]?.ToString(), path: path,
                                    detail: ManualPauseOffset > 0 ? $"手动播控（从 {Math.Round(ManualPauseOffset, 1)} 秒续播）" : "手动播控");
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogHelper.Log($"Manual Play Start Failed: {path}", ex);
                                PlayLog.Write("播放", "失败", "error", name: music["name"]?.ToString(), path: path, detail: "启动播放进程失败：" + ex.Message);
                                AlertCenter.Publish("播放", $"手动播控《{music["name"]}》启动失败，已跳过。", "error", dedupKey: $"manual-fail-{path}");
                                MpProcess = null;
                            }
                        }
                        else
                        {
                            PlayLog.WriteOnce($"manual-missing-{path}", "播放", "引用失效", "error", name: music["name"]?.ToString(), path: path,
                                detail: "手动播控队列中的文件不存在，已跳过");
                            AlertCenter.Publish("播放", $"手动播控《{music["name"]}》的文件不存在，已跳过。", "error", dedupKey: $"manual-missing-{path}");
                        }
                        ManualCurrentIndex++; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; }
                    }
                }
            }
        afterKill:
            // 拖动/调音量收尾：**在锁外**结束旧进程并清理（Kill 要等进程退出，绝不能在 ManualLock 里做）
            if (pendingKill != null)
            {
                try { AudioFade.StopNow(pendingKill); } catch { }
                UnregisterAudioProcess(pendingKill);
                try { pendingKill.Dispose(); } catch { }
            }
        }
    }

    public static class UIHelper
    {
        public static Font MainFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        public static Font BoldFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        public static Font TitleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
        public static Color Primary = Color.FromArgb(0, 120, 212);
        public static Color Danger = Color.FromArgb(232, 17, 35);
        public static Color Success = Color.FromArgb(16, 137, 62);
        public static Color TextDark = Color.FromArgb(27, 27, 27);
        public static Color TextGray = Color.FromArgb(102, 102, 102);

        public static Button CreateBtn(string text, Color bg, Color fg) { var btn = new Button { BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = MainFont, Text = text, Dock = DockStyle.Fill, Margin = new Padding(6), TextAlign = ContentAlignment.MiddleCenter }; btn.FlatAppearance.BorderSize = 0; return btn; }
        public static Button CreateAutoBtn(string text, Color bg, Color fg) { var btn = new Button { BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = MainFont, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(4), TextAlign = ContentAlignment.MiddleCenter }; btn.FlatAppearance.BorderSize = 0; return btn; }
    }

    public class SubscriptionForm : Form
    {
        private TextBox txtUrl;
        private NumericUpDown numInterval;

        public SubscriptionForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = Color.White;
            this.Text = "云端调休同步设置";
            this.ClientSize = new Size(540, 310);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            this.Icon = Program.AppIcon;

            TableLayoutPanel tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(25) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lblTitle = new Label { Text = "远程调休订阅配置", Font = UIHelper.TitleFont, ForeColor = UIHelper.TextDark, AutoSize = true, Margin = new Padding(0, 0, 0, 25) };
            tlp.Controls.Add(lblTitle, 0, 0); tlp.SetColumnSpan(lblTitle, 2);

            tlp.Controls.Add(new Label { Text = "订阅源 (JSON):", Font = UIHelper.BoldFont, ForeColor = UIHelper.TextDark, Anchor = AnchorStyles.Right, AutoSize = true }, 0, 1);
            txtUrl = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10F), Margin = new Padding(5, 0, 0, 20) };
            tlp.Controls.Add(txtUrl, 1, 1);

            tlp.Controls.Add(new Label { Text = "轮询周期 (小时):", Font = UIHelper.BoldFont, ForeColor = UIHelper.TextDark, Anchor = AnchorStyles.Right, AutoSize = true }, 0, 2);
            numInterval = new NumericUpDown { Width = 100, Minimum = 1, Maximum = 720, Value = 12, Font = UIHelper.MainFont, Margin = new Padding(5, 0, 0, 25) };
            tlp.Controls.Add(numInterval, 1, 2);

            var btnSave = UIHelper.CreateBtn("保存并强制向云端同步一次", UIHelper.Primary, Color.White);
            btnSave.Click += async (s, e) => {
                btnSave.Text = "正在向云端拉取数据..."; btnSave.Enabled = false;
                var all = Program.ReadSettingMerged();
                JsonObject globalItem = null;
                foreach (var item in all) { if (item["_type"]?.ToString() == "global") { globalItem = item.AsObject(); break; } }
                if (globalItem == null) { globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" }; all.Insert(0, globalItem); }
                globalItem["subscription_url"] = txtUrl.Text.Trim();
                globalItem["subscription_interval"] = (int)numInterval.Value;
                Program.WriteSettingMerged( all);
                Program.UpdateSubscriptionTimer();
                bool success = await Program.ForceSyncSubscription();
                if (success) MessageBox.Show("同步成功！已成功将云端调休数据写入系统配置。\n你可以随时在网页端的【调休管理】中查看最新状况。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show("无法拉取或解析，请检查订阅链接是否正确且包含合法 JSON 数组。\n配置已保存，系统后台将按预设周期持续重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.Close();
            };
            tlp.Controls.Add(btnSave, 0, 3); tlp.SetColumnSpan(btnSave, 2);
            this.Controls.Add(tlp);

            var allSettings = Program.ReadSettingMerged();
            foreach (var item in allSettings)
            {
                if (item["_type"]?.ToString() == "global")
                {
                    txtUrl.Text = item["subscription_url"]?.ToString() ?? "";
                    if (item["subscription_interval"] != null) numInterval.Value = item["subscription_interval"].GetValue<int>();
                    break;
                }
            }
        }
    }

    public class MainForm : Form
    {
        private Label lblStatus;
        private Button btnToggle;
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer uiTimer;

        public MainForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = Color.White;
            this.Text = "校园广播 - 控制核心";
            this.ClientSize = new Size(500, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.Icon = Program.AppIcon;

            TableLayoutPanel mainPnl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            mainPnl.RowStyles.Add(new RowStyle(SizeType.Percent, 50F)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            var lblTitle = new Label { Text = "智能校园广播核心", Font = UIHelper.TitleFont, ForeColor = UIHelper.TextDark, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 15) }; mainPnl.Controls.Add(lblTitle, 0, 1);
            lblStatus = new Label { Text = "系统状态: 读取中...", Font = UIHelper.BoldFont, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 25) }; mainPnl.Controls.Add(lblStatus, 0, 2);

            var gridPnl = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3, Anchor = AnchorStyles.None };
            gridPnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); gridPnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

            var btnWeb = UIHelper.CreateBtn("打开控制看板", UIHelper.Primary, Color.White); btnWeb.Click += (s, e) => Process.Start(new ProcessStartInfo("http://127.0.0.1:5000") { UseShellExecute = true }); gridPnl.Controls.Add(btnWeb, 0, 0);
            var btnMusic = UIHelper.CreateBtn("本地音频管理", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnMusic.Click += (s, e) => new MusicToolForm().Show(); gridPnl.Controls.Add(btnMusic, 1, 0);

            btnToggle = UIHelper.CreateBtn("停用广播系统", UIHelper.Danger, Color.White); btnToggle.Click += (s, e) => { System.Threading.Tasks.Task.Run(() => Program.ToggleGlobalSwitch()); UpdateUIState(); }; gridPnl.Controls.Add(btnToggle, 0, 1);

            var btnSub = UIHelper.CreateBtn("云端调休配置", Color.FromArgb(240, 240, 240), UIHelper.TextDark);
            btnSub.Click += (s, e) => new SubscriptionForm().ShowDialog();
            gridPnl.Controls.Add(btnSub, 1, 1);

            var btnBackup = UIHelper.CreateBtn("备份系统数据", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnBackup.Click += (s, e) => BackupConfig(); gridPnl.Controls.Add(btnBackup, 0, 2);
            var btnRestore = UIHelper.CreateBtn("从备份中恢复", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnRestore.Click += (s, e) => RestoreConfig(); gridPnl.Controls.Add(btnRestore, 1, 2);

            mainPnl.Controls.Add(gridPnl, 0, 3);
            var lblHint = new Label { Text = "提示：点击右上角关闭将最小化到托盘，后台持续运行", Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIHelper.TextGray, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 25, 0, 0) }; mainPnl.Controls.Add(lblHint, 0, 4);
            this.Controls.Add(mainPnl);

            trayIcon = new NotifyIcon { Icon = Program.AppIcon, Text = "智能校园广播", Visible = true }; trayIcon.DoubleClick += (s, e) => { this.ShowInTaskbar = true; this.Show(); this.WindowState = FormWindowState.Normal; };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主控制台 (GUI)", null, (s, e) => { this.ShowInTaskbar = true; this.Show(); this.WindowState = FormWindowState.Normal; }); menu.Items.Add("打开网页看板 (Web)", null, (s, e) => Process.Start(new ProcessStartInfo("http://127.0.0.1:5000") { UseShellExecute = true })); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("本地音频管理", null, (s, e) => new MusicToolForm().Show());
            menu.Items.Add("云端调休配置", null, (s, e) => new SubscriptionForm().ShowDialog()); menu.Items.Add(new ToolStripSeparator());
            var toggleMenuItem = new ToolStripMenuItem("切换系统状态"); toggleMenuItem.Click += (s, e) => { System.Threading.Tasks.Task.Run(() => Program.ToggleGlobalSwitch()); UpdateUIState(); }; menu.Items.Add(toggleMenuItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("完全退出系统", null, (s, e) => { trayIcon.Visible = false; Program.KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { } Program.ReleaseSingleInstance(); Environment.Exit(0); });
            menu.Opening += (s, e) => { toggleMenuItem.Text = Program.IsGlobalSwitchOn() ? "紧急停用广播系统" : "恢复广播系统运行"; }; trayIcon.ContextMenuStrip = menu;

            this.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; this.Hide(); } };
            uiTimer = new System.Windows.Forms.Timer { Interval = 2000 }; uiTimer.Tick += (s, e) => UpdateUIState(); uiTimer.Start(); UpdateUIState();
        }

        private void BackupConfig()
        {
            using var sfd = new SaveFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip", FileName = $"BroadcastBackup_{DateTime.Now:yyyyMMdd_HHmmss}.zip" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // 【导出选项】默认不导出隐私数据（密码/令牌）；需要时才勾选，跨电脑导入并不需要它。
                    bool withPrivacy = false;
                    try
                    {
                        withPrivacy = BroadcastSystemNet.Desktop.AppDialog.Confirm(null, "导出选项 · 隐私数据",
                            "是否把隐私数据一并导出？\n\n隐私数据包括：网页口令、组网令牌、数据密码、凭证密钥。\n\n默认不导出（推荐）：排程、调休、预警配置、音乐库、铃声都能正常还原；vnt 只带服务器地址，\n跨电脑导入时令牌与密码以本机现有为准，不会互相污染。",
                            "导出隐私数据", "不导出（推荐）", BroadcastSystemNet.Desktop.AppDialogKind.Warning);
                    }
                    catch { withPrivacy = false; }
                    if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                    System.IO.Compression.ZipFile.CreateFromDirectory(Program.AppDataDir, sfd.FileName, System.IO.Compression.CompressionLevel.Optimal, false);
                    if (!withPrivacy)
                    {
                        int cleared = Program.StripPrivacyFromBackup(sfd.FileName);
                        PlayLog.Write("数据", "备份", "info", detail: $"{sfd.FileName}（已按默认策略清空隐私字段 {cleared} 处）");
                    }
                    else
                    {
                        PlayLog.Write("数据", "备份", "warn", detail: $"{sfd.FileName}（含隐私数据，明文，请妥善保管）");
                    }
                    MessageBox.Show("数据备份成功！\n此压缩包包含了您的所有排程、配置和本地铃声。", "备份成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void RestoreConfig()
        {
            using var ofd = new OpenFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                if (MessageBox.Show("整包还原会把备份包里的配置按同一批导入器写入本机（含合法性校验与旧格式迁移），不再盲目解压覆盖。\n\nvnt 的设备 ID / 令牌 / 数据密码 / 凭证密钥一律以本机现有为准，不会被覆盖。\n程序随后会自动重启。\n\n确定要继续吗？", "警告 - 数据覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try
                    {
                        Program.KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { }
                        // 走同一批导入器：逐类校验 + 旧格式迁移 + 按规则合并（与桌面窗口、拖入导入共用同一份规则）
                        var rep = ConfigTransfer.ImportWholePackage(ofd.FileName);
                        if (rep["status"]?.ToString() == "error")
                        {
                            MessageBox.Show("还原失败：" + (rep["message"]?.ToString() ?? ""), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                                                MessageBox.Show("还原完成！\n\n" + (rep["message"]?.ToString() ?? "") + "\n\n程序即将重新启动以加载新配置。", "还原完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Application.Restart(); Environment.Exit(0);
                    }
                    catch (Exception ex) { MessageBox.Show("恢复失败: 压缩包可能损坏或文件被占用。\n\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
        }

        private void UpdateUIState()
        {
            bool isOn = Program.IsGlobalSwitchOn();
            if (isOn) { lblStatus.Text = "运行状态: 正常广播中"; lblStatus.ForeColor = UIHelper.Success; btnToggle.Text = "停用广播系统"; btnToggle.BackColor = UIHelper.Danger; btnToggle.ForeColor = Color.White; }
            else { lblStatus.Text = "运行状态: 已全局静音停用"; lblStatus.ForeColor = UIHelper.Danger; btnToggle.Text = "恢复系统运行"; btnToggle.BackColor = UIHelper.Success; btnToggle.ForeColor = Color.White; }
        }
    }

    public class MusicToolForm : Form
    {
        private TextBox txtName, txtPath; private ComboBox cmbType; private ListView lvMusic; private List<JsonObject> musicList = new List<JsonObject>(); private bool isUpdatingUI = false;
        public MusicToolForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi; this.BackColor = Color.White; this.Text = "音乐库与流媒体资源管理"; this.Size = new Size(1150, 800); this.StartPosition = FormStartPosition.CenterScreen; this.Icon = Program.AppIcon;
            InitializeComponents(); LoadData();
        }

        private void InitializeComponents()
        {
            var pnlTop = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(15) }; var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(15) }; var pnlCenter = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15, 0, 15, 0) };
            this.Controls.Add(pnlCenter); this.Controls.Add(pnlBottom); this.Controls.Add(pnlTop);
            var tlpTop = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 }; tlpTop.RowStyles.Add(new RowStyle(SizeType.AutoSize)); tlpTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var actionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 10) };

            var btnFile = UIHelper.CreateAutoBtn("导入文件", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnFile.Click += (s, e) => BrowseFile(); actionFlow.Controls.Add(btnFile);
            var btnDir = UIHelper.CreateAutoBtn("导入整个文件夹", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnDir.Click += (s, e) => BrowseFolder(); actionFlow.Controls.Add(btnDir);
            var btnScanDef = UIHelper.CreateAutoBtn("扫描默认铃声库 (AppData/ring)", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnScanDef.Click += (s, e) => DoScan(Program.RingDir); actionFlow.Controls.Add(btnScanDef);
            var btnScanCust = UIHelper.CreateAutoBtn("扫描自定义目录...", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnScanCust.Click += (s, e) => { using var fbd = new FolderBrowserDialog { Description = "请选择要批量提取音频的文件夹" }; if (fbd.ShowDialog() == DialogResult.OK) DoScan(fbd.SelectedPath); }; actionFlow.Controls.Add(btnScanCust);
            var btnOpenRingDir = UIHelper.CreateAutoBtn("在资源管理器中打开默认库", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnOpenRingDir.Click += (s, e) => { Process.Start(new ProcessStartInfo(Program.RingDir) { UseShellExecute = true }); }; actionFlow.Controls.Add(btnOpenRingDir);

            tlpTop.Controls.Add(actionFlow, 0, 0);
            var gbForm = new GroupBox { Text = " 选中下方的资源后，在此处实时修改内容", Dock = DockStyle.Fill, Font = UIHelper.MainFont, Padding = new Padding(10) }; var tlpForm = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, RowCount = 1, Padding = new Padding(5) };
            tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

            tlpForm.Controls.Add(new Label { Text = "显示名称:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 0, 0); txtName = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 15, 0) }; tlpForm.Controls.Add(txtName, 1, 0); txtName.TextChanged += RealTimeUpdate;
            tlpForm.Controls.Add(new Label { Text = "物理路径 / URL:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 2, 0); txtPath = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 15, 0) }; tlpForm.Controls.Add(txtPath, 3, 0); txtPath.TextChanged += RealTimeUpdate;
            tlpForm.Controls.Add(new Label { Text = "类型:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 4, 0); cmbType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 5, 0, 0) }; cmbType.Items.AddRange(new string[] { "file", "folder", "hls", "web" }); cmbType.SelectedIndex = 0; cmbType.SelectedIndexChanged += RealTimeUpdate; tlpForm.Controls.Add(cmbType, 5, 0);

            gbForm.Controls.Add(tlpForm); tlpTop.Controls.Add(gbForm, 0, 1); pnlTop.Controls.Add(tlpTop);
            var gbList = new GroupBox { Text = " 系统音乐库列表 (编号稳定：删除后编号保留空缺，新增自动顶替空缺；排序不改变编号)", Dock = DockStyle.Fill, Font = UIHelper.MainFont }; lvMusic = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false, Margin = new Padding(10), HideSelection = false };
            lvMusic.Columns.Add("编号", 90); lvMusic.Columns.Add("显示名称", 280); lvMusic.Columns.Add("物理路径 / 网络源 URL", 550); lvMusic.Columns.Add("解析类型", 120);
            lvMusic.SelectedIndexChanged += (s, e) => { if (lvMusic.SelectedItems.Count > 0) { isUpdatingUI = true; var item = lvMusic.SelectedItems[0]; txtName.Text = item.SubItems[1].Text; txtPath.Text = item.SubItems[2].Text; cmbType.Text = item.SubItems[3].Text; isUpdatingUI = false; } else { isUpdatingUI = true; txtName.Text = ""; txtPath.Text = ""; cmbType.SelectedIndex = 0; isUpdatingUI = false; } };
            gbList.Controls.Add(lvMusic); pnlCenter.Controls.Add(gbList);

            var tlpBottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; tlpBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); tlpBottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var flpLeft = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.None };
            var btnTop = UIHelper.CreateAutoBtn("置于页首", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnTop.Click += (s, e) => MoveItemToExtreme(true); flpLeft.Controls.Add(btnTop); var btnUp = UIHelper.CreateAutoBtn("上移", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnUp.Click += (s, e) => MoveItem(-1); flpLeft.Controls.Add(btnUp); var btnDown = UIHelper.CreateAutoBtn("下移", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnDown.Click += (s, e) => MoveItem(1); flpLeft.Controls.Add(btnDown); var btnBottom = UIHelper.CreateAutoBtn("置于页尾", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnBottom.Click += (s, e) => MoveItemToExtreme(false); flpLeft.Controls.Add(btnBottom);
            var btnDel = UIHelper.CreateAutoBtn("删除选中", UIHelper.Danger, Color.White); btnDel.Margin = new Padding(25, 4, 4, 4); btnDel.Click += (s, e) => DeleteSelected(); flpLeft.Controls.Add(btnDel); var btnClearAll = UIHelper.CreateAutoBtn("清空列表", Color.FromArgb(240, 240, 240), UIHelper.TextGray); btnClearAll.Click += (s, e) => { if (MessageBox.Show("确定清空全部资源？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes) { musicList.Clear(); UpdateList(); } }; flpLeft.Controls.Add(btnClearAll);

            tlpBottom.Controls.Add(flpLeft, 0, 0);
            var btnSave = UIHelper.CreateAutoBtn("保存配置并应用到网页端", UIHelper.Primary, Color.White); btnSave.Padding = new Padding(20, 8, 20, 8); btnSave.Anchor = AnchorStyles.Right | AnchorStyles.None; btnSave.Click += (s, e) => SaveData(); tlpBottom.Controls.Add(btnSave, 1, 0);
            pnlBottom.Controls.Add(tlpBottom);
        }

        private void RealTimeUpdate(object sender, EventArgs e) { if (isUpdatingUI || lvMusic.SelectedItems.Count == 0) return; var item = lvMusic.SelectedItems[0]; item.SubItems[1].Text = txtName.Text.Trim(); item.SubItems[2].Text = txtPath.Text.Trim(); item.SubItems[3].Text = cmbType.Text; var obj = (JsonObject)item.Tag; obj["name"] = txtName.Text.Trim(); obj["path"] = txtPath.Text.Trim(); obj["type"] = cmbType.Text; }
        private void MoveItem(int direction) { if (lvMusic.SelectedItems.Count == 0) return; int idx = lvMusic.SelectedIndices[0]; if (idx + direction < 0 || idx + direction >= musicList.Count) return; var item = musicList[idx]; musicList.RemoveAt(idx); musicList.Insert(idx + direction, item); UpdateList(); lvMusic.Items[idx + direction].Selected = true; lvMusic.Focus(); }
        private void MoveItemToExtreme(bool toTop) { if (lvMusic.SelectedItems.Count == 0) return; int idx = lvMusic.SelectedIndices[0]; if (toTop && idx == 0) return; if (!toTop && idx == musicList.Count - 1) return; var item = musicList[idx]; musicList.RemoveAt(idx); if (toTop) musicList.Insert(0, item); else musicList.Add(item); UpdateList(); int newIdx = toTop ? 0 : musicList.Count - 1; lvMusic.Items[newIdx].Selected = true; lvMusic.Focus(); }
        private string MakeRelPath(string filepath) { string bDir = Program.AppDataDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar; string absPath = Path.GetFullPath(filepath); if (absPath.StartsWith(bDir, StringComparison.OrdinalIgnoreCase)) return absPath.Substring(bDir.Length).Replace('\\', '/'); return filepath.Replace('\\', '/'); }
        private void LoadData() { musicList.Clear(); var all = Program.ReadJsonl("music.jsonl"); foreach (var item in all) { if (item["_type"]?.ToString() != "settings" && item["id"] != null) musicList.Add(item.AsObject()); } UpdateList(); }

        // ================= 稳定编号：编号一经分配就固定，删除留空缺，新增顶替最小空缺 =================
        private HashSet<int> UsedIds()
        {
            var used = new HashSet<int>();
            foreach (var m in musicList) { if (int.TryParse(m["id"]?.ToString(), out int id) && id > 0) used.Add(id); }
            return used;
        }
        /// <summary>取当前列表里最小的空闲正整数编号。</summary>
        private string NextFreeId() { var used = UsedIds(); int c = 1; while (used.Contains(c)) c++; return c.ToString(); }
        /// <summary>批量取编号：从小到大依次占用空缺，用于一次导入多个条目。</summary>
        private List<string> NextFreeIds(int count)
        {
            var used = UsedIds(); var res = new List<string>();
            int c = 1;
            while (res.Count < count) { while (used.Contains(c)) c++; used.Add(c); res.Add(c.ToString()); c++; }
            return res;
        }
        private void AssignIdsToMissing()
        {
            var missing = musicList.Where(m => string.IsNullOrEmpty(m["id"]?.ToString())).ToList();
            if (missing.Count == 0) return;
            var ids = NextFreeIds(missing.Count);
            for (int i = 0; i < missing.Count; i++) missing[i]["id"] = ids[i];
        }

        private void SaveData()
        {
            try
            {
                AssignIdsToMissing();
                var arr = new JsonArray();
                foreach (var m in musicList) arr.Add(m.DeepClone());
                Program.WriteJsonl("music.jsonl", arr);
                PlayLog.Write("音乐库", "保存", "info", detail: $"共 {musicList.Count} 个条目（编号保持稳定）");
                MessageBox.Show("配置保存成功！网页端数据已实时更新。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private void UpdateList()
        {
            AssignIdsToMissing();
            lvMusic.Items.Clear();
            foreach (var m in musicList)
            {
                var lid = new ListViewItem(new[] { m["id"]?.ToString(), m["name"]?.ToString(), m["path"]?.ToString(), m["type"]?.ToString() }) { Tag = m };
                lvMusic.Items.Add(lid);
            }
            if (lvMusic.Items.Count > 0 && lvMusic.SelectedItems.Count == 0) { lvMusic.Items[lvMusic.Items.Count - 1].Selected = true; lvMusic.Items[lvMusic.Items.Count - 1].EnsureVisible(); }
        }

        private void BrowseFile()
        {
            using var ofd = new OpenFileDialog { Filter = "音频/列表|*.mp3;*.wav;*.flac;*.m3u8;*.m3u|所有文件|*.*" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                string p = MakeRelPath(ofd.FileName); string n = Path.GetFileNameWithoutExtension(ofd.FileName);
                var obj = new JsonObject { ["id"] = NextFreeId(), ["name"] = n, ["path"] = p, ["type"] = "file" };
                musicList.Add(obj); UpdateList(); lvMusic.Items[lvMusic.Items.Count - 1].Selected = true;
                PlayLog.Write("音乐库", "新增", "info", name: n, path: p, detail: "分配编号 " + obj["id"]);
            }
        }

        private void BrowseFolder()
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
            {
                string p = MakeRelPath(fbd.SelectedPath); string n = Path.GetFileName(fbd.SelectedPath) + " (目录)";
                var obj = new JsonObject { ["id"] = NextFreeId(), ["name"] = n, ["path"] = p, ["type"] = "folder" };
                musicList.Add(obj); UpdateList(); lvMusic.Items[lvMusic.Items.Count - 1].Selected = true;
                PlayLog.Write("音乐库", "新增目录", "info", name: n, path: p, detail: "分配编号 " + obj["id"]);
            }
        }

        private void DeleteSelected()
        {
            if (lvMusic.SelectedItems.Count == 0) return;
            var selIds = lvMusic.SelectedItems.Cast<ListViewItem>().Select(i => ((JsonObject)i.Tag)["id"]?.ToString()).ToHashSet();
            var removedNames = musicList.Where(m => selIds.Contains(m["id"]?.ToString())).Select(m => $"{m["id"]}:{m["name"]}").ToList();
            musicList.RemoveAll(m => selIds.Contains(m["id"]?.ToString()));
            UpdateList();
            PlayLog.Write("音乐库", "删除", "warn", detail: "已删除 " + string.Join("、", removedNames) + "（其编号留作空缺，供新条目顶替）");
        }

        private void DoScan(string targetDir)
        {
            if (!Directory.Exists(targetDir)) return; var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac" }; int added = 0, updated = 0;
            var pending = new List<JsonObject>();
            foreach (var fp in Directory.GetFileSystemEntries(targetDir).OrderBy(f => f))
            {
                string name = Path.GetFileName(fp); if (name.StartsWith(".")) continue; string storePath = MakeRelPath(fp); string entryName, entryType;
                if (Directory.Exists(fp)) { entryName = name + " (目录)"; entryType = "folder"; } else if (File.Exists(fp) && exts.Contains(Path.GetExtension(fp).ToLower())) { entryName = Path.GetFileNameWithoutExtension(fp); entryType = "file"; } else continue;
                var existing = musicList.FirstOrDefault(m => m["name"]?.ToString() == entryName);
                if (existing != null) { existing["path"] = storePath; existing["type"] = entryType; updated++; }
                else { pending.Add(new JsonObject { ["name"] = entryName, ["path"] = storePath, ["type"] = entryType }); added++; }
            }
            if (pending.Count > 0)
            {
                var ids = NextFreeIds(pending.Count);
                for (int i = 0; i < pending.Count; i++) { pending[i]["id"] = ids[i]; musicList.Add(pending[i]); }
            }
            UpdateList(); MessageBox.Show($"扫描完毕！\n\n- 新增资源: {added} 个\n- 更新覆盖: {updated} 个\n\n（编号保持不变，新增项优先顶替空缺编号）", "扫描完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

