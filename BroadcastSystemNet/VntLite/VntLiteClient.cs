using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Timer = System.Threading.Timer;

namespace BroadcastSystemNet.VntLite
{
    public enum VntLiteState { Idle, Handshaking, HandshakeDone, Registered, Failed }

    /// <summary>
    /// vnt-lite 客户端（里程碑 2）：只做"与 vnts 服务端握手 + 注册 + 保活"，**不建虚拟网卡、不需要管理员权限**。
    /// 协议依据：docs\vnt-1.2.16-C#重写方案.md §4（12 字节头 + protobuf 报文 + 哨兵地址）。
    /// 后续里程碑在这条 UDP 通道上跑我们自己的加密音频（MicProtocol）。
    /// </summary>
    public sealed class VntLiteClient : IDisposable
    {
        public VntLiteState State { get; private set; } = VntLiteState.Idle;
        public string LastError = "";
        public string LastPacketInfo = "";

        // 注册响应带回来的信息
        public uint VirtualIp, VirtualGateway, VirtualNetmask, Epoch, PublicPort;
        public string PublicIp = "";
        public readonly List<DeviceInfo> Devices = new List<DeviceInfo>();

        // 配置
        public string ServerHost = "";
        public int ServerPort;
        public string Token = "";
        public string DeviceId = "";
        public string Name = Environment.MachineName;
        /// <summary>节点间密码（`-w`）：留空则不带 client_secret（会与开了密码的设备互相看不见）。</summary>
        public string Password = "";
        /// <summary>加密模式名（`--model`）：参与 client_secret_hash 计算，必须与对端一致。</summary>
        public string Model = "aes_gcm";

        /// <summary>
        /// 复刻上游算法（vnt\src\core\mod.rs password_hash）：
        ///   hash = SHA256(model 名 utf8 ++ password utf8 ++ token utf8)，取**后 16 字节**（key[16..]）。
        /// 服务端按这个值把设备分组；不一致的设备互相看不见、也通信不了。
        /// </summary>
        public static byte[] ComputeClientSecretHash(string model, string password, string token)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var input = Encoding.UTF8.GetBytes((model ?? "") + (password ?? "") + (token ?? ""));
            var full = sha.ComputeHash(input);
            var r = new byte[16];
            Array.Copy(full, 16, r, 0, 16);        // 注意：是后 16 字节，不是前 16
            return r;
        }

        public long SentPackets, RecvPackets;
        public DateTime LastRecvAt = DateTime.MinValue;

        /// <summary>
        /// 中继优先时：经服务端中继的数据包**不加 vnt 层加密**（e=0）。
        /// 音频内容本身已由 MicProtocol 端到端 AEAD 加密，vnt 层明文只暴露包结构。由 VntManager.RelayFirst 驱动。
        ///
        /// 重要（真机踩过的坑）：**本实现的数据面永远是明文（e=0）**，
        /// 所以注册时的 `client_secret` **必须永远是 false** —— vnts 的中继条件是
        /// `目标注册的 client_secret == 数据包的 e 位`（`vnts/src/core/service/server.rs` L604-611）。
        /// 以前这里按 `hasPw && !RelayPlaintext` 填，于是"P2P 优先（默认）+ 填了数据密码"的电脑端
        /// 会注册成 client_secret=true，而它和手机端发的数据包都是 e=0 →
        /// **服务端把手机发来的所有包都丢掉**：表现为"组网成功，但手机上所有功能都不能用"。
        /// 详见 docs\交接-下一步.md 7.14。
        /// </summary>
        public bool RelayPlaintext;
        UdpClient _udp;
        VntTcpLink _tcp;                     // 非空 = 走 TCP 链路（服务端中继需要它）
        /// <summary>服务端地址是否要求 TCP 链路（写 tcp:// 前缀，或调用 <see cref="UseTcp"/> 强制）。</summary>
        public static bool WantsTcp(string serverHostPort)
            => !string.IsNullOrWhiteSpace(serverHostPort)
               && serverHostPort.TrimStart().StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);

        // ---------------- 服务端加密（-W）：第二次握手 + 之后全报文加密 ----------------

        /// <summary>
        /// 「服务端加密」（对应官方客户端 `--secret` / 配置里的 server_encrypt）**自动适配开关**。
        ///
        /// 语义（默认 true = 自动）：
        ///   第一次握手永远是明文；服务端在握手响应里带 RSA 公钥（vnts 的 `handshake()`
        ///   只要 `rsa_cipher` 可用就会带，见 server.rs L465-473）时，**自动**补发 RSA 密钥握手
        ///   并切到加密通道；服务端没带公钥（不带加密能力的实现）就一直走明文。
        ///   所以「服务器带 -W」和「不带 -W」都能连，用户不用管这个开关。
        ///
        /// 置 false = 强制明文（对应界面里的「强制关闭」）。
        /// </summary>
        public bool ServerEncrypt = true;

        /// <summary>
        /// 「强制开启」：服务端握手响应里没有 RSA 公钥时**直接报错**，不静默降级成明文。
        /// 只有用户在界面上显式选了「强制开启」才为 true。
        /// </summary>
        public bool ForceServerEncrypt;

        /// <summary>自动切到加密通道后的兜底：等 SecretHandshakeResponse 的截止时间（超时降级明文注册，避免永远卡在握手中）。</summary>
        DateTime _secretHandshakeDeadline = DateTime.MinValue;
        bool _secretHandshakeFellBack;

        /// <summary>服务端对称密钥（-W 握手协商出来的 32 字节）；非空即表示后续报文都要加密。</summary>
        byte[] _serverKey;
        /// <summary>服务端 RSA 公钥（DER，SPKI）与指纹，来自握手响应。</summary>
        byte[] _serverPubDer;
        string _serverFinger = "";
        /// <summary>是否已经发过第二次握手（避免重复发）。</summary>
        bool _secretHandshakeSent;

        /// <summary>当前是否处于"报文需加密"状态。</summary>
        public bool ServerEncrypted => _serverKey != null && _serverKey.Length == 32;

        /// <summary>诊断用：最近一次"服务端加密握手"的关键数值（长度/前缀/指纹），供探针打印。</summary>
        public string SecretHandshakeDiag = "";

        /// <summary>诊断钩子：每个**实际发到网线上**的报文都会回调一次（自检/探针用，产品里为 null）。</summary>
        public Action<byte[]> OnOutgoingPacket;

        /// <summary>
        /// 诊断用：把**实际发出的原始包**按顺序收集起来（探针据此重放，判定"我们的字节对不对"）。
        /// 产品里为空（不影响任何逻辑）。
        /// </summary>
        public readonly List<byte[]> OutgoingPackets = new List<byte[]>();

        /// <summary>诊断：第一个"加密后发往服务端"的包（注册包）的关键字段，供探针打印对照 finger 失败原因。</summary>
        public string EncryptedSendDiag = "";

        /// <summary>诊断：每个被加密出站包的形状与"finger 自校验"结果（探针用）。</summary>
        public readonly List<string> EncryptedSendLog = new List<string>();

        /// <summary>诊断：我们实际用于 AES 的对称密钥的 SHA256（十六进制），用于与服务端侧比对。</summary>
        public string SecretHandshakeKeyHash = "";

        /// <summary>诊断：成功解开过多少个服务端加密报文？0 表示我们的密钥与服务端不一致。</summary>
        public long ServerDecryptOk;
        /// <summary>诊断：收包解密失败的次数与原因。</summary>
        public long ServerDecryptFailures;
        public string ServerDecryptDiag = "";
        /// <summary>诊断：最近一次握手响应的关键字段（探针用）。</summary>
        public string HandshakeDiag = "";

        /// <summary>诊断：进入加密态后，收到的包里有几个带 e 位、几个是明文。</summary>
        public long ServerRecvEncrypted, ServerRecvPlain;

        /// <summary>诊断：最近收到的**服务端加密报文**原始字节（用于离线复算密钥一致性）。</summary>
        public byte[] LastServerEncryptedPacket;

        /// <summary>诊断：我们当前用于 AES 的对称密钥原始字节（探针落盘用来离线比对）。</summary>
        public byte[] ServerKeyBytes => _serverKey == null ? null : (byte[])_serverKey.Clone();

        /// <summary>
        /// 诊断用（-W 排障）：用指定的 32 字节密钥，对"已经构造好的明文载荷"加密并发出。
        /// 走的是**产品同一条加密路径**（同样的包头/顺序/字段），因此结果比外部重放可信。
        /// </summary>
        public bool SendWithKey(byte[] key32, byte type, uint sid, uint seq, byte[] plain)
        {
            if (key32 == null || key32.Length != 32) return false;
            var pkt = new VntPacket
            {
                GatewayFlag = true,
                Protocol = VntOuterProtocol.Service,
                Transport = type,
                SourceIp = VntPacket.SelfIp,
                DestIp = VntPacket.GatewayIp,
                InitTtl = VntPacket.MaxTtl,
                Ttl = VntPacket.MaxTtl
            };
            var nonce = VntCipher.BuildNonce(pkt);
            pkt.Encrypted = true;
            pkt.Payload = VntCipher.Encrypt(plain, key32, nonce, VntCipher.FingerHash(Token), true);
            var data = pkt.Build();
            if (_tcp != null) return _tcp.SendPacket(data);
            if (_udp == null) return false;
            _udp.Send(data, data.Length, ServerHost, ServerPort);
            SentPackets++;
            return true;
        }

        /// <summary>
        /// 把明文载荷封成"加密体"，布局与官方完全一致：
        ///     `密文 ‖ tag(16) ‖ random(4) ‖ finger(12)`
        /// 其中 `finger = SHA256(nonce ‖ 密文‖tag‖random ‖ SHA256(token))[20..32]`
        /// （见 `finger.rs` 的 `calculate_finger` 与 `aes_gcm_cipher.rs` 的 `encrypt_ipv4`）。
        ///
        /// 易错点（两次踩坑）：`VntCipher.Encrypt` 的最后一个参数是"**是否写这 12 字节 finger**"，
        /// 不是"是否用 --finger 校验"。服务端加密（-W）这一层**永远要写**它（vnts 用
        /// `SecretBody::new(payload, true)` 解包，缺了就会把 random+tag 尾部误当 finger → `finger err`）。
        /// 而 `fingerHash` 传 `SHA256(token)`：这正是计算 finger 所需的第三项，与 --finger 无关。
        /// </summary>
        byte[] WrapPayload(byte[] plain, VntPacket pkt)
            => VntCipher.Encrypt(plain, _serverKey, VntCipher.BuildNonce(pkt), VntCipher.FingerHash(Token), true);
        /// <summary>是否强制用 TCP 链路（T1：中继需要 tcp_sender）。</summary>
        public bool ForceTcp;

        /// <summary>处理一个收到的完整 vnt 报文（UDP 与 TCP 两条链路共用）。</summary>
        public void DispatchPacket(byte[] data, string srcHost, int srcPort)
        {
            if (data == null || data.Length == 0) return;
            // 话筒包（"MC"）与打洞探针（"VLP1"）是裸包，先判定
            if (data.Length >= 4 && data[0] == 0x56 && data[1] == 0x4C && data[2] == 0x50 && data[3] == 0x31)
            {
                PunchPacketsReceived++;
                P2PEstablished = true;
                return;
            }
            if (data.Length >= 2 && data[0] == 0x4D && data[1] == 0x43)
            {
                P2PRawReceived++;
                try { OnMicPacket?.Invoke(data, srcHost, srcPort); } catch { }
                return;
            }
            if (!VntPacket.TryParse(data, out var pkt, out _)) return;
            LastPacketInfo = pkt.ToString();
            if (ServerEncrypted)
            {
                if (pkt.Encrypted)
                {
                    ServerRecvEncrypted++;
                    LastServerEncryptedPacket = (byte[])data.Clone();   // 诊断：留一份原始字节
                }
                else ServerRecvPlain++;
            }
            // -W 生效后：服务端回的**控制面**报文也是加密体，先解密再分发
            //（否则会把密文当 protobuf 解析，得到莫名其妙的字段）。
            // 【务必限制在控制面】数据面（IpTurn/OtherTurn）是用**数据密码**加密的（带 -f 的服务器要求密文），
            // 拿 -W 的会话密钥去解必然失败 —— 老代码在这里直接 return，等于把对端的数据包整包丢掉。
            if (pkt.Encrypted && ServerEncrypted
                && (pkt.Protocol == VntOuterProtocol.Service
                    || pkt.Protocol == VntOuterProtocol.Control
                    || pkt.Protocol == VntOuterProtocol.Error))
            {
                // 同上：解密服务端加密体时也要按"带 finger12"解（布局与发送侧一致）
                if (VntCipher.TryDecrypt(pkt.Payload, _serverKey, VntCipher.BuildNonce(pkt), VntCipher.FingerHash(Token),
                        true, out var plain, out string derr))
                {
                    pkt.Payload = plain;
                    ServerDecryptOk++;
                }
                else
                {
                    LastError = "服务端加密报文解密失败：" + derr;
                    ServerDecryptFailures++;
                    if (ServerDecryptDiag.Length < 400)
                        ServerDecryptDiag += $"[收包解密失败 transport={pkt.Transport} 长度={pkt.Payload.Length} {derr}] ";
                    // 特例（实测真实 vnts 1.2.12 会出现）：SecretHandshakeResponse 是**空明文**封出来的
                    // 32 字节加密体（tag16+random4+finger12），我们这边解不开它的 tag。
                    // 但它**不携带任何有用信息**（明文 0 字节）—— 服务端此时已经把会话密钥建好了
                    // （服务端日志会打 secret_handshake）。所以这里不能因为解不开就卡死：
                    // 直接把状态推进到"加密就绪"并照常注册，否则注册包永远发不出去、表现为"连不上"。
                    if (ServerEncrypted && pkt.Transport == VntServiceProtocol.SecretHandshakeResponse)
                    {
                        PlayLog.Write("vnt-lite", "服务端加密就绪(容错)", "warn",
                            detail: "SecretHandshakeResponse 解密失败（空响应体，无有效载荷），"
                                  + "但服务端已建立会话密钥，按就绪处理并继续注册。" + derr);
                        HandleService(pkt.Transport, Array.Empty<byte>());
                        return;
                    }
                    return;
                }
            }
            // ---------------- 收到的数据面包（诊断：判断"手机发的到底有没有到电脑"）----------------
            // 只在前若干个包上写日志，避免刷屏；格式：源虚IP -> 目标虚IP 载荷长度 前 16 字节
            if (pkt.Protocol == VntOuterProtocol.IpTurn || pkt.Protocol == VntOuterProtocol.OtherTurn)
            {
                PeerIpTurnReceived += 0;
                if (DataPlaneDiagLog.Count < 20)
                {
                    string line = $"外层{(int)pkt.Protocol}/上层{(int)pkt.Transport} 源{VntPacket.UintToIp(pkt.SourceIp)}->目标{VntPacket.UintToIp(pkt.DestIp)} 载荷{pkt.Payload?.Length ?? 0}字节 "
                        + (pkt.Payload != null && pkt.Payload.Length > 0
                            ? BitConverter.ToString(pkt.Payload, 0, Math.Min(16, pkt.Payload.Length))
                            : "(空)");
                    DataPlaneDiagLog.Add(line);
                    PlayLog.Write("vnt-lite", "收到数据包", "info", detail: line);
                }
            }
            if (pkt.Protocol == VntOuterProtocol.Service) HandleService(pkt.Transport, pkt.Payload);
            else if (pkt.Protocol == VntOuterProtocol.Control) HandleControl(pkt.Transport, pkt.Payload, pkt.SourceIp);
            else if (pkt.Protocol == VntOuterProtocol.Error) HandleError(pkt.Transport, pkt.Payload);
            else if (pkt.Protocol == VntOuterProtocol.IpTurn && pkt.Transport == VntIpTurnProtocol.Ipv4)
            {
                // 服务端是"原样转发"密文：收到 e=1 的包必须先按 vnt 客户端加密解密，才能拿到里面的 IP 帧
                PeerIpTurnReceived++;
                byte[] frame = null;
                if (pkt.Encrypted && !string.IsNullOrEmpty(Password))
                {
                    frame = TryDecryptDataPlane(pkt, out string derr);
                    if (frame == null) LastError = "收到加密数据但解密失败：" + derr;
                }
                else frame = pkt.Payload;
                if (frame != null && VntIpFrame.TryParse(frame, out _, out _, out _, out _, out byte[] inner, out _) && inner.Length > 0)
                {
                    PeerPacketsReceived++;
                    try { OnPeerData?.Invoke(inner); } catch { }
                    try { OnPeerDataFrom?.Invoke(inner, VntPacket.UintToIp(pkt.SourceIp)); } catch { }
                }
            }
            else if (pkt.Protocol == VntOuterProtocol.OtherTurn)
            {
                if (pkt.Transport == VntOtherTurnProtocol.Punch) LastPunchInfo = pkt.Payload;
                else if (pkt.Transport == TransportMicData)
                {
                    // 数据面可能被对端加密（带 -f 的服务器要求密文），这里先解一层再交给上层
                    byte[] body = pkt.Payload;
                    if (pkt.Encrypted && !string.IsNullOrEmpty(Password))
                    {
                        var plain = TryDecryptDataPlane(pkt, out string derr);
                        if (plain != null) body = plain;
                        else { LastError = "收到加密的对端数据但解密失败：" + derr; return; }
                    }
                    if (body == null || body.Length == 0) return;
                    PeerPacketsReceived++;
                    try { OnPeerData?.Invoke(body); } catch { }
                    try { OnPeerDataFrom?.Invoke(body, VntPacket.UintToIp(pkt.SourceIp)); } catch { }
                }
            }
        }

        /// <summary>
        /// 解一条**数据面**密文（e=1）。
        /// 带指纹和不带指纹都试一遍：AEAD 的 tag 校验决定只有一种能成功，
        /// 这样既能收我们自己发的（永远带 12 字节 finger），也能收官方客户端的包。
        /// </summary>
        byte[] TryDecryptDataPlane(VntPacket pkt, out string error)
        {
            error = "";
            try
            {
                var key = VntCipher.DeriveKey(Password, out _);
                var fh = VntCipher.FingerHash(Token);
                var nonce = VntCipher.BuildNonce(pkt);
                if (VntCipher.TryDecrypt(pkt.Payload, key, nonce, fh, true, out var plain, out string e1)) return plain;
                if (VntCipher.TryDecrypt(pkt.Payload, key, nonce, fh, false, out var plain2, out string e2)) return plain2;
                error = e1 + " / " + e2;
            }
            catch (Exception ex) { error = ex.Message; }
            return null;
        }
        Thread _rx;
        Timer _hb;
        DateTime _lastHandshake = DateTime.MinValue;
        volatile bool _running;

        public const int HandshakeThrottleSeconds = 3;    // 与上游一致：3 秒内不重复发握手
        /// <summary>
        /// 心跳间隔（秒）。**官方 vnt 客户端是 3 秒**（`vnt/src/handle/maintain/heartbeat.rs` L34
        /// `Duration::from_secs(3)`）。我们原来写 30 秒，是"连接稳定性不如原版 App"的直接原因：
        /// 心跳太稀 → NAT 映射/服务端活跃度容易过期 → 掉线重连、看起来"时好时坏"。
        /// </summary>
        public const int HeartbeatSeconds = 3;
        /// <summary>
        /// "多久没收到服务端任何报文就重新握手"的判定基准（实际阈值 = 它 × 2）。
        /// 上游 `up_status.rs` 是 60 秒；我们把心跳对齐成 3 秒后（见 HeartbeatSeconds），
        /// 20 秒静默就足以判定链路已断——比原来的 120 秒恢复快得多，用户感受就是"掉线立刻自愈"。
        /// </summary>
        public const int StatusSeconds = 20;

        public bool Running => _running;

        /// <summary>当前进程里正在运行的 vnt-lite 实例（替代原来的 vnt-cli 子进程；程序内已不再依赖 vnt 二进制）。</summary>
        public static VntLiteClient Instance;

        // ---------------- 启停 ----------------

        public bool Start(string serverHostPort, string token, string deviceId, string name, out string error)
        {
            error = null;
            if (_running) { error = "已在运行"; return false; }
            if (!TryParseServer(serverHostPort, out string host, out int port, out error)) return false;
            if (string.IsNullOrWhiteSpace(token)) { error = "组网令牌（token）不能为空"; return false; }
            ServerHost = host; ServerPort = port; Token = token.Trim();
            DeviceId = deviceId ?? ""; Name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
            try
            {
                bool tcp = ForceTcp || WantsTcp(serverHostPort);
                if (tcp)
                {
                    // T1：走 TCP 链路 —— 服务端才会持有 tcp_sender，中继转发（send_one）才可能生效
                    _tcp = new VntTcpLink();
                    _tcp.OnPacket = d => DispatchPacket(d, ServerHost, ServerPort);
                    if (!_tcp.Connect(host, port, out string terr)) { error = terr; return false; }
                }
                else
                {
                    // 不建网卡：普通 UDP socket 即可，无需管理员权限
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    _udp.Client.ReceiveTimeout = 1000;
                }
                RelayPlaintext = VntManager.RelayFirst;
                // 传输模式：中继优先 → 经服务端中继且不加 vnt 层加密
                _running = true;
                Instance = this;                      // 供程序内其它界面/服务读取（替代 vnt-cli 子进程）
                State = VntLiteState.Handshaking;
                SendHandshake(force: true);
                _rx = new Thread(RxLoop) { IsBackground = true, Name = "vntlite-rx" };
                _rx.Start();
                _hb = new Timer(_ => OnHeartbeat(), null, HeartbeatSeconds * 1000, HeartbeatSeconds * 1000);
                PlayLog.Write("vnt-lite", "启动", "info", detail: $"vnt-lite 已启动：服务器 {host}:{port}，设备名 {Name}（无网卡模式）");
                return true;
            }
            catch (Exception ex)
            {
                error = "启动失败：" + ex.Message;
                LastError = error;
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _hb?.Dispose(); } catch { }
            _hb = null;
            try { _udp?.Close(); } catch { }
            _udp = null;
            StopVnetListener();
            try { _rx?.Join(800); } catch { }
            _rx = null;
            State = VntLiteState.Idle;
            if (Instance == this) Instance = null;
        }

        public void Dispose() => Stop();

        /// <summary>解析 "host:port"（也接受 udp://host:port 形式；tcp/ws 由里程碑 5 处理）。</summary>
        public static bool TryParseServer(string s, out string host, out int port, out string error)
        {
            host = ""; port = 0; error = null;
            if (string.IsNullOrWhiteSpace(s)) { error = "服务器地址为空"; return false; }
            string t = s.Trim();
            foreach (var pre in new[] { "udp://", "tcp://", "ws://", "wss://" })
                if (t.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) t = t.Substring(pre.Length);
            int i = t.LastIndexOf(':');
            if (i <= 0 || i == t.Length - 1) { error = "服务器地址要写成 地址:端口，例如 1.2.3.4:29871（你自己的 vnts 地址）"; return false; }
            host = t.Substring(0, i);
            if (!int.TryParse(t.Substring(i + 1), out port) || port <= 0 || port > 65535)
            { error = "服务器端口不合法"; return false; }
            return true;
        }

        // ---------------- 报文构造（可脱离网络断言） ----------------

        /// <summary>握手是否该发了（上游：同一目标 3 秒内不重复发）。</summary>
        public bool ShouldSendHandshake(DateTime nowUtc)
            => (nowUtc - _lastHandshake).TotalSeconds >= HandshakeThrottleSeconds;

        /// <summary>
        /// 上报给服务端的客户端版本号（第一次握手与注册都用它）。
        /// 默认 1.2.16；探针可临时改成别的值，用来排除"服务端按版本号区别对待"这类问题。
        /// </summary>
        public string ClientVersion = "1.2.16";

        /// <summary>
        /// vnt 的"数据密码"。配了它时，与服务端通信的对称密钥用 `SHA256(密码)` 派生
        /// （与官方客户端一致）；没配时才用随机密钥。
        /// </summary>
        public string DataPassword = "";

        public byte[] BuildHandshakeBytes()
        {
            var req = new HandshakeRequest
            {
                Version = ClientVersion,
                Secret = false,
                // 带**本机**公钥指纹：官方客户端就是这么做（handshaker.rs L59-62）。
                //   服务端 `handshake()` L473 的逻辑是：`res.key_finger != req.key_finger` 时才下发自己的公钥。
                //   如果这里发空串，某些情况下会被服务端当成"指纹一致"而**不下发公钥** →
                //   我们不发第二次握手 → 服务端仍用会话密钥加密响应 → 我们解不开 → 永远停在"握手完成"。
                //   所以"拿到自己的指纹"是这条通道的正确性前提，而不是可选项。
                KeyFinger = VntRsa.LocalFinger() ?? ""
            };
            var pkt = VntPacket.NewService(VntServiceProtocol.HandshakeRequest, req.ToBytes());
            return pkt.Build();
        }

        /// <summary>
        /// 构造打洞请求（外层 Control/PunchRequest，**目的地址 = 目标设备的虚拟 IP**）。
        /// 载荷带上"我们自己的公网端点 + 本地端口 + NAT 类型"，服务端会把它转给目标设备。
        /// </summary>
        public byte[] BuildPunchRequestBytes(uint targetVirtualIp, int localPort)
        {
            var pi = new PunchInfo
            {
                PublicPort = PublicPort,                 // 服务端在注册响应里告诉我们的公网端口
                LocalIp = VntPacket.IpToUint(DetectLocalIpv4()),
                LocalPort = (uint)localPort,
                NatType = 1,                             // 先按 Cone 上报（真正的 NAT 类型要靠 re_nat_type 探测，后续再做）
                PunchModel = 1                           // IPv4
            };
            if (!string.IsNullOrEmpty(PublicIp))
            {
                try { pi.PublicIpList.Add(VntPacket.IpToUint(PublicIp)); } catch { }
            }
            return new VntPacket
            {
                // 这是发给【对端设备】的包，不是给网关的：带 gw 标志会被服务端当服务包处理（日志里的 Unknown=...,Control,3）
                GatewayFlag = false,
                Protocol = VntOuterProtocol.Control,
                Transport = VntControlProtocol.PunchRequest,
                SourceIp = VirtualIp != 0 ? VirtualIp : VntPacket.SelfIp,
                DestIp = targetVirtualIp,
                Payload = pi.ToBytes()
            }.Build();
        }

        /// <summary>
        /// 本机**局域网** IPv4（不是虚拟网卡地址）。
        ///
        /// 为什么要挑：以前这里是"第一个非回环 IPv4"，在装了 vnt 的机器上会返回**虚拟网卡**的地址
        /// （实测把 10.8.0.105 当成局域网地址写进二维码），而手机端没有虚拟网卡、裸 UDP 根本发不到 10.x，
        /// 于是二维码里的"对端地址"永远连不通。这里改成：跳过虚拟/TUN/隧道类网卡、跳过落在本机虚拟网段里的地址，
        /// 并优先选**有默认网关**的那块网卡（真正连着路由器的那个）。
        /// </summary>
        public static string DetectLocalIpv4()
        {
            try
            {
                string best = "";
                string fallback = "";
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    string tag = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
                    if (tag.Contains("vnt") || tag.Contains("tun") || tag.Contains("tap") || tag.Contains("wintun")
                        || tag.Contains("wireguard") || tag.Contains("virtual") || tag.Contains("vmware")
                        || tag.Contains("hyper-v") || tag.Contains("vbox") || tag.Contains("bluetooth")
                        || tag.Contains("loopback")) continue;
                    bool hasGateway = false;
                    try { hasGateway = ni.GetIPProperties().GatewayAddresses.Count > 0; } catch { }
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var ip = ua.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (System.Net.IPAddress.IsLoopback(ip)) continue;
                        string s = ip.ToString();
                        if (VntManager.LooksLikeVntAddress(s)) continue;              // 落在虚拟网段里的一律不算局域网
                        if (s.StartsWith("169.254.")) continue;                        // 链路本地
                        if (hasGateway) { if (best.Length == 0) best = s; }
                        else if (fallback.Length == 0) fallback = s;
                    }
                }
                if (best.Length > 0) return best;
                if (fallback.Length > 0) return fallback;
                // 真找不到（只有虚拟网卡）时，退回"最近看到的虚拟地址"，让调用方知道拿不到局域网地址
                return "0.0.0.0";
            }
            catch { }
            return "0.0.0.0";
        }

        /// <summary>向某个虚拟 IP 发起打洞（把 PunchRequest 发给服务端，由它转给对端）。</summary>
        public void PunchTo(uint targetVirtualIp, int localPort)
        {
            Send(BuildPunchRequestBytes(targetVirtualIp, localPort));
            PlayLog.Write("vnt-lite", "打洞请求", "info",
                detail: $"向 {VntPacket.UintToIp(targetVirtualIp)} 发起打洞（本机公网 {PublicIp}:{PublicPort}）");
        }

        /// <summary>
        /// 发给对端设备的数据包（**中继路径**）：外层用 OtherTurn(5)、自定义上层号 200，
        /// 目的地址 = 对端虚拟 IP；服务端会把它转发给对端（不需要虚拟网卡）。
        /// 我方音频（MicProtocol 的 AEAD 包）就走这里。
        /// </summary>
        public const byte TransportMicData = 200;
        /// <summary>P2P 直连：把原始数据（例如 MicProtocol 的 AEAD 音频包）直接发到对端公网端点，不经服务端。</summary>
        public void SendRawToPeer(byte[] data, string host, int port)
        {
            if (data == null || data.Length == 0) return;
            if (_udp == null) { LastError = "vnt-lite 未启动，无法 P2P 发送"; return; }   // 没启动就明确失败，不计入统计
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(host), port);
                _udp.Send(data, data.Length, ep);
                P2PRawSent++;                       // 只有真的发出去了才计数（统计要真实）
            }
            catch (Exception ex) { LastError = "P2P 发送失败：" + ex.Message; }
        }

        public long P2PRawSent, P2PRawReceived;
        /// <summary>收到对端 P2P 直连来的话筒包（"MC" 开头）时的回调：载荷、源地址、源端口。</summary>
        public Action<byte[], string, int> OnMicPacket;
        /// <summary>收到对端数据包（payload, **对端虚拟 IP**）：中继时 UDP 源是服务端，业务侧必须用这个内层源地址。
        /// 产品用它把中继/P2P 收到的音频喂给 MicServer.HandleAudioPacket。</summary>
        public Action<byte[], string> OnPeerDataFrom;

        public long PeerPacketsSent, PeerPacketsReceived;
        /// <summary>收到的 IpTurn 数据包数（不分解密成败，用于区分"服务端没转"与"我们没解开"）。</summary>
        public long PeerIpTurnReceived;
        /// <summary>收到的 P2P 打洞探针数；&gt;0 表示直连路径可用（无需服务端转发数据）。</summary>
        public long PunchPacketsReceived;
        public bool P2PEstablished;

        /// <summary>原始收包统计：按"外层/上层协议号"计数（用于区分"服务端没转"与"转了但解析失败"）。</summary>
        public readonly System.Collections.Generic.Dictionary<string, int> RawByType = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>最近若干原始包的前 16 字节（十六进制），排错用。</summary>
        public readonly System.Collections.Generic.List<string> RecentRaw = new System.Collections.Generic.List<string>();

        /// <summary>诊断：最近收到的数据面报文（最多 20 条），用于判断"对端发的包到底有没有到本机"。</summary>
        public readonly System.Collections.Generic.List<string> DataPlaneDiagLog = new System.Collections.Generic.List<string>();

        void RecordRaw(byte[] data)
        {
            string key;
            if (VntPacket.TryParse(data, out var p, out _)) key = $"外层{p.Protocol}/上层{p.Transport}";
            else key = "解析失败(前4字节 " + BitConverter.ToString(data, 0, Math.Min(4, data.Length)) + ")";
            RawByType[key] = RawByType.TryGetValue(key, out int c) ? c + 1 : 1;
            if (RecentRaw.Count < 8)
                RecentRaw.Add(BitConverter.ToString(data, 0, Math.Min(16, data.Length)));
        }
        /// <summary>收到对端自定义数据时的回调（载荷原样交给上层，例如话筒音频）。</summary>
        public Action<byte[]> OnPeerData;

        public void SendPeer(byte[] payload, uint destVirtualIp)
        {
            if (payload == null || payload.Length == 0) return;
            var pkt = new VntPacket
            {
                GatewayFlag = true,
                Protocol = VntOuterProtocol.OtherTurn,
                Transport = TransportMicData,
                SourceIp = VirtualIp != 0 ? VirtualIp : VntPacket.SelfIp,
                DestIp = destVirtualIp,
                Payload = payload
            };
            // 与 SendPeerViaIp 同理：数据面要加密 + 带指纹，否则带 -f 的服务器会丢包
            if (!string.IsNullOrEmpty(Password))
            {
                try
                {
                    var key = VntCipher.DeriveKey(Password, out _);
                    var nonce = VntCipher.BuildNonce(pkt);
                    pkt.Encrypted = true;
                    pkt.Payload = VntCipher.Encrypt(payload, key, nonce, VntCipher.FingerHash(Token), true);
                }
                catch (Exception ex) { LastError = "数据面加密失败：" + DescribeThrowable(ex); return; }
            }
            Send(pkt.Build());
            PeerPacketsSent++;
        }

        /// <summary>把对端公网端点也作为一条直连路径发一发（P2P 打洞用，失败不影响中继）。</summary>
        public void SendPunchBurst(string host, int port, int count)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(host), port);
                var probe = new byte[] { 0x56, 0x4C, 0x50, 0x31 };   // "VLP1"
                for (int i = 0; i < count; i++)
                {
                    try { _udp?.Send(probe, probe.Length, ep); SentPackets++; } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// **正确的中继路径**：把数据封成 IPv4+UDP 帧，外层用 <c>IpTurn(4)/Ipv4(4)</c>、目的 = 对端虚拟 IP。
        /// 服务端会按目的虚拟 IP 转给对端（上游 recv_data\server.rs 就是这么处理 Ipv4 的）。
        /// </summary>
        public ushort LocalUdpPort = 40000;

        public void SendPeerViaIp(byte[] payload, uint destVirtualIp)
        {
            if (payload == null || payload.Length == 0) return;
            uint src = VirtualIp != 0 ? VirtualIp : VntPacket.SelfIp;
            var frame = VntIpFrame.Build(src, destVirtualIp, LocalUdpPort, (ushort)(LocalUdpPort + 1), payload);
            var pkt = new VntPacket
            {
                GatewayFlag = false,
                Protocol = VntOuterProtocol.IpTurn,
                Transport = VntIpTurnProtocol.Ipv4,
                SourceIp = src,
                DestIp = destVirtualIp,
                InitTtl = VntPacket.MaxTtl,
                Ttl = VntPacket.MaxTtl,
                Payload = frame
            };
            // 【重要】数据面也加密（与官方客户端一致）：服务端开了 `-f`（指纹校验）时，
            //   `finger.rs` L21 对**未加密**的数据包直接 return Err("not encrypt") 丢掉；
            //   而只有 e=1 才能与注册里的 client_secret=true 匹配（client.rs L80 的转发条件）。
            //   e=0 的明文数据面在带 -f 的服务器上**永远收不到**：表现就是"能入网、但手机上所有功能都不能用"。
            //   接收方用数据密码派生的同一把密钥解密（并校验 12 字节指纹）。
            if (!string.IsNullOrEmpty(Password))
            {
                try
                {
                    var key = VntCipher.DeriveKey(Password, out _);
                    var nonce = VntCipher.BuildNonce(pkt);
                    pkt.Encrypted = true;
                    pkt.Payload = VntCipher.Encrypt(frame, key, nonce, VntCipher.FingerHash(Token), true);
                }
                catch (Exception ex) { LastError = "数据面加密失败：" + DescribeThrowable(ex); return; }
            }
            Send(pkt.Build());
            PeerPacketsSent++;
        }

        /// <summary>用户指定的固定虚拟 IP（0 = 由服务端分配）。</summary>
        public uint RequestedIp;

        public byte[] BuildRegisterBytes()
        {
            bool hasPw = !string.IsNullOrEmpty(Password);
            var req = new RegistrationRequest
            {
                Token = Token,
                DeviceId = DeviceId,
                Name = Name,
                Version = ClientVersion,
                VirtualIp = RequestedIp,  // 用户填了固定 IP 就请求它，否则 0 = 由服务端分配（与官方 App 一致）
                AllowIpChange = RequestedIp == 0,   // 固定 IP 时不允许服务器改
                // 数据面**已加密**（见 SendPeerViaIp）：注册必须带 client_secret=true，
                // 否则服务端转发条件 `目标.client_secret == 包 e 位` 不成立（client.rs L80），
                // 而且带 -f 的服务器会因为"数据包不是密文"直接丢弃（finger.rs L21）。
                ClientSecret = hasPw,
                ClientSecretHash = hasPw ? ComputeClientSecretHash(Model, Password, Token) : Array.Empty<byte>()
            };
            var pkt = VntPacket.NewService(VntServiceProtocol.RegistrationRequest, req.ToBytes());
            return pkt.Build();
        }

        public void SendHandshake(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && !ShouldSendHandshake(now)) return;
            Send(BuildHandshakeBytes());
            _lastHandshake = now;
            if (_handshakeSendAt == DateTime.MinValue) _handshakeSendAt = now;   // 供 DiagnoseStall 判断"发了多久没响应"
        }

        void Send(byte[] data)
        {
            try
            {
                // -W 生效后：**所有**报文都要带加密体（与官方 NetPacket::new_encrypt + server_cipher.encrypt_ipv4 一致）。
                // 但已经带加密体的包不能再包一层：第二次握手（SecretHandshakeRequest）的载荷是 **RSA 密文**，
                //   它自己就是"已加密"；若再套一层 AES，线上会从 268 字节变成 300 字节，服务端必然解不开
                //   （实测踩过：服务端日志 secret_handshake 之后立刻 decrypt failed）。
                //   判据就是包头里的 e 位（pkt.Encrypted）—— 这也正是官方那边的语义。
                // -W 生效后：**只有控制面**（Service/Control）才带加密体；
                // 数据面（IpTurn/OtherTurn）**必须保持明文**，否则服务端不转发（server.rs L611：
                // target.client_secret == 数据包的 e 位）。
                // 但第二次握手（SecretHandshakeRequest）本身不能被加密——它的载荷是 RSA 密文，
                // 此时 serverKey 还没建，若再套一层 AES 服务端必然解不开。
                // 判据：已建密钥 + 是控制面 + 不是 SecretHandshakeRequest + 未带 e 位。
                if (ServerEncrypted && VntPacket.TryParse(data, out var pkt, out string perr))
                {
                    if (!pkt.Encrypted
                        && (pkt.Protocol == VntOuterProtocol.Service || pkt.Protocol == VntOuterProtocol.Control)
                        && pkt.Transport != VntServiceProtocol.SecretHandshakeRequest)
                    {
                        var nonceDbg = VntCipher.BuildNonce(pkt);
                        pkt.Encrypted = true;
                        pkt.Payload = WrapPayload(pkt.Payload, pkt);
                        data = pkt.Build();
                        if (EncryptedSendDiag == null && pkt.Transport == VntServiceProtocol.RegistrationRequest)
                        {
                            EncryptedSendDiag = "（注册包指纹自校验见下）";
                        }
                        // 每个被加密的包都追加一行，便于看清顺序与形状
                        {
                            var nb = VntCipher.BuildNonce(pkt);
                            int payLen = pkt.Payload.Length;
                            // 真实布局（与 Rust SecretBody 一致）：密文 ‖ tag(16) ‖ finger(12)，
                            // 其中 random(4) 是明文的一部分（data‖random4），密文长度 = 数据长度 + 4
                            int ctLen = payLen - 16 - 12;
                            bool shapeOk = ctLen > 0;
                            var enBody = new byte[shapeOk ? payLen - 12 : 0];
                            if (shapeOk) Buffer.BlockCopy(pkt.Payload, 0, enBody, 0, enBody.Length);
                            var want = shapeOk ? VntCipher.ComputeFinger(nb, enBody, VntCipher.FingerHash(Token)) : new byte[12];
                            var got = new byte[12];
                            if (payLen >= 12) Buffer.BlockCopy(pkt.Payload, payLen - 12, got, 0, 12);
                            bool selfOk = want.AsSpan().SequenceEqual(got);
                            EncryptedSendLog.Add($"上层={pkt.Transport} 网关位={(pkt.GatewayFlag ? 1 : 0)} 载荷={payLen} "
                                + $"（密文{ctLen}+tag16+finger12，random4计入密文） nonce={BitConverter.ToString(nb)} "
                                + $"finger自校验={(selfOk ? "通过" : "失败")}"
                                + (selfOk ? "" : $" 期望={BitConverter.ToString(want)} 实际={BitConverter.ToString(got)}"));
                        }
                    }
                }
                if (_tcp != null)                      // TCP 链路：加 4 字节分帧头
                {
                    if (_tcp.SendPacket(data)) SentPackets++;
                    else LastError = _tcp.LastError;
                    return;
                }
                if (_udp == null) return;
                try { OnOutgoingPacket?.Invoke(data); } catch { }     // 诊断钩子：记录真实线上字节
                try { if (OutgoingPackets.Count < 32) OutgoingPackets.Add((byte[])data.Clone()); } catch { }
                _udp.Send(data, data.Length, ServerHost, ServerPort);
                SentPackets++;
            }
            catch (Exception ex) { LastError = "发送失败：" + DescribeThrowable(ex); }
        }

        /// <summary>
        /// 异常描述：`Message` 可能为 null（NullReferenceException / 越界就是这样），
        /// 直接拼 Message 只会得到难以排查的"发送失败：null"，所以带上类型与首个堆栈帧。
        /// </summary>
        public static string DescribeThrowable(Exception ex)
        {
            if (ex == null) return "(空异常)";
            string frames = "";
            try
            {
                var st = new System.Diagnostics.StackTrace(ex, false);
                if (st.FrameCount > 0) frames = "  @" + st.GetFrame(0);
            }
            catch { }
            return ex.GetType().Name + "：" + (ex.Message ?? "(无消息)") + frames;
        }

        /// <summary>
        /// 发第二次握手（服务端加密 -W）：随机 32 字节对称密钥 + 组网令牌，用**服务端 RSA 公钥**加密。
        /// 依据：`vnt\src\handle\handshaker.rs` L85-111 与 `vnts\src\core\service\server.rs` L487-511。
        ///
        /// 注意执行顺序：**先**把 `_serverKey` 设好再发包 —— 因为服务端的
        /// `SecretHandshakeResponse` 本身就是用这把密钥加密回来的（server.rs L503-508），
        /// 收到时若还没设密钥就会解不开。
        /// </summary>
        bool SendSecretHandshake()
        {
            try
            {
                var serverKey = VntRsa.ImportPublicKey(_serverPubDer);
                byte[] symKey;
                if (!string.IsNullOrEmpty(DataPassword))
                {
                    // 与服务端通信的对称密钥：官方客户端在配置了"数据密码"时用的是
                    // SHA256(data_password) 派生的密钥，而不是随机密钥（见 vnt 的 cipher 派生逻辑）。
                    symKey = VntCipher.DeriveKey(DataPassword, out _);
                }
                else
                {
                    symKey = new byte[32];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(symKey);
                }
                _serverKey = symKey;                       // 先设好，才能解开回来的加密报文
                // 诊断：把"我们实际将用于 AES 的密钥"的哈希记下来，与服务端能解出的那份比对
                try
                {
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    SecretHandshakeKeyHash = Convert.ToHexString(sha.ComputeHash(symKey));
                }
                catch { }

                // 与官方一致：头部的源/目的仍用哨兵地址（真实地址在密文里）
                var head = new VntPacket
                {
                    GatewayFlag = true,
                    Protocol = VntOuterProtocol.Service,
                    Transport = VntServiceProtocol.SecretHandshakeRequest,
                    SourceIp = VntPacket.SelfIp,
                    DestIp = VntPacket.GatewayIp,
                    InitTtl = VntPacket.MaxTtl,
                    Ttl = VntPacket.MaxTtl,
                    Payload = Array.Empty<byte>(),
                    // 必须置位：这个包的载荷是 **RSA 加密体**，已经处理完了。
                    //  若不置位，Send() 会把它当成"待加密的明文"，再用 AES 包一层 ——
                    //  结果线上变成 300 字节（268+32），服务端自然解不开（实测踩过这个坑）。
                    Encrypted = true
                };
                var nonce = VntCipher.BuildNonce(head);
                var payload = VntRsa.BuildSecretHandshakePayload(serverKey, Token, symKey, nonce);
                head.Payload = payload;
                _secretHandshakeSent = true;
                // 诊断：密文长度必须是 256（RSA-2048）；长度不对说明 RSA 参数或装载有问题
                SecretHandshakeDiag = $"密文 {payload.Length} 字节，前 4 字节 {BitConverter.ToString(payload, 0, 4)}，"
                    + $"后 4 字节 {BitConverter.ToString(payload, payload.Length - 4, 4)}，"
                    + $"服务端指纹 {_serverFinger}，本机指纹 {VntRsa.LocalFinger()}";
                if (payload.Length != 256)
                {
                    LastError = "服务端加密握手密文长度异常：" + payload.Length + "（应为 256）";
                    PlayLog.Write("vnt-lite", "服务端加密握手异常", "error", detail: LastError);
                    return false;
                }
                PlayLog.Write("vnt-lite", "服务端加密握手", "info", detail: SecretHandshakeDiag);
                var built = head.Build();
                // 诊断：把"实际发到网线上的包"的摘要记下来，与服务端收到的比对
                SecretHandshakeDiag += $"，整包 {built.Length} 字节（应为 268），"
                    + $"包内 12..16 = {BitConverter.ToString(built, 12, 4)}，包内 264..268 = {BitConverter.ToString(built, built.Length - 4, 4)}";
                Send(built);
                return true;
            }
            catch (Exception ex)
            {
                _serverKey = null;
                LastError = "服务端加密握手失败：" + ex.Message;
                PlayLog.Write("vnt-lite", "服务端加密握手失败", "error", detail: ex.Message);
                return false;
            }
        }

        /// <summary>首次握手发出时刻（用于诊断"服务端没回"）。</summary>
        DateTime _handshakeSendAt = DateTime.MinValue;        /// <summary>是否已经就"服务端要求 -W"提醒过（只提醒一次，避免刷日志）。</summary>
        bool _serverSecretWarned;
        /// <summary>是否已经就"握手一直没响应"提醒过。</summary>
        bool _handshakeStallWarned;

        /// <summary>
        /// 诊断：握手/注册长时间没进展时，把"为什么"写清楚。
        /// 之前这里完全静默 —— 服务端地址填错、被防火墙拦、服务端要求 -W 都表现为
        /// "状态灯一直黄、什么都不报"，用户只能猜。现在至少能告诉他是哪一类。
        /// </summary>
        public void DiagnoseStall()
        {
            if (State == VntLiteState.Registered || State == VntLiteState.Failed) return;
            if (_handshakeSendAt == DateTime.MinValue) return;
            var waited = DateTime.UtcNow - _handshakeSendAt;
            if (waited.TotalSeconds < 8) return;

            if (LastRecvAt == DateTime.MinValue)
            {
                if (!_handshakeStallWarned)
                {
                    _handshakeStallWarned = true;
                    LastError = $"已向 {ServerHost}:{ServerPort} 发握手 {waited.TotalSeconds:0} 秒，**一个字节都没收到**："
                        + "请检查服务器地址/端口是否正确、UDP 端口是否被防火墙或运营商拦截、vnts 是否正在运行。";
                    PlayLog.Write("vnt-lite", "握手无响应", "error", detail: LastError
                        + $"（累计发送 {SentPackets} 包，收 {RecvPackets} 包）");
                }
                return;
            }
            // 收到过数据但没注册成功：多半是服务端要求加密、或令牌/密码分组不匹配
            if (!_serverSecretWarned && State != VntLiteState.Registered)
            {
                _serverSecretWarned = true;
                LastError = $"收到了服务端响应（收 {RecvPackets} 包）但一直没能注册成功："
                    + "最常见的原因是组网令牌不对（与电脑端填的不一致）、"
                    + "服务端开了 -f（指纹强校验，需要两边都开指纹校验）"
                    + "，或服务端要求 -W 而加密握手没走通（本程序默认自动适配 -W）。";
                PlayLog.Write("vnt-lite", "注册未成功", "error", detail: LastError);
            }
        }

        public long PongsSent;
        public byte LastControl = 0;

        /// <summary>处理控制层报文（外层 = Control(3)）：公开以便自检直接喂字节。</summary>
        public void HandleControl(byte transport, byte[] payload, uint fromVirtualIp = 0)
        {
            LastControl = transport;
            LastFromVirtualIp = fromVirtualIp;      // 收到控制包的源虚拟 IP（回包要发回它，服务端按虚拟 IP 路由）
            switch (transport)
            {
                case VntControlProtocol.Ping:
                    Send(NewControlBytes(VntControlProtocol.Pong));        // 必须回 Pong，否则服务端会认为我们掉线
                    PongsSent++;
                    break;
                case VntControlProtocol.Pong:
                    break;                                                  // 保活确认
                case VntControlProtocol.PunchRequest:
                case VntControlProtocol.PunchResponse:
                    {
                        // 打洞协商信息：把对端端点解出来（后续据此互发打洞包）
                        var pi = PunchInfo.Parse(payload);
                        P2PInfo = pi;
                        PlayLog.Write("vnt-lite", transport == VntControlProtocol.PunchRequest ? "收到打洞请求" : "收到打洞响应",
                            "info", detail: pi.Describe());
                        if (transport == VntControlProtocol.PunchRequest && !pi.Reply)
                        {
                            // 对端也在打洞：回一个 reply=true 的响应
                            var mine = new PunchInfo
                            {
                                PublicPort = PublicPort,
                                NatType = 1,
                                Reply = true,
                                LocalIp = VntPacket.IpToUint(DetectLocalIpv4()),
                                PunchModel = 1
                            };
                            if (!string.IsNullOrEmpty(PublicIp)) { try { mine.PublicIpList.Add(VntPacket.IpToUint(PublicIp)); } catch { } }
                            Send(new VntPacket
                            {
                                // 回给【对端设备】：必须是它的虚拟 IP（服务端按虚拟 IP 路由），不能写它的公网 IP
                                GatewayFlag = false,
                                Protocol = VntOuterProtocol.Control,
                                Transport = VntControlProtocol.PunchResponse,
                                SourceIp = VirtualIp != 0 ? VirtualIp : VntPacket.SelfIp,
                                DestIp = LastFromVirtualIp != 0 ? LastFromVirtualIp : VntPacket.GatewayIp,
                                Payload = mine.ToBytes()
                            }.Build());
                        }
                        break;
                    }
                case VntControlProtocol.AddrResponse:
                    LastPunchInfo = payload;
                    break;
                default:
                    break;
            }
        }

        /// <summary>处理错误报文（外层 = Error(2)）：把错误码变成人话，并据此改状态。</summary>
        public void HandleError(byte transport, byte[] payload)
        {
            string msg = VntErrorProtocol.Text(transport);
            LastError = msg;
            PlayLog.Write("vnt-lite", "服务端错误", "error", detail: msg);
            if (transport == VntErrorProtocol.TokenError || transport == VntErrorProtocol.Disconnect
                || transport == VntErrorProtocol.NoKey)
                State = VntLiteState.Failed;
        }

        public byte[] NewControlBytes(byte controlProtocol, byte[] payload = null)
            => new VntPacket
            {
                // 这是发给【对端设备】的包，不是给网关的：带 gw 标志会被服务端当服务包处理（日志里的 Unknown=...,Control,3）
                GatewayFlag = false,
                Protocol = VntOuterProtocol.Control,
                Transport = controlProtocol,
                SourceIp = VntPacket.SelfIp,
                DestIp = VntPacket.GatewayIp,
                Payload = payload ?? Array.Empty<byte>()
            }.Build();

        /// <summary>
        /// 心跳（Control/Ping）的载荷：**必须 ≥4 字节**。
        ///
        /// 【真机踩到的坑】vnts 的 `control_ping` 会把载荷交给 `PingPacket::new()`，而它对载荷的要求是
        /// `len >= 4`（见 `vnts/src/protocol/body/control_packet.rs` L92-98），否则直接 Err、**不回 Pong**。
        /// 我们原来发空载荷 → 服务端从不回应 → 客户端"超过 N 秒没收到服务端报文" → 每 45 秒白重连一次
        /// （电脑端日志里那串规律的「超时重连」就是它；每次重连还会打断话筒中继）。
        /// 载荷布局：前 2 字节 = 时间（大端），后 2 字节服务端会填 epoch（`set_epoch`）。
        /// </summary>
        public static byte[] BuildPingPayload()
        {
            var p = new byte[4];
            ushort t = (ushort)(Environment.TickCount & 0xFFFF);
            p[0] = (byte)(t >> 8); p[1] = (byte)(t & 0xFF);
            p[2] = 0; p[3] = 0;
            return p;
        }

        /// <summary>最近一次收到的打洞/地址信息（下一轮解析 PunchInfo）。</summary>
        public byte[] LastPunchInfo = Array.Empty<byte>();
        /// <summary>最近收到的控制包/数据包的源虚拟 IP（回包用）。</summary>
        public uint LastFromVirtualIp;

        /// <summary>最近一次协商到的对端打洞信息（P2P 端点）。</summary>
        public PunchInfo P2PInfo;

        /**
         * 虚拟网监听 socket（关键！）：
         * 官方客户端靠虚拟网卡把虚拟网里的包路由进来；我们没有虚拟网卡，就必须**自己起一个 UDP socket
         * 绑定到虚拟 IP 的端口上**，否则对端发来的包永远到不了（表现为"注册成功但收不到数据"）。
         *
         * 监听地址：注册成功后拿到的虚拟 IP；端口：与发送侧一致（40000）。
         */
        UdpClient _vnetUdp;
        Thread _vnetRx;

        void StartVnetListener()
        {
            if (VirtualIp == 0 || _vnetUdp != null) return;
            try
            {
                _vnetUdp = new UdpClient(new IPEndPoint(IPAddress.Any, LocalUdpPort));
                _vnetRx = new Thread(() =>
                {
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    while (_running)
                    {
                        byte[] data = null;
                        try { data = _vnetUdp.Receive(ref any); }
                        catch (SocketException) { continue; }
                        catch { if (!_running) return; continue; }
                        try
                        {
                            // 虚拟网里来的包就是 VntIpFrame（IPv4+UDP），剥掉 IP/UDP 头把载荷交给上层
                            if (VntIpFrame.TryParse(data, out _, out _, out _, out _, out byte[] inner, out _) && inner.Length > 0)
                            {
                                PeerPacketsReceived++;
                                try { OnPeerData?.Invoke(inner); } catch { }
                                try { OnPeerDataFrom?.Invoke(inner, VntPacket.UintToIp(VirtualIp)); } catch { }
                            }
                        }
                        catch { }
                    }
                }) { IsBackground = true };
                _vnetRx.Start();
                PlayLog.Write("vnt-lite", "虚拟网监听启动", "info",
                    detail: $"已在 {VntPacket.UintToIp(VirtualIp)}:{LocalUdpPort} 监听虚拟网数据包");
            }
            catch (Exception ex)
            {
                // 同一进程里多个客户端（如中继测试）会争用同一端口，这是测试场景特有的；
                // 生产环境只有一个客户端，不会有这个问题。虚拟网监听失败只影响 P2P 直连，
                // 不影响经服务端的中继路径，所以降级为 warn、不阻塞主流程。
                LastError = "虚拟网监听启动失败：" + ex.Message;
                PlayLog.Write("vnt-lite", "虚拟网监听失败", "warn", detail: ex.Message + "（不影响中继路径）");
            }
        }

        void StopVnetListener()
        {
            try { _vnetUdp?.Close(); } catch { }
            _vnetUdp = null; _vnetRx = null;
        }

        // ---------------- 收包与分发 ----------------

        void RxLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data = null;
                try { data = _udp.Receive(ref any); }
                catch (SocketException) { continue; }
                catch { if (!_running) return; continue; }
                try
                {
                    RecvPackets++;
                    LastRecvAt = DateTime.UtcNow;
                    // P2P 打洞探针包（我们自己约定的魔术字，不是 vnt 报文）：收到即认为直连可用
                    if (data.Length >= 4 && data[0] == 0x56 && data[1] == 0x4C && data[2] == 0x50 && data[3] == 0x31)
                    {
                        PunchPacketsReceived++;
                        if (!P2PEstablished)
                        {
                            P2PEstablished = true;
                            PlayLog.Write("vnt-lite", "P2P 直连可用", "info",
                                detail: $"从 {any} 收到打洞探针（第 {PunchPacketsReceived} 个），P2P 路径已建立");
                        }
                        continue;
                    }
                    // 话筒包（"MC" 魔术字，来自 MicProtocol）：P2P 直连路径收到 → 交给上层做 AEAD 校验
                    if (data.Length >= 2 && data[0] == 0x4D && data[1] == 0x43)
                    {
                        P2PRawReceived++;
                        try { OnMicPacket?.Invoke(data, any.Address.ToString(), any.Port); } catch { }
                        continue;
                    }
                    RecordRaw(data);
                    // 统一走 DispatchPacket：它带着"服务端加密(-W)报文的解密"这一步。
                    //   这里曾经自己又写了一遍分发（只调 HandleService、漏了解密）——
                    //   结果服务端的 SecretHandshakeResponse（e=1 密文）被当成 protobuf 明文解析，
                    //   状态机永远停在"握手完成"、注册包发不出去，而服务端那边只看到"注册从没来"。
                    //   收包路径只保留一条，避免这种"两处实现不一致"的坑。
                    DispatchPacket(data, any.Address.ToString(), any.Port);
                }
                catch (Exception ex) { LastError = "处理收包异常：" + ex.Message; }
            }
        }

        /// <summary>处理服务报文（公开以便自检直接喂字节，不需要真服务器）。</summary>
        public void HandleService(byte transport, byte[] payload)
        {
            switch (transport)
            {
                case VntServiceProtocol.HandshakeResponse:
                    {
                        var r = HandshakeResponse.Parse(payload);
                        // 诊断：握手响应的关键字段（判定服务端是否真的按 -W 回包）
                        HandshakeDiag = $"版本={r.Version} secret={r.Secret} 公钥长度={r.PublicKey?.Length ?? 0} "
                            + $"服务端指纹={(string.IsNullOrEmpty(r.KeyFinger) ? "(空)" : r.KeyFinger)}";
                        if (State == VntLiteState.Handshaking || State == VntLiteState.HandshakeDone)
                        {
                            // 服务端加密（-W）——**自动适配**：
                            //   第一次握手永远是明文，它的响应就告诉我们"服务端有没有加密能力"：
                            //   vnts 只要 rsa_cipher 可用就会回 secret=true + RSA 公钥（server.rs 的 handshake()）。
                            //   → 有公钥：自动补发 SecretHandshakeRequest{token,key}（RSA 加密），之后所有报文改用协商密钥；
                            //   → 无公钥：一直明文，照常注册。
                            //   这样"服务器带 -W"和"不带 -W"都能连，用户不用管开关。
                            bool serverOffers = r.Secret && r.PublicKey != null && r.PublicKey.Length > 0;
                            if (serverOffers && ServerEncrypt && !_secretHandshakeSent)
                            {
                                _serverPubDer = r.PublicKey;
                                _serverFinger = string.IsNullOrEmpty(r.KeyFinger)
                                    ? VntRsa.FingerOfSpki(r.PublicKey) : r.KeyFinger;
                                if (SendSecretHandshake())
                                {
                                    // 兜底：加密响应迟迟不来就降级成明文注册 —— 宁可弱一点，也别永远卡在"握手中"
                                    _secretHandshakeDeadline = DateTime.UtcNow.AddSeconds(4);
                                    PlayLog.Write("vnt-lite", "服务端加密握手", "info",
                                        detail: $"服务端提供加密（指纹 {_serverFinger}），已自动发送 SecretHandshakeRequest，"
                                              + "后续报文将全部加密（-W）");
                                    break;      // 等 SecretHandshakeResponse 再注册
                                }
                            }
                            else if (ForceServerEncrypt && !serverOffers)
                            {
                                // 用户显式选了「强制开启」而服务端没这个能力：明确报错，不静默降级
                                State = VntLiteState.Failed;
                                LastError = "已选「强制开启服务端加密」，但服务端握手响应里没有 RSA 公钥："
                                          + "这台服务器不支持 -W。请把该选项改回「自动」。";
                                PlayLog.Write("vnt-lite", "服务端加密不可用", "error", detail: LastError);
                                return;
                            }
                            State = VntLiteState.HandshakeDone;
                            Send(BuildRegisterBytes());          // 握手完成后立刻注册
                            PlayLog.Write("vnt-lite", "握手完成", "info",
                                detail: $"服务端版本 {r.Version}，public_key {(r.PublicKey.Length > 0 ? "有" : "无")}，key_finger {(string.IsNullOrEmpty(r.KeyFinger) ? "无" : "有")}"
                                        + $"，服务端支持加密(secret)={(r.Secret ? "是" : "否")}"
                                        + $"，本次链路 {(ServerEncrypted ? "已加密(-W)" : "明文")}"
                                        + $"，往返 {LastRecvAt.Subtract(_handshakeSendAt).TotalMilliseconds:0} ms");
                            if (r.Secret && !_serverSecretWarned && !ServerEncrypt)
                            {
                                _serverSecretWarned = true;
                                PlayLog.Write("vnt-lite", "服务端支持加密", "warn",
                                    detail: "服务端握手响应里带了 RSA 公钥（它支持 -W），但本机被设为「强制关闭」："
                                          + "本次以明文通信。组网与中继照常工作，"
                                          + "话筒音频与配对通道仍由各自的 AEAD 保护。");
                            }
                        }
                        break;
                    }
                case VntServiceProtocol.SecretHandshakeResponse:
                    {
                        // 第二次握手成功：对称密钥已被服务端接受，可以（加密地）注册了
                        State = VntLiteState.HandshakeDone;
                        _secretHandshakeDeadline = DateTime.MinValue;      // 兜底计时取消
                        Send(BuildRegisterBytes());
                        PlayLog.Write("vnt-lite", "服务端加密就绪", "info",
                            detail: "第二次握手完成，已切换到加密通道（-W），开始注册");
                        break;
                    }
                case VntServiceProtocol.RegistrationResponse:
                    {
                        var r = RegistrationResponse.Parse(payload);
                        VirtualIp = r.VirtualIp; VirtualGateway = r.VirtualGateway; VirtualNetmask = r.VirtualNetmask;
                        Epoch = r.Epoch; PublicIp = VntPacket.UintToIp(r.PublicIp); PublicPort = r.PublicPort;
                        Devices.Clear(); Devices.AddRange(r.Devices);
                        State = VntLiteState.Registered;
                        StartVnetListener();        // 关键：虚拟网监听（否则对端发来的包永远到不了）
                        PlayLog.Write("vnt-lite", "注册成功", "info", detail:
                            $"虚拟 IP {VntPacket.UintToIp(VirtualIp)}（网关 {VntPacket.UintToIp(VirtualGateway)}/"
                            + $"{VntPacket.UintToIp(VirtualNetmask)}），epoch {Epoch}，本机公网 {PublicIp}:{PublicPort}，同网设备 {Devices.Count} 台");
                        break;
                    }
                case VntServiceProtocol.PushDeviceList:
                    {
                        var r = new Pb.Reader(payload);
                        var list = new List<DeviceInfo>();
                        uint epoch = 0;
                        while (!r.Eof)
                        {
                            var (f, w) = r.ReadTag(); if (w < 0) break;
                            if (f == 1) epoch = (uint)r.Varint();
                            else if (f == 2) list.Add(DeviceInfo.Parse(new Pb.Reader(r.Bytes())));
                            else r.Skip(w);
                        }
                        Devices.Clear(); Devices.AddRange(list);
                        if (epoch > 0) Epoch = epoch;
                        break;
                    }
                default:
                    break;
            }
        }

        void OnHeartbeat()
        {
            if (!_running) return;
            // 自动适配的兜底：发了 RSA 密钥握手但服务端一直不回加密响应 → 降级成明文注册，
            //   避免"永远卡在握手中"。只降级一次，避免和正常流程来回打架。
            if (_secretHandshakeDeadline != DateTime.MinValue && DateTime.UtcNow > _secretHandshakeDeadline
                && State != VntLiteState.Registered)
            {
                _secretHandshakeDeadline = DateTime.MinValue;
                if (!_secretHandshakeFellBack)
                {
                    _secretHandshakeFellBack = true;
                    PlayLog.Write("vnt-lite", "服务端加密握手超时", "warn",
                        detail: "已发送 SecretHandshakeRequest 但 4 秒内没收到加密响应，"
                              + "改为按明文继续注册（服务器可能不支持 -W）。");
                    State = VntLiteState.HandshakeDone;
                    Send(BuildRegisterBytes());
                }
            }
            if (State != VntLiteState.Registered) SendHandshake();          // 还没注册上就重试握手（3 秒节流生效）
            else Send(NewControlBytes(VntControlProtocol.Ping, BuildPingPayload()));   // 已注册：Control/Ping 保活（载荷必须 ≥4 字节，否则服务端不回 Pong）
            DiagnoseStall();                                              // 长时间没进展就说清楚原因，不要静默
            if (LastRecvAt != DateTime.MinValue && (DateTime.UtcNow - LastRecvAt).TotalSeconds > StatusSeconds * 2)
            {
                State = VntLiteState.Handshaking;                          // 服务端长时间无响应 → 回到握手
                // 会话已经作废：清掉上一轮的加密密钥，重新走一次明文握手（否则后续包还带着旧密钥加密）
                _serverKey = null;
                _secretHandshakeSent = false;
                _secretHandshakeDeadline = DateTime.MinValue;
                _secretHandshakeFellBack = false;
                PlayLog.Write("vnt-lite", "超时重连", "warn", detail: $"超过 {StatusSeconds * 2} 秒没收到服务端报文，重新握手");
            }
        }

        public string StatusText()
        {
            string st = State switch
            {
                VntLiteState.Idle => "未启动",
                VntLiteState.Handshaking => "握手中…",
                VntLiteState.HandshakeDone => "握手完成，注册中…",
                VntLiteState.Registered => $"已入网 · 虚拟 IP {VntPacket.UintToIp(VirtualIp)}",
                _ => "失败"
            };
            return st + $"（{ServerHost}:{ServerPort}，收 {RecvPackets}/发 {SentPackets} 包"
                 + (string.IsNullOrEmpty(LastError) ? "" : "，最近错误 " + LastError) + "）";
        }
    }
}
