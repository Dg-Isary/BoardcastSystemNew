using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒播放（ffplay 管道版）：把 PCM 从 MicPcm.Ring 取出来，过峰值限幅后按 s16le
    /// 喂给**已经随包的 ffplay**（-f s16le -ar 48000 -ac 1 -i pipe:0）。
    /// 为什么不用 WASAPI：自写的 IAudioClient 渲染通道真机验证时会让进程崩（COM 结构体封送没吃透），
    /// 而 ffplay 是本项目沿用多年、已被反复验证的音频链路 —— 零 COM 风险，延迟约 +100~200ms。
    /// 进程异常退出会自动重启（有次数上限，避免疯狂重启）。
    /// </summary>
    public static class MicPipePlayer
    {
        public static string LastError = "";
        public static long BytesWritten;
        public static long Restarts;
        public const int MaxRestarts = 5;

        static Process _proc;
        static Thread _thread;
        static volatile bool _running;
        static int _sampleRate = MicAudio.SampleRate;
        static int _channels = MicAudio.Channels;

        public static bool Running => _running;

        /// <summary>
        /// true = Ogg 直通模式（默认）：安卓端发 Ogg-Opus 页，我们原样写进 ffplay 的 stdin，
        /// 解码全在 ffplay 里完成（用的是已打包的 ffmpeg，无需 opus.dll、无新增二进制，MIT 干净）。
        /// false = PCM 模式：自己解码（有 opus.dll 时）后按 s16le 喂进去。
        /// </summary>
        public static bool OggMode = true;
        public static long OggBytes;

        /// <summary>Ogg 直通：把收到的 Ogg 页原样写进 ffplay。</summary>
        /// <remarks>
        /// 【绝不在这里直接写管道】本方法是在 **vnt 接收线程**上被调用的（音频包一路进来）。
        /// ffplay 正在被拖动/调音量杀掉重启时，管道写会**无限阻塞** —— 一旦阻塞，整条接收线程停摆，
        /// 心跳、音频、接口请求全部堵住，表现就是"电脑端卡死"。
        /// 现在只把页丢进有界队列（满了就丢这一帧，丢帧听不出来，堵死接收线程才是灾难），
        /// 真正写管道由 Loop 线程负责。
        /// </remarks>
        public static bool WriteOgg(byte[] page)
        {
            if (page == null || page.Length == 0) return false;
            // 默认播放设备变过（连/断蓝牙、拔插耳机）→ 在这里（接收线程，不是 Loop 线程）安全重启，
            // 让 ffplay 重新绑定当前默认设备。放在接收线程是因为 Stop() 会 Join 播放线程，
            // 若在 Loop 线程（心跳里）调用就会自己等自己，直接死锁。
            if (_restartRequested)
            {
                _restartRequested = false;
                try { Stop(); } catch { }
            }
            if (!_running)
            {
                OggMode = true;
                if (!Start(out _)) return false;
            }
            if (!_oggQueue.TryAdd(page)) { OggDrops++; return false; }
            OggBytes += page.Length;
            return true;
        }

        /// <summary>管道写队列满被丢掉的音频帧数（诊断：正常应接近 0）。</summary>
        public static long OggDrops;
        /// <summary>上次打印"播放器心跳"的时间（每 5 秒一条，用于区分"排队"与"真写进管道"）。</summary>
        static DateTime _lastDiagAt = DateTime.MinValue;
        /// <summary>是否已成功把 ffplay 的会话音量拉回 100%（失败会在心跳里一直重试）。</summary>
        static bool _volumeForced;
        /// <summary>启动播放器时绑定的默认播放设备 ID（用于检测设备切换）。</summary>
        static string _deviceIdAtStart = "";
        /// <summary>检测到默认设备变化 → 置位，由**接收线程**（WriteOgg）安全重启，避免在 Loop 线程里 Join 自己。</summary>
        static volatile bool _restartRequested;

        /// <summary>待写管道的 Ogg 页（有界：接收线程只入队，不阻塞）。</summary>
        static readonly System.Collections.Concurrent.BlockingCollection<byte[]> _oggQueue
            = new System.Collections.Concurrent.BlockingCollection<byte[]>(boundedCapacity: 64);

        public static bool Start(out string error, int sampleRate = MicAudio.SampleRate, int channels = MicAudio.Channels)
        {
            error = null;
            if (_running && _proc != null && !_proc.HasExited) return true;
            _sampleRate = sampleRate; _channels = channels;
            if (!Spawn(out error)) return false;
            BytesWritten = 0; Restarts = 0;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "mic-pipe" };
            _thread.Start();
            PlayLog.Write("话筒", "播放启动", "info", detail: $"ffplay 管道播放已启动：{sampleRate}Hz {channels}ch s16le（延迟约 100~200ms）");
            return true;
        }

        static bool Spawn(out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Program.FfplayPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var a in OggMode
                    ? new[]
                    {
                        "-nodisp", "-autoexit",
                        "-fflags", "nobuffer", "-flags", "low_delay",   // 尽量少缓冲，压低延迟
                        "-infbuf",                                       // 实时流：不要限制输入缓冲
                        "-sync", "audio",
                        "-f", "ogg",                                     // 安卓端发来的是 Ogg-Opus 页
                        "-i", "pipe:0"
                    }
                    : new[]
                    {
                        "-nodisp", "-autoexit",
                        "-fflags", "nobuffer", "-flags", "low_delay",
                        "-infbuf",
                        "-sync", "audio",
                        "-f", "s16le",
                        "-ar", _sampleRate.ToString(),
                        // 注意：ffplay 不支持 ffmpeg 的 -ac，声道要用 -ch_layout（原始 PCM 默认 mono）
                        "-ch_layout", _channels <= 1 ? "mono" : "stereo",
                        "-i", "pipe:0"
                    })
                    psi.ArgumentList.Add(a);
                _proc = Process.Start(psi);
                if (_proc == null) { error = "ffplay 启动失败"; LastError = error; return false; }
                // 【必须把 ffplay 的 stdout/stderr 读走】我们重定向了这两条管道却从没人读：
                // ffplay 会持续往 stderr 打日志，管道缓冲（约 64KB）满了以后它自己就**卡住/退出** ——
                // 真机现象就是"播放几十秒后 ffplay 退出，已自动重启"，用户听到的是突然没声。
                // 这里各起一条后台线程持续读，并把最后 4KB 留下来（退出时写进日志，能直接看到 ffplay 为什么退）。
                DrainStreams(_proc);
                try { _deviceIdAtStart = AudioFade.DefaultRenderDeviceId(); } catch { }
                // 【没声音的直接修复】Windows 会按"应用"记住音量（音量合成器里 ffplay.exe 若被调成 0% 或静音，
                // ffplay 照常实时播放、日志里一切正常，但一点声音都没有 —— 真机日志已证实这一点）。
                // 这里在拉起 ffplay 之后**强制把它的会话音量设回 100%**，并记一条日志便于核对。
                try
                {
                    bool ok = AudioFade.SetProcessVolume(_proc, 1.0f);
                    PlayLog.Write("话筒", "会话音量", "info",
                        detail: "已把 ffplay 的音频会话音量强制设为 100%（若音量合成器里被调过 0%/静音，这一步就能恢复声音）：" + (ok ? "成功" : "失败（可能会话还没建立，稍后会再试）"));
                }
                catch (Exception vex) { PlayLog.Write("话筒", "会话音量", "warn", detail: "设置会话音量失败：" + vex.Message); }
                return true;
            }
            catch (Exception ex) { error = "ffplay 启动异常：" + ex.Message; LastError = error; return false; }
        }

        /// <summary>ffplay 最后输出的一小段（诊断：它为什么退出）。</summary>
        public static volatile string LastFfplayOutput = "";

        /// <summary>
        /// 供日志用的 ffplay 输出摘要（最后几行、压成一行、限长）。
        /// 用途：电脑端本机没声音时，ffplay 的 stderr（SDL 音频设备报错）是唯一直接证据；
        /// 以前只在 ffplay 退出时才写日志，而"打开了哑音频驱动"这种情况它不会退出，于是看不到原因。
        /// </summary>
        public static string FfplayTailForLog()
        {
            try
            {
                string s = LastFfplayOutput ?? "";
                if (s.Length == 0) return "(无输出)";
                s = s.Replace("\r", " ").Replace("\n", " ").Trim();
                if (s.Length > 220) s = s.Substring(s.Length - 220);
                return s;
            }
            catch { return "(读取失败)"; }
        }

        /// <summary>后台读走 ffplay 的 stdout/stderr（不读会把管道写满 → ffplay 卡住/退出）。</summary>
        static void DrainStreams(Process proc)
        {
            try
            {
                void Pump(System.IO.StreamReader r, bool keepTail)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (!keepTail) continue;
                            sb.AppendLine(line);
                            if (sb.Length > 4000) sb.Remove(0, sb.Length - 3000);
                            LastFfplayOutput = sb.ToString();
                        }
                    }
                    catch { }
                }
                var pOut = proc.StandardOutput; var pErr = proc.StandardError;
                new Thread(() => Pump(pOut, false)) { IsBackground = true, Name = "ffplay-out" }.Start();
                new Thread(() => Pump(pErr, true)) { IsBackground = true, Name = "ffplay-err" }.Start();
            }
            catch { }
        }

        public static void Stop()
        {
            _running = false;
            try { _thread?.Join(1500); } catch { }
            _thread = null;
            try { if (_proc != null && !_proc.HasExited) { _proc.StandardInput.Close(); } } catch { }
            try { if (_proc != null && !_proc.HasExited) { if (!_proc.WaitForExit(800)) _proc.Kill(); } } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        static void Loop()
        {
            var buf = new short[4096];
            var bytes = new byte[8192];
            while (_running)
            {
                try
                {
                    var proc = _proc;
                    if (proc == null || proc.HasExited)
                    {
                        if (Restarts >= MaxRestarts) { LastError = $"ffplay 已异常退出 {Restarts} 次，停止重启"; _running = false; return; }
                        Restarts++;
                        if (!Spawn(out string serr)) { LastError = serr; _running = false; return; }
                        PlayLog.Write("话筒", "播放重启", "warn",
                            detail: $"ffplay 退出，已自动重启（第 {Restarts} 次）"
                                  + (string.IsNullOrWhiteSpace(LastFfplayOutput) ? "" : "；它退出前最后输出：" + LastFfplayOutput.Replace("\r", "").Replace("\n", " | ").Trim()));
                        continue;
                    }
                    int n = Feed(buf, buf.Length);
                    if (OggMode)
                    {
                        // Ogg 直通：接收线程只入队，这里负责真正写管道（写阻塞也只影响这条线程）
                        // 【心跳诊断】每 5 秒记一次：真正写进管道的字节 / 排队字节 / 丢帧 / 队列深度 / ffplay 是否存活。
                        // 用途：用户反馈"电脑端显示播放器在跑但没声音" —— 这行能区分"只是排队了没写进去"、
                        // "写进去了但 ffplay 已退出"、还是"两者都正常（问题在系统音量/设备）"。
                        // 每 5 秒顺手确保 ffplay 的会话音量是 100%（进程刚起来时会话常常还没建立，第一次会失败）。
                        // 话筒这一路**永远不该被压低**（闪避只压播控/定时播放），所以这里无条件拉满。
                        // 检测默认播放设备是否变了（最容易被忽略的一条：设备一变，复用的播放器就再也听不到了）
                        try
                        {
                            string devNow = AudioFade.DefaultRenderDeviceId();
                            if (devNow.Length > 0 && _deviceIdAtStart.Length > 0 && devNow != _deviceIdAtStart)
                            {
                                _deviceIdAtStart = devNow;
                                _restartRequested = true;
                                PlayLog.Write("话筒", "输出设备已切换", "warn",
                                    detail: "默认播放设备已变化，将重启话筒播放器以跟随新设备（否则会一直往旧设备放，表现为没声音）");
                            }
                        }
                        catch { }
                        try { var pv = _proc; if (pv != null && !pv.HasExited && !_volumeForced) { if (AudioFade.SetProcessVolume(pv, 1.0f)) _volumeForced = true; } } catch { }
                        if ((DateTime.UtcNow - _lastDiagAt).TotalSeconds >= 5)
                        {
                            _lastDiagAt = DateTime.UtcNow;
                            var pd = _proc;
                            try
                            {
                                PlayLog.Write("话筒", "播放器心跳", "info",
                                    detail: $"已写管道 {BytesWritten / 1024} KB；排队 {OggBytes / 1024} KB；丢 {OggDrops} 帧；队列 {_oggQueue.Count}/64；ffplay 存活={(pd != null && !pd.HasExited)}；输出设备={_deviceIdAtStart.Replace("{0.0.0.00000000}.", "").Substring(Math.Min(20, _deviceIdAtStart.Replace("{0.0.0.00000000}.", "").Length))}；ffplay 输出={FfplayTailForLog()}");
                            }
                            catch { }
                        }
                        while (_oggQueue.TryTake(out var pg))
                        {
                            var p2 = _proc;
                            if (p2 == null || p2.HasExited) break;
                            try
                            {
                                p2.StandardInput.BaseStream.Write(pg, 0, pg.Length);
                                p2.StandardInput.BaseStream.Flush();
                                BytesWritten += pg.Length;
                            }
                            catch (Exception wex) { LastError = "写入 Ogg 失败：" + wex.Message; break; }
                        }
                        Thread.Sleep(10);
                        continue;
                    }
                    if (n <= 0) { Thread.Sleep(5); continue; }                 // 没数据就等，不写静音（让 ffplay 自己缓冲）
                    MicPcm.ApplyPeakLimit(buf, VntManager.MicPeakDb);          // 全程过限幅（防啸叫/过载）
                    int need = n * 2;
                    Buffer.BlockCopy(buf, 0, bytes, 0, need);
                    proc.StandardInput.BaseStream.Write(bytes, 0, need);
                    proc.StandardInput.BaseStream.Flush();
                    BytesWritten += need;
                }
                catch (Exception ex)
                {
                    LastError = "播放写入异常：" + ex.Message;
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>从话筒环形缓冲取数据（供 Loop 与自检用）。</summary>
        public static int Feed(short[] dst, int count)
        {
            var src = new short[count];
            int n = Ring.Read(src);
            if (n > 0) Array.Copy(src, dst, n);
            return n;
        }

        /// <summary>话筒数据环形缓冲（收包线程写、播放线程读）。容量约 400ms，超了丢最旧。</summary>
        public static readonly MicPcm.Ring Ring = new MicPcm.Ring(MicAudio.SampleRate * MicAudio.Channels / 1000 * 400);

        public static string StatusText()
            => _running
                ? $"播放中（ffplay 管道，{_sampleRate}Hz {_channels}ch，已送 {BytesWritten / Math.Max(1, _sampleRate * _channels * 2)} 秒" +
                  (Restarts > 0 ? $"，重启 {Restarts} 次" : "") + "）"
                : (string.IsNullOrWhiteSpace(LastError) ? "未播放" : "未播放（" + LastError + "）");
    }
}
