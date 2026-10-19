using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 播放进程的停止与音量控制（WASAPI 音频会话）。
    ///
    /// 【2026-10-15 变更：停止播放不再渐弱，改为立即结束进程】
    /// 原设计是"边把会话音量斜坡降到 0、边等 800ms、再杀进程"，用来避免硬切的"啪"声。但它带来三类不稳定性：
    ///   ① 阻塞调用线程 —— 停用广播/切歌时 UI 与 HTTP 接口会被卡住 0.8~2.4 秒（手动 + 定时 + 台账各渐弱一次）；
    ///   ② 与新起进程抢音量 —— 旧进程还在渐弱、新进程已起播，两者是同一个 ffplay.exe 的同一会话组，互相覆盖；
    ///   ③ 会把会话音量留在 0% —— Windows 按应用记住这个 0，下次起播就彻底没声，只能重启电脑。
    /// 现在改为直接结束（连同子进程树），**完全不碰会话音量**，上面三类问题一起消失。代价是硬切时可能有轻微"啪"声。
    ///
    /// 本模块**不引入任何第三方依赖**，COM 接口按 Windows SDK 的 vtable 顺序声明。
    /// </summary>
    public static class AudioFade
    {
        // ---------------- 公开入口 ----------------

        /// <summary>
        /// 立即结束播放进程（含子进程树），**不碰会话音量**。
        /// 返回 true 表示调用时进程还活着（确实结束了它）；false 表示进程已退出或对象无效。
        /// 之所以刻意不碰音量：以前渐弱会把会话写成 0，而 Windows 按应用记住这个 0，
        /// 于是"下次起播彻底没声、只能重启电脑" —— 现在从根上不再产生这种状态。
        /// </summary>
        public static bool StopNow(Process proc)
        {
            if (proc == null) return false;
            try { if (proc.HasExited) return false; } catch { return false; }
            try { proc.Kill(true); } catch { try { proc.Kill(); } catch { } }   // true = 连同子进程树
            return true;
        }

        /// <summary>批量立即结束（批量清理与自检用）。</summary>
        public static int StopNowAll(IEnumerable<Process> procs)
        {
            if (procs == null) return 0;
            int n = 0;
            foreach (var p in procs) { try { if (StopNow(p)) n++; } catch { } }
            return n;
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
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return "";
                string id;
                return dev.GetId(out id) == 0 ? (id ?? "") : "";
            }
            catch { return ""; }
            finally
            {
                // 这两个都是 RCW（COM 包装对象）。不显式释放就只能等 GC 终结器回收，
                // 而调用方（话筒播放线程）是**每 10ms 轮询一次**——不释放会每秒堆几百个包装对象，
                // 把终结器线程拖住、进而影响音频子系统。
                if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } }
                if (en != null) { try { Marshal.ReleaseComObject(en); } catch { } }
            }
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
                // 【关键】WASAPI 里 MasterVolume 与 Mute 是**两个独立状态**，而且都会被 Windows 按应用记住。
                // 如果这个会话曾经被静音（音量合成器里点了静音、或某次快捷键/外部程序改过），
                // 只把音量设成 1.0 是**完全听不见的** —— 日志会照常打印"已把 ffplay 的会话音量拉回 100%"，
                // 而喇叭一声没有。所以只要目标音量 > 0，就顺手把 Mute 解除。
                if (level > 0f) { try { vol.SetMute(false, ref ctx); } catch { } }
                return vol.SetMasterVolume(level, ref ctx) == 0;
            }
            catch { return false; }
            finally { try { if (sessionObj != null) Marshal.ReleaseComObject(sessionObj); } catch { } }
        }

        // （原来的 KillQuiet 已并入 StopNow：现在停止播放一律走 StopNow，且不碰会话音量）

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
        /// 那样就找不到会话 —— 闪避（压低音乐）和"起播时拉回 100%"都会失效（实测踩过这个坑）。
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
