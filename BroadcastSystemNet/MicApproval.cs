using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 配对**异步人工确认**（确认弹窗模式）：手机点「配对」后，电脑端弹一个确认框，
    /// 管理员点「允许」才建会话。相比"手输 6 位动态码"，这种方式对使用者更省事
    /// （不用看着电脑屏幕抄数字），但安全模型不同 —— 它验证的是"电脑前的管理员同意"，
    /// 而不是"你能看到电脑屏幕"，所以：
    ///
    /// · 保留 6 位动态码作为**备用方式**（`VntManager.MicPairCodeMode`）；
    /// · 每个来源 IP 有**未决请求上限**与**触发间隔**，避免有人反复弹窗骚扰管理员；
    /// · 请求**不消耗**动态码（即使被拒也不影响备用方式）；
    /// · 审批是**异步**的：配对线程立刻回 `AUTH_PENDING` 并释放，等管理员点完再回最终应答，
    ///   这样弹窗等待期间不会把配对线程堵死（手机重试也不会排队）。
    ///
    /// 本类不碰 socket、不碰界面：界面只需订阅 <see cref="OnRequest"/> 并调用 <see cref="Complete"/>。
    /// </summary>
    public static class MicApproval
    {
        /// <summary>一条待确认的配对请求（界面展示用）。</summary>
        public sealed class Request
        {
            public string Id = "";
            public string DeviceId = "";
            public string DisplayName = "";
            public string SourceIp = "";
            public string VirtualIp = "";
            public DateTime CreatedUtc;
            public DateTime ExpiresUtc;
            /// <summary>管理员是否已经处理过（过期/自动拒绝也算）。</summary>
            public bool Decided;
        }

        /// <summary>未决请求的存活时长（超过即算拒绝，手机端会收到 denied/expired）。</summary>
        public const int RequestTtlSeconds = 45;
        /// <summary>同一来源 IP 两次弹窗之间的最小间隔（防骚扰）。</summary>
        public static readonly TimeSpan MinIntervalPerSource = TimeSpan.FromSeconds(5);
        /// <summary>同一来源 IP 同时最多几个未决请求。</summary>
        public const int MaxPendingPerSource = 1;
        /// <summary>全局未决请求上限（再多直接拒绝，避免界面被刷屏）。</summary>
        public const int MaxPendingTotal = 8;

        /// <summary>界面订阅：有新请求时触发（在配对线程上调用，界面内部自行切到 UI 线程）。</summary>
        public static Action<Request> OnRequest;
        /// <summary>界面订阅：请求被处理/过期时触发，用于关掉对应的确认框。</summary>
        public static Action<Request, bool> OnClosed;

        static readonly ConcurrentDictionary<string, Request> _pending = new ConcurrentDictionary<string, Request>();
        static readonly ConcurrentDictionary<string, DateTime> _lastAsk = new ConcurrentDictionary<string, DateTime>();

        public static int PendingCount => _pending.Count;
        public static IReadOnlyList<Request> Pending => _pending.Values.ToList();

        /// <summary>
        /// 发起一次人工确认。返回 false 表示"这次不该弹窗"（被节流/超限/重复），调用方应直接拒绝。
        /// 返回 true 时会通过 <see cref="OnRequest"/> 通知界面。
        /// </summary>
        public static bool TryBegin(string deviceId, string displayName, string sourceIp, string virtualIp,
            DateTime nowUtc, out Request request, out string rejectReason)
        {
            request = null;
            rejectReason = null;
            string src = sourceIp ?? "";
            if (_pending.Count >= MaxPendingTotal) { rejectReason = "未决配对请求过多"; return false; }
            if (_pending.Values.Count(r => string.Equals(r.SourceIp, src, StringComparison.Ordinal) && !r.Decided) >= MaxPendingPerSource)
            { rejectReason = "同一来源已有未决请求"; return false; }
            // 已经批准/拒绝过的短时间内不再弹：手机端会重试同一请求，不该反复打扰管理员
            if (_lastAsk.TryGetValue(src, out var last) && nowUtc - last < MinIntervalPerSource)
            { rejectReason = "刚刚已处理过该来源的请求"; return false; }

            var req = new Request
            {
                Id = Guid.NewGuid().ToString("N"),
                DeviceId = deviceId ?? "",
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? (deviceId ?? "未知设备") : displayName,
                SourceIp = src,
                VirtualIp = virtualIp ?? "",
                CreatedUtc = nowUtc,
                ExpiresUtc = nowUtc.AddSeconds(RequestTtlSeconds)
            };
            _pending[req.Id] = req;
            _lastAsk[src] = nowUtc;
            request = req;
            try { OnRequest?.Invoke(req); } catch { }
            return true;
        }

        /// <summary>管理员点了允许/拒绝（或界面关闭）。approve=true 表示允许接入。</summary>
        public static void Complete(string requestId, bool approve)
        {
            if (string.IsNullOrEmpty(requestId)) return;
            if (!_pending.TryRemove(requestId, out var req)) return;
            req.Decided = true;
            try { OnClosed?.Invoke(req, approve); } catch { }
            try { OnDecided?.Invoke(req, approve); } catch { }
        }

        /// <summary>服务端内部用：拿到某个请求的最终决定（弹窗关闭/过期时也该有人来收尾）。</summary>
        public static Action<Request, bool> OnDecided;

        /// <summary>过期清理：到期仍未处理的一律按拒绝处理（并通知界面关框）。</summary>
        public static int SweepExpired(DateTime nowUtc)
        {
            int n = 0;
            foreach (var kv in _pending.ToList())
            {
                if (nowUtc < kv.Value.ExpiresUtc) continue;
                if (_pending.TryRemove(kv.Key, out var req))
                {
                    req.Decided = true;
                    n++;
                    try { OnClosed?.Invoke(req, false); } catch { }
                    try { OnDecided?.Invoke(req, false); } catch { }
                }
            }
            return n;
        }

        /// <summary>清空（关麦/退出时用）：全部按拒绝处理。</summary>
        public static void ClearAll()
        {
            foreach (var kv in _pending.ToList()) Complete(kv.Key, false);
            _lastAsk.Clear();
        }

        public static string StatusText()
            => PendingCount == 0 ? "确认弹窗：无未决请求" : $"确认弹窗：{PendingCount} 个未决请求";
    }
}
