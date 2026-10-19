using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BroadcastSystemNet
{
    /// <summary>
    /// 话筒服务：UDP 收音频帧 + TCP 收配对请求（接口文档 §3/§4）。
    /// 安全要点：**只绑定配置的虚拟网卡地址**（拒绝 0.0.0.0/::），默认关闭；
    /// 每包走 MicProtocol(AEAD) → MicSecurity 校验链；解码播放通过 OnAudio 回调交给上层（便于单独测试）。
    /// </summary>
    public static class MicServer
    {
        public static readonly MicSecurity.Registry Registry = new MicSecurity.Registry();
        public static readonly MicSecurity.Stats Stats = new MicSecurity.Stats();
        public static MicSecurity.PairingGuard Guard = new MicSecurity.PairingGuard();
        static MicSecurity.CodeStore _codes;

        /// <summary>当前动态码仓库（守候/联调模式可替换成"固定码"实现，正常运行时用 code_seed 轮换）。</summary>
        public static MicSecurity.CodeStore Codes
        {
            get => _codes;
            set => _codes = value;
        }

        static UdpClient _udp;
        static UdpClient _pairUdp;                 // §15.3：配对/控制口上的 UDP 配对通道（与 TCP 并存）
        static TcpListener _tcp;
        static Thread _audioThread, _pairThread, _pairUdpThread;
        static volatile bool _running;

        public static string BoundAddress = "";
        public static int AudioPort, CtlPort;
        /// <summary>UDP 配对通道是否可用（需要配置了"数据密码"：密钥 = SHA256(数据密码)）。</summary>
        public static bool PairUdpReady => _pairUdp != null;
        public static long PairUdpReceived, PairUdpReplied, PairUdpRejected;
        /// <summary>配对结果观测点（探针/守候模式用）：(设备标识, 来源, 是否成功, 原因或空, 会话号)。</summary>
        public static Action<string, string, bool, string, uint> OnPairResult;
        /// <summary>诊断：最近若干条"认不出的包"（含原始字节），用于判断手机发来的包是否到达、格式对不对。</summary>
        public static readonly System.Collections.Generic.List<string> BadHeaderDiag = new System.Collections.Generic.List<string>();
        public static string LastError = "";

        /// <summary>最近一次配对**失败的原因**（界面直接显示，省得只有计数看不出问题）。</summary>
        public static string LastPairFailure = "";
        public static DateTime LastAudioUtc = DateTime.MinValue;

        /// <summary>收到合法音频帧时回调（下一轮接 Opus 解码 + WASAPI 播放）。</summary>
        public static Action<byte[], ushort> OnAudio;
        /// <summary>静默超时（秒）：这么久没有有效音频帧就结束会话。</summary>
        public static int SilenceTimeoutSeconds = 15;

        /// <summary>没虚拟网卡时（0.0.0.0）：只接受来自该网段前缀的包（如 "10.26."）。</summary>
        static string _vnetPrefixFilter;

        static System.Threading.Timer _sweepTimer;
        /// <summary>清理定时器是否在跑（自检用）。</summary>
        public static bool SweepTimerWired => _sweepTimer != null;

        public static bool Running => _running;

        /// <summary>
        /// 启动。bindAddress 规则：
        ///   · 有虚拟网卡时：必须是 vnt 虚拟 IP（如 10.26.0.x）或 127.0.0.1（本机自检）；
        ///   · **没有虚拟网卡时**（vnt-lite，我们自建组网）：允许 0.0.0.0，
        ///     但**必须同时指定虚拟网段前缀**（如 `10.26.`），只接受来自该网段的包——
        ///     这样既满足"不暴露到公网"的安全限制，又能在没虚拟网卡的情况下工作。
        /// </summary>
        public static bool Start(string bindAddress, int audioPort, int ctlPort, string codeSeed, out string error)
        {
            error = null;
            if (_running) { error = "话筒服务已在运行"; return false; }

            bool allowAny = false;
            string vnetPrefix = null;
            // 【只要程序自己在跑 vnt-lite，就绑 0.0.0.0】
            // 理由：vnt-lite 是**用户态**组网，本机没有那块网卡，它的虚拟 IP（如 10.8.0.2）压根不能 bind；
            // 而机器上"看起来像虚拟网"的地址（如你自己另装的那份 vnt 的 10.8.0.105）属于**另一个网**，
            // 手机根本发不到它 —— 绑过去只会让人以为配对地址是 105（用户当场质疑过）。
            // 绑 0.0.0.0 + 虚拟网段来源过滤，才是这套架构下正确的做法（安全约束不变）。
            bool managedVnet = VntProcess.IsRunning || VntManager.VntConfigured;
            if (managedVnet && !string.IsNullOrWhiteSpace(VntManager.EffectiveVnetPrefix))
            {
                PlayLog.Write("话筒", "绑定 0.0.0.0", "info",
                    detail: $"程序内置 vnt-lite 在跑（请求的地址 {bindAddress} 不是它在本机的真实地址），"
                          + $"改为绑定 0.0.0.0 + 只接受虚拟网段 {VntManager.EffectiveVnetPrefix} 内的来源。");
                vnetPrefix = VntManager.EffectiveVnetPrefix;
                allowAny = true;
                bindAddress = "0.0.0.0";
            }
            if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress == "0.0.0.0" || bindAddress == "::" || bindAddress == "*")
            {
                // 没虚拟网卡时（vnt-lite）：允许 0.0.0.0，但必须指定虚拟网段前缀做来源过滤
                vnetPrefix = VntManager.EffectiveVnetPrefix;
                if (string.IsNullOrWhiteSpace(vnetPrefix))
                {
                    error = "安全限制：没虚拟网卡时必须指定虚拟网段前缀（vnt 设置 → 虚拟网段，如 10.26.）";
                    return false;
                }
                allowAny = true;
                bindAddress = "0.0.0.0";
            }
            if (!IPAddress.TryParse(bindAddress, out var addr))
            {
                error = "绑定地址不是合法 IP：" + bindAddress;
                return false;
            }
            // 【重要】传进来的地址必须是**本机真实存在的地址**，否则 Windows 会报
            // "在其上下文中，该请求的地址无效"（真机上就踩到了：vnt-lite 的虚拟 IP 10.x
            // 属于用户态组网，本机并没有这块网卡，拿它 bind 必然失败）。
            // 这里做兜底：不是本机地址就退回 0.0.0.0，并按虚拟网段做来源过滤（安全约束不变）。
            if (!allowAny && !IsLocalAddress(addr))
            {
                if (string.IsNullOrWhiteSpace(VntManager.EffectiveVnetPrefix))
                {
                    error = "安全限制：地址 " + bindAddress + " 不是本机地址，且没配虚拟网段前缀，无法退回 0.0.0.0";
                    return false;
                }
                PlayLog.Write("话筒", "绑定地址不可用", "warn",
                    detail: $"{bindAddress} 不是本机真实地址（vnt-lite 是用户态组网，本机没有这块网卡），"
                          + $"已自动改为绑定 0.0.0.0 + 按虚拟网段 {VntManager.EffectiveVnetPrefix} 过滤来源。");
                vnetPrefix = VntManager.EffectiveVnetPrefix;
                allowAny = true;
                bindAddress = "0.0.0.0";
                addr = IPAddress.Any;
            }
            try
            {
                _codes = new MicSecurity.CodeStore(codeSeed);
                BoundAddress = bindAddress; AudioPort = audioPort; CtlPort = ctlPort;
                _udp = new UdpClient(new IPEndPoint(addr, audioPort));
                _tcp = new TcpListener(addr, ctlPort);
                _tcp.Start();
                // 没虚拟网卡时（0.0.0.0）：把虚拟网段前缀记下来，HandleAudioPacket / HandlePairLine 里按它过滤来源
                _vnetPrefixFilter = allowAny ? vnetPrefix : null;
                // §15.3 UDP 配对通道：与 TCP 配对**并存**，给"没有虚拟网里 TCP 栈"的手机端用。
                // 手机端（BroadcastApp，不建 TUN）只能发 UDP，无法 TCP 连接虚拟 IP，所以必须补这一条；
                // 载荷用 SHA256(数据密码) 做 AES-GCM 包装（HELLO 里含 6 位动态码，不能明文上网）。
                _pairUdp = null;
                try
                {
                    var key = MicPairUdp.DeriveKey(VntManager.DataPassword);
                    if (key.Length != 32)
                    {
                        PlayLog.Write("话筒", "UDP配对未启用", "warn", detail:
                            "尚未配置【数据密码】：UDP 配对通道需要它作为加密密钥（密钥 = SHA256(数据密码)）。请在「vnt 设置」里填一个数据密码，手机端填同一个；在此之前手机端只能走 TCP 配对（虚拟网卡模式）。");
                    }
                    else
                    {
                        _pairUdp = new UdpClient(new IPEndPoint(addr, ctlPort));
                        _pairUdpThread = new Thread(PairUdpLoop) { IsBackground = true, Name = "mic-pair-udp" };
                        _pairUdpThread.Start();
                    }
                }
                catch (Exception ex)
                {
                    _pairUdp = null;
                    PlayLog.Write("话筒", "UDP配对启动失败", "error", detail: ex.Message
                        + "（TCP 配对不受影响；UDP 配对与音频口/配对口共用地址，端口被占用时会失败）");
                }
                _running = true;
                _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "mic-audio" };
                _pairThread = new Thread(PairLoop) { IsBackground = true, Name = "mic-pair" };
                _audioThread.Start(); _pairThread.Start();
                // 接线（默认行为）：收到音频就触发闪避；每秒清理静默会话并检查是否该恢复音乐。
                // 上层可以覆盖 OnAudio（接 Opus 解码播放）而不影响这里的兜底。
                OnAudio ??= (payload, frameMs) =>
                {
                    try { MicDuck.NoteAudio(DateTime.UtcNow); } catch { }
                    // 解一帧 Opus → 16bit PCM → 塞进播放缓冲（ffplay 管道播放，已真机验证）
                    try
                    {
                        var pcm = new short[5760];                     // 60ms@48k 单声道上限
                        int n = MicAudio.DecodeFrame(payload, pcm);
                        if (n > 0)
                        {
                            var outPcm = new short[n * MicAudio.Channels];
                            Array.Copy(pcm, outPcm, outPcm.Length);
                            MicPipePlayer.Ring.Write(outPcm);
                            if (!MicPipePlayer.Running) MicPipePlayer.Start(out string perr);
                        }
                    }
                    catch { }
                };
                MicDuck.ApplyLevel ??= level =>
                {
                    // 只改"正在播的那一路"的会话音量，不动任务里配置的音量值
                    try { AudioFade.SetProcessVolume(Program.ScheduleProcess, level); } catch { }
                    try { AudioFade.SetProcessVolume(Program.MpProcess, level); } catch { }
                };
                _sweepTimer = new System.Threading.Timer(_ =>
                {
                    try { SweepSilentSessions(DateTime.UtcNow); } catch { }
                    try { MicDuck.Sweep(DateTime.UtcNow); } catch { }     // 停麦 800ms 后自动恢复音乐
                    try { WatchVntLink(); } catch { }                     // vnt 掉线/虚拟IP变化 → 停麦并重绑
                }, null, 1000, 1000);
                PlayLog.Write("话筒", "启动", "info", detail: $"话筒服务已启动：音频 UDP {bindAddress}:{audioPort}，配对 TCP {bindAddress}:{ctlPort}"
                    + (_pairUdp != null ? $"，配对 UDP {bindAddress}:{ctlPort}（cbs-pair 加密通道）" : "，配对 UDP 未启用（缺数据密码）"));
                return true;
            }
            catch (Exception ex)
            {
                error = "话筒服务启动失败：" + ex.Message;
                LastError = error;
                Stop();
                return false;
            }
        }

        public static void Stop()
        {
            _running = false;
            try { MicPipePlayer.Stop(); } catch { }      // 先停播放，再拆网络
            try { _sweepTimer?.Dispose(); } catch { }
            _sweepTimer = null;
            try { _udp?.Close(); } catch { }
            try { _pairUdp?.Close(); } catch { }
            try { _tcp?.Stop(); } catch { }
            _udp = null; _tcp = null; _pairUdp = null;
            int n = Registry.Count;
            Registry.Clear();
            if (n > 0) PlayLog.Write("话筒", "停止", "info", detail: $"话筒服务已停止，强制结束 {n} 路会话");
        }

        // ---------------- UDP 音频 ----------------

        static void AudioLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data = null;
                try { data = _udp.Receive(ref remote); }
                catch { if (!_running) return; continue; }
                try { HandleAudioPacket(data, remote.Address.ToString()); }
                catch (Exception ex) { LastError = "音频处理异常：" + ex.Message; }
            }
        }

        /// <summary>
        /// 处理一条 cbs-api 请求：解密后的 JSON 交给 <see cref="MicApiBridge"/> 转发到本机 HTTP 接口，
        /// 响应再用**同一会话密钥**加密成 TypeApi 包，按虚拟 IP 回发给手机。
        ///
        /// 回包走 <see cref="VntProcess.SendToPeer"/>（中继优先时经服务端、否则 P2P 直发），
        /// 与音频共用发送逻辑，不新增通道。
        /// </summary>
        static void HandleApiRequest(MicSecurity.Session session, byte[] payload, string deviceVirtualIp)
        {
            string json;
            try { json = Encoding.UTF8.GetString(payload); }
            catch (Exception ex)
            {
                LastError = "接口请求不是 UTF-8：" + ex.Message;
                Stats.Note("cbs-api 请求解码失败", DateTime.UtcNow);
                return;
            }
            session.ApiRequests++;
            // 【与 sid=0 数据通道统一】会话通道以前是"在 vnt 接收线程里同步等本机 HTTP + 整包回发"：
            //   · 同步等 → 接收线程被堵（心跳/音频/后续请求全停）；
            //   · 整包回发 → 5KB 的排程/调休响应变成一个 5.5KB UDP 报文，公网上 IP 分片丢一片就全丢
            //     （真机现象：/api/sys 一直正常，tasks/tiaoxiu/music 一直超时；手机日志里 分片收 0）。
            // 现在同样只入队，由专用线程处理，并且**同样走压缩分片**。
            EnsureApiWorker();
            if (!_apiQueue.TryAdd(new ApiJob
            {
                Json = json, Peer = deviceVirtualIp, Key = session.Key,
                Sid = session.Sid, Session = session
            }))
            {
                ApiQueueDrops++;
                LastError = "接口请求队列已满（电脑端忙不过来），本次请求被丢弃";
            }
        }

        /// <summary>一条待处理的接口请求（sid=0 数据通道时 Session 为 null）。</summary>
        struct ApiJob
        {
            public string Json;
            public string Peer;
            public byte[] Key;
            public uint Sid;
            public MicSecurity.Session Session;
        }

        /// <summary>
        /// 处理一个音频报文（公开以便自检直接调用，无需真的发 UDP）。
        /// <paramref name="claimedVirtualIp"/> = 内层 vnt 帧里的源虚拟 IP（中继时外层源地址是服务端，
        /// 只有它才是"手机是谁"的真值）；不传则退化为只按外层源地址校验。
        /// </summary>
        public static void HandleAudioPacket(byte[] data, string sourceIp, string claimedVirtualIp = null)
        {
            var now = DateTime.UtcNow;
            // 配对帧（"cbs-pair:"）也可能经 vnt-lite 中继过来：手机不建虚拟网卡，只能把配对帧
            // 封进 vnt 数据包发到电脑的虚拟 IP；电脑这边由 vnt-lite 收下来交给这里。
            // 这类包必须走配对逻辑（不能当音频包解析），并按内层虚拟 IP 回包。
            if (MicPairUdp.LooksLikePairFrame(data))
            {
                HandlePairFrameFromVnet(data, claimedVirtualIp ?? sourceIp);
                return;
            }
            // 没虚拟网卡时（0.0.0.0）：只接受虚拟网段或私网来源的包（防公网扫描；见 IsAcceptableSource）
            if (_vnetPrefixFilter != null)
            {
                bool ok = false;
                if (IsAcceptableSource(sourceIp)) ok = true;
                if (!ok && !string.IsNullOrWhiteSpace(claimedVirtualIp) && IsAcceptableSource(claimedVirtualIp)) ok = true;
                if (!ok) { Stats.BadPackets++; Stats.Note("非虚拟网来源被丢弃：" + sourceIp, now); return; }
            }
            if (!MicProtocol.TryParseHeader(data, out byte type, out uint sid, out ulong seq, out ushort frameMs, out _, out string hdrErr))
            {
                Stats.BadPackets++; Stats.Note("非法包头：" + hdrErr, now);
                // 诊断（真机排错用）：是不是"手机发来了东西，但我们认不出来"？
                // 只记前若干条，避免被扫描刷屏。
                if (BadHeaderDiag.Count < 10 && data != null && data.Length > 0)
                {
                    string hex = BitConverter.ToString(data, 0, Math.Min(16, data.Length));
                    string line = $"长 {data.Length}，{hdrErr}，前 16 字节 {hex}"
                        + (MicPairUdp.LooksLikePairFrame(data) ? "（其实是 cbs-pair 配对帧）" : "");
                    BadHeaderDiag.Add(line);
                    PlayLog.Write("话筒", "收到认不出的包", "warn",
                        detail: line + $"，来源 {sourceIp}（虚拟 {claimedVirtualIp}）");
                }
                return;
            }
            // 【数据通道】sid = 0 的 TypeApi = "不需要配对码"的接口通道：
            //   授权条件 = 来源在虚拟网/私网内（上面的来源过滤）+ 能解开 SHA256(数据密码) 这层 AEAD。
            //   为什么这样设计：手机没建虚拟网卡，到不了电脑的 TCP 端口，只能走 UDP 加密包；
            //   而用户的要求是"只有话筒喊话需要 6 位动态码，播控/排程/调休连上就能用"。
            //   数据密码在二维码里，扫过码的设备天然知道它；电脑端没填数据密码时这条通道自动关闭。
            if (type == MicProtocol.TypeApi && sid == 0)
            {
                var openKey = MicPairUdp.DeriveKey(VntManager.DataPassword);
                if (openKey.Length != 32)
                {
                    Stats.Note("数据通道未启用（电脑端没填数据密码）", now);
                    return;
                }
                if (!MicProtocol.TryOpenPacket(data, openKey, out _, out _, out ulong oSeq, out _, out byte[] oPayload, out string oErr))
                {
                    Stats.BadPackets++; Stats.Note("数据通道包解不开：" + oErr, now);
                    return;
                }
                string peer = claimedVirtualIp ?? sourceIp;
                // 防重放：同一来源的序号必须递增（App 重启会跳回小序号，所以允许大幅回退一次）
                if (!AcceptDataSeq(peer, oSeq, now))
                {
                    Stats.Replays++; Stats.Note("数据通道重放包被丢弃：" + peer, now);
                    return;
                }
                HandleOpenApiRequest(oPayload, peer, openKey);
                return;
            }
            var session = Registry.Find(sid);
            if (session == null)
            {
                Stats.Note("未知会话的音频包被丢弃", now);
                return;
            }
            // AEAD 校验（失败即伪造/篡改）
            if (!MicProtocol.TryOpenPacket(data, session.Key, out _, out _, out _, out _, out byte[] payload, out string aeadErr))
            {
                Stats.BadPackets++; Stats.Note("AEAD 校验失败：" + aeadErr, now);
                session.Rejected++;
                return;
            }
            // 校验链：源 IP → 超时 → 防重放 → 限速
            // 音频帧（Ogg/原始 PCM）**不参与防重放窗口**：手机端音频与接口请求共用一套递增序号，
            // 但两股流到达顺序会交错，同一窗口校验会把其中一股整批误判成"重放"丢掉
            // （真机现象：手机已发上千帧、电脑端 `Ogg 0 页`、播放器从未启动、"重放"计数一直涨）。
            // 音频重复一帧无害（20ms 重复没人听得出）；安全仍由 AEAD + 来源白名单 + 限速保证。
            bool skipWindow = type != MicProtocol.TypeApi;
            var verdict = Registry.ValidateIncoming(sid, sourceIp, seq, data.Length, now, out _, claimedVirtualIp, skipWindow);
            if (verdict != MicSecurity.PacketReject.None)
            {
                switch (verdict)
                {
                    case MicSecurity.PacketReject.Replay: Stats.Replays++; Stats.Note("重放包被丢弃", now); break;
                    case MicSecurity.PacketReject.WrongSource: Stats.WrongSource++; Stats.Note("来源不符的包被丢弃：" + sourceIp, now); break;
                    case MicSecurity.PacketReject.RateLimited: Stats.RateLimited++; Stats.Note("超速包被丢弃（限流）", now); break;
                    case MicSecurity.PacketReject.Expired:
                        Stats.SessionsKilledByLimit++;
                        PlayLog.Write("话筒", "超时结束", "warn", detail: $"会话 {sid} 达到单次时长上限({session.MaxMinutes} 分钟)，已强制结束");
                        Registry.Remove(sid);
                        break;
                }
                return;
            }
            LastAudioUtc = now;
            // 接口请求/响应（cbs-api）：手机通过同一条加密通道调电脑端现成 HTTP 接口。
            // 单独分支的原因：它不该触发"正在说话"的闪避逻辑，也不进 ffplay 管道。
            if (type == MicProtocol.TypeApi && payload.Length > 0)
            {
                session.ApiRequests++;
                HandleApiRequest(session, payload, claimedVirtualIp ?? sourceIp);
                return;
            }
            // Ogg 直通（安卓端发 Ogg-Opus 页）：原样写进 ffplay，PC 端不做解码（MIT 干净、无 opus.dll 依赖）
            if (type == MicProtocol.TypeOgg && payload.Length > 0)
            {
                // **过期音频帧丢弃**（用户要求：音频流按时间判超时）：手机端每帧 20ms、序号递增，
                // 落后最新序号 >15 帧（≈300ms）的帧直接丢掉 —— 迟到的声音没有意义，留着只会越积越延迟。
                // 注意：只用序号判"落后多少"，乱序但在窗口内（≤15 帧）的帧照常播放，不影响正常抖动。
                // 【关键顺序】先更新"最新序号"，再做迟到判断。
                // 以前顺序反了：手机端音频与接口**共用一个序号计数器**，OpusHead 之后接口请求会插进来，
                // 于是音频页的 seq 比"最新序号"大 → uint 相减得到约 42 亿 → 永远判成"落后 42 亿帧"被丢，
                // 而且因为 return 在更新之前，NewestAudioSeq 永远停在旧值 → **整条音频全被丢光**（真机踩过）。
                // seq 是 ulong（4.0.0 起 8 字节）：用无符号比较判前进，不能再用 (int)(seq - newest) ——
                // 否则接口流（0x8000…）插进来时差值会溢出，重演"整条音频被丢光"的老问题（真机踩过）。
                bool ahead = session.NewestAudioSeq == 0 || seq > session.NewestAudioSeq;
                if (ahead) session.NewestAudioSeq = seq;

                if (session.NewestAudioSeq != 0 && session.OggHeadSeen && !ahead)   // 流还没建立时绝不丢；只对"确实更旧"的帧做判断
                {
                    ulong behind = session.NewestAudioSeq - seq;
                    if (behind > 1000000UL) behind = 1000000UL;   // 跨流跳变时不要算出天文数字（迟到判断只关心小值）
                    int lateMs = VntManager.MicLateDropMs;   // 默认 0 = 不丢（可在「话筒」窗调）
                    if (lateMs > 0 && behind * 20UL > (ulong)lateMs)
                    {
                        session.LateDropped++;
                        if (session.LateDropped % 100 == 1) Stats.Note($"音频迟到帧丢弃：落后 {behind} 帧 ≈ {behind * 20}ms（阈值 {lateMs}ms）", now);
                        return;
                    }
                }                // 会话级校验：手机端必须先发 OpusHead 页，否则 ffplay 不出声；这里给出可诊断的告警而不是静默
                if (!session.OggHeadSeen)
                {
                    if (LooksLikeOpusHead(payload))
                    {
                        session.OggHeadSeen = true;
                        PlayLog.Write("话筒", "Ogg流开始", "info", detail: $"会话 {sid} 收到 OpusHead，开始播放（Ogg 直通）");
                    }
                    else
                    {
                        session.Rejected++;
                        Stats.BadPackets++;
                        if (!session.OggHeadWarned)
                        {
                            session.OggHeadWarned = true;
                            PlayLog.Write("话筒", "对端未发 OpusHead", "warn", detail:
                                $"会话 {sid}（{session.DeviceId}）发来的 Ogg 流没有 OpusHead 页，已丢弃；请在手机端先发 OpusHead（见文档 §4.1），否则没有声音");
                        }
                        return;
                    }
                }
                else if (LooksLikeOpusHead(payload))
                {
                    // 【诊断，不改变任何行为】同一会话里又收到 OpusHead = 手机端重新开了一段 Ogg 流
                    //（关麦再开麦，或换另一台手机说话）。当前实现会把这段新流**直接接进正在播放的
                    // 同一个 ffplay 管道**：上一个流没发 EOS 就插入新的 BOS 页，ffmpeg 的 ogg 解复用器
                    // 可能因此失步，表现就是"第一次开麦有声、第二次开麦彻底没声音"。
                    // 这里只记录、不改行为，用来确认这个猜测是否成立 —— 真机复现时看这条日志即可。
                    session.OggHeadReseen++;
                    PlayLog.Write("话筒", "Ogg流重启", "warn",
                        detail: $"会话 {sid} 在同一会话内第 {session.OggHeadReseen + 1} 次收到 OpusHead"
                              + "（手机端重新开麦或换了说话人）；当前实现会把新流直接接进正在播放的 ffplay 管道，"
                              + "若此刻听不到声音，就是新旧 Ogg 流衔接问题（诊断日志，未改变行为）");
                }
                if (payload.Length > 0 && !LooksLikeOggPage(payload))
                {
                    session.Rejected++; Stats.BadPackets++;
                    if (!session.OggPageWarned)
                    {
                        session.OggPageWarned = true;
                        PlayLog.Write("话筒", "Ogg 页头异常", "warn", detail:
                            $"会话 {sid}：载荷不是合法的 Ogg 页（应以 OggS 开头、CRC 正确），已丢弃并只提醒一次");
                    }
                    return;
                }
                session.OggPages++;
                session.OggBytes += payload.Length;
                // 【补漏】Ogg 直通模式以前**从不触发话筒闪避**：MicDuck.NoteAudio 只写在 TypeAudio 分支里，
                // 而手机端实际发的是 TypeOgg → 喊话时背景音乐根本不会自动压低。
                // 这里补上，让"谁说话听谁 + 音乐自动降噪"在 Ogg 模式下也成立。
                try { MicDuck.NoteAudio(DateTime.UtcNow); } catch { }
                // ---- 多路话筒："谁说话听谁"（用户选定）----
                // 不再独占（maxSessions 默认 8，谁配对上来都能用），但 Ogg 直通只有一条播放管道：
                // 两路同时灌进去会互相污染成噪音。所以同一时刻只放"最近在说话"的那一路，
                // 另一路在 800 毫秒内没有新包就让位（说话人切换是无缝的）。
                var nowSpk = DateTime.UtcNow;
                if (_lastSpeakerSid != sid)
                {
                    if ((nowSpk - _lastSpeakerAt).TotalMilliseconds < 800)
                    {
                        OtherSpeakerDrops++;
                        if (OtherSpeakerDrops == 1 || OtherSpeakerDrops % 200 == 0)
                            PlayLog.Write("话筒", "多路话筒", "warn",
                                detail: $"会话 {_lastSpeakerSid} 正在说话，会话 {sid}（{session.DeviceId}）的音频暂时丢弃（共丢 {OtherSpeakerDrops} 帧）；"
                                      + "对方停下 0.8 秒后自动切换过去");
                        return;
                    }
                    PlayLog.Write("话筒", "多路话筒", "info", detail: $"说话人切到会话 {sid}（{session.DeviceId}）");
                }
                _lastSpeakerSid = sid; _lastSpeakerAt = nowSpk;
                try { MicPipePlayer.WriteOgg(payload); } catch (Exception ex) { LastError = "Ogg 播放异常：" + ex.Message; }
                return;
            }
            if (type == MicProtocol.TypeAudio && payload.Length > 0)
            {
                try { OnAudio?.Invoke(payload, frameMs); } catch (Exception ex) { LastError = "播放异常：" + ex.Message; }
            }
        }

        /// <summary>最近在说话的会话号 + 时间（多路话筒"谁说话听谁"，见 Ogg 分支）。</summary>
        static uint _lastSpeakerSid;
        static DateTime _lastSpeakerAt = DateTime.MinValue;
        /// <summary>因为"别人正在说话"而丢弃的音频帧数（诊断）。</summary>
        public static long OtherSpeakerDrops;

        // ---------------- TCP 配对 ----------------

        static void PairLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = _tcp.AcceptTcpClient(); }
                catch { if (!_running) return; continue; }
                try { HandlePairClient(client); }
                catch (Exception ex) { LastError = "配对处理异常：" + ex.Message; }
                finally { try { client?.Close(); } catch { } }
            }
        }

        /// <summary>
        /// UDP 配对通道（§15.3）：收 "cbs-pair:" 加密帧 → 解出单行 cbs-mic JSON → 交给**现成的**
        /// <see cref="HandlePairLine"/>（配对逻辑与 TCP 完全共用）→ 回复行同样加密后 UDP 回发来源地址。
        /// 任何解析失败都只计数 + 记一次审计，不回包（不给探测者反馈）。
        /// </summary>
        static void PairUdpLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data = null;
                try { data = _pairUdp.Receive(ref remote); }
                catch { if (!_running) return; continue; }
                try { HandlePairDatagram(data, remote); }
                catch (Exception ex) { LastError = "UDP 配对处理异常：" + ex.Message; }
            }
        }

        /// <summary>极简 JSON 取值（只在观测点做日志用，不用于校验）：找不到返回空串。</summary>
        static string JsonField(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                string k = "\"" + name + "\":\"";
                int i = json.IndexOf(k, StringComparison.Ordinal);
                if (i < 0) return "";
                int s = i + k.Length;
                int e = json.IndexOf('"', s);
                return e < 0 ? "" : json.Substring(s, e - s);
            }
            catch { return ""; }
        }

        /// <summary>
        /// 经 vnt-lite 中继过来的配对帧：解封 → 走与 UDP/TCP **完全相同**的配对逻辑 → 用同一条 vnt 通道把回复发回去。
        ///
        /// 为什么需要它：手机端不建虚拟网卡，"直接发到电脑虚拟 IP"的裸 UDP 发不出去，只能把 cbs-pair 帧
        /// 封进 vnt 数据包（见手机端 `AppState.pairExchange`）。电脑这边由 vnt-lite 收下来交给这里 ——
        /// 它不是真的 UDP socket，所以不能走 `HandlePairDatagram`（那条要 IPEndPoint 才能回包）。
        /// </summary>
        public static void HandlePairFrameFromVnet(byte[] frame, string peerVirtualIp)
        {
            if (frame == null || frame.Length == 0) return;
            PairUdpReceived++;
            var now = DateTime.UtcNow;
            var key = MicPairUdp.DeriveKey(VntManager.DataPassword);
            string line = MicPairUdp.TryUnwrap(frame, key, out string vnetErr);
            if (line == null)
            {
                PairUdpRejected++;
                Stats.Note("经 vnt 的配对帧被拒（" + vnetErr + "）", now);
                return;
            }
            uint sid = 0; byte[] sessionKey = null;
            string reply = HandlePairLine(line, peerVirtualIp, VntManager.MicEnabled, maxSessions: VntManager.MicMaxSessions,
                VntManager.MicCodeSeed, now, out sid, out sessionKey, peerVirtualIp);
            // 【兜底】回给手机的"成功"答复里**必须带服务端公钥与随机数**（ps/ns），否则手机端根本派生不出会话密钥
            //（真机上出现过"ok:true 但没有 ps"→ 手机报"公钥长度不对 0 字节"、或 X25519 退化异常）。
            // 这种事不能糊过去（用新密钥重建答复会让两端密钥不一致），所以：**销毁刚建的会话 + 回明确失败**，
            // 让手机端重试一次（重试会走完整流程，拿到带 ps/ns 的正常答复）。
            if (reply != null && reply.Contains("\"ok\":true") && !MicPairing.HasKeyMaterial(reply))
            {
                PlayLog.Write("话筒", "配对答复缺少密钥素材", "error",
                    detail: $"内部异常：OK 答复里没有 ps/ns（sid={sid}）。已销毁该会话并让手机端重试，避免两端密钥不一致");
                if (sid != 0) { try { Registry.Remove(sid); } catch { } }
                Stats.PairingFail++;
                LastPairFailure = "内部异常：OK 答复缺少 ps/ns（已要求重试）";
                reply = MicPairing.BuildAuthFail(MicPairing.Reason.BadRequest);
            }
            bool okFlag = reply.Contains("\"ok\":true");
            string reasonHint = okFlag ? "" : JsonField(reply, "reason");
            try { OnPairResult?.Invoke(JsonField(line, "device_id"), peerVirtualIp, okFlag, reasonHint, sid); } catch { }
            if (string.IsNullOrWhiteSpace(peerVirtualIp))
            {
                LastError = "经 vnt 收到的配对请求没有内层虚拟 IP，无法回包";
                return;
            }
            try
            {
                var outFrame = MicPairUdp.Build(reply, key);
                if (VntProcess.SendToPeer(peerVirtualIp, outFrame, out string sendErr)) PairUdpReplied++;
                else LastError = "经 vnt 回配对结果失败：" + sendErr;
            }
            catch (Exception ex) { LastError = "经 vnt 回配对结果异常：" + ex.Message; }
        }

        /// <summary>
        /// 数据通道（sid=0）的防重放：同一来源的序号必须递增。
        /// App 重启后序号会跳回小值，所以允许一次"大幅回退"（视为新会话），其余重复/回退一律拒绝。
        /// </summary>
        static readonly Dictionary<string, KeyValuePair<ulong, DateTime>> _openSeq = new Dictionary<string, KeyValuePair<ulong, DateTime>>();
        static bool AcceptDataSeq(string peer, ulong seq, DateTime nowUtc)
        {
            lock (_openSeq)
            {
                // 顺手清理 10 分钟前的记录，避免无限增长
                if (_openSeq.Count > 64)
                {
                    var dead = new List<string>();
                    foreach (var kv in _openSeq) if ((nowUtc - kv.Value.Value).TotalMinutes > 10) dead.Add(kv.Key);
                    foreach (var k in dead) _openSeq.Remove(k);
                }
                if (_openSeq.TryGetValue(peer ?? "", out var last))
                {
                    // 等价于原来的 `seq <= last && seq > last - 100000`，但写成减法不会在 ulong 下回绕
                    if (seq <= last.Key && last.Key - seq < 100000UL) return false;   // 重放/回退
                }
                _openSeq[peer ?? ""] = new KeyValuePair<ulong, DateTime>(seq, nowUtc);
                return true;
            }
        }

        /// <summary>
        /// 处理一条**数据通道**（sid=0）的接口请求：解密后交给同一套 <see cref="MicApiBridge"/>，
        /// 响应仍用数据密码密钥 + sid=0 发回来源。
        ///
        /// 【为什么要分片】接口响应可能好几 KB（tasks.jsonl 就有 5KB+）。这么大的 UDP 报文在公网上会被
        /// **IP 分片**，只要丢一个分片整包就没了 —— 真机表现就是"1KB 的 /api/sys 常成功、5KB 的 /api/tasks 经常失败"。
        /// 所以超过 [ApiChunkThreshold] 就压缩 + base64 + 拆成 ~800 字节一片，一片一个包发。
        /// </summary>
        static void HandleOpenApiRequest(byte[] payload, string peerVirtualIp, byte[] key)
        {
            string json;
            try { json = Encoding.UTF8.GetString(payload); }
            catch (Exception ex) { LastError = "数据通道请求不是 UTF-8：" + ex.Message; return; }

            // 收到的可能是一片：先拼齐（请求方向也可能是整表提交这种大 JSON）
            string complete = ReassembleIncomingApi(json, out string chunkErr);
            if (chunkErr != null) { LastError = "数据通道分片：" + chunkErr; return; }
            if (complete == null) { ApiChunksPending++; return; }        // 还差几片，安静等着

            // 【绝不在这里同步处理】本方法是**在 vnt 接收线程上**被调用的
            //（VntProcess 的 OnPeerDataFrom → HandleAudioPacket → 这里），
            // 而 MicApiBridge.Handle 内部要同步等本机 HTTP 接口（最长 TimeoutMs）。
            // 一旦在这条线程上等，接收线程就停了：心跳、音频、后续请求全部堵住，
            // 手机连发几个请求，电脑端就"彻底卡死"（真机实测）。
            // 现在只入队，由专用线程串行处理；接收路径永远只是几次内存操作。
            EnsureApiWorker();
            if (!_apiQueue.TryAdd(new ApiJob { Json = complete, Peer = peerVirtualIp ?? "", Key = key, Sid = 0, Session = null }))
            {
                ApiQueueDrops++;
                LastError = "接口请求队列已满（电脑端忙不过来），本次请求被丢弃";
            }
        }

        // ================= 数据通道请求的串行工作线程 =================

        static readonly BlockingCollection<ApiJob> _apiQueue
            = new BlockingCollection<ApiJob>(boundedCapacity: 64);
        static Thread _apiWorker;
        /// <summary>队列满被丢弃的请求数（诊断）。</summary>
        public static long ApiQueueDrops;
        /// <summary>已串行处理的请求数（诊断）。</summary>
        public static long ApiQueueHandled;

        static readonly object _apiWorkerLock = new object();
        static void EnsureApiWorker()
        {
            if (_apiWorker != null) return;
            lock (_apiWorkerLock)
            {
                if (_apiWorker != null) return;
                _apiWorker = new Thread(ApiWorkerLoop) { IsBackground = true, Name = "cbs-api-worker" };
                _apiWorker.Start();
            }
        }

        static void ApiWorkerLoop()
        {
            foreach (var item in _apiQueue.GetConsumingEnumerable())
            {
                try
                {
                    string reply = MicApiBridge.Handle(item.Json, item.Peer);   // 这里可以慢，接收线程不受影响
                    if (string.IsNullOrWhiteSpace(item.Peer)) { LastError = "数据通道不知道回包地址"; }
                    else SendApiReplyTo(item.Peer, reply ?? "", item.Key, item.Session, item.Sid);
                    Interlocked.Increment(ref ApiQueueHandled);
                }
                catch (Exception ex) { LastError = "数据通道接口处理异常：" + ex.Message; }
            }
        }

        /// <summary>
        /// 回一条接口响应：**两条通道共用**（sid=0 数据通道 / 已配对的会话通道）。
        /// 小响应一个包；大响应压缩 + 分片（每片 ~800 字符），片与片之间留 12ms，
        /// 避免瞬间多个报文被中继路径丢弃。
        /// </summary>
        static void SendApiReplyTo(string peerVirtualIp, string reply, byte[] key, MicSecurity.Session session, uint sid)
        {
            var chunks = BuildApiChunks(reply, out _);
            if (chunks == null)
            {
                SendApiPacket(peerVirtualIp, reply ?? "", key, session, sid);
                return;
            }
            foreach (var env in chunks)
            {
                SendApiPacket(peerVirtualIp, env, key, session, sid);
                try { Thread.Sleep(12); } catch { }
            }
            ApiChunkedReplies++;
        }

        // ================= cbs-api 分片（防 IP 分片丢包） =================

        /// <summary>超过这个字节数就分片；一片 ~800 字节远小于 MTU，不会再触发 IP 分片。</summary>
        const int ApiChunkThreshold = 1000;
        /// <summary>每片 base64 字符数（解码后约 600 字节）。</summary>
        const int ApiChunkDataLen = 800;

        static readonly Dictionary<string, (int Total, string[] Parts, DateTime At)> _apiInChunks
            = new Dictionary<string, (int, string[], DateTime)>();
        static readonly object _apiChunkLock = new object();
        /// <summary>正在等待拼齐的分片数（诊断用）。</summary>
        public static long ApiChunksPending;

        /// <summary>
        /// 收到的若是一片就攒起来：全部到齐返回完整 JSON，还没齐返回 null，不是分片就原样返回。
        /// 分片信封：{"v":1,"id":"<请求 id>","z":1,"i":<第几片>,"n":<共几片>,"d":"<base64 片段>"}
        /// </summary>
        public static string ReassembleIncomingApi(string json, out string error)
        {
            error = null;
            System.Text.Json.Nodes.JsonNode node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(json); } catch { return json; }   // 不是 JSON：交给上层报错
            var o = node as System.Text.Json.Nodes.JsonObject;
            if (o == null || !o.ContainsKey("n") || !o.ContainsKey("i") || !o.ContainsKey("d")) return json;
            string id = o["id"]?.ToString() ?? "";
            int n = o["n"]?.GetValue<int>() ?? 0;
            int i = o["i"]?.GetValue<int>() ?? -1;
            string d = o["d"]?.ToString() ?? "";
            if (n <= 0 || i < 0 || i >= n) { error = "分片参数不合法（i=$i n=$n）"; return null; }
            lock (_apiChunkLock)
            {
                if (!_apiInChunks.TryGetValue(id, out var st) || st.Total != n || (DateTime.UtcNow - st.At).TotalSeconds > 20)
                    st = (n, new string[n], DateTime.UtcNow);
                st.Parts[i] = d;
                st.At = DateTime.UtcNow;
                _apiInChunks[id] = st;
                for (int k = 0; k < n; k++) if (st.Parts[k] == null) return null;
                _apiInChunks.Remove(id);
                if (_apiInChunks.Count > 32) _apiInChunks.Clear();
                try
                {
                    byte[] gz = Convert.FromBase64String(string.Concat(st.Parts));
                    using var ms = new MemoryStream(gz);
                    using var g = new GZipStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    g.CopyTo(outMs);
                    return Encoding.UTF8.GetString(outMs.ToArray());
                }
                catch (Exception ex) { error = "分片解压失败：" + ex.Message; return null; }
            }
        }

        /// <summary>
        /// 把一个响应切成若干分片信封（纯函数，便于自检）；返回 null 表示"不用分片"。
        /// 信封：{"v":1,"id":"…","z":1,"i":<第几片>,"n":<共几片>,"d":"<base64 片段>"}
        /// </summary>
        public static List<string> BuildApiChunks(string reply, out string id)
        {
            id = JsonField(reply, "id");
            byte[] raw = Encoding.UTF8.GetBytes(reply ?? "");
            if (raw.Length <= ApiChunkThreshold) return null;
            using var ms = new MemoryStream();
            using (var g = new GZipStream(ms, CompressionLevel.Fastest, true)) g.Write(raw, 0, raw.Length);
            string b64 = Convert.ToBase64String(ms.ToArray());
            int total = (b64.Length + ApiChunkDataLen - 1) / ApiChunkDataLen;
            var list = new List<string>(total);
            for (int i = 0; i < total; i++)
            {
                int len = Math.Min(ApiChunkDataLen, b64.Length - i * ApiChunkDataLen);
                var env = new System.Text.Json.Nodes.JsonObject
                {
                    ["v"] = 1, ["id"] = id, ["z"] = 1, ["i"] = i, ["n"] = total,
                    ["d"] = b64.Substring(i * ApiChunkDataLen, len)
                };
                list.Add(env.ToJsonString());
            }
            return list;
        }

        /// <summary>回一条接口响应：小就直接发，大就压缩 + 分片发。</summary>
        static void SendApiReply(string peerVirtualIp, string reply, byte[] key)
            => SendApiReplyTo(peerVirtualIp, reply, key, null, 0);

        static void SendApiPacket(string peerVirtualIp, string json, byte[] key,
            MicSecurity.Session session = null, uint sid = 0)
        {
            // 会话通道：序号必须用会话自己的计数器（nonce = sid‖seq，与音频包共用密钥、不能重复）；
            // 数据通道（sid=0）：用数据通道自己的计数器。
            ulong seq = session != null ? session.NextApiSeq() : (ulong)System.Threading.Interlocked.Increment(ref _openApiSeq);
            var pkt = MicProtocol.BuildPacket(MicProtocol.TypeApi, sid, seq, 0, Encoding.UTF8.GetBytes(json), key);
            if (VntProcess.SendToPeer(peerVirtualIp, pkt, out string serr))
            {
                if (session != null) session.ApiReplies++;
                else OpenApiReplies++;
            }
            else
            {
                LastError = "数据通道回包失败：" + serr;
                if (session != null) Stats.Note("cbs-api 回包失败：" + serr, DateTime.UtcNow);
            }
        }

        /// <summary>分片响应的发片数（诊断：确认"大响应确实走了分片"）。</summary>
        public static long ApiChunkedReplies;

        // 【nonce 复用 · 4.0.0 正式根治】数据通道（sid=0）的密钥是**由数据密码派生的静态密钥**（所有手机共用一把），
        // 所以 seq 空间必须做到"跨重启、跨设备都不重叠"。两端各负责一半：
        //   · 手机端（发起方）：**每安装持久化的 64 位单调计数器**（首次取随机起点，见安卓 core\ApiClient.kt）；
        //   · 电脑端（回包方）：**每次启动随机取一个 62 位基数**，随后单调递增。
        // 62 位随机基数下，两次启动的 seq 区间重叠概率约 n/2^62（n = 单次运行包数），可忽略。
        // 若将来要更严格，可把这个基数也持久化（需要额外的落盘时机，暂不做）。
        static long _openApiSeq = NewOpenApiSeqBase();
        static long NewOpenApiSeqBase()
        {
            var b = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(b);
            return (long)(BitConverter.ToUInt64(b, 0) >> 2);   // 62 位正整数，留出递增余量
        }
        /// <summary>数据通道已回的包数（状态里显示，确认"播控/排程/调休"这条路是通的）。</summary>
        public static long OpenApiReplies;


        /// <summary>
        /// 处理一条 UDP 配对数据报（公开以便自检/探针直接调用，无需真的发 UDP）。
        /// <paramref name="claimedVirtualIp"/> = 内层 vnt 帧里的源虚拟 IP：中继模式下外层源地址是服务端，
        /// 手机真正的身份是虚拟 IP，必须把它也登记进会话白名单，否则后续音频会被判"来源不符"丢弃。
        /// </summary>
        public static void HandlePairDatagram(byte[] frame, IPEndPoint source, string claimedVirtualIp = null)
        {
            if (frame == null || frame.Length == 0) return;
            PairUdpReceived++;
            var now = DateTime.UtcNow;
            var key = MicPairUdp.DeriveKey(VntManager.DataPassword);
            string line = MicPairUdp.TryUnwrap(frame, key, out string err);
            if (line == null)
            {
                PairUdpRejected++;
                Stats.Note("UDP 配对帧被拒（" + err + "）", now);
                // 只提醒一次级别的问题才写日志，避免被扫描/重放刷爆日志
                return;
            }
            string sourceIp = source?.Address?.ToString() ?? "";
            uint sid = 0; byte[] sessionKey = null;
            string reply = HandlePairLine(line, sourceIp, VntManager.MicEnabled, maxSessions: VntManager.MicMaxSessions,
                VntManager.MicCodeSeed, now, out sid, out sessionKey, claimedVirtualIp);
            Stats.Note("UDP 配对回复 会话 " + sid, now);
            // 观测点：让探针/守候模式能实时看到"谁配对成功/为什么失败"，不必解析日志
            bool okFlag = reply.Contains("\"ok\":true");
            string reasonHint = okFlag ? "" : JsonField(reply, "reason");
            try { OnPairResult?.Invoke(JsonField(line, "device_id"), sourceIp, okFlag, reasonHint, sid); } catch { }
            if (!MicPairUdp.IsUsableReplyEndpoint(source)) return;      // 来源地址不可回包
            try
            {
                // 回包地址：优先直接回来源（P2P 场景）；中继场景下 vnts 会把它转给手机
                var outFrame = MicPairUdp.Build(reply, key);
                _pairUdp.Send(outFrame, outFrame.Length, source);
                PairUdpReplied++;
            }
            catch (Exception ex) { LastError = "UDP 配对回包失败：" + ex.Message; }
        }


        /// <summary>处理一次配对请求（TCP 一行一答、UDP 一报一答共用；公开以便自检直接调用）。</summary>
        public static string HandlePairLine(string helloLine, string sourceIp, bool micEnabled, int maxSessions,
            string codeSeed, DateTime nowUtc, out uint sid, out byte[] sessionKey, string claimedVirtualIp = null)
        {
            sid = 0; sessionKey = null;
            // 【配对请求(诊断)】把电脑端**实际收到的载荷**记下来，并标明是否命中"管理员免码"。
            // 用途：手机报"配对被拒：请求格式不对"时，一眼看出是报文没到、格式不符、还是走了普通解析。
            try
            {
                string peek = helloLine ?? "";
                if (peek.Length > 140) peek = peek.Substring(0, 140) + "...";
                PlayLog.Write("话筒", "配对请求(诊断)", "info",
                    detail: $"PAIRDIAG 来源 {sourceIp}；管理员免码命中={MicPairing.IsAdminHello(helloLine, out _)}；公钥/码存在={peek.Contains("\"pk\"")}/{peek.Contains("\"code\"")}；载荷={peek}");
            }
            catch { }
            // (0) 【管理员免码配对】用户要求：管理员设备的音频包**直接跳过配对**（不输 6 位动态码、无限时）。
            //    密钥两端各自用 HKDF(SHA256(数据密码)) 派生，不经过网络（见《话筒功能接口文档.md》§16.3）。
            if (MicPairing.IsAdminHello(helloLine, out string adminDev))
            {
                if (!VntManager.IsAdminDevice(adminDev))
                {
                    Stats.PairingFail++;
                    PlayLog.Write("话筒", "免码配对被拒", "warn", detail: $"设备 {adminDev} 不在「管理员设备」名单里（来源 {sourceIp}）");
                    return MicPairing.BuildAuthFail("not_admin");
                }
                // 【nonce 复用根治】手机在 hello 里声明 "s":1 时，本次会话用 16 字节随机盐派生密钥并随答复下发
                // （手机用同一盐派生）→ 密钥每会话唯一，彻底消除 (key, nonce) 重复；老手机不带该字段，行为不变。
                bool saltSupport = helloLine.Contains("\"s\":1");
                byte[] adminSalt = null;
                if (saltSupport) { adminSalt = new byte[16]; System.Security.Cryptography.RandomNumberGenerator.Fill(adminSalt); }
                var adminKey = MicPairing.AdminMicKey(VntManager.DataPassword, adminSalt);
                if (adminKey.Length != 32)
                {
                    Stats.PairingFail++;
                    PlayLog.Write("话筒", "免码配对不可用", "warn", detail: "尚未配置【数据密码】：免码配对需要它派生成会话密钥，请在「vnt 设置」里填一个");
                    return MicPairing.BuildAuthFail(MicPairing.Reason.BadRequest);
                }
                // 【会话去重】同一设备重复配对（手机重连、重试、切网络都会）：
                // 先移除该设备的旧会话，否则「当前设备」列表会出现两条一模一样的话筒记录，
                // 而且多路话筒的"谁说话听谁"会被僵尸会话干扰（用户反馈）。
                try
                {
                    foreach (var dup in Registry.All.Where(x => string.Equals(x.DeviceId ?? "", adminDev, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        Registry.Remove(dup.Sid);
                        PlayLog.Write("话筒", "会话去重", "info", detail: $"设备 {adminDev} 重新配对，已移除旧会话 {dup.Sid}");
                    }
                }
                catch { }
                var adminSess = Registry.Create(sourceIp, adminDev, adminKey, nowUtc, 0);   // 0 = 无限时（管理员特权）
                sid = adminSess.Sid; sessionKey = adminKey;
                Stats.PairingOk++; Stats.SessionsStarted++;
                PlayLog.Write("话筒", "管理员免码配对", "info",
                    detail: $"设备 {adminDev}（{sourceIp}）已接入，会话 {sid}，**无限时**（管理员特权，未使用动态码）");
                return MicPairing.BuildAdminOk(sid, adminDev, adminSalt);
            }
            // 没虚拟网卡时（0.0.0.0）：配对也要过滤来源（虚拟网段或私网；防公网扫描）
            if (_vnetPrefixFilter != null)
            {
                bool ok = false;
                if (IsAcceptableSource(sourceIp)) ok = true;
                if (!ok && !string.IsNullOrWhiteSpace(claimedVirtualIp) && IsAcceptableSource(claimedVirtualIp)) ok = true;
                if (!ok) return "{\"t\":\"cbs-mic\",\"v\":1,\"ok\":false,\"reason\":\"denied\"}";
            }
            // ① 断线恢复：RESUME 报文（网络抖动重连，不必再输动态码）
            if (MicPairing.TryParseResume(helloLine, out uint rSid, out string rToken, out _))
            {
                var old = Registry.Find(rSid);
                if (old == null || !MicSecurity.VerifyResumeToken(old.Key, rSid, rToken))
                {
                    Stats.PairingFail++;
                    Stats.Note("恢复失败(令牌不符) 会话 " + rSid, nowUtc);
                    PlayLog.Write("话筒", "恢复失败", "warn", detail: $"会话 {rSid} 的恢复令牌不符，拒绝");
                    return MicPairing.BuildAuthFail(MicPairing.Reason.Expired);
                }
                if (!old.TryResume(nowUtc, Math.Max(5, VntManager.MicResumeGraceSeconds)))
                {
                    Registry.Remove(old.Sid);
                    Stats.PairingFail++; Stats.SessionsEnded++;
                    Stats.Note("恢复失败(超过宽限期) 会话 " + rSid, nowUtc);
                    PlayLog.Write("话筒", "恢复失败", "warn", detail: $"会话 {rSid} 超过宽限期，已销毁（需重新输动态码）");
                    return MicPairing.BuildAuthFail(MicPairing.Reason.Expired);
                }
                sid = old.Sid; sessionKey = old.Key;
                Stats.Note("恢复成功 设备 " + old.DeviceId, nowUtc);
                PlayLog.Write("话筒", "恢复成功", "info", detail: $"设备 {old.DeviceId}（{old.SourceIp}）已恢复会话 {old.Sid}（免动态码，时长上限不重置）");
                return MicPairing.BuildResumeOk(old.Sid);
            }
            if (_codes == null) _codes = new MicSecurity.CodeStore(codeSeed);

            // 【幂等】手机端配对时会**同时试"经 vnt 中继"和"局域网直发"两条通道**（还有重试），
            // 同一个 6 位动态码会到达电脑端多次；而动态码是一次性的（消费过就标记为已用），
            // 于是第二次到达会被判 bad_code —— 真机上就表现成"配对成功 2、失败 11"这种怪异计数。
            // 这里把"同一设备 + 同一动态码窗口"的成功答复缓存几分钟，重复请求直接重发同样的答复。
            {
                string devId0 = JsonField(helloLine, "device_id");
                string code0 = JsonField(helloLine, "code");
                if (!string.IsNullOrWhiteSpace(code0) && MicProtocol.VerifyCode(codeSeed, code0, nowUtc, out long codeWin))
                {
                    // 缓存键含**客户端随机数**：手机重试时会带新的 nc，这样它能拿到一把**新的**服务端密钥。
                    // （只用 设备+窗口 做键的话，手机端万一在某把密钥上出问题，重试会一直拿回同一把，
                    //   真机就出现过"连续两次同一个 X25519 异常"。）
                    string ck = (devId0 ?? "") + "#" + codeWin + "#" + (JsonField(helloLine, "nc") ?? "");
                    lock (_pairReplayLock)
                    {
                        if (_pairReplay.TryGetValue(ck, out var cached) && (nowUtc - cached.At).TotalMinutes < 3)
                        {
                            sid = cached.Sid; sessionKey = cached.Key;
                            Stats.Note("配对重发（同一动态码的重复请求，重发上次结果，不计失败）", nowUtc);
                            PlayLog.Write("话筒", "配对重发", "info",
                                detail: $"设备 {devId0} 用同一个动态码又请求了一次（手机同时试了多条通道），直接重发上次的成功答复");
                            return cached.Reply;
                        }
                    }
                }
            }

            var auth = MicPairing.Authenticate(helloLine, sourceIp, nowUtc, micEnabled, maxSessions,
                Registry.Count, Guard, _codes, codeSeed);
            if (!auth.Ok)
            {
                Stats.PairingFail++;
                if (auth.Reason == MicPairing.Reason.TooMany) Stats.Locked++;
                LastPairFailure = auth.Reason + "（来源 " + sourceIp
                    + (string.IsNullOrWhiteSpace(claimedVirtualIp) ? "" : "，虚拟 IP " + claimedVirtualIp)
                    + "，时间 " + nowUtc.ToLocalTime().ToString("HH:mm:ss")
                    + (auth.Reason == MicPairing.Reason.BadCode ? " —— 动态码不对或已过期（60 秒一换，用一次作废）" : "")
                    + (auth.Reason == MicPairing.Reason.TooMany ? " —— 输错次数过多，已锁定" : "")
                    + (auth.Reason == MicPairing.Reason.MicOff ? " —— 电脑端话筒开关是「关」" : "")
                    + (auth.Reason == MicPairing.Reason.Busy || auth.Reason == MicPairing.Reason.TooMany
                        ? " —— 已有话筒在跑：请在「话筒」窗点『断开当前话筒』再试" : "") + "）";
                Stats.Note("配对失败(" + auth.Reason + ") 来源 " + sourceIp, nowUtc);
                PlayLog.Write("话筒", "配对失败", "warn", detail: $"来源 {sourceIp}：{auth.Reason}");
                return auth.Reply;
            }
            // 【两种话筒权限的时限】用户确认：
            //   · 信任设备（已加入信任名单）→ MaxMinutes = 0 = **无限时**；
            //   · 配对码连接（临时权限）→ 用配置的 60 分钟（VntManager.MicMaxMinutes，可在「话筒」窗调）。
            bool unlimitedMic = VntManager.IsTrustedDevice(auth.DeviceId) || VntManager.IsAdminDevice(auth.DeviceId);
            int micMaxMin = unlimitedMic ? 0 : VntManager.MicMaxMinutes;
            // 【会话去重】同上：同一设备只保留最新会话，避免重复记录与多路话筒误判
            try
            {
                foreach (var dup in Registry.All.Where(x => string.Equals(x.DeviceId ?? "", auth.DeviceId ?? "", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    Registry.Remove(dup.Sid);
                    PlayLog.Write("话筒", "会话去重", "info", detail: $"设备 {auth.DeviceId} 重新配对，已移除旧会话 {dup.Sid}");
                }
            }
            catch { }
            var sess = Registry.Create(sourceIp, auth.DeviceId, auth.SessionKey, nowUtc, micMaxMin);
            // 配对成功：把设备 ID 加入信任列表（下次同一设备配对免动态码）
            try { if (!string.IsNullOrWhiteSpace(auth.DeviceId)) VntManager.NoteSeenDevice(auth.DeviceId); } catch { }   // 见过的设备（内存）
            // 【不再自动写信任名单】用户确认的语义：配对成功 = 拿到**临时权限**（内存会话，默认 60 分钟上限），
            // 信任名单（永久免码）只能由管理员在「远程 → 管理员」里手动添加。
            // （以前这里自动 AddTrustedDevice，导致"踢出后重连又变成免码信任"，正是用户反馈的现象。）
            // 中继场景：外层源是 vnts 服务端，真正的手机身份是内层虚拟 IP —— 两个都登记，
            // 否则手机发的音频会因为"来源不符"被丢弃（表现为"配对成功但电脑没声音"）。
            if (!string.IsNullOrWhiteSpace(claimedVirtualIp)) sess.AllowSource(claimedVirtualIp);
            sid = sess.Sid; sessionKey = auth.SessionKey;
            Stats.PairingOk++; Stats.SessionsStarted++;
            LastPairFailure = "";      // 成功就清掉上次的失败原因（界面不该一直挂着旧原因）
            Stats.Note("配对成功 设备 " + auth.DeviceId, nowUtc);
            PlayLog.Write("话筒", "配对成功", "info", detail: $"设备 {auth.DeviceId}（来源 {sourceIp}"
                + (string.IsNullOrWhiteSpace(claimedVirtualIp) ? "" : $"，虚拟 IP {claimedVirtualIp}")
                + $"）已接入，会话 {sid}，单次上限 {sess.MaxMinutes} 分钟；允许来源 {sess.AllowedSourcesText}");
            string okReply = MicPairing.RebuildAuthOk(sid, auth.ServerPublicKey, auth.ServerNonce, auth.DeviceId);
            // 缓存这次成功答复（同一设备 + 同一动态码窗口），让"多通道重试"变成幂等
            try
            {
                if (!string.IsNullOrWhiteSpace(auth.DeviceId) &&
                    MicProtocol.VerifyCode(codeSeed, JsonField(helloLine, "code"), nowUtc, out long w2))
                {
                    lock (_pairReplayLock)
                    {
                        if (_pairReplay.Count > 32) _pairReplay.Clear();
                        _pairReplay[auth.DeviceId + "#" + w2 + "#" + (JsonField(helloLine, "nc") ?? "")] = (nowUtc, okReply, sid, auth.SessionKey);
                    }
                }
            }
            catch { }
            return okReply;
        }

        /// <summary>同一（设备, 动态码窗口）的成功答复缓存，用于让多通道重试幂等。</summary>
        static readonly Dictionary<string, (DateTime At, string Reply, uint Sid, byte[] Key)> _pairReplay
            = new Dictionary<string, (DateTime, string, uint, byte[])>();
        static readonly object _pairReplayLock = new object();

        static void HandlePairClient(TcpClient client)
        {
            client.ReceiveTimeout = 10000;
            var stream = client.GetStream();
            var sb = new StringBuilder();
            var buf = new byte[512];
            while (sb.Length < MicPairing.MaxHelloBytes)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); } catch { return; }
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            string line = sb.ToString().Split('\n')[0].Trim();
            if (line.Length == 0) return;
            string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            string reply = HandlePairLine(line, ip, VntManager.MicEnabled, maxSessions: VntManager.MicMaxSessions,
                VntManager.MicCodeSeed, DateTime.UtcNow, out _, out _);
            try
            {
                var outBytes = Encoding.UTF8.GetBytes(reply + "\n");
                stream.Write(outBytes, 0, outBytes.Length);
                stream.Flush();
            }
            catch { }
        }

        /// <summary>载荷里是否出现 OpusHead（Ogg 流必须以它开头，否则 ffplay 不出声）。</summary>
        public static bool LooksLikeOpusHead(byte[] payload)
        {
            if (payload == null || payload.Length < 27 + 8) return false;
            try
            {
                int n = Math.Min(payload.Length, 96);
                string head = Encoding.ASCII.GetString(payload, 0, n);
                return head.Contains("OpusHead");
            }
            catch { return false; }
        }

        /// <summary>是否是合法的 Ogg 页（以 "OggS" 开头、版本 0、段数非 0）。CRC 由 ffplay 校验。</summary>
        public static bool LooksLikeOggPage(byte[] payload)
        {
            if (payload == null || payload.Length < 28) return false;
            return payload[0] == (byte)'O' && payload[1] == (byte)'g' && payload[2] == (byte)'g' && payload[3] == (byte)'S'
                   && payload[4] == 0 && payload[26] != 0;
        }

        /// <summary>
        /// 周期清理：静默超时先**挂起**（保留密钥，宽限期内可免动态码恢复），宽限期满才真正销毁。
        /// 返回 (新挂起数, 销毁数)。
        /// </summary>
        public static (int suspended, int ended) SweepSilentSessions(DateTime nowUtc)
        {
            int suspended = 0, ended = 0;
            int grace = Math.Max(5, VntManager.MicResumeGraceSeconds);
            foreach (var s in Registry.All)
            {
                if (s.Expired(nowUtc))                       // 到单次时长上限：直接销毁
                {
                    Registry.Remove(s.Sid); Stats.SessionsEnded++; Stats.SessionsKilledByLimit++; ended++;
                    PlayLog.Write("话筒", "超时结束", "warn", detail: $"会话 {s.Sid} 达到单次上限({s.MaxMinutes} 分钟)，已结束");
                    continue;
                }
                if (!s.Suspended)
                {
                    if (s.Silent(nowUtc, TimeSpan.FromSeconds(SilenceTimeoutSeconds)))
                    {
                        s.Suspend(nowUtc); suspended++;
                        PlayLog.Write("话筒", "挂起", "warn", detail:
                            $"会话 {s.Sid}（{s.DeviceId}）静默超过 {SilenceTimeoutSeconds} 秒，先挂起保留 {grace} 秒等重连（此期间不播放）");
                    }
                    continue;
                }
                if ((nowUtc - s.SuspendedAt!.Value).TotalSeconds > grace)
                {
                    Registry.Remove(s.Sid); Stats.SessionsEnded++; ended++;
                    PlayLog.Write("话筒", "会话注销", "warn", detail: $"会话 {s.Sid}（{s.DeviceId}）超过 {grace} 秒未恢复，已销毁，需重新输动态码");
                }
            }
            return (suspended, ended);
        }

        /// <summary>本机当前是否有 vnt 虚拟网地址（10.26.x.x）；用网卡枚举判断，不启动任何进程，可每秒调用。</summary>
        public static string DetectLocalVntIp()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        string ip = ua.Address?.ToString() ?? "";
                        if (VntManager.LooksLikeVntAddress(ip)) return ip;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>给定地址是不是**本机真实存在**的地址（用来挡住"拿用户态虚拟 IP 去 bind"这种错）。</summary>
        public static bool IsLocalAddress(IPAddress addr)
        {
            try
            {
                if (addr == null) return false;
                if (IPAddress.Any.Equals(addr) || IPAddress.IPv6Any.Equals(addr)) return true;
                if (IPAddress.IsLoopback(addr)) return true;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address != null && ua.Address.Equals(addr)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 来源是否可接受（绑 0.0.0.0 时的准入判断）：
        ///   ① 在虚拟网段内（经 vnt 中继/直连来的包，内层源虚拟 IP）；或
        ///   ② 私网 / 回环 / 链路本地来源（**手机与电脑在同一个局域网**、且手机没有 TUN 时，
        ///      它只能直接发到电脑的局域网地址，源地址就是手机的 192.168.x.x —— 这类必须放行，
        ///      否则真机永远配对不上：自检里"正确动态码配对成功 → denied"就是这个原因）。
        /// 公网来源一律拒绝（配合 cbs-pair 的 AEAD + 6 位动态码 + 限速 + 审计）。
        /// </summary>
        public static bool IsAcceptableSource(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (IsInVnet(ip)) return true;
            if (!System.Net.IPAddress.TryParse(ip, out var a)) return false;
            if (System.Net.IPAddress.IsLoopback(a)) return true;
            var bytes = a.GetAddressBytes();
            if (bytes.Length == 16) return true;                       // IPv6 交给上层策略，这里不拦
            if (bytes.Length != 4) return false;
            if (bytes[0] == 10) return true;                           // 10/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;   // 172.16/12
            if (bytes[0] == 192 && bytes[1] == 168) return true;       // 192.168/16
            if (bytes[0] == 169 && bytes[1] == 254) return true;       // 169.254/16 链路本地
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;  // 100.64/10 CGNAT
            return false;
        }

        /// <summary>
        /// 二维码/状态里应该告诉手机"**能直接发到的**地址" —— 只有**局域网 IPv4** 符合这个语义
        /// （手机没有虚拟网卡，10.x 它没有路由，发不出去）。
        /// 【不要回退成虚拟网地址】：以前"拿不到局域网地址就回退到绑定地址/虚拟地址"，
        /// 结果二维码里印的是 10.8.0.105，手机端白发一轮（真机被用户当场质疑过）。
        /// 拿不到就返回空串 —— 手机端会自动只走 vnt 中继（二维码里的 `mic.vnt_ip`）。
        /// </summary>
        public static string AdvertisedHost
        {
            get
            {
                string lan = VntLite.VntLiteClient.DetectLocalIpv4();
                if (string.IsNullOrWhiteSpace(lan) || lan == "0.0.0.0") return "";
                if (VntManager.LooksLikeVntAddress(lan)) return "";     // 双保险：虚拟网地址一律不当"直连地址"
                return lan;
            }
        }

        /// <summary>按 CIDR 前缀判断 IP 是否在虚拟网段内（如 _vnetPrefixFilter = "10.26.0.0/24"）。</summary>
        static bool IsInVnet(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(_vnetPrefixFilter)) return false;
            string cidr = _vnetPrefixFilter.Trim();
            int slash = cidr.IndexOf('/');
            if (slash < 0) return false;
            string net = cidr.Substring(0, slash);
            if (!int.TryParse(cidr.Substring(slash + 1), out int bits)) return false;
            if (!System.Net.IPAddress.TryParse(ip, out var addr) || !System.Net.IPAddress.TryParse(net, out var netAddr)) return false;
            var a = addr.GetAddressBytes();
            var n = netAddr.GetAddressBytes();
            if (a.Length != 4 || n.Length != 4) return false;
            int fullBytes = bits / 8, remBits = bits % 8;
            for (int i = 0; i < fullBytes; i++) if (a[i] != n[i]) return false;
            if (remBits > 0)
            {
                int mask = 0xFF << (8 - remBits);
                if ((a[fullBytes] & mask) != (n[fullBytes] & mask)) return false;
            }
            return true;
        }

        /// <summary>
        /// vnt 链路看护（每秒一次）：
        ///   ① 开麦中但虚拟网地址消失（掉线）→ 停止话筒服务 + 恢复音乐音量 + 告警；
        ///   ② 虚拟 IP 变了 → 停止并**自动重绑**到新地址（端口跟随配置值）；
        ///   ③ 虚拟地址回来后不自动开麦（开麦要人点，避免"远程偷偷开麦"）。
        /// </summary>
        public static void WatchVntLink()
        {
            if (!VntManager.MicEnabled) return;
            string ip = DetectLocalVntIp();
            if (!_running)
            {
                if (!string.IsNullOrWhiteSpace(ip)) _lastSeenVntIp = ip;   // 仅记录，供二维码/状态显示
                return;
            }
            // 【绑 0.0.0.0 时绝不做"地址变化就重绑"，也绝不用别的 vnt 地址判断掉线】
            // 绑 0.0.0.0 的语义就是"本机任何地址都收"，虚拟 IP 变不变都不需要重绑；而 `DetectLocalVntIp()`
            // 看到的是**用户自己另装那份 vnt** 的网卡地址（如 10.8.0.105），跟我们的 vnt-lite 没关系。
            // 老逻辑拿它和 "0.0.0.0" 比，永远不相等 → **每秒 Stop+Start 一次**：解码器与音频管道每秒被拆掉，
            // 表现就是"手机一直在发帧、电脑端完全没声音"（真机日志：连续多条「绑定 0.0.0.0 / 启动 / 重绑」）。
            if (BoundAddress == "0.0.0.0")
            {
                if (!string.IsNullOrWhiteSpace(ip)) _lastSeenVntIp = ip;      // 只更新显示用地址
                // 虚拟 IP 是"服务器分配"来的：启动时可能还没到手（那时过滤为空），拿到后补上
                if (string.IsNullOrWhiteSpace(_vnetPrefixFilter))
                {
                    string eff = VntManager.EffectiveVnetPrefix;
                    if (!string.IsNullOrWhiteSpace(eff))
                    {
                        _vnetPrefixFilter = eff;
                        PlayLog.Write("话筒", "网段过滤", "info", detail: "已按服务器分配的虚拟 IP 启用来源过滤：" + eff);
                    }
                }
                if (VntManager.ManagedByApp && !VntProcess.IsRunning)
                {
                    PlayLog.Write("话筒", "掉线停麦", "warn", detail: "内置 vnt-lite 已停止，话筒服务随之停止（恢复组网后会自动重开）");
                    Stop();
                    MicDuck.ForceRestore();
                    Stats.Note("vnt-lite 停止，话筒已停", DateTime.UtcNow);
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(ip))
            {
                PlayLog.Write("话筒", "掉线停麦", "warn", detail: $"检测到 vnt 虚拟网地址消失（原绑定 {BoundAddress}），已停止话筒并恢复音乐音量");
                Stop();
                MicDuck.ForceRestore();
                Stats.Note("vnt 掉线，话筒已停", DateTime.UtcNow);
                return;
            }
            if (!string.Equals(ip, BoundAddress, StringComparison.Ordinal))
            {
                string oldIp = BoundAddress;
                Stop();
                MicDuck.ForceRestore();
                if (Start(ip, VntManager.MicPort, VntManager.MicCtlPort, VntManager.MicCodeSeed, out string err))
                    PlayLog.Write("话筒", "重绑", "warn", detail: $"vnt 虚拟 IP 由 {oldIp} 变为 {ip}，话筒服务已重绑；手机端请刷新端口/重新扫码");
                else
                    PlayLog.Write("话筒", "重绑失败", "error", detail: $"vnt 虚拟 IP 变为 {ip}，但重绑失败：{err}");
            }
        }

        static string _lastSeenVntIp = "";
        /// <summary>最近一次看到的 vnt 虚拟 IP（供二维码/状态显示）。</summary>
        public static string LastSeenVntIp => string.IsNullOrWhiteSpace(DetectLocalVntIp()) ? _lastSeenVntIp : DetectLocalVntIp();

        /// <summary>
        /// 音频链路"体检"一行话。手机端会在开麦后定期拉这一行写进自己的日志 ——
        /// 这样"话筒没声音"这种问题只要一份**手机日志**就能定位（不用再翻电脑端）：
        /// 看它是"没收到包"、"没见 OpusHead"、"播放器没起来"，还是"已在写管道但没声音"。
        /// </summary>
        public static string AudioHealthText()
        {
            var sb = new StringBuilder();
            sb.Append(Running ? "话筒服务在跑" : "话筒服务未启动");
            sb.Append("；播放器 ").Append(MicPipePlayer.Running
                ? $"在跑（已写 {MicPipePlayer.BytesWritten / 1024}KB）"
                : "**未启动**");
            if (MicPipePlayer.OggDrops > 0) sb.Append($"，管道丢帧 {MicPipePlayer.OggDrops}");
            if (OtherSpeakerDrops > 0) sb.Append($"，多路让位丢帧 {OtherSpeakerDrops}");
            var list = Registry.All.ToList();
            if (list.Count == 0) sb.Append("；**没有活动会话**");
            foreach (var s in list)
                sb.Append($"；会话{s.Sid}({s.DeviceId}) 收{s.Accepted}包 / Ogg {s.OggPages}页"
                    + (s.OggHeadSeen ? "（已见OpusHead）" : "（**没见OpusHead**）"));
            if (!string.IsNullOrWhiteSpace(MicPipePlayer.LastError)) sb.Append("；播放器错误：" + MicPipePlayer.LastError);
            if (!string.IsNullOrWhiteSpace(LastError)) sb.Append("；最近错误：" + LastError);
            return sb.ToString();
        }

        /// <summary>状态摘要（菜单/网页显示用）。</summary>
        public static string StatusText()
        {
            var info = new StringBuilder();
            if (Running)
            {
                // 绑 0.0.0.0 时把"来源限定在哪个网段"写清楚，避免看到 0.0.0.0 或某个虚拟地址犯迷糊
                string where = BoundAddress == "0.0.0.0"
                    ? $"0.0.0.0（只接受虚拟网段 {VntManager.EffectiveVnetPrefix} 内的来源）"
                    : BoundAddress;
                info.Append($"运行中 · 音频 {where}:{AudioPort} · 配对 {where}:{CtlPort}");
            }
            else info.Append("未启动");
            info.Append(PairUdpReady ? "（配对通道：TCP + UDP 加密）" : "（配对通道：仅 TCP，缺数据密码则 UDP 通道关闭）");
            info.Append($"　活动会话 {Registry.Count} 路");
            foreach (var s in Registry.All)
                info.Append($"\n· 设备 {s.DeviceId}（{s.SourceIp}）会话 {s.Sid}：已收 {s.Accepted} 包 / 丢弃 {s.Rejected}（重放 {s.Replayed}、限速 {s.RateHits}）"
                    + $"\n    UDP 丢包 {s.LossText}（丢 {s.LostFrames} 帧 / 统计 {s.SeqSeen + s.LostFrames} 帧，乱序 {s.OutOfOrder}）"
                    + (s.ApiRequests > 0 ? $"；接口请求 {s.ApiRequests}/回包 {s.ApiReplies}" : ""));
            // 多路话筒/音频管道/接口队列的丢弃计数（可靠性一眼可见）
            if (OtherSpeakerDrops > 0 || MicPipePlayer.OggDrops > 0 || ApiQueueDrops > 0 || ApiChunksPending > 0)
                info.Append($"\n音频/队列：让位丢弃 {OtherSpeakerDrops} 帧 · 管道丢 {MicPipePlayer.OggDrops} 帧 · 接口队列丢 {ApiQueueDrops} 次 · 待拼分片 {ApiChunksPending}");
            info.Append("\n" + Stats.Summary());
            // 音频链路体检直接放进状态摘要：这样「话筒」窗里一眼就能看到播放器/Ogg/OpusHead 的状态
            info.Append("\n音频链路：" + AudioHealthText());
            // 最近一次配对失败的原因（手机端输错码/码过期/已锁定都会写这里）——比只有计数有用得多
            if (!string.IsNullOrWhiteSpace(LastPairFailure))
                info.Append("\n最近配对失败原因：" + LastPairFailure);
            if (!string.IsNullOrWhiteSpace(LastError)) info.Append("\n最近错误：" + LastError);
            return info.ToString();
        }
    }
}
