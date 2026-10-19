using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// WASAPI 低延迟播放：共享模式 + 轮询缓冲，从 MicPcm.Ring 取 16bit PCM，
    /// 过峰值限幅后转 float 写入声卡。丢帧时填静音（宁可短暂静音也不要爆音/卡顿）。
    /// 与 AudioFade 一样是"零新依赖"的 COM 互操作；出错只记 LastError，不抛给调用方。
    /// </summary>
    public static class MicRender
    {
        public static string LastError = "";
        public static long SamplesRendered;     // 已送出声卡的样本数
        public static long Underruns;           // 没数据可播、填了静音的块数

        static IAudioClient _client;
        static IAudioRenderClient _render;
        static Thread _thread;
        static volatile bool _running;
        static int _sampleRate = MicAudio.SampleRate;
        static int _channels = MicAudio.Channels;

        public static bool Running => _running;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        // ---------------- 启停 ----------------

        public static bool Start(out string error, int sampleRate = MicAudio.SampleRate, int channels = MicAudio.Channels)
        {
            error = null;
            // 安全闸门：WASAPI 渲染通道目前**默认禁用**。它在真机探针里会让进程崩（0xC0000374/0xC0000005，
            // 崩点落在框架代码里 = Start 里的 COM 调用已经破坏内存），在彻底查清之前不允许默认启用，
            // 否则"开麦"会把整个广播程序带崩。要试验时把 vnt.jsonl 的 mic.render_enabled 设为 true。
            if (!VntManager.GetBool("mic", "render_enabled", false))
            {
                error = "话筒播放（WASAPI）当前版本默认禁用：该通道仍需加固（真机验证时会崩）。"
                      + "如需试验，请在配置 mic 段把 render_enabled 设为 true。";
                LastError = error;
                return false;
            }
            if (_running) return true;
            _sampleRate = sampleRate; _channels = channels;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out IMMDevice dev) != 0 || dev == null)
                { error = "找不到默认播放设备"; LastError = error; return false; }
                Guid iid = typeof(IAudioClient).GUID;
                if (dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out IAudioClient client) != 0 || client == null)
                { error = "无法激活音频客户端"; LastError = error; return false; }

                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = 1,                                   // PCM
                    nChannels = (ushort)channels,
                    nSamplesPerSec = (uint)sampleRate,
                    wBitsPerSample = 16,
                    nBlockAlign = (ushort)(channels * 2),
                    nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                    cbSize = 0
                };
                // 共享模式、事件驱动（AUTOCONVERTPCM 让不同采样率的设备也能直接吃 48k）
                int hr = client.Initialize(0 /*AUDCLNT_SHAREMODE_SHARED*/,
                    unchecked((int)(0x00080000u /*AUTOCONVERTPCM*/ | 0x80000000u /*SRC_DEFAULT_QUALITY*/)),
                    200000 /*10ms 缓冲*/, 0, ref fmt, IntPtr.Zero /* 无会话GUID：必须传指针，不能按值传 Guid */);
                if (hr != 0) { error = $"音频客户端初始化失败 hr=0x{hr:X8}"; LastError = error; try { Marshal.ReleaseComObject(client); } catch { } return false; }
                if (client.GetService(typeof(IAudioRenderClient).GUID, out object svc) != 0 || svc == null)
                { error = "取渲染接口失败"; LastError = error; return false; }
                _render = (IAudioRenderClient)svc;
                _client = client;
                if (client.Start() != 0) { error = "启动播放流失败"; LastError = error; return false; }
                SamplesRendered = 0; Underruns = 0;
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "mic-render" };
                _thread.Start();
                PlayLog.Write("话筒", "播放启动", "info", detail: $"WASAPI 播放已启动：{sampleRate}Hz {channels}ch（共享模式，10ms 缓冲）");
                return true;
            }
            catch (Exception ex) { error = "启动播放失败：" + ex.Message; LastError = error; return false; }
        }

        public static void Stop()
        {
            _running = false;
            // 关键：播放线程还在用 COM 时**绝不能**释放接口，否则就是"释放后使用"→ 堆损坏(0xC0000374)。
            // 线程没在超时内退出就宁可泄漏这几个 COM 引用，也绝不释放。
            var t = _thread;
            if (t != null && t.IsAlive && !t.Join(3000))
            {
                LastError = "播放线程未在 3 秒内退出，为避免崩溃未释放音频接口（下次开麦会重新创建）";
                PlayLog.Write("话筒", "播放线程未退出", "warn", detail: LastError);
                return;
            }
            _thread = null;
            try { _client?.Stop(); } catch { }
            try { if (_render != null) Marshal.ReleaseComObject(_render); } catch { }
            try { if (_client != null) Marshal.ReleaseComObject(_client); } catch { }
            _render = null; _client = null;
        }

        static void Loop()
        {
            var buf = new short[4096];
            while (_running)
            {
                // 用局部引用：即便 Stop() 把字段置空，本线程手里的引用仍然有效（且 Stop 会等我们退出）
                var client = _client;
                var render = _render;
                if (client == null || render == null) return;
                try
                {
                    if (!_running) return;
                    if (client.GetCurrentPadding(out int padding) != 0) { Thread.Sleep(10); continue; }
                    if (client.GetBufferSize(out int bufFrames) != 0) { Thread.Sleep(10); continue; }
                    int frames = bufFrames - padding;
                    if (frames <= 0) { Thread.Sleep(5); continue; }
                    int need = frames * _channels;
                    if (need > buf.Length) buf = new short[need];
                    int got = Feed(buf, need);                     // 从环形缓冲取，尽量取满
                    if (got < need)
                    {
                        for (int i = got; i < need; i++) buf[i] = 0;   // 数据不够 → 填静音
                        Underruns++;
                    }
                    MicPcm.ApplyPeakLimit(buf, VntManager.MicPeakDb);  // 全程过限幅（防啸叫/过载）
                    if (render.GetBuffer(frames, out IntPtr p) != 0 || p == IntPtr.Zero) { Thread.Sleep(5); continue; }
                    try
                    {
                        // 声卡缓冲按"帧 × 声道"的 float 计，长度与 need 一致（need = frames × channels）
                        var floats = new float[need];
                        for (int i = 0; i < need; i++) floats[i] = buf[i] / 32768f;
                        Marshal.Copy(floats, 0, p, need);
                    }
                    finally { render.ReleaseBuffer(frames, 0); }
                    SamplesRendered += need;
                }
                catch (Exception ex) { LastError = "播放循环异常：" + ex.Message; Thread.Sleep(20); }
            }
        }

        /// <summary>从话筒环形缓冲取数据（供 Loop 与自检用）：返回实际取到的样本数。</summary>
        public static int Feed(short[] dst, int count)
        {
            var src = new short[count];
            int n = Ring.Read(src);
            if (n > 0) Array.Copy(src, dst, n);
            return n;
        }

        /// <summary>话筒数据环形缓冲（收包线程写、播放线程读）。容量约 400ms，超了就丢最旧。</summary>
        public static readonly MicPcm.Ring Ring = new MicPcm.Ring(MicAudio.SampleRate * MicAudio.Channels / 1000 * 400);

        public static string StatusText()
            => _running
                ? $"播放中（{_sampleRate}Hz {_channels}ch，已播 {SamplesRendered / (_sampleRate * _channels)} 秒，补静音 {Underruns} 次）"
                : (string.IsNullOrWhiteSpace(LastError) ? "未播放" : "未播放（" + LastError + "）");

        // ---------------- WASAPI COM 互操作 ----------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, out IAudioClient instance);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity,
                ref WAVEFORMATEX format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out int frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out int padding);
            [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(int numFramesRequested, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(int numFramesWritten, int flags);
        }
    }
}
