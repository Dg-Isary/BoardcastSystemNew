using System;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒"闪避"（ducking）：话筒说话期间把正在播放的音乐/打铃压低，停麦后恢复。
    /// 这里只做状态机与判定（纯逻辑、可自检）；真正的音量施加通过 ApplyLevel 回调交给上层，
    /// 由 AudioFade 用 Windows 音频会话接口去改那一路的音量（不改变任务里配置的音量值）。
    /// </summary>
    public static class MicDuck
    {
        /// <summary>是否启用闪避（关掉就是话筒与音乐各放各的）。</summary>
        public static bool Enabled = true;
        /// <summary>压低到百分之几（默认 20）。</summary>
        public static int DuckPercent = 20;
        /// <summary>静默多久算"说完话了"（毫秒），默认 800。</summary>
        public static int RestoreAfterSilenceMs = 800;

        static readonly object _lock = new object();
        static bool _ducked;
        static DateTime _lastAudioUtc = DateTime.MinValue;

        /// <summary>音量施加回调：参数是目标音量（0.0-1.0）。上层负责真正去改播放进程的会话音量。</summary>
        public static Action<float> ApplyLevel;

        public static bool IsDucked { get { lock (_lock) return _ducked; } }

        /// <summary>压低后的音量比例（0.0-1.0），已做边界夹紧。</summary>
        public static float DuckLevel
        {
            get
            {
                int p = DuckPercent;
                if (p < 0) p = 0;
                if (p > 100) p = 100;
                return p / 100f;
            }
        }

        /// <summary>收到一个合法话筒音频帧：首次触发压低，持续刷新"最后有声时间"。返回本次是否刚好触发压制。</summary>
        public static bool NoteAudio(DateTime nowUtc)
        {
            bool justDucked = false;
            lock (_lock)
            {
                _lastAudioUtc = nowUtc;
                if (Enabled && !_ducked)
                {
                    _ducked = true;
                    justDucked = true;
                }
            }
            if (justDucked) Apply(DuckLevel);
            return justDucked;
        }

        /// <summary>
        /// 周期性检查：静默超过阈值就恢复音量。返回本次是否刚好恢复。
        /// 调用方传 nowUtc（不读系统时间），便于自检精确验证。
        /// </summary>
        public static bool Sweep(DateTime nowUtc, int? silenceMs = null)
        {
            int limit = silenceMs ?? RestoreAfterSilenceMs;
            bool restore = false;
            lock (_lock)
            {
                if (!_ducked) return false;
                if (_lastAudioUtc == DateTime.MinValue) return false;
                if ((nowUtc - _lastAudioUtc).TotalMilliseconds >= limit)
                {
                    _ducked = false;
                    restore = true;
                }
            }
            if (restore) Apply(1.0f);
            return restore;
        }

        /// <summary>立即恢复（会话结束、关麦、异常退出时调用），返回之前是否处于压制状态。</summary>
        public static bool ForceRestore()
        {
            bool was;
            lock (_lock)
            {
                was = _ducked;
                _ducked = false;
                _lastAudioUtc = DateTime.MinValue;
            }
            if (was) Apply(1.0f);
            return was;
        }

        /// <summary>状态文案（菜单/状态页显示）。</summary>
        public static string StatusText()
            => Enabled
                ? (IsDucked ? $"话筒说话中：音乐已压低到 {DuckPercent}%" : $"待机（话筒说话时自动压低音乐到 {DuckPercent}%）")
                : "闪避已关闭（话筒与音乐各放各的）";

        static void Apply(float level)
        {
            try { ApplyLevel?.Invoke(level); } catch { }
        }
    }
}
