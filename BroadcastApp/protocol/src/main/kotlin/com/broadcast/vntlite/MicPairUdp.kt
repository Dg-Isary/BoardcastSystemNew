package com.broadcast.vntlite

import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 话筒 UDP 配对通道的帧格式（与电脑端 `MicPairUdp.cs` **逐字节一致**，见《话筒功能接口文档.md》§15.3）：
 *
 *     "cbs-pair:"(9 字节 ASCII) ‖ nonce(12) ‖ 密文(n) ‖ tag(16)
 *     密文 = AES-256-GCM(key, nonce, 明文 = 单行 cbs-mic JSON, AAD = 空)
 *     key  = SHA256(UTF8(数据密码))     ← 与 vnt 的 -w 同一把，手机端从二维码/设置里拿到
 *
 * 为什么必须加密：中继模式下 vnt 层是明文（e=0），服务端能看到载荷，而配对报文里含**6 位动态码**。
 * 手机不建虚拟网卡、没有虚拟网里的 TCP 栈，所以配对帧要**作为 vnt 数据包的载荷**发出去
 * （电脑端 `MicServer.HandleAudioPacket` 会识别 `cbs-pair:` 前缀并就地处理、原路回包）。
 */
object MicPairUdp {
    const val PREFIX = "cbs-pair:"
    const val NONCE = 12
    const val TAG = 16
    private val rng = SecureRandom()

    /** 配对通道密钥 = SHA256(数据密码)；为空（没配数据密码）时返回空数组 = 通道不可用 */
    fun deriveKey(dataPassword: String): ByteArray =
        if (dataPassword.isEmpty()) ByteArray(0) else VntCipher.sha256(dataPassword.toByteArray(Charsets.UTF_8))

    fun looksLikePairFrame(frame: ByteArray): Boolean {
        if (frame.size < PREFIX.length) return false
        for (i in PREFIX.indices) if (frame[i] != PREFIX[i].code.toByte()) return false
        return true
    }

    /** 把一行明文（单行 cbs-mic JSON）封成配对帧 */
    fun build(line: String, key: ByteArray): ByteArray {
        require(key.size == 32) { "配对密钥必须是 32 字节（SHA256(数据密码)）" }
        val plain = line.replace("\r", "").replace("\n", "").toByteArray(Charsets.UTF_8)
        val nonce = ByteArray(NONCE).also { rng.nextBytes(it) }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG * 8, nonce))
        val ctTag = c.doFinal(plain)                     // 注意：doFinal 返回 密文‖tag
        val pre = PREFIX.toByteArray(Charsets.US_ASCII)
        val out = ByteArray(pre.size + NONCE + ctTag.size)
        System.arraycopy(pre, 0, out, 0, pre.size)
        System.arraycopy(nonce, 0, out, pre.size, NONCE)
        System.arraycopy(ctTag, 0, out, pre.size + NONCE, ctTag.size)
        return out
    }

    /** 解封配对帧；失败返回 (null, 原因) */
    fun tryUnwrap(frame: ByteArray, key: ByteArray): Pair<String?, String?> {
        if (key.size != 32) return Pair(null, "配对密钥未配置或长度不对（需要 SHA256(数据密码)）")
        if (!looksLikePairFrame(frame)) return Pair(null, "前缀不是 cbs-pair:")
        if (frame.size < PREFIX.length + NONCE + TAG) return Pair(null, "帧太短")
        val bodyLen = frame.size - PREFIX.length - NONCE - TAG
        val nonce = frame.copyOfRange(PREFIX.length, PREFIX.length + NONCE)
        val ctTag = frame.copyOfRange(PREFIX.length + NONCE, frame.size)
        return try {
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG * 8, nonce))
            Pair(String(c.doFinal(ctTag), Charsets.UTF_8).trim(), null)
        } catch (e: Exception) { Pair(null, "AEAD 校验失败（数据密码不对或被篡改）") }
    }

    /**
     * 一次一问一答（给自检与"直连 UDP 配对口"用）：
     * 把 frame 发到 host:port，等 timeoutMs 收一个回复帧并解封。
     * 返回 Triple(明文行, 错误原因, 是否收到回复)——**不抛异常**，超时也走返回值，
     * 便于界面给出"通道不通"的可诊断提示。
     */
    fun exchange(host: String, port: Int, frame: ByteArray, key: ByteArray, timeoutMs: Int = 2000): Triple<String?, String?, Boolean> {
        var sock: java.net.DatagramSocket? = null
        return try {
            sock = java.net.DatagramSocket()
            sock.soTimeout = timeoutMs
            sock.send(java.net.DatagramPacket(frame, frame.size, java.net.InetAddress.getByName(host), port))
            val buf = ByteArray(4096)
            val pkt = java.net.DatagramPacket(buf, buf.size)
            sock.receive(pkt)
            val got = pkt.data.copyOfRange(0, pkt.length)
            val (line, err) = tryUnwrap(got, key)
            if (line == null) Triple(null, err, true) else Triple(line, null, true)
        } catch (e: java.net.SocketTimeoutException) {
            Triple(null, "超时 ${timeoutMs}ms 没收到配对回复（通道不通或对端没开麦）", false)
        } catch (e: Exception) {
            Triple(null, "发送/接收失败：" + e.message, false)
        } finally { try { sock?.close() } catch (_: Exception) {} }
    }

    /**
     * 管理员免码配对的会话密钥（与电脑端 `MicPairing.AdminMicKey` 一致，两端各自派生、不经过网络）：
     * HKDF-SHA256(ikm = SHA256(数据密码), salt = "cbs-mic-admin", info = "cbs-mic-admin-v1", 32 字节)
     */
    fun adminMicKey(dataPassword: String, sessionSalt: ByteArray? = null): ByteArray =
        if (dataPassword.isEmpty()) ByteArray(0)
        else MicProtocol.hkdfSha256(
            VntCipher.sha256(dataPassword.toByteArray(Charsets.UTF_8)),
            ("cbs-mic-admin".toByteArray(Charsets.UTF_8) + (sessionSalt ?: ByteArray(0))),
            "cbs-mic-admin-v1".toByteArray(Charsets.UTF_8), 32)
}