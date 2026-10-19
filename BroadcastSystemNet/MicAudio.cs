using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒音频底座：Opus 解码器加载（优先随包 res\bin\opus.dll）+ PCM 落盘（WAV）。
    /// 设计原则：**缺依赖不崩、不静默**——加载失败只返回 false 并把原因写进 LastError，
    /// 上层据此在菜单里提示"把 opus.dll 放到 res\bin 下"，或退化为"录成 WAV 供试听"。
    /// </summary>
    public static class MicAudio
    {
        public const int SampleRate = 48000;     // 安卓端按文档统一 48kHz
        public const int Channels = 1;           // 单声道

        public static string LastError = "";
        static IntPtr _opusLib = IntPtr.Zero;
        static IntPtr _decoder = IntPtr.Zero;
        static int _decoderRate, _decoderChannels;

        // ---------------- opus.dll 加载 ----------------

        /// <summary>查找 opus 库（res\bin → 程序目录）。同时认 libopus-0.dll / libopus.dll（不同来源命名不同）。</summary>
        public static string ResolveOpusDll()
        {
            try
            {
                foreach (var dir in new[] { System.IO.Path.Combine(Program.ResDir, "bin"), Program.BaseDir })
                {
                    foreach (var name in new[] { "opus.dll", "libopus-0.dll", "libopus.dll" })
                    {
                        string cand = System.IO.Path.Combine(dir, name);
                        if (File.Exists(cand)) return cand;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>加载 opus.dll；失败时 LastError 说明原因（不会抛异常）。</summary>
        public static bool TryLoadOpus()
        {
            if (_opusLib != IntPtr.Zero) return true;
            string path = ResolveOpusDll();
            if (string.IsNullOrWhiteSpace(path))
            {
                LastError = "没找到 opus 解码库：请把 opus.dll（或 libopus-0.dll）放到 res\\bin\\ 下（与 ffmpeg 放在一起）";
                return false;
            }
            try
            {
                _opusLib = NativeLibrary.Load(path);
                // 用版本字符串验证确实是可用的 opus 库，而不是同名假文件
                var verPtr = NativeLibrary.GetExport(_opusLib, "opus_get_version_string");
                var getVer = Marshal.GetDelegateForFunctionPointer<OpusVersionString>(verPtr);
                OpusVersion = Marshal.PtrToStringAnsi(getVer()) ?? "";
                LastError = "";
                return OpusVersion.Length > 0;
            }
            catch (Exception ex)
            {
                LastError = "加载 opus.dll 失败：" + ex.Message;
                _opusLib = IntPtr.Zero;
                return false;
            }
        }

        public static string OpusVersion = "";

        /// <summary>创建解码器（默认 48kHz 单声道）。失败返回 false 并给出原因。</summary>
        public static bool TryCreateDecoder(int sampleRate = SampleRate, int channels = Channels)
        {
            if (_decoder != IntPtr.Zero && _decoderRate == sampleRate && _decoderChannels == channels) return true;
            if (!TryLoadOpus()) return false;
            try
            {
                if (_decoder != IntPtr.Zero) { OpusDestroy(_decoder); _decoder = IntPtr.Zero; }
                int err = 0;
                _decoder = OpusCreate(sampleRate, channels, out err);
                if (_decoder == IntPtr.Zero) { LastError = "创建 Opus 解码器失败，错误码 " + err; return false; }
                _decoderRate = sampleRate; _decoderChannels = channels;
                return true;
            }
            catch (Exception ex) { LastError = "创建 Opus 解码器异常：" + ex.Message; return false; }
        }

        /// <summary>
        /// 解一帧 Opus → 16bit PCM。返回实际样本数（每声道）；失败返回 -1 并给出原因。
        /// frame 为 UDP 包里那一段明文（一个 Opus 帧）。
        /// </summary>
        public static int DecodeFrame(byte[] frame, short[] pcm)
        {
            if (!TryCreateDecoder()) return -1;
            if (frame == null || frame.Length == 0) return -1;
            try
            {
                int samples = OpusDecode(_decoder, frame, frame.Length, pcm, pcm.Length);
                if (samples < 0) { LastError = "Opus 解码失败，错误码 " + samples; return -1; }
                return samples;
            }
            catch (Exception ex) { LastError = "Opus 解码异常：" + ex.Message; return -1; }
        }

        /// <summary>释放解码器（关麦时调用）。</summary>
        public static void ReleaseDecoder()
        {
            try { if (_decoder != IntPtr.Zero) { OpusDestroy(_decoder); _decoder = IntPtr.Zero; } } catch { }
        }

        public static string Status()
            => _decoder != IntPtr.Zero
                ? $"Opus 解码就绪（{OpusVersion}，{_decoderRate}Hz {_decoderChannels}ch）"
                : (string.IsNullOrWhiteSpace(ResolveOpusDll()) ? "缺少 opus.dll（放到 res\\bin 下即可开麦出声）" : "opus.dll 已找到但尚未初始化");

        // ---------------- PCM → WAV（自检可验证的字节级行为） ----------------

        /// <summary>
        /// 把 16bit PCM 写成标准 WAV（44 字节头 + 数据）。
        /// 用途：① 调试录音（丢包/杂音排查）；② 没有 opus.dll 时退化为"录下来供试听"。
        /// </summary>
        public static bool WriteWav(string path, short[] pcm, int sampleRate = SampleRate, int channels = Channels)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { LastError = "WAV 路径为空"; return false; }
                pcm ??= Array.Empty<short>();
                int dataBytes = pcm.Length * 2;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var w = new BinaryWriter(fs);
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);                                   // fmt 块长度
                w.Write((short)1);                             // PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);            // 字节率
                w.Write((short)(channels * 2));                // 块对齐
                w.Write((short)16);                            // 位深
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                var bytes = new byte[dataBytes];
                Buffer.BlockCopy(pcm, 0, bytes, 0, dataBytes);
                w.Write(bytes);
                w.Flush();
                return true;
            }
            catch (Exception ex) { LastError = "写 WAV 失败：" + ex.Message; return false; }
        }

        /// <summary>读取 WAV 的采样率/声道/数据长度（自检用，也便于排查"录出来的对不对"）。</summary>
        public static bool TryReadWavInfo(string path, out int sampleRate, out int channels, out int dataBytes, out string error)
        {
            sampleRate = 0; channels = 0; dataBytes = 0; error = null;
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 44) { error = "文件太短"; return false; }
                if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') { error = "不是 RIFF"; return false; }
                channels = BitConverter.ToInt16(b, 22);
                sampleRate = BitConverter.ToInt32(b, 24);
                dataBytes = BitConverter.ToInt32(b, 40);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ---------------- P/Invoke（opus 的 C API） ----------------

        delegate IntPtr OpusVersionString();

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_create")]
        static extern IntPtr OpusCreate(int Fs, int channels, out int error);

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decode")]
        static extern int OpusDecode(IntPtr st, byte[] data, int len, short[] pcm, int frameSize, int decodeFec = 0);

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_decoder_destroy")]
        static extern void OpusDestroy(IntPtr st);
    }
}
