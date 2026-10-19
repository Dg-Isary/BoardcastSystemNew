using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒功能的安全状态机（不碰网络，便于自检）：
    ///   · PairingGuard：配对失败限速与锁定（5 次/5 分钟 → 锁 10 分钟）
    ///   · CodeStore   ：动态码一次性消费（60 秒窗口，当前或上一窗口有效，用过即废）
    ///   · Registry    ：会话注册、每包校验链（白名单 → 防重放 → 限速 → 时长/静默超时）
    ///   · Stats       ：审计计数（丢包/重放/AEAD失败/限速）供状态页与告警用
    /// 所有方法都接受 DateTime now 参数（不直接读系统时间），这样时间相关的逻辑可以被自检精确验证。
    /// </summary>
    public static class MicSecurity
    {
        // ================= 配对限速 =================

        public sealed class PairingGuard
        {
            public const int MaxFailures = 5;
            public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
            public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

            readonly object _lock = new object();
            readonly Dictionary<string, List<DateTime>> _fails = new Dictionary<string, List<DateTime>>();
            readonly Dictionary<string, DateTime> _lockedUntil = new Dictionary<string, DateTime>();

            public bool IsLocked(string ip, DateTime now)
            {
                lock (_lock)
                {
                    if (_lockedUntil.TryGetValue(ip ?? "", out var until) && now < until) return true;
                    return false;
                }
            }

            public int FailsInWindow(string ip, DateTime now)
            {
                lock (_lock)
                {
                    if (!_fails.TryGetValue(ip ?? "", out var list)) return 0;
                    list.RemoveAll(t => now - t > FailureWindow);
                    return list.Count;
                }
            }

            /// <summary>记录一次失败；达到阈值即锁定，返回是否因此被锁定。</summary>
            public bool NoteFailure(string ip, DateTime now)
            {
                lock (_lock)
                {
                    string k = ip ?? "";
                    if (!_fails.TryGetValue(k, out var list)) { list = new List<DateTime>(); _fails[k] = list; }
                    list.RemoveAll(t => now - t > FailureWindow);
                    list.Add(now);
                    if (list.Count >= MaxFailures)
                    {
                        _lockedUntil[k] = now + LockDuration;
                        list.Clear();
                        return true;
                    }
                    return false;
                }
            }

            public void NoteSuccess(string ip)
            {
                lock (_lock)
                {
                    _fails.Remove(ip ?? "");
                    _lockedUntil.Remove(ip ?? "");
                }
            }
        }

        // ================= 动态码一次性消费 =================

        public sealed class CodeStore
        {
            readonly string _seed;
            readonly object _lock = new object();
            readonly HashSet<long> _usedWindows = new HashSet<long>();

            public CodeStore(string seed) { _seed = seed ?? ""; }

            /// <summary>校验并消费动态码：成功返回 true 且 window 为该码所属窗口；同一窗口只能用一次。</summary>
            public bool TryConsume(string input, DateTime now, out long window)
            {
                window = 0;
                if (!MicProtocol.VerifyCode(_seed, input, now, out long w)) return false;
                lock (_lock)
                {
                    if (_usedWindows.Contains(w)) return false;   // 已经用过（一次性）
                    _usedWindows.Add(w);
                    if (_usedWindows.Count > 64)                  // 老窗口定期清理，避免无限增长
                    {
                        long keep = MicProtocol.WindowIndex(now) - 4;
                        foreach (var old in _usedWindows.Where(x => x < keep).ToList()) _usedWindows.Remove(old);
                    }
                    window = w;
                    return true;
                }
            }
        }

        // ================= 会话与校验链 =================

        public enum PacketReject
        {
            None = 0,
            NoSession,        // sid 不匹配任何活动会话
            WrongSource,      // 源 IP 与会话登记的不一致（白名单）
            Expired,          // 超过单次时长上限
            Replay,           // 序号重复或过旧
            RateLimited,      // 超出包速率/带宽上限
            BadPacket         // 包头/AEAD 不合法（由 MicProtocol 判定）
        }

        public sealed class Session
        {
            public uint Sid;
            public string SourceIp = "";
            public string DeviceId = "";
            public byte[] Key = Array.Empty<byte>();
            public DateTime StartedAt;
            public DateTime LastPacketAt;
            public int MaxMinutes = 60;

            // 允许的来源地址集合（中继模式下"服务端转发"与"P2P 直连"的源地址不一样，必须都认）：
            //   · 电脑端是虚拟网卡模式时，配对 TCP 的源 = 手机虚拟 IP；
            //   · 走中继时，UDP 报文的源 = **vnts 服务端**（文档 §14 明确：中继时 UDP 源地址是服务端）；
            //   · 走 P2P 直连时，源 = 手机公网地址。
            // 业务侧还要用**内层 vnt 帧的源虚拟 IP**核对一次（见 ValidateIncoming），所以多认几个地址不降低安全性。
            readonly HashSet<string> _allowedSources = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>追加一个允许的来源地址（空字符串忽略）。</summary>
            public void AllowSource(string ip)
            {
                if (!string.IsNullOrWhiteSpace(ip)) _allowedSources.Add(ip);
            }

            /// <summary>该地址是否在允许集合里（集合为空时退化为原 SourceIp 比较）。</summary>
            public bool AllowsSource(string ip)
                => _allowedSources.Count > 0 ? _allowedSources.Contains(ip ?? "") : string.Equals(SourceIp, ip ?? "", StringComparison.Ordinal);

            /// <summary>已允许的来源地址（状态页/排错显示用）。</summary>
            public string AllowedSourcesText => _allowedSources.Count == 0 ? SourceIp : string.Join("|", _allowedSources);

            public readonly MicProtocol.ReplayWindow Window = new MicProtocol.ReplayWindow();

            // 限速：按 1 秒窗口统计包数与字节数（≈64kbps 上限）
            public const int MaxPacketsPerSecond = 100;
            public const long MaxBytesPerSecond = 8 * 1024;      // 64kbps
            DateTime _secStart;
            int _secPackets;
            long _secBytes;

            // 统计（供状态页/告警）
            public long Accepted, Rejected, Replayed, RateHits;

            // ---- UDP 丢包统计（靠 seq 连续性算，见 ValidateIncoming）----
            public bool SeqInited;            // 是否已见过第一帧
            public ulong ExpectedSeq;         // 期望的下一帧序号
            public long SeqSeen;              // 参与统计的帧数
            public long LostFrames;           // 序号跳号累计出来的丢帧数
            /// <summary>音频流见过的最新序号：用来判断"迟到帧"。</summary>
            public ulong NewestAudioSeq;
            /// <summary>因迟到被丢弃的音频帧数（落后最新序号 >15 帧 ≈ 300ms 的帧不要了）。</summary>
            public long LateDropped;
            public long OutOfOrder;           // 乱序/迟到的帧数（不算丢包）

            /// <summary>丢包率（0~1）：丢帧 / （丢帧 + 实收帧）。没有样本时返回 -1。</summary>
            public double LossRate
            {
                get
                {
                    long total = LostFrames + SeqSeen;
                    if (total <= 0) return -1;
                    return (double)LostFrames / total;
                }
            }
            /// <summary>人话形式的丢包率（界面/日志用）。</summary>
            public string LossText => LossRate < 0 ? "暂无样本" : (LossRate * 100).ToString("0.0") + "%";

            // Ogg 直通（type=3）会话状态：手机端实现有误时能查出原因，而不是"开了麦没声音"
            public bool OggHeadSeen;          // 是否已收到 OpusHead
            public bool OggHeadWarned;        // 缺 OpusHead 的告警只记一次
            public int OggHeadReseen;         // 同一会话内**第二次及以后**收到 OpusHead 的次数（诊断，见 MicServer）
            public bool OggPageWarned;        // 页头异常的告警只记一次
            public long OggPages, OggBytes;   // 已转发的 Ogg 页数与字节数

            // cbs-api（接口请求/响应）计数：手机端播控/排程/调休靠它
            long _apiSeq;
            public long ApiRequests, ApiReplies;

            /// <summary>
            /// cbs-api 回包用的序号（单调递增）。
            /// 与音频包**共用同一把会话密钥**，但 AEAD 的 nonce = sid‖seq，所以两条流的 seq
            /// 绝不能撞车：这里给接口流单独一段空间（从 0x8000_0000_0000_0000 起），与音频的 1,2,3… 天然分开。
            /// （4.0.0 起 seq 为 8 字节，这段空间的起点同步从 0x8000_0000 上移到 64 位的同一位置。）
            /// </summary>
            public ulong NextApiSeq() => 0x8000000000000000UL + (ulong)System.Threading.Interlocked.Increment(ref _apiSeq);

            public bool Expired(DateTime now) => MaxMinutes > 0 && (now - StartedAt).TotalMinutes >= MaxMinutes;

            /// <summary>挂起时间：静默或 TCP 断开时置位，宽限期内允许免动态码恢复。</summary>
            public DateTime? SuspendedAt;

            public bool Suspended => SuspendedAt != null;

            /// <summary>进入挂起（保留密钥与会话号，宽限期内可恢复）。</summary>
            public void Suspend(DateTime now)
            {
                if (SuspendedAt == null) SuspendedAt = now;
            }

            /// <summary>宽限期内恢复：复用原会话与原密钥，且**不重置**单次时长上限。返回是否恢复成功。</summary>
            public bool TryResume(DateTime now, int graceSeconds)
            {
                if (SuspendedAt == null) return true;                     // 没挂起，直接用
                if ((now - SuspendedAt.Value).TotalSeconds > graceSeconds) return false;
                SuspendedAt = null;
                LastPacketAt = now;
                return true;
            }

            /// <summary>静默超时：连续这么久没有有效音频帧就认为对端断了。</summary>
            public bool Silent(DateTime now, TimeSpan timeout) => now - LastPacketAt > timeout;

            /// <summary>限速判定（同时累计本次窗口的用量）。超限返回 true。</summary>
            public bool HitRateLimit(DateTime now, int bytes)
            {
                if ((now - _secStart).TotalSeconds >= 1.0) { _secStart = now; _secPackets = 0; _secBytes = 0; }
                _secPackets++;
                _secBytes += bytes;
                if (_secPackets > MaxPacketsPerSecond || _secBytes > MaxBytesPerSecond) { RateHits++; return true; }
                return false;
            }
        }

        public sealed class Registry
        {
            readonly ConcurrentDictionary<uint, Session> _sessions = new ConcurrentDictionary<uint, Session>();
            int _nextSid = 0;

            public int Count => _sessions.Count;
            public IEnumerable<Session> All => _sessions.Values.ToList();

            public Session Create(string sourceIp, string deviceId, byte[] key, DateTime now, int maxMinutes)
            {
                uint sid = (uint)Interlocked.Increment(ref _nextSid);
                var s = new Session
                {
                    Sid = sid,
                    SourceIp = sourceIp ?? "",
                    DeviceId = deviceId ?? "",
                    Key = key ?? Array.Empty<byte>(),
                    StartedAt = now,
                    LastPacketAt = now,
                    MaxMinutes = maxMinutes
                };
                s.AllowSource(sourceIp);
                _sessions[sid] = s;
                return s;
            }

            public Session Find(uint sid) => _sessions.TryGetValue(sid, out var s) ? s : null;

            public bool Remove(uint sid)
            {
                if (!_sessions.TryRemove(sid, out var s)) return false;
                try { if (s.Key != null) Array.Clear(s.Key, 0, s.Key.Length); } catch { }   // 会话密钥用完即毁
                return true;
            }

            public void Clear() { foreach (var k in _sessions.Keys.ToList()) Remove(k); }

            /// <summary>
            /// 每包校验链（顺序即优先级）：会话存在 → 来源地址白名单 → 是否超时 → 防重放 → 限速。
            /// 注意：AEAD 校验在调用本方法之前由 MicProtocol.TryOpenPacket 完成（失败即 BadPacket）。
            /// <paramref name="claimedVirtualIp"/> 是**内层 vnt 帧里的源虚拟 IP**（中继时外层源地址是服务端，
            /// 只有它才是"手机是谁"的真值），两个地址都在白名单里才算通过。
            /// </summary>
            public PacketReject ValidateIncoming(uint sid, string sourceIp, ulong seq, int packetBytes, DateTime now, out Session session,
                string claimedVirtualIp = null, bool skipReplayWindow = false)
            {
                session = Find(sid);
                if (session == null) return PacketReject.NoSession;
                if (!session.AllowsSource(sourceIp))
                {
                    session.Rejected++;
                    return PacketReject.WrongSource;
                }
                if (!string.IsNullOrWhiteSpace(claimedVirtualIp) && !session.AllowsSource(claimedVirtualIp))
                {
                    session.Rejected++;
                    return PacketReject.WrongSource;
                }
                if (session.Expired(now)) { session.Rejected++; return PacketReject.Expired; }
                if (!skipReplayWindow && !session.Window.Accept(seq))
                {
                    session.Rejected++;
                    session.Replayed++;
                    return PacketReject.Replay;
                }
                // ---- UDP 丢包统计（用户问"丢包怎么算"：这就是算法）----
                // 手机每帧都带单调递增的 seq。这里看"序号跳了多少"：
                //   seq == 期望值        → 正常
                //   seq >  期望值        → 中间少了 (seq-期望值) 帧 = **丢包**（UDP 没有重传，掉了就是掉了）
                //   seq <  期望值（且窗口还接受）→ 乱序/迟到，不算丢包
                // 计数只用于统计与界面显示，绝不因此拒包。
                // 【R6】丢包统计**只针对音频流**：接口流（cbs-api）与音频流共用会话密钥，但 seq 各占一段
                // （接口从 0x8000_0000_0000_0000 起，音频从 1 起）。两段交替时若都喂给同一套 ExpectedSeq，
                // 统计会看到"跳了 9.2e18 帧"这种天文数字（更早的 uint 版本还因此溢出过）。
                // 现在：接口包不参与音频丢包统计，也不推进音频的 ExpectedSeq。
                bool isAudioStream = seq < 0x8000000000000000UL;
                if (isAudioStream)
                {
                    if (session.SeqInited)
                    {
                        if (seq > session.ExpectedSeq)
                        {
                            ulong d = seq - session.ExpectedSeq;
                            if (d <= 1000000UL) session.LostFrames += (long)d;   // 正常跳号 = 丢包
                            else session.OutOfOrder++;                          // 异常大跳变：按乱序计，不进丢包数
                        }
                        else if (seq < session.ExpectedSeq) session.OutOfOrder++;
                        session.ExpectedSeq = seq + 1;
                    }
                    else { session.SeqInited = true; session.ExpectedSeq = seq + 1; }
                }
                session.SeqSeen++;
                if (session.HitRateLimit(now, packetBytes))
                {
                    session.Rejected++;
                    return PacketReject.RateLimited;
                }
                session.Accepted++;
                session.LastPacketAt = now;
                return PacketReject.None;
            }
        }

        // ================= 断线恢复（免动态码） =================

        /// <summary>
        /// 恢复令牌 = base64(HMAC-SHA256(会话密钥, "cbs-mic-resume:" + sid))。
        /// 只有真正握过手的手机才知道会话密钥，所以网络抖动重连时不必再输一次动态码。
        /// </summary>
        public static string ResumeToken(byte[] sessionKey, uint sid)
        {
            if (sessionKey == null || sessionKey.Length == 0) return "";
            using var hmac = new System.Security.Cryptography.HMACSHA256(sessionKey);
            var mac = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("cbs-mic-resume:" + sid));
            return Convert.ToBase64String(mac);
        }

        /// <summary>校验恢复令牌（常量时间比较，避免时序侧信道）。</summary>
        public static bool VerifyResumeToken(byte[] sessionKey, uint sid, string token)
            => MicProtocol.FixedTimeEquals(ResumeToken(sessionKey, sid), (token ?? "").Trim());

        // ================= 审计计数 =================

        public sealed class Stats
        {
            public long PairingOk, PairingFail, Locked;
            public long SessionsStarted, SessionsEnded, SessionsKilledByLimit;
            public long BadPackets, Replays, WrongSource, RateLimited;
            public DateTime LastEventAt = DateTime.MinValue;
            public string LastEvent = "";

            public void Note(string what, DateTime now)
            {
                LastEvent = what ?? "";
                LastEventAt = now;
            }

            public string Summary()
                => $"配对成功 {PairingOk} / 失败 {PairingFail}（锁定 {Locked}）；会话开始 {SessionsStarted} / 结束 {SessionsEnded}" +
                   $"（超时强制结束 {SessionsKilledByLimit}）；丢弃包 非法 {BadPackets} / 重放 {Replays} / 来源不符 {WrongSource} / 限速 {RateLimited}";
        }
    }
}
