using System;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒播放前的 PCM 处理：环形缓冲 + 电平检测 + 增益 + 峰值限幅。
    /// 纯逻辑、可自检；WASAPI 播放线程只管从这里取数据送声卡。
    /// 限幅是文档里承诺的安全项：远程喊话不允许把喇叭推到过载/啸叫。
    /// </summary>
    public static class MicPcm
    {
        /// <summary>
        /// 单声道 16bit PCM 环形缓冲：写满后**丢最旧的**（宁可丢一点旧音频，也不让延迟无界增长）。
        /// 播放线程与收包线程分别调用 Read/Write，内部加锁（临界区极小）。
        /// </summary>
        public sealed class Ring
        {
            readonly short[] _buf;
            readonly object _lock = new object();
            int _head, _count;

            public Ring(int capacitySamples) { _buf = new short[Math.Max(64, capacitySamples)]; }

            public int Capacity => _buf.Length;
            public int Count { get { lock (_lock) return _count; } }

            public void Write(short[] data)
            {
                if (data == null || data.Length == 0) return;
                lock (_lock)
                {
                    foreach (var s in data)
                    {
                        if (_count == _buf.Length)                      // 满：丢最旧
                        {
                            _head = (_head + 1) % _buf.Length;
                            _count--;
                        }
                        _buf[(_head + _count) % _buf.Length] = s;
                        _count++;
                    }
                }
            }

            /// <summary>读取最多 dst.Length 个样本，返回实际读到的数量（不足则返回已有的）。</summary>
            public int Read(short[] dst)
            {
                if (dst == null || dst.Length == 0) return 0;
                lock (_lock)
                {
                    int n = Math.Min(dst.Length, _count);
                    for (int i = 0; i < n; i++) dst[i] = _buf[(_head + i) % _buf.Length];
                    _head = (_head + n) % _buf.Length;
                    _count -= n;
                    return n;
                }
            }

            public void Clear() { lock (_lock) { _head = 0; _count = 0; } }
        }

        /// <summary>分贝（相对满量程）：0 dBFS = 32767。空数组返回 -120。</summary>
        public static double PeakDb(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return -120;
            int peak = 0;
            foreach (var s in pcm) { int a = Math.Abs((int)s); if (a > peak) peak = a; }
            if (peak == 0) return -120;
            return 20 * Math.Log10(peak / 32767.0);
        }

        public static double RmsDb(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return -120;
            double sum = 0;
            foreach (var s in pcm) { double v = s / 32768.0; sum += v * v; }
            double rms = Math.Sqrt(sum / pcm.Length);
            return rms <= 0 ? -120 : 20 * Math.Log10(rms);
        }

        /// <summary>按比例增益（0.0-4.0），带饱和裁切，不会溢出短整型。</summary>
        public static void ApplyGain(short[] pcm, float gain)
        {
            if (pcm == null || pcm.Length == 0) return;
            if (gain < 0f) gain = 0f;
            if (gain > 4f) gain = 4f;
            for (int i = 0; i < pcm.Length; i++)
            {
                int v = (int)(pcm[i] * gain);
                if (v > short.MaxValue) v = short.MaxValue;
                if (v < short.MinValue) v = short.MinValue;
                pcm[i] = (short)v;
            }
        }

        /// <summary>
        /// 峰值限幅：若本块峰值超过 peakDb（默认 -3 dBFS），整体等比缩到上限以下。
        /// 返回实际施加的增益（1.0 = 没动）。只做"压"，不做"提"，避免把底噪拉起来。
        /// </summary>
        public static double ApplyPeakLimit(short[] pcm, double peakDb = -3.0)
        {
            if (pcm == null || pcm.Length == 0) return 1.0;
            double cur = PeakDb(pcm);
            if (cur <= peakDb) return 1.0;
            double gain = Math.Pow(10, (peakDb - cur) / 20.0);
            ApplyGain(pcm, (float)gain);
            return gain;
        }
    }
}
