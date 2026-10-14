using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 停止播放时的"渐弱"：直接控制 Windows 音频会话（WASAPI）里那一路（ffplay 进程）的音量，
    /// 从当前值斜坡降到 0，再把进程结束掉，避免硬切产生的"啪"声与突兀感。
    ///
    /// 为什么不用 ffmpeg：ffplay 的音量只能在启动时写死（-af volume=），运行中没有控制通道，
    /// afade 又只能用在"提前知道结束时刻"的场景；而手动停止、被定时任务抢占这些时刻是不可预知的。
    ///
    /// 本模块**不引入任何第三方依赖**，COM 接口按 Windows SDK 的 vtable 顺序声明。
    /// 找不到会话（进程已退出、或它此刻没有音频输出）时直接结束进程，不会卡住调用方。
    /// </summary>
    public static class AudioFade
    {
        /// <summary>默认渐弱时长（毫秒）。</summary>
        public const int DefaultMs = 800;

        /// <summary>当前生效的渐弱时长；0 = 关闭（直接停）。启动时从配置 fade_ms 读入。</summary>
        public static int DurationMs { get; set; } = DefaultMs;

        // ---------------- 公开入口 ----------------

        /// <summary>把斜坡切成若干级（纯函数，便于自检）。steps 级会返回 steps+1 个值：1.0 → 0。</summary>
        public static double[] Ramp(int steps)
        {
            steps = Math.Max(2, Math.Min(40, steps));
            var arr = new double[steps + 1];
            for (int i = 0; i <= steps; i++) arr[i] = Math.Round(1.0 - (double)i / steps, 3);
            return arr;
        }

        /// <summary>
        /// 渐弱到静音再结束进程（同步等待，最长 ms 毫秒）。
        /// 返回 true 表示确实控制过音量；false 表示没找到音频会话（已直接结束进程）。
        /// </summary>
        public static bool FadeOutAndKill(Process proc, int ms = -1)
        {
            if (proc == null) return false;
            int dur = ms >= 0 ? ms : DurationMs;
            object sessionObj = null;
            try
            {
                if (dur <= 0) { KillQuiet(proc); return false; }
                bool exited0; try { exited0 = proc.HasExited; } catch { exited0 = true; }
                if (exited0) return false;

                sessionObj = FindSessionVolume(proc.Id);
                if (sessionObj == null) { KillQuiet(proc); return false; }
                var vol = (ISimpleAudioVolume)sessionObj;
                var ramp = Ramp(Math.Max(4, Math.Min(20, dur / 60)));
                int per = Math.Max(10, dur / (ramp.Length - 1));
                Guid ctx = Guid.Empty;
                foreach (var v in ramp)
                {
                    try { vol.SetMasterVolume((float)v, ref ctx); } catch { break; }
                    bool exited; try { exited = proc.HasExited; } catch { exited = true; }
                    if (exited) break;
                    Thread.Sleep(per);
                }
                // 【根治】渐弱已经降到 0（Ramp 末值就是 0），这里**必须把音量恢复成 1.0 再杀进程**：
                // Windows 把"会话音量"当作**按应用音量**持久记住，若最后写入的是 0，之后新起的 ffplay 就会以 0% 播放
                // —— 表现是"进程在跑、日志一切正常、却完全没声音，只能重启电脑"（广播室真机已复现）。
                // 恢复音量与 KillQuiet 之间只有约 1ms（Kill 是同步立即返回），最坏情况是渐弱结尾一声极轻的咔哒。
                try { vol.SetMasterVolume(1f, ref ctx); } catch { }
                KillQuiet(proc);
                return true;
            }
            catch { KillQuiet(proc); return false; }
            finally { try { if (sessionObj != null) Marshal.ReleaseComObject(sessionObj); } catch { } }
        }

        /// <summary>多个进程共用同一条斜坡一起渐弱（先全部降到 0，再逐个结束）。</summary>
        public static int FadeOutAndKillAll(IEnumerable<Process> procs, int ms = -1)
        {
            int n = 0;
            var list = new List<Process>();
            if (procs != null)
                foreach (var p in procs)
                {
                    if (p == null) continue;
                    try { if (p.HasExited) continue; } catch { continue; }
                    list.Add(p);
                }
            foreach (var p in list) { try { FadeOutAndKill(p, ms); n++; } catch { } }
            return n;
        }

        /// <summary>从配置读渐弱时长（setting.jsonl 的全局记录 fade_ms）；读不到就用默认 800。</summary>
        public static int ReadConfiguredMs()
        {
            try
            {
                foreach (var item in Program.ReadSettingMerged())
                    if (item is System.Text.Json.Nodes.JsonObject g && g["_type"]?.ToString() == "global")
                    {
                        var node = g["fade_ms"];
                        if (node == null) return DefaultMs;
                        int v = node.GetValue<int>();
                        return v < 0 ? DefaultMs : v;      // 0 = 关闭
                    }
            }
            catch { }
            return DefaultMs;
        }

        /// <summary>
        /// 直接设置某个音频进程的会话音量（0.0-1.0），不结束进程。
        /// 话筒"闪避"用：说话时把音乐压到 20%，停麦后恢复 100%。
        /// 找不到会话（进程没在播/已退出）返回 false，不抛异常。
        /// </summary>
        /// <summary>
        /// 当前**默认播放（渲染）设备**的 ID；取不到返回空串。
        /// 用途：ffplay/SDL 只在进程启动那一刻绑定默认设备，之后设备变了不会跟随（连蓝牙、拔插耳机都会变），
        /// 于是"日志里在实时播放、用户却听不到声音"。靠这个 ID 变化来触发播放器重启。
        /// </summary>
        public static string DefaultRenderDeviceId()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return "";
                string id;
                return dev.GetId(out id) == 0 ? (id ?? "") : "";
            }
            catch { return ""; }
        }
        public static bool SetProcessVolume(Process proc, float level)
        {
            if (proc == null) return false;
            if (level < 0f) level = 0f;
            if (level > 1f) level = 1f;
            object sessionObj = null;
            try
            {
                bool exited; try { exited = proc.HasExited; } catch { exited = true; }
                if (exited) return false;
                sessionObj = FindSessionVolume(proc.Id);
                if (sessionObj == null) return false;
                var vol = (ISimpleAudioVolume)sessionObj;
                var ctx = Guid.Empty;
                return vol.SetMasterVolume(level, ref ctx) == 0;
            }
            catch { return false; }
            finally { try { if (sessionObj != null) Marshal.ReleaseComObject(sessionObj); } catch { } }
        }

        static void KillQuiet(Process proc)
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        }

        // ---------------- WASAPI COM 互操作（按 vtable 顺序声明） ----------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, out IAudioSessionManager2 instance);
            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            // 前两个来自基接口 IAudioSessionManager，必须保留位置
            [PreserveSig]
            int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
            [PreserveSig]
            int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            [PreserveSig]
            int RegisterSessionNotification(IntPtr sessionNotification);
            [PreserveSig]
            int UnregisterSessionNotification(IntPtr sessionNotification);
            [PreserveSig]
            int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
            [PreserveSig]
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // 前 9 个来自 IAudioSessionControl，顺序不能动
            [PreserveSig]
            int GetState(out int state);
            [PreserveSig]
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig]
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            [PreserveSig]
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig]
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            [PreserveSig]
            int GetGroupingParam(out Guid groupingId);
            [PreserveSig]
            int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            [PreserveSig]
            int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig]
            int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2 自己
            [PreserveSig]
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetProcessId(out int pid);
            [PreserveSig]
            int IsSystemSoundsSession();
            [PreserveSig]
            int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig]
            int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig]
            int GetMasterVolume(out float level);
            [PreserveSig]
            int SetMute(bool mute, ref Guid eventContext);
            [PreserveSig]
            int GetMute(out bool mute);
        }

        /// <summary>找出指定进程的会话音量控制对象；找不到返回 null。</summary>
        private static object FindSessionVolume(int pid)
        {
            return Find(pid, null);
        }

        /// <summary>诊断用：列出所有输出设备上的音频会话 PID。</summary>
        public static List<int> ListSessionPids(out string detail)
        {
            var pids = new List<int>();
            var sb = new System.Text.StringBuilder();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                try
                {
                    if (en.EnumAudioEndpoints(EDataFlow.eRender, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection col) != 0 || col == null) { detail = "枚举输出设备失败"; return pids; }
                    try
                    {
                        if (col.GetCount(out int n) != 0) { detail = "取设备数失败"; return pids; }
                        sb.Append("输出设备 " + n + " 个：");
                        for (int d = 0; d < n; d++)
                        {
                            IMMDevice dev = null;
                            try
                            {
                                if (col.Item(d, out dev) != 0 || dev == null) continue;
                                string id = ""; try { dev.GetId(out id); } catch { }
                                Guid iid = typeof(IAudioSessionManager2).GUID;
                                                                int hrAct = dev.Activate(ref iid, 0x17, IntPtr.Zero, out IAudioSessionManager2 mgr);
                                if (hrAct != 0 || mgr == null) { sb.Append(" [设备" + d + " 激活失败 hr=0x" + hrAct.ToString("X8") + " iid=" + iid + "]"); continue; }
                                try
                                {
                                    if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator ses) != 0 || ses == null) continue;
                                    try
                                    {
                                        if (ses.GetCount(out int sc) != 0) continue;
                                        sb.Append(" [设备" + d + " 会话" + sc + "：");
                                        for (int s = 0; s < sc; s++)
                                        {
                                            object so = null;
                                            try
                                            {
                                                if (ses.GetSession(s, out so) != 0 || so == null) continue;
                                                int sp = -1;
                                                var c2 = so as IAudioSessionControl2;
                                                if (c2 != null) { try { c2.GetProcessId(out sp); } catch { } }
                                                sb.Append(sp + " ");
                                                if (sp > 0 && !pids.Contains(sp)) pids.Add(sp);
                                            }
                                            finally { if (so != null) { try { Marshal.ReleaseComObject(so); } catch { } } }
                                        }
                                        sb.Append("]");
                                    }
                                    finally { try { Marshal.ReleaseComObject(ses); } catch { } }
                                }
                                finally { try { Marshal.ReleaseComObject(mgr); } catch { } }
                            }
                            finally { if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } } }
                        }
                    }
                    finally { try { Marshal.ReleaseComObject(col); } catch { } }
                }
                finally { try { Marshal.ReleaseComObject(en); } catch { } }
            }
            catch (Exception ex) { sb.Append("异常：" + ex.Message); }
            detail = sb.ToString();
            return pids;
        }

        /// <summary>
        /// 在所有**活动的输出设备**上找这个进程的会话音量对象。
        /// 只查"默认设备"不够：ffplay/SDL 可能输出到别的设备（HDMI、USB 声卡、虚拟声卡），
        /// 那样就找不到会话，渐弱会退化成直接结束（实测踩过这个坑）。
        /// </summary>
        private static object Find(int pid, string unused)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection col = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1 /* DEVICE_STATE_ACTIVE */, out col) != 0 || col == null) return null;
                if (col.GetCount(out int n) != 0) return null;
                for (int d = 0; d < n; d++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (col.Item(d, out device) != 0 || device == null) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out IAudioSessionManager2 manager) != 0 || manager == null) continue;
                        if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0 || sessions == null) continue;
                        try
                        {
                            if (sessions.GetCount(out int count) != 0) continue;
                            for (int i = 0; i < count; i++)
                            {
                                object session = null;
                                try
                                {
                                    if (sessions.GetSession(i, out session) != 0 || session == null) continue;
                                    var ctl2 = session as IAudioSessionControl2;
                                    if (ctl2 == null) continue;
                                    int spid = -1;
                                    if (ctl2.GetProcessId(out spid) != 0 || spid != pid) continue;
                                    var vol = session as ISimpleAudioVolume;
                                    if (vol != null) { session = null; return vol; }   // 交给调用方释放
                                }
                                finally { if (session != null) { try { Marshal.ReleaseComObject(session); } catch { } } }
                            }
                        }
                        finally { try { if (sessions != null) Marshal.ReleaseComObject(sessions); } catch { } }
                    }
                    catch { }
                    finally
                    {
                        try { if (device != null) Marshal.ReleaseComObject(device); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (col != null) Marshal.ReleaseComObject(col); } catch { }
                try { if (enumerator != null) Marshal.ReleaseComObject(enumerator); } catch { }
            }
            return null;
        }
    }
}
