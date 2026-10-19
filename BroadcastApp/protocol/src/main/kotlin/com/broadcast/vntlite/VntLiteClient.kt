package com.broadcast.vntlite

import java.io.ByteArrayOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

/**
 * vnt-lite 客户端（Kotlin 参考实现）：纯 UDP、不建虚拟网卡、不需要 root。
 * 功能：握手 → 注册（拿虚拟 IP/网关/设备表）→ 30s 保活 → 打洞（P2P 直发）→ 中继（经服务端转发）。
 * 与 C# 侧 BroadcastSystemNet\VntLite\VntLiteClient.cs 行为一致。
 */
class VntLiteClient {
    enum class State { Idle, Handshaking, HandshakeDone, Registered, Failed }

    var serverHost = ""
    var serverPort = 0
    var token = ""
    var deviceId = ""
    var name = "android"
    var password = ""
    var model = "aes_gcm"
    /** 中继优先：经服务端的数据包不加 vnt 层加密（e=0）。服务端开了 -f 时必须为 false。 */
    var relayPlaintext = false
    var useFinger = false
    /**
     * 服务端加密（-W）**自动适配**开关（默认 true）：
     * 第一次握手永远是明文；服务端在握手响应里带 RSA 公钥（vnts 只要 rsa_cipher 可用就会带）
     * 就自动补发密钥握手并切到加密通道；没带公钥就一直明文。于是"服务器带 -W / 不带 -W"都能连。
     * 置 false = 强制明文。（「中继优先」模式下电脑端会自动关掉它：vnts 只转发明文数据包。）
     */
    var serverEncrypt = true
    /** 数据密码（vnt -W 时用于派生与服务端通信的对称密钥；官方客户端配了密码就用它，没配用随机）。 */
    var dataPassword = ""
    /** 客户端上报的版本号（第一次握手与注册都用它）。 */
    var clientVersion = "1.2.16"
    /** 本机密钥对的存放目录（用于 -W 的 key_finger 持久化；必传）。 */
    var keyDir: java.io.File? = null

    // ---- -W 运行时状态 ----
    private var localPriv: java.security.PrivateKey? = null
    private var localPubDer: ByteArray? = null
    private var serverPubDer: ByteArray? = null
    private var serverKey: ByteArray? = null
    private var secretHandshakeSent = false

    /** 最后一次*收到*服务端/对端包的时间：用于"静默太久就重新握手"（稳定性）。 */
    private var lastRecvAt = 0L

    /** 当前是否处于"报文需加密"状态（-W）。 */
    val serverEncrypted: Boolean get() = serverKey != null && serverKey!!.size == 32

    var state = State.Idle; private set
    var virtualIp = 0; private set
    var gatewayIp = 0; private set
    var netmaskIp = 0; private set
    var publicIp = ""; private set
    var publicPort = 0L; private set
    val devices = ArrayList<DeviceInfo>()
    var lastError = ""; private set
    var lastPacketInfo = ""; private set
    var p2pInfo: PunchInfo? = null; private set

    var sentPackets = 0L; private set
    var recvPackets = 0L; private set
    var peerPacketsSent = 0L; private set
    var peerPacketsReceived = 0L; private set
    var punchPacketsReceived = 0L; private set
    var p2pRawSent = 0L; private set
    var p2pRawReceived = 0L; private set

    /** 收到经服务端/P2P 来的业务载荷（payload, 对端虚拟 IP） */
    var onPeerData: ((ByteArray, String) -> Unit)? = null
    /** 收到裸的发洞探针/裸音频（magic 前缀） */
    var onRawMic: ((ByteArray, String, Int) -> Unit)? = null

    private var sock: DatagramSocket? = null
    private val running = AtomicBoolean(false)
    private var rxThread: Thread? = null
    private var tickThread: Thread? = null
    private var lastHandshake = 0L
    private var lastHeartbeat = 0L

    /**
     * 异常描述：`message` 可能为 null（NPE/越界就是这样），直接拼 message 只会得到
     * 难以排查的"发送失败：null"，所以带上类型与最上面两帧。
     * 放在 companion 里，别处（如 AppState）也能直接用 `VntLiteClient.describeThrowable(t)`。
     */
    companion object {
        const val HANDSHAKE_THROTTLE_MS = 3000L
        /**
         * 心跳间隔：**官方 vnt 客户端是 3 秒**（`vnt/src/handle/maintain/heartbeat.rs` L34
         * `Duration::from_secs(3)`）。我们原来用 30 秒，是"连接稳定性不如原版 App"的直接原因：
         * 心跳太稀 → NAT 映射 / 服务端活跃度过期 → 断流重连，用户感受就是"时好时坏"。
         */
        const val HEARTBEAT_MS = 3000L

        /** 静默多久就重新握手（官方客户端也是靠心跳失败触发重连；15 秒 = 连续 5 次心跳没回应）。 */
        const val SILENCE_RESET_MS = 30000L
        const val TRANSPORT_MIC_DATA: Byte = 200.toByte()
        const val PUNCH_MAGIC = "VLP1"
        fun wantsTcp(s: String) = s.trim().startsWith("tcp://", true)

        /** 异常描述（见上面的说明）：`类型：消息 @帧`，消息为空也不会只剩 "null"。 */
        fun describeThrowable(t: Throwable): String {
            val msg = t.message ?: "(无消息)"
            val frames = t.stackTrace.take(2).joinToString(" <- ") { it.toString() }
            return t.javaClass.simpleName + "：" + msg + (if (frames.isNotEmpty()) "  @" + frames else "")
        }
        fun parseServer(s: String): Pair<String, Int> {
            val t = s.trim().removePrefix("udp://").removePrefix("tcp://").removePrefix("ws://").removePrefix("wss://")
            val i = t.lastIndexOf(':')
            require(i > 0) { "服务器地址应为 host:port，当前：$s" }
            return Pair(t.substring(0, i), t.substring(i + 1).toInt())
        }
    }

    fun start(server: String, token: String, deviceId: String, name: String): Pair<Boolean, String?> {
        if (running.get()) return Pair(true, null)
        return try {
            val (h, p) = parseServer(server)
            serverHost = h; serverPort = p; this.token = token; this.deviceId = deviceId; this.name = name
            if (token.isBlank()) return Pair(false, "组网令牌不能为空")
            val s = DatagramSocket().apply { try { broadcast = true; reuseAddress = true } catch (_: Throwable) { } }
            s.soTimeout = 500
            sock = s
            running.set(true)
            state = State.Handshaking
            sendHandshake(true)
            rxThread = Thread { rxLoop() }.also { it.isDaemon = true; it.start() }
            tickThread = Thread { tickLoop() }.also { it.isDaemon = true; it.start() }
            Pair(true, null)
        } catch (e: Exception) { lastError = "启动失败：" + e.message; stop(); Pair(false, lastError) }
    }

    fun stop() {
        running.set(false)
        try { sock?.close() } catch (_: Exception) {}
        sock = null; state = State.Idle
    }

    // ---------------- 发送 ----------------

    private fun send(data: ByteArray) {
        // -W 生效后：**只有控制面**（Service/Control，outer=1 或 3）才带加密体；
        // 数据面（IpTurn=4 / OtherTurn=5）**必须保持明文**，否则服务端不转发。
        // 但第二次握手（SecretHandshakeRequest）本身不能被加密——它的载荷是 RSA 密文，
        // 此时 serverKey 还没建（加密前就先设好了），若再套一层 AES 服务端必然解不开。
        // 判据：已建密钥 + 是控制面 + 不是 SecretHandshakeRequest + 未带 e 位。
        var out = data
        if (serverEncrypted) {
            val pkt = VntPacket.parse(out)
            if (pkt != null && !pkt.encrypted
                && (pkt.outer == VntOuterProto.Service || pkt.outer == VntOuterProto.Control)
                && pkt.inner != VntServiceProto.SecretHandshakeRequest) {
                pkt.encrypted = true
                pkt.payload = wrapPayload(pkt.payload, pkt)
                out = pkt.build()
            }
        }
        val s = sock ?: return
        try { s.send(DatagramPacket(out, out.size, InetAddress.getByName(serverHost), serverPort)); sentPackets++ }
        catch (e: Exception) { lastError = "发送失败：" + describeThrowable(e) }
    }

    /**
     * 把明文载荷封成"加密体"（布局：密文‖tag16‖finger12，random4 是明文的一部分，见 VntCipher）。
     * 【仅用于控制面 -W】数据面请直接用数据密码派生的密钥，不要走这里（否则 -W 没建立时会退回明文却仍标 e=1）。
     */
    private fun wrapPayload(plain: ByteArray, pkt: VntPacket): ByteArray =
        VntCipher.encrypt(plain, serverKey ?: return plain, VntCipher.buildNonce(pkt), VntCipher.fingerHash(token), true)

    /** 第一次握手：带本机公钥指纹（key_finger）。没有它，服务端可能认为"客户端已有公钥"而不下发公钥。 */
    fun sendHandshake(force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force && now - lastHandshake < HANDSHAKE_THROTTLE_MS) return
        lastHandshake = now
        val req = HandshakeRequest().apply {
            version = clientVersion; secret = false
            keyFinger = run {
                val dir = keyDir
                if (dir != null) {
                    val (priv, pub) = VntRsa.localKey(dir)
                    localPriv = priv; localPubDer = pub
                    VntRsa.localFinger(pub)
                } else ""
            }
        }
        send(newService(VntServiceProto.HandshakeRequest, req.toBytes()))
        state = State.Handshaking
    }

    /** 第二次握手（服务端加密 -W）：用服务端公钥加密 SecretHandshakeRequest{token,key}。 */
    private fun sendSecretHandshake(): Boolean {
        val pubDer = serverPubDer ?: return false
        return try {
            val serverPub = VntRsa.importPublicKey(pubDer)
            val symKey = if (dataPassword.isNotEmpty()) VntCipher.deriveKey(dataPassword) else ByteArray(32).also { java.security.SecureRandom().nextBytes(it) }
            serverKey = symKey

            val head = VntPacket().apply {
                gateway = true; outer = VntOuterProto.Service; inner = VntServiceProto.SecretHandshakeRequest
                srcIp = VntPacket.SELF_IP; dstIp = VntPacket.GATEWAY_IP
                initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
                // 必须置位：这个包的载荷是 RSA 加密体，已经处理完了；否则 send() 会再套一层 AES。
                encrypted = true
            }
            val nonce = VntCipher.buildNonce(head)
            head.payload = VntRsa.buildSecretHandshakePayload(serverPub, token, symKey, nonce)
            secretHandshakeSent = true
            send(head.build())
            true
        } catch (e: Exception) {
            serverKey = null
            lastError = "服务端加密握手失败：" + e.message
            false
        }
    }

    private fun register() {
        val hasPw = password.isNotEmpty()
        val req = RegistrationRequest().apply {
            token = this@VntLiteClient.token
            deviceId = this@VntLiteClient.deviceId
            name = this@VntLiteClient.name
            version = "1.2.16"
            isFast = false
            virtualIp = 0
            allowIpChange = true
            // 数据面**已加密**（见 sendPeerViaIp）：注册必须带 client_secret = true，
            // 否则服务端转发条件「目标.client_secret == 包 e 位」不成立（client.rs L80），
            // 带 -f 的服务器还会因为"数据包不是密文"直接把包丢掉（finger.rs L21）。
            // 结论：**两端必须填同一个数据密码**，否则这条通道根本不通。
            clientSecret = hasPw
            clientSecretHash = if (hasPw)
                VntCipher.sha256((model + password + this@VntLiteClient.token).toByteArray(Charsets.UTF_8)).copyOfRange(16, 32)
            else ByteArray(0)
        }
        send(newService(VntServiceProto.RegistrationRequest, req.toBytes()))
    }

    private fun newService(serviceProto: Int, payload: ByteArray): ByteArray = VntPacket().apply {
        gateway = true; outer = VntOuterProto.Service; inner = serviceProto
        srcIp = VntPacket.SELF_IP; dstIp = VntPacket.GATEWAY_IP
        initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
        this.payload = payload
    }.build()

    /**
     * 心跳（Control/Ping）的载荷：**必须 ≥4 字节**。
     *
     * 真机踩到的坑：vnts 的 `control_ping` 把载荷交给 `PingPacket::new()`，它对载荷的要求是 `len >= 4`
     *（`vnts/src/protocol/body/control_packet.rs` L92-98），否则 Err、**不回 Pong**。
     * 我们原来发空载荷 → 服务端从不回应 → 客户端"静默 N 秒"误判 → 反复白重连（电脑端日志里那串规律的
     * 「超时重连」就是它，每次重连还会打断话筒中继）。
     * 布局：前 2 字节时间（大端），后 2 字节由服务端填 epoch。
     */
    private fun pingPayload(): ByteArray {
        val t = (System.nanoTime() / 1_000_000L).toInt() and 0xFFFF
        return byteArrayOf(((t shr 8) and 0xFF).toByte(), (t and 0xFF).toByte(), 0, 0)
    }

    fun newControlBytes(proto: Int, payload: ByteArray = ByteArray(0)): ByteArray = VntPacket().apply {        gateway = false; outer = VntOuterProto.Control; inner = proto
        srcIp = VntPacket.SELF_IP; dstIp = VntPacket.GATEWAY_IP
        initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
        this.payload = payload
    }.build()

    /**
     * 经服务端转发一个包给对端虚拟 IP（中继）。
     *
     * 数据面**必须加密 + 带 12 字节指纹**（与官方客户端一致）。两条硬约束，缺一条就是"能入网但什么都不通"：
     *   ① vnts 只在「目标注册的 client_secret == 数据包 e 位」时才转发（`core/service/client.rs` L80）；
     *   ② 服务端带 `-f`（指纹校验）时**未加密的数据包被直接丢弃**
     *      （`cipher/finger.rs` L21 对非密文返回 `not encrypt`）。
     * 所以两端都要填同一个数据密码：注册 client_secret=true，数据包 e=1 且末尾带 finger。
     */
    fun sendPeerViaIp(micPacket: ByteArray, destVip: Int) {
        val src = if (virtualIp != 0) virtualIp else VntPacket.SELF_IP
        val frame = VntIpFrame.build(src, destVip, 40000, 40001, micPacket)
        val pkt = VntPacket().apply {
            gateway = false; outer = VntOuterProto.IpTurn; inner = VntIpTurnProto.Ipv4
            srcIp = src; dstIp = destVip
            initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
            payload = frame
        }
        if (password.isNotEmpty()) {
            // 【必须用"数据密码派生的密钥"】不能借 serverKey（那是 -W 控制面的会话密钥，
            // 没做 -W 握手时它是 null，会让 wrapPayload 原样返回明文却把包标成 e=1 → 对端解不开）。
            try {
                val key = VntCipher.deriveKey(password)
                pkt.encrypted = true
                pkt.payload = VntCipher.encrypt(frame, key, VntCipher.buildNonce(pkt), VntCipher.fingerHash(token), true)
            } catch (t: Throwable) {
                lastError = "数据面加密失败：" + describeThrowable(t)
                return
            }
        }
        send(pkt.build()); peerPacketsSent++
    }

    /** P2P 直发（打洞成功后用对端公网端点） */
    fun sendRawToPeer(data: ByteArray, host: String, port: Int) {
        if (data.isEmpty()) return
        val s = sock ?: return
        try { s.send(DatagramPacket(data, data.size, InetAddress.getByName(host), port)); p2pRawSent++ }
        catch (e: Exception) { lastError = "P2P 发送失败：" + e.message }
    }

    fun sendPunchBurst(host: String, port: Int, times: Int) {
        for (i in 0 until times) sendRawToPeer(PUNCH_MAGIC.toByteArray(Charsets.US_ASCII), host, port)
    }

    // ---------------- 接收 ----------------

    private fun rxLoop() {
        val buf = ByteArray(65535)
        while (running.get()) {
            val s = sock ?: break
            try {
                val p = DatagramPacket(buf, buf.size)
                s.receive(p)
                recvPackets++
                dispatch(p.data.copyOfRange(0, p.length), p.address.hostAddress ?: "", p.port)
            } catch (_: java.net.SocketTimeoutException) { }
            catch (_: Exception) { if (!running.get()) break }
        }
    }

    /**
     * 虚拟网监听 socket（关键！）：
     * 官方客户端靠虚拟网卡把虚拟网里的包路由进来；我们没有虚拟网卡，就必须**自己起一个 UDP socket
     * 绑定到虚拟 IP 的端口上**，否则对端发来的包永远到不了（表现为"注册成功但收不到数据"）。
     *
     * 监听地址：注册成功后拿到的虚拟 IP；端口：与发送侧一致（40000）。
     */
    private var vnetSock: DatagramSocket? = null
    private var vnetRxThread: Thread? = null

    private fun startVnetListener() {
        if (virtualIp == 0 || vnetSock != null) return
        try {
            val ip = InetAddress.getByName(VntPacket.intToIp(virtualIp))
            val s = DatagramSocket(40000, ip)
            s.soTimeout = 500
            vnetSock = s
            vnetRxThread = Thread {
                val buf = ByteArray(65535)
                while (running.get()) {
                    try {
                        val p = DatagramPacket(buf, buf.size)
                        s.receive(p)
                        // 虚拟网里来的包就是 VntIpFrame（IPv4+UDP），剥掉 IP/UDP 头把载荷交给上层
                        val ipf = VntIpFrame.tryParse(p.data.copyOfRange(0, p.length))
                        if (ipf != null) {
                            peerPacketsReceived++
                            onPeerData?.invoke(ipf.payload, VntPacket.intToIp(ipf.srcIp))
                        }
                    } catch (_: java.net.SocketTimeoutException) { }
                    catch (_: Exception) { if (!running.get()) break }
                }
            }.also { it.isDaemon = true; it.start() }
        } catch (e: Exception) {
            lastError = "虚拟网监听启动失败：" + e.message
        }
    }

    private fun stopVnetListener() {
        try { vnetSock?.close() } catch (_: Exception) {}
        vnetSock = null; vnetRxThread = null
    }

    fun dispatch(data: ByteArray, srcHost: String, srcPort: Int) {
        if (data.size >= 4 && data[0] == 'V'.code.toByte() && data[1] == 'L'.code.toByte() &&
            data[2] == 'P'.code.toByte() && data[3] == '1'.code.toByte()) { punchPacketsReceived++; return }
        if (data.size >= 2 && data[0] == 'M'.code.toByte() && data[1] == 'C'.code.toByte()) {
            p2pRawReceived++; onRawMic?.invoke(data, srcHost, srcPort); return
        }
        val pkt = VntPacket.parse(data) ?: return
        lastRecvAt = System.currentTimeMillis()      // 有包进来就记为"活着"（静默检测用）
        lastPacketInfo = pkt.toString()
        // -W 生效后：服务端回的**控制面**报文也是加密体，先解密再分发。
        // 【务必限制在控制面】数据面（IpTurn/OtherTurn）是用**数据密码**加密的（带 -f 的服务器要求密文），
        // 拿 -W 的会话密钥去解必然失败——老代码在这里直接 return，等于把对端数据包整包丢掉。
        if (pkt.encrypted && serverEncrypted
            && (pkt.outer == VntOuterProto.Service || pkt.outer == VntOuterProto.Control || pkt.outer == VntOuterProto.Error)) {
            val (plain, err) = VntCipher.tryDecrypt(pkt.payload, serverKey ?: return, VntCipher.buildNonce(pkt), VntCipher.fingerHash(token), true)
            if (plain != null) pkt.payload = plain
            else {
                lastError = "服务端加密报文解密失败：" + err
                // 特例（实测真实 vnts 1.2.12 会出现）：SecretHandshakeResponse 是空明封出来的 32 字节加密体，
                // 我们这边解不开它的 tag。但它不携带任何有用信息（明文 0 字节）——服务端此时已经把会话密钥建好了。
                // 所以这里不能因为解不开就卡死：直接推进到"加密就绪"并照常注册，否则注册包永远发不出去。
                if (pkt.inner == VntServiceProto.SecretHandshakeResponse) {
                    handleService(pkt.inner, ByteArray(0))
                    return
                }
                return
            }
        }
        when (pkt.outer) {
            VntOuterProto.Service -> handleService(pkt.inner, pkt.payload)
            VntOuterProto.Control -> handleControl(pkt.inner, pkt.payload, pkt.srcIp)
            VntOuterProto.Error -> { lastError = VntErrorProto.text(pkt.inner); if (pkt.inner == VntErrorProto.TokenError || pkt.inner == VntErrorProto.Disconnect || pkt.inner == VntErrorProto.NoKey) state = State.Failed }
            VntOuterProto.IpTurn -> if (pkt.inner == VntIpTurnProto.Ipv4) handleIpTurn(pkt)
            VntOuterProto.OtherTurn -> if (pkt.inner == VntOtherTurnProto.Punch.toInt()) { } else if (pkt.inner == TRANSPORT_MIC_DATA.toInt()) {
                // 数据面可能被对端加密（带 -f 的服务端要求密文），先解一层
                var body = pkt.payload
                if (pkt.encrypted && password.isNotEmpty()) {
                    val key = VntCipher.deriveKey(password)
                    val fh = VntCipher.fingerHash(token)
                    val nonce = VntCipher.buildNonce(pkt)
                    var plain = VntCipher.tryDecrypt(pkt.payload, key, nonce, fh, true).first
                    if (plain == null) plain = VntCipher.tryDecrypt(pkt.payload, key, nonce, fh, false).first
                    if (plain == null) { lastError = "收到加密的对端数据但解密失败"; return }
                    body = plain
                }
                if (body.isEmpty()) return
                peerPacketsReceived++; onPeerData?.invoke(body, VntPacket.intToIp(pkt.srcIp))
            }
        }
    }

    private fun handleService(inner: Int, payload: ByteArray) {
        when (inner) {
            VntServiceProto.HandshakeResponse -> {
                val r = HandshakeResponse.parse(payload)
                // 服务端加密（-W）自动适配：第一次握手是明文，响应里带 RSA 公钥就自动补发密钥握手并切到加密；
                // 服务端没有公钥（不支持 -W）就一直走明文 —— 两种服务器都能连。
                if (r.secret && serverEncrypt && r.publicKey.isNotEmpty() && !secretHandshakeSent) {
                    serverPubDer = r.publicKey
                    if (sendSecretHandshake()) {
                        // 等 SecretHandshakeResponse 再注册
                        return
                    }
                }
                state = State.HandshakeDone; register()
                if (r.secret && !serverEncrypt) {
                    lastError = "服务端支持加密(-W)，但本机被设为强制明文：链路以明文通信（组网不受影响）。"
                }
            }
            VntServiceProto.SecretHandshakeResponse -> {
                // 第二次握手成功：对称密钥已被服务端接受，可以（加密地）注册了
                state = State.HandshakeDone; register()
            }
            VntServiceProto.RegistrationResponse -> {
                val r = RegistrationResponse.parse(payload)
                virtualIp = r.virtualIp; gatewayIp = r.virtualGateway; netmaskIp = r.virtualNetmask
                publicIp = if (r.publicIp != 0) VntPacket.intToIp(r.publicIp) else ""
                publicPort = r.publicPort
                devices.clear(); devices.addAll(r.devices)
                state = State.Registered; lastError = ""
            }
        }
    }

    private var lastFromVip = 0

    private fun handleControl(inner: Int, payload: ByteArray, fromVip: Int) {
        lastFromVip = fromVip
        when (inner) {
            VntControlProto.Ping -> send(newControlBytes(VntControlProto.Pong))
            VntControlProto.PunchRequest, VntControlProto.PunchResponse -> {
                val pi = PunchInfo.parse(payload); p2pInfo = pi
                if (inner == VntControlProto.PunchRequest && !pi.reply) {
                    val mine = PunchInfo().apply {
                        publicPort = this@VntLiteClient.publicPort; natType = 1; reply = true; localPort = 40000; punchModel = 1
                        if (this@VntLiteClient.publicIp.isNotEmpty()) publicIpList.add(VntPacket.ipToInt(this@VntLiteClient.publicIp))
                    }
                    val replyPkt = VntPacket().apply {
                        gateway = false; outer = VntOuterProto.Control; this.inner = VntControlProto.PunchResponse
                        srcIp = if (virtualIp != 0) virtualIp else VntPacket.SELF_IP
                        dstIp = if (lastFromVip != 0) lastFromVip else VntPacket.GATEWAY_IP
                        initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
                        this.payload = mine.toBytes()
                    }
                    send(replyPkt.build())
                }
            }
            VntControlProto.AddrResponse -> { }
        }
    }

    private fun handleIpTurn(pkt: VntPacket) {
        var frame = pkt.payload
        // 数据面是**加密**的（带 -f 的服务端要求密文，见 sendPeerViaIp）：
        // 带指纹和不带指纹都试一遍，AEAD tag 决定只有一种能成功。
        if (pkt.encrypted) {
            if (password.isEmpty()) return
            val key = VntCipher.deriveKey(password)
            val fh = VntCipher.fingerHash(token)
            val nonce = VntCipher.buildNonce(pkt)
            var plain = VntCipher.tryDecrypt(pkt.payload, key, nonce, fh, true).first
            if (plain == null) plain = VntCipher.tryDecrypt(pkt.payload, key, nonce, fh, false).first
            if (plain == null) { lastError = "收到加密数据但解密失败（指纹/密码不一致？）"; return }
            frame = plain
        }
        val ip = VntIpFrame.tryParse(frame) ?: return
        peerPacketsReceived++
        onPeerData?.invoke(ip.payload, VntPacket.intToIp(pkt.srcIp))
    }

    fun punchTo(targetVip: Int) {
        val pi = PunchInfo().apply {
            publicPort = this@VntLiteClient.publicPort; natType = 1; localPort = 40000; punchModel = 1
            if (this@VntLiteClient.publicIp.isNotEmpty()) publicIpList.add(VntPacket.ipToInt(this@VntLiteClient.publicIp))
        }
        val pkt = VntPacket().apply {
            gateway = false; outer = VntOuterProto.Control; inner = VntControlProto.PunchRequest
            srcIp = if (virtualIp != 0) virtualIp else VntPacket.SELF_IP; dstIp = targetVip
            initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
            payload = pi.toBytes()
        }
        send(pkt.build())
    }

    private fun tickLoop() {
        while (running.get()) {
            try {
                Thread.sleep(1000)
                if (!running.get()) break
                val now = System.currentTimeMillis()
                // 静默太久（连续几次心跳都没回应）就重新握手 —— 官方客户端也走这条路恢复，
                // 只是它靠心跳失败计数；我们直接看"最后一次收到包"的时间，更直接。
                if (state == State.Registered && lastRecvAt > 0 && now - lastRecvAt > SILENCE_RESET_MS) {
                    lastError = "服务端静默超过 ${SILENCE_RESET_MS / 1000} 秒，重新握手"
                    state = State.Handshaking
                    serverKey = null              // serverEncrypted 是由 serverKey 派生的（val），清密钥即可
                    secretHandshakeSent = false
                    sendHandshake(true)
                }
                if (state != State.Registered) sendHandshake(false)
                else if (now - lastHeartbeat >= HEARTBEAT_MS) { lastHeartbeat = now; send(newControlBytes(VntControlProto.Ping, pingPayload())) }
            } catch (_: Exception) { }
        }
    }

    fun statusText(): String = when (state) {
        State.Registered -> "已入网"
        State.HandshakeDone -> "握手完成"
        State.Handshaking -> "握手中"
        State.Failed -> "失败"
        else -> "未启动"
    }
}