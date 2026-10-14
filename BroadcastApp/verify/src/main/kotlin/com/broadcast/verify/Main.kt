package com.broadcast.verify

import com.broadcast.vntlite.*
import kotlin.system.exitProcess

/**
 * vnt-lite（Kotlin 参考实现）全功能自检。
 * 用法：
 *   java -jar app.jar                 仅跑离线单元验证（不需要服务端）
 *   java -jar app.jar 127.0.0.1:29873 额外跑"真机联网验证"：注册 → 设备表 → 中继 AEAD 双向
 */
private var pass = 0
private var fail = 0

private fun check(name: String, ok: Boolean, detail: String? = null) {
    if (ok) { pass++; println("[PASS] $name" + (if (detail != null) "  ($detail)" else "")) }
    else { fail++; println("[FAIL] $name" + (if (detail != null) "  → $detail" else "")) }
}

fun main(args: Array<String>) {
    println("=== vnt-lite Kotlin 参考实现 · 全功能验证 ===")
    println("JVM: " + System.getProperty("java.version"))

    // ---------- 1. 报文头 ----------
    val p = VntPacket().apply {
        outer = VntOuterProto.IpTurn; inner = VntIpTurnProto.Ipv4
        srcIp = VntPacket.ipToInt("10.26.0.9"); dstIp = VntPacket.ipToInt("10.26.0.8")
        payload = byteArrayOf(1, 2, 3)
    }
    val pb = p.build()
    val pr = VntPacket.parse(pb)
    check("报文头 12 字节 + 往返一致",
        pb.size == 12 + 3 && pr != null && pr.outer == VntOuterProto.IpTurn && pr.inner == VntIpTurnProto.Ipv4 &&
        pr.srcIp == VntPacket.ipToInt("10.26.0.9") && pr.dstIp == VntPacket.ipToInt("10.26.0.8") && pr.payload.contentEquals(byteArrayOf(1, 2, 3)),
        pr?.toString())
    check("IP 字符串↔整数互转", VntPacket.intToIp(VntPacket.ipToInt("192.168.1.200")) == "192.168.1.200")

    // ---------- 2. protobuf 消息 ----------
    val hs = HandshakeRequest().apply { version = "1.2.16"; secret = true; keyFinger = "abc" }
    val hs2 = HandshakeRequest.parse(hs.toBytes())
    check("HandshakeRequest 往返", hs2.version == "1.2.16" && hs2.secret && hs2.keyFinger == "abc")
    val rr = RegistrationRequest().apply { token = "t"; deviceId = "d"; name = "n"; version = "1.2.16"; allowIpChange = true; clientSecret = true; clientSecretHash = ByteArray(16) { 7 } }
    val rrBytes = rr.toBytes()
    check("RegistrationRequest 编码非空且含 token", rrBytes.isNotEmpty() && String(rrBytes, Charsets.ISO_8859_1).contains("t"))
    val pi = PunchInfo().apply { publicIpList.add(VntPacket.ipToInt("1.2.3.4")); publicPort = 5000; natType = 1; punchModel = 1; localPort = 40000 }
    val pi2 = PunchInfo.parse(pi.toBytes())
    check("PunchInfo 往返（公网端点/NAT/端口）", pi2.publicIpList.size == 1 && VntPacket.intToIp(pi2.publicIpList[0]) == "1.2.3.4" && pi2.publicPort == 5000L && pi2.punchModel == 1L, pi2.describe())
    val di = DeviceInfo().apply { name = "phone"; virtualIp = VntPacket.ipToInt("10.26.0.5"); deviceStatus = 0; clientSecret = false }
    val di2 = DeviceInfo.parse(di.toBytes())
    check("DeviceInfo 往返", di2.name == "phone" && VntPacket.intToIp(di2.virtualIp) == "10.26.0.5" && !di2.clientSecret)

    // ---------- 3. vnt 客户端加密 ----------
    val k32 = VntCipher.deriveKey("0123456789abcdef0123456789abcdef")
    val k16 = VntCipher.deriveKey("short")
    check("数据密钥 = SHA256(密码)，长密码 32 字节 / 短密码 16 字节", k32.size == 32 && k16.size == 16)
    val fh = VntCipher.fingerHash("tok-1")
    val nz = VntCipher.buildNonce(p)
    check("AEAD nonce = 包头字段（源/目的IP + 协议 + 标志 + TTL）", nz.size == 12 && nz[8] == VntOuterProto.IpTurn.toByte() && nz[9] == VntIpTurnProto.Ipv4.toByte())
    val enc = VntCipher.encrypt("hello-vnt".toByteArray(), k32, nz, fh, true)
    // 与服务端 SecretBody 一致：明文 = data‖random4（random 是明文的一部分），
    // 所以 密文长度 = 数据长度 + 4；密文体 = 密文‖tag16‖finger12 = 数据长度 + 32
    check("密文体长度 = 明文数据 + 32（tag16+random4+finger12，random 计入密文）", enc.size == 9 + 32, "实际 ${enc.size}")
    val (dec, derr) = VntCipher.tryDecrypt(enc, k32, nz, fh, true)
    check("加密→解密往返正确（AAD 为空，解密后丢弃末尾 random4）", dec != null && String(dec) == "hello-vnt", derr)
    val bad = enc.copyOf(); bad[0] = (bad[0].toInt() xor 0x5A).toByte()
    val (d2, e2) = VntCipher.tryDecrypt(bad, k32, nz, fh, true)
    check("篡改密文被 finger 拦住", d2 == null && e2 != null && e2.contains("finger"), e2)
    val badF = enc.copyOf(); badF[badF.size - 1] = (badF[badF.size - 1].toInt() xor 1).toByte()
    check("伪造 finger 被拒绝", VntCipher.tryDecrypt(badF, k32, nz, fh, true).first == null)

    // ---------- 4. 话筒包（MicProtocol） ----------
    val micKey = ByteArray(32) { (it + 1).toByte() }
    val mic = MicProtocol.buildPacket(MicProtocol.TYPE_OGG, 7, 1L, 20, "ogg-page".toByteArray(), micKey)
    check("话筒包 = 20 字节头 + 密文 + tag", mic.size == 20 + 8 + 16, "实际 ${mic.size}")
    val (opened, oerr) = MicProtocol.tryOpenPacket(mic, micKey)
    check("话筒包 AEAD 往返（AAD=包头、nonce=sid‖seq）",
        opened != null && String(opened.payload) == "ogg-page" && opened.sid == 7 && opened.seq == 1L && opened.frameMs == 20, oerr)
    // 4.0.0：seq 扩到 8 字节 —— > 2^32 的序号必须完整往返（数据通道的持久化 64 位计数器靠它）
    val bigSeq = 0x0123456789ABCDEFL
    val big = MicProtocol.buildPacket(MicProtocol.TYPE_API, 0, bigSeq, 0, "x".toByteArray(), micKey)
    val (bigOpened, bigErr) = MicProtocol.tryOpenPacket(big, micKey)
    check("64 位序号完整往返（> 2^32 的 seq 不截断）", bigOpened != null && bigOpened.seq == bigSeq, bigErr ?: "")
    check("协议版本 2、包头 20 字节",
        MicProtocol.VERSION.toInt() == 2 && MicProtocol.HEADER_SIZE == 20,
        "ver=${MicProtocol.VERSION} header=${MicProtocol.HEADER_SIZE}")
    val micBad = mic.copyOf(); micBad[MicProtocol.HEADER_SIZE] = (micBad[MicProtocol.HEADER_SIZE].toInt() xor 0xFF).toByte()
    check("话筒包篡改被拒绝", MicProtocol.tryOpenPacket(micBad, micKey).first == null)
    check("错误密钥无法解开话筒包", MicProtocol.tryOpenPacket(mic, ByteArray(32) { 9 }).first == null)
    val silence = MicProtocol.buildPacket(MicProtocol.TYPE_SILENCE, 7, 2L, 20, ByteArray(0), micKey)
    check("静音帧（payload 为空）可组包", silence.size == 36)
    val hk = MicProtocol.hkdfSha256("ikm".toByteArray(), ByteArray(32), "info".toByteArray(), 32)
    check("HKDF-SHA256 派生 32 字节且确定性",
        hk.size == 32 && hk.contentEquals(MicProtocol.hkdfSha256("ikm".toByteArray(), ByteArray(32), "info".toByteArray(), 32)))

    // ---------- 5. IPv4/UDP 帧 ----------
    val fr = VntIpFrame.build(VntPacket.ipToInt("10.26.0.9"), VntPacket.ipToInt("10.26.0.8"), 40000, 40001, "abc".toByteArray())
    val fp = VntIpFrame.tryParse(fr)
    check("IPv4+UDP 帧往返 + IP 头校验和为 0",
        fr.size == 28 + 3 && VntIpFrame.checksum(fr, 0, 20) == 0 && fp != null && fp.srcPort == 40000 && fp.dstPort == 40001 && String(fp.payload) == "abc")
    check("非 IPv4 帧被拒绝", VntIpFrame.tryParse(byteArrayOf(0x60, 0, 0, 0)) == null)

    // ---------- 5.5 配对（6 位动态码 + X25519 + HKDF） ----------
    // 5.5.0 X25519 纯 Kotlin 实现，用 RFC 7748 官方测试向量自检：
    //   这是"手机端（Kotlin）与电脑端（BouncyCastle）算出的密钥完全一致"的**唯一硬证据**，
    //   两边都按 RFC 7748，向量对上了就一定互通。老机型没有 XDH 也不影响（不再用系统 JCE）。
    run {
        // §5.2 向量 1
        val k1 = X25519.hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4")
        val u1 = X25519.hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")
        val o1 = X25519.hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")
        check("RFC 7748 §5.2 向量 1：X25519(k,u) 与官方输出一致",
            X25519.scalarMult(k1, u1).contentEquals(o1), X25519.toHex(X25519.scalarMult(k1, u1)))
        // §5.2 向量 2
        val k2 = X25519.hex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d")
        val u2 = X25519.hex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493")
        val o2 = X25519.hex("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957")
        check("RFC 7748 §5.2 向量 2：X25519(k,u) 与官方输出一致",
            X25519.scalarMult(k2, u2).contentEquals(o2), X25519.toHex(X25519.scalarMult(k2, u2)))
        // §6.1 Alice/Bob：私钥 → 公钥，以及双方共享密钥
        val aPriv = X25519.hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
        val aPub = X25519.hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")
        val bPriv = X25519.hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")
        val bPub = X25519.hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
        val shared = X25519.hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742")
        check("RFC 7748 §6.1：Alice 私钥算出的公钥与官方一致", X25519.basePoint(aPriv).contentEquals(aPub))
        check("RFC 7748 §6.1：Bob 私钥算出的公钥与官方一致", X25519.basePoint(bPriv).contentEquals(bPub))
        check("RFC 7748 §6.1：双方共享密钥一致（与官方值相同）",
            X25519.scalarMult(aPriv, bPub).contentEquals(shared) && X25519.scalarMult(bPriv, aPub).contentEquals(shared))
        // clamp：最高位被清、次高位置 1、最低 3 位清 0
        val clamped = X25519.clamp(ByteArray(32) { 0xFF.toByte() })
        check("标量 clamp 符合 RFC 7748（最低 3 位清 0、次高位置 1、最高位清 0）",
            (clamped[0].toInt() and 0x07) == 0 && (clamped[31].toInt() and 0x80) == 0 && (clamped[31].toInt() and 0x40) != 0)
    }
    if (MicPairing.isSupported()) {
        val cli = MicPairing.newKeyPair(); val srv = MicPairing.newKeyPair()
        val nc = MicPairing.newNonce(); val ns = MicPairing.newNonce()
        val code = "123456"
        val kClient = MicPairing.deriveSessionKey(cli.privateKey, srv.publicKey, code, nc, ns)
        val kServer = MicPairing.deriveSessionKey(srv.privateKey, cli.publicKey, code, nc, ns)
        check("配对：两端用 6 位动态码 + X25519 + HKDF 派生出同一把会话密钥", kClient.contentEquals(kServer), "长度 ${kClient.size}")
        val kOther = MicPairing.deriveSessionKey(cli.privateKey, srv.publicKey, "654321", nc, ns)
        check("动态码不同 → 会话密钥不同（拿不到码就解不开）", !kClient.contentEquals(kOther))
        val hello = MicPairing.buildHello(code, "android-x", cli.publicKey, nc)
        check("HELLO 报文含类型/版本/动态码/公钥/随机数",
            hello.contains("\"t\":\"cbs-mic\"") && hello.contains("\"v\":1") && hello.contains("\"code\":\"$code\"") && hello.contains("\"pk\":"))
        val okReply = "{\"t\":\"cbs-mic\",\"v\":1,\"ok\":true,\"sid\":7788,\"ps\":\"${MicPairing.b64(srv.publicKey)}\",\"ns\":\"${MicPairing.b64(ns)}\"}"
        val r = MicPairing.parseReply(okReply)
        check("解析 AUTH_OK（sid/服务端公钥/服务端随机数）", r != null && r.ok && r.sid == 7788 && r.serverPublicKey.size == 32 && r.serverNonce.size == 16)
        val fail = MicPairing.parseReply("{\"t\":\"cbs-mic\",\"v\":1,\"ok\":false,\"reason\":\"bad_code\"}")
        check("解析 AUTH_FAIL（reason 透出给用户）", fail != null && !fail.ok && fail.reason == "bad_code", fail?.reason)
        check("动态码算法与电脑端一致（HMAC-SHA256(seed,\"cbs-mic-code:\"+窗口) % 1000000）",
            MicPairing.codeForWindow("seed-x", 100) == MicPairing.currentCode("seed-x", 100 * 60 + 5),
            MicPairing.currentCode("seed-x", 100 * 60 + 5))
    } else {
        println("[SKIP] 本机 JVM/Android 不支持 X25519(XDH)，跳过配对断言")
    }
    // ---------- 配对答复的 JSON 转义（真机踩过：\u002B 让手机端公钥变 0 字节、配对时好时坏） ----------
    run {
        val key = ByteArray(32) { 0xFB.toByte() }   // 0xFB 的 base64 是 "+/v7"：必然同时含 + 和 /
        val b64 = java.util.Base64.getEncoder().encodeToString(key)
        check("前置：这份 base64 里确实含 +", b64.contains("+"), b64)
        val escaped = "{\"t\":\"cbs-mic\",\"v\":1,\"ok\":true,\"sid\":3,\"ps\":\"" +
            b64.replace("+", "\\u002B") + "\",\"ns\":\"AAAA\"}"
        val rEsc = MicPairing.parseReply(escaped)
        check("配对答复：\\u002B 转义能还原成 32 字节公钥（不再出现 0 字节公钥）",
            rEsc != null && rEsc.serverPublicKey.size == 32, "实际 ${rEsc?.serverPublicKey?.size ?: -1} 字节")
        val plain = "{\"t\":\"cbs-mic\",\"v\":1,\"ok\":true,\"sid\":3,\"ps\":\"" + b64 + "\",\"ns\":\"AAAA\"}"
        val rPlain = MicPairing.parseReply(plain)
        check("配对答复：未转义的 base64 也正常", rPlain != null && rPlain.serverPublicKey.size == 32)
    }

    // ---------- 5.7 数据面加密路径（不联网也能验证：加密+组帧不抛异常、形状正确） ----------
    // 真机曾出现"发送失败：null"（异常消息为 null），所以这里把这一步单独拉出来自检：
    // 它确保 sendPeerViaIp 内部（数据密码派生密钥、AES-GCM、写指纹、组 12 字节头）不会抛异常。
    run {
        val c = VntLiteClient()
        c.token = "test-token"; c.password = "pw-1234567890ab"; c.deviceId = "d"; c.name = "n"
        val payload = "cbs-pair-frame-fake".toByteArray()
        val threw = try { c.sendPeerViaIp(payload, VntPacket.ipToInt("10.26.0.9")); null } catch (t: Throwable) { t }
        check("数据面加密发送（无 socket 时只做加密+组帧）不抛异常",
            threw == null, threw?.let { it.javaClass.simpleName + "：" + (it.message ?: "(无消息)") } ?: "ok")
        // 密钥必须是"数据密码派生"的那把：换一个密码也应当能自己解开自己发的包
        val key = VntCipher.deriveKey("pw-1234567890ab")
        val pkt = VntPacket().apply {
            gateway = false; outer = VntOuterProto.IpTurn; inner = VntIpTurnProto.Ipv4
            srcIp = VntPacket.SELF_IP; dstIp = VntPacket.ipToInt("10.26.0.9")
            initTtl = VntPacket.MAX_TTL; ttl = VntPacket.MAX_TTL
        }
        val frame = VntIpFrame.build(VntPacket.SELF_IP, VntPacket.ipToInt("10.26.0.9"), 40000, 40001, payload)
        val enc = VntCipher.encrypt(frame, key, VntCipher.buildNonce(pkt), VntCipher.fingerHash("test-token"), true)
        check("数据面密文形状 = 明文 + 4(random) + 16(tag) + 12(finger)",
            enc.size == frame.size + 32, "实际 ${enc.size}")
        val back = VntCipher.tryDecrypt(enc, key, VntCipher.buildNonce(pkt), VntCipher.fingerHash("test-token"), true).first
        check("数据面加密→解密往返一致（两端用同一把数据密码密钥）", back != null && back.contentEquals(frame))
    }

    // ---------- 5.6 UDP 配对通道（cbs-pair 加密帧，接口文档 §15.3） ----------
    // 电脑端在 ctl_port 上同时开 TCP 与 UDP 两条配对通道；手机端不建虚拟网卡，只能走 UDP 这条。
    run {
        val pw = "pair-pw-123456"
        val udpKey = MicPairUdp.deriveKey(pw)
        check("UDP 配对密钥 = SHA256(数据密码)（32 字节，两端一致）",
            udpKey.size == 32 && udpKey.contentEquals(MicPairUdp.deriveKey(pw)), "${udpKey.size} 字节")
        check("数据密码不同 → 密钥不同", !udpKey.contentEquals(MicPairUdp.deriveKey(pw + "x")))
        check("数据密码为空 → 不生成密钥（拒绝空密码当密钥）", MicPairUdp.deriveKey("").isEmpty())

        val helloLine = "{\"t\":\"cbs-mic\",\"v\":1,\"code\":\"246810\",\"device_id\":\"android-udp\",\"pk\":\"AAAA\",\"nc\":\"BBBB\"}"
        val frame = MicPairUdp.build(helloLine, udpKey)
        check("配对帧 = \"cbs-pair:\"(9) + nonce(12) + 密文(n) + tag(16)",
            frame.size == 9 + 12 + helloLine.toByteArray(Charsets.UTF_8).size + 16 && MicPairUdp.looksLikePairFrame(frame),
            "实际 ${frame.size} 字节")
        check("帧里**不含**明文动态码（密文上网，中继模式下服务端也读不到）",
            !String(frame, Charsets.ISO_8859_1).contains("246810") && !String(frame, Charsets.ISO_8859_1).contains("android-udp"))
        val (back, uerr) = MicPairUdp.tryUnwrap(frame, udpKey)
        check("配对帧解封往返一致", back == helloLine, uerr)
        check("错误密钥解不开配对帧（AEAD 认证）", MicPairUdp.tryUnwrap(frame, MicPairUdp.deriveKey(pw + "x")).first == null)
        val tampered = frame.copyOf(); tampered[tampered.size - 1] = (tampered[tampered.size - 1].toInt() xor 0x01).toByte()
        check("被篡改的配对帧被拒绝", MicPairUdp.tryUnwrap(tampered, udpKey).first == null)
        check("前缀不符/过短的帧被拒绝",
            MicPairUdp.tryUnwrap("not-cbs-pair!!".toByteArray(), udpKey).first == null &&
            MicPairUdp.tryUnwrap(byteArrayOf(1, 2, 3), udpKey).first == null)

        // 同进程真实 UDP 一问一答（本机回环），证明"发帧→收回复→解封"整条链路在 Kotlin 侧可用
        var serverReply: String? = null
        val srv = java.net.DatagramSocket(0)
        val port = srv.localPort
        val th = Thread {
            try {
                val buf = ByteArray(2048)
                val pkt = java.net.DatagramPacket(buf, buf.size)
                srv.receive(pkt)
                val got = pkt.data.copyOfRange(0, pkt.length)
                val (line, _) = MicPairUdp.tryUnwrap(got, udpKey)
                val ok = line == helloLine
                val reply = "{\"t\":\"cbs-mic\",\"v\":1,\"ok\":true,\"sid\":4242,\"ps\":\"AAAA\",\"ns\":\"BBBB\"}"
                serverReply = if (ok) reply else "BAD"
                val out = MicPairUdp.build(serverReply!!, udpKey)
                srv.send(java.net.DatagramPacket(out, out.size, pkt.address, pkt.port))
            } catch (t: Throwable) { serverReply = "ERR:" + t.message }
        }.also { it.isDaemon = true; it.start() }
        val (replyLine, xerr, gotReply) = MicPairUdp.exchange("127.0.0.1", port, frame, udpKey, 2000)
        check("UDP 一问一答（本机回环）：发出 cbs-pair 加密帧并收到可解封的 AUTH_OK",
            gotReply && replyLine != null && replyLine.contains("\"ok\":true") && replyLine.contains("4242"),
            if (gotReply) (xerr ?: replyLine) else "没有收到回复")
        val r2 = if (replyLine != null) MicPairing.parseReply(replyLine) else null
        check("回复可被 parseReply 解析出 sid", r2 != null && r2.ok && r2.sid == 4242)
        try { srv.close() } catch (_: Throwable) {}

        // 没有服务端时的超时路径：必须给出"没收到回复"的可诊断结论，而不是抛异常
        val (noneLine, noneErr, noneGot) = MicPairUdp.exchange("127.0.0.1", 9, frame, udpKey, 300)
        check("对端不存在时：300ms 超时返回（不抛异常、gotReply=false，供界面提示通道不通）",
            noneLine == null && !noneGot && noneErr != null, noneErr)
    }

    // ---------- 5.5 RSA / 密钥自洽验证（-W 前置，不依赖真实服务器） ----------
    run {
        val tmp = java.io.File(System.getProperty("java.io.tmpdir"), "vnt-rsa-test-" + System.currentTimeMillis()).apply { mkdirs() }
        val (priv, pubDer) = VntRsa.localKey(tmp)
        val pub = VntRsa.importPublicKey(pubDer)
        val token = "test-token-abc"
        val key = ByteArray(32) { (it + 1).toByte() }
        val nonce = byteArrayOf(0, 0, 0, 2, 0, 0, 0, 1, 1, 7, 1, 15)

        val cipher = VntRsa.buildSecretHandshakePayload(pub, token, key, nonce)
        check("SecretHandshakeRequest 密文长度 = 256（RSA-2048）", cipher.size == 256, "实际 " + cipher.size)
        val msg = VntRsa.tryOpenSecretBody(priv, cipher, nonce)
        check("用本机私钥能解开 RsaSecretBody", msg != null)
        if (msg != null) {
            val req = SecretHandshakeRequest.parse(msg)
            check("解出的 token/key 与原文一致", req.token == token && req.key.contentEquals(key),
                "token=" + req.token + " keyLen=" + req.key.size)
        }
        // 密钥持久化：两次 localKey 应得到同一指纹（这是之前 C# 踩过的坑，必须防回归）
        val (_, pubDer2) = VntRsa.localKey(tmp)
        check("RSA 密钥指纹持久化（两次 localKey 同一指纹）", VntRsa.localFinger(pubDer) == VntRsa.localFinger(pubDer2),
            VntRsa.localFinger(pubDer))
        try { tmp.deleteRecursively() } catch (_: Throwable) {}
    }

    // ---------- 6. 联网验证（可选） ----------
    if (args.isNotEmpty()) {
        liveTest(args[0])
    } else {
        println("（未给服务器地址，跳过联网验证；用法：java -jar app.jar 127.0.0.1:29873）")
    }

    println("=== 结果：PASS $pass / FAIL $fail ===")
    exitProcess(if (fail == 0) 0 else 1)
}

/** 真机联网验证：两个客户端注册到同一服务端，A 经服务端中继给 B 发话筒 AEAD 包，B 校验。
 *  支持 -W（服务端加密）：serverEncrypt=true 时会走完整的 RSA 密钥握手流程。 */
private fun liveTest(server: String) {
    println("--- 联网验证：$server ---")
    val micKey = ByteArray(32) { (it + 1).toByte() }
    val keyDir = java.io.File(System.getProperty("java.io.tmpdir"), "vnt-verify-keys").apply { mkdirs() }
    fun newClient(tag: String): VntLiteClient {
        val c = VntLiteClient()
        c.token = "notest"; c.name = "kotlin-$tag"; c.deviceId = java.util.UUID.randomUUID().toString()
        // 数据面模式由"有没有数据密码"决定（不再看 relayPlaintext）：
        //   有密码 → 数据包 e=1 + 12 字节指纹，注册 client_secret=true（**带 -f 的服务器也必须这样**）；
        //   没密码 → 数据包明文 e=0，注册 client_secret=false（只有不带 -f 的服务器能用）。
        // 本用例故意不配密码，所以跑的是明文模式，对应"本地 vnts 不带 -f"。
        c.useFinger = false
        c.serverEncrypt = true           // 打开 -W：走完整 RSA 密钥握手（这是本轮修复要验证的点）
        c.dataPassword = ""              // 没配数据密码 → 用随机对称密钥
        c.keyDir = keyDir                // key_finger 持久化（第一次握手要带指纹）
        return c
    }
    val a = newClient("A"); val b = newClient("B")
    val gotB = ArrayList<ByteArray>(); val gotA = ArrayList<ByteArray>()
    b.onPeerData = { d, from -> gotB.add(d); println("  [B 收到] ${d.size} 字节 from $from") }
    a.onPeerData = { d, from -> gotA.add(d); println("  [A 收到] ${d.size} 字节 from $from") }
    val (oa, ea) = a.start(server, "notest", a.deviceId, a.name); check("A 启动", oa, ea)
    val (ob, eb) = b.start(server, "notest", b.deviceId, b.name); check("B 启动", ob, eb)
    val t0 = System.currentTimeMillis()
    while (System.currentTimeMillis() - t0 < 12000 && (a.state != VntLiteClient.State.Registered || b.state != VntLiteClient.State.Registered)) Thread.sleep(100)
    check("A 注册成功（拿到虚拟 IP，-W 通道）", a.state == VntLiteClient.State.Registered, "${a.statusText()} ${VntPacket.intToIp(a.virtualIp)}")
    check("B 注册成功（拿到虚拟 IP，-W 通道）", b.state == VntLiteClient.State.Registered, "${b.statusText()} ${VntPacket.intToIp(b.virtualIp)}")
    // 注册响应里的设备表是【注册时刻的快照】：后注册的一方能看到先注册的一方（先注册的看不到后来者）
    check("设备表（注册快照）至少一方能看到对方",
        a.devices.any { it.virtualIp == b.virtualIp } || b.devices.any { it.virtualIp == a.virtualIp },
        "A 看到 ${a.devices.size} 台，B 看到 ${b.devices.size} 台（快照语义，属预期）")

    // 中继：A→B、B→A 各发一个话筒 AEAD 包
    var round = 0
    val t1 = System.currentTimeMillis()
    while (System.currentTimeMillis() - t1 < 8000 && (gotB.isEmpty() || gotA.isEmpty())) {
        round++
        a.sendPeerViaIp(MicProtocol.buildPacket(MicProtocol.TYPE_OGG, 1, round.toLong(), 20, "relay-A#$round".toByteArray(), micKey), b.virtualIp)
        b.sendPeerViaIp(MicProtocol.buildPacket(MicProtocol.TYPE_OGG, 2, round.toLong(), 20, "relay-B#$round".toByteArray(), micKey), a.virtualIp)
        Thread.sleep(400)
    }
    val okB = gotB.any { MicProtocol.tryOpenPacket(it, micKey).first != null }
    val okA = gotA.any { MicProtocol.tryOpenPacket(it, micKey).first != null }
    check("中继路径：B 收到并解开 A 的话筒 AEAD 包", okB, "收到 ${gotB.size} 个包，轮次 $round")
    check("中继路径：A 收到并解开 B 的话筒 AEAD 包", okA, "收到 ${gotA.size} 个包")
    println("  统计：A 发 ${a.peerPacketsSent}/收 ${a.peerPacketsReceived}；B 发 ${b.peerPacketsSent}/收 ${b.peerPacketsReceived}；收包总数 A=${a.recvPackets}/B=${b.recvPackets}；错 A=${a.lastError}/B=${b.lastError}")
    a.stop(); b.stop()
}