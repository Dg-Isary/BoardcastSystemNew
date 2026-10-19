package com.broadcast.vntlite

import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * 话筒配对（与电脑端 C# MicPairing.cs / MicProtocol.cs 逐字节一致）。
 *
 * 握手（单行 JSON，UTF-8）：
 *   客户端 → 电脑：{"t":"cbs-mic","v":1,"code":"123456","device_id":"...","pk":"<b64 32B X25519 公钥>","nc":"<b64 16B 随机>"}
 *   电脑 → 客户端：{"t":"cbs-mic","v":1,"ok":true,"sid":N,"ps":"<b64 32B 服务端公钥>","ns":"<b64 16B 服务端随机>"}
 *   失败：        {"t":"cbs-mic","v":1,"ok":false,"reason":"bad_code|too_many|mic_off|busy|expired|version|bad_request"}
 *   断线恢复：     {"t":"cbs-mic","v":1,"resume":true,"sid":N,"token":"<b64>"}
 *
 * 会话密钥（两端必须一致）：
 *   key = HKDF-SHA256(ikm = X25519(我的私钥, 对方公钥), salt = nc ‖ ns ‖ UTF8(动态码), info = "cbs-mic-v1", 32 字节)
 * 之后所有音频包都用 MicProtocol.buildPacket(..., key) 加密（AAD = 16 字节包头）。
 */
object MicPairing {
    const val TYPE = "cbs-mic"
    const val VERSION = 1
    const val HKDF_INFO = "cbs-mic-v1"
    const val CODE_LEN = 6
    const val NONCE_LEN = 16
    private val rng = SecureRandom()

    data class KeyPair(val privateKey: ByteArray, val publicKey: ByteArray)
    data class Reply(val ok: Boolean, val sid: Int, val serverPublicKey: ByteArray, val serverNonce: ByteArray,
                     val reason: String?, val resumed: Boolean, val admin: Boolean = false, val cred: String = "")

    /**
     * 本机是否支持 X25519 —— **永远是 true**。
     *
     * 历史坑（真机实测）：以前这里用 `KeyPairGenerator.getInstance("XDH")`，
     * 而 `NamedParameterSpec("X25519")` / `XECPublicKeySpec` 是 Android 13(API 33) 才有的；
     * 老机型上 `initialize(NamedParameterSpec)` 抛 `No AlgorithmParameterSpec classes are supported`
     * （`getInstance("XDH")` 本身却能成功，所以光靠它判断会误判成"支持"），
     * 结果配对在生成密钥对时就抛异常、HELLO 发不出去，表现为"配对不上"。
     * 现在改成纯 Kotlin 实现（[X25519]），不再依赖系统提供者，任何 Android 版本都能用。
     */
    fun isSupported(): Boolean = true

    fun newKeyPair(): KeyPair {
        val priv = X25519.newPrivateKey(rng)
        return KeyPair(priv, X25519.basePoint(priv))
    }

    fun newNonce(): ByteArray = ByteArray(NONCE_LEN).also { rng.nextBytes(it) }

    fun buildHello(code: String, deviceId: String, publicKey: ByteArray, nonce: ByteArray): String =
        "{\"t\":\"" + TYPE + "\",\"v\":" + VERSION +
        ",\"code\":\"" + jsonEscape(code) + "\"" +
        ",\"device_id\":\"" + jsonEscape(deviceId) + "\"" +
        ",\"pk\":\"" + b64(publicKey) + "\"" +
        ",\"nc\":\"" + b64(nonce) + "\"}"

    fun buildResume(sid: Int, token: String): String =
        "{\"t\":\"" + TYPE + "\",\"v\":" + VERSION + ",\"resume\":true,\"sid\":" + sid +
        ",\"token\":\"" + jsonEscape(token) + "\"}"

    /**
     * 取配对答复里的**设备凭证**（类 cookie）：电脑端在 AUTH_OK 里下发，之后手机每个请求都带上它。
     * 界面上不显示、也不允许复制（用户确认的语义）。
     */
    /** 最近一次解析到的设备凭证（parseReply 时顺手记下，配对成功后取用）。 */
    @Volatile var lastCredential: String? = null

    fun parseCredential(line: String): String? {
        if (line == null || !line.contains("\"ok\":true")) return null
        val v = strField(line, "cred")
        return if (v.isNullOrBlank()) null else v
    }

    /** 管理员免码 HELLO：不含 6 位动态码，电脑端按"设备 ID 是否在管理员名单"判定 */
    fun buildAdminHello(deviceId: String): String =
        "{\"t\":\"" + TYPE + "\",\"v\":" + VERSION + ",\"admin\":true,\"s\":1,\"device_id\":\"" + jsonEscape(deviceId) + "\"}"   // "s":1 = 支持每会话盐（nonce 复用根治）

    fun parseReply(line: String): Reply? {
        if (!line.contains("\"t\":\"" + TYPE + "\"")) return null
        val ok = line.contains("\"ok\":true")
        val sid = intField(line, "sid") ?: 0
        val reason = strField(line, "reason")
        val resumed = line.contains("\"resumed\":true")
        lastCredential = strField(line, "cred")   // 设备凭证（类 cookie）：顺手记下
        val ps = b64d(strField(line, "ps") ?: "") ?: ByteArray(0)
        val ns = b64d(strField(line, "ns") ?: "") ?: ByteArray(0)
        return Reply(ok, sid, ps, ns, reason, resumed, line.contains("\"admin\":true"), strField(line, "cred") ?: "")
    }

    /** 两端一致的会话密钥派生（客户端用服务端公钥，服务端用客户端公钥） */
    fun deriveSessionKey(myPrivate: ByteArray, peerPublic: ByteArray, code: String, clientNonce: ByteArray, serverNonce: ByteArray): ByteArray {
        // 纯 Kotlin X25519（与电脑端 BouncyCastle 同算法，RFC 7748；不依赖系统 JCE）
        require(peerPublic.size == 32) { "服务端公钥长度不对（${peerPublic.size} 字节，应为 32）：重新配对一次" }
        // 全零公钥（低阶点）必须拒绝：RFC 7748 规定这种情况下共享密钥是全零，用它等于没有密钥。
        if (peerPublic.all { it == 0.toByte() }) throw IllegalArgumentException("服务端公钥全零（无效）：重新配对一次")
        val shared = X25519.scalarMult(myPrivate, peerPublic)
        // 退化结果（全零）= 对方公钥是低阶点：不能拿来当密钥，明确报错让上层重试（电脑端每次配对都会换新密钥）
        if (shared.all { it == 0.toByte() }) throw IllegalStateException("X25519 共享密钥为全零（对方公钥是低阶点）：重新配对一次")
        val codeBytes = code.toByteArray(Charsets.UTF_8)
        // salt 顺序必须与电脑端 MicProtocol.DeriveSessionKey 一致：nc ‖ ns ‖ code
        val salt = ByteArray(clientNonce.size + serverNonce.size + codeBytes.size)
        var off = 0
        System.arraycopy(clientNonce, 0, salt, off, clientNonce.size); off += clientNonce.size
        System.arraycopy(serverNonce, 0, salt, off, serverNonce.size); off += serverNonce.size
        System.arraycopy(codeBytes, 0, salt, off, codeBytes.size)
        return hkdf(shared, salt, HKDF_INFO.toByteArray(Charsets.UTF_8), 32)
    }

    /** 6 位动态码（电脑端同算法，可用来对照：HMAC-SHA256(seed,"cbs-mic-code:"+窗口号) % 1000000） */
    fun codeForWindow(seed: String, window: Long): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(seed.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        val m = mac.doFinal(("cbs-mic-code:" + window).toByteArray(Charsets.UTF_8))
        val v = (((m[0].toInt() and 0xFF) shl 24) or ((m[1].toInt() and 0xFF) shl 16) or ((m[2].toInt() and 0xFF) shl 8) or (m[3].toInt() and 0xFF)) and 0x7FFFFFFF
        return String.format("%06d", v % 1000000)
    }

    /**
     * 断线恢复令牌（与电脑端 MicSecurity.ResumeToken 一致）：
     *   token = base64(HMAC-SHA256(会话密钥, "cbs-mic-resume:" + sid))
     * 只有真正握过手的手机才知道会话密钥，所以网络抖动重连时不必再输一次动态码（宽限期内有效）。
     */
    fun resumeToken(sessionKey: ByteArray, sid: Int): String {
        if (sessionKey.isEmpty()) return ""
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(sessionKey, "HmacSHA256"))
        return b64(mac.doFinal(("cbs-mic-resume:" + sid).toByteArray(Charsets.UTF_8)))
    }

    fun currentCode(seed: String, epochSeconds: Long): String = codeForWindow(seed, epochSeconds / 60)

    fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, len: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(if (salt.isEmpty()) ByteArray(32) else salt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)
        mac.init(SecretKeySpec(prk, "HmacSHA256"))
        val out = ByteArray(len); var t = ByteArray(0); var pos = 0; var ctr = 1
        while (pos < len) {
            mac.reset(); mac.update(t); mac.update(info); mac.update(ctr.toByte())
            t = mac.doFinal()
            val n = minOf(t.size, len - pos); System.arraycopy(t, 0, out, pos, n); pos += n; ctr++
        }
        return out
    }

    // ---- 极简 JSON 取值（不依赖 org.json，JVM/Android 通用）----
    private fun strField(json: String, name: String): String? {
        val k = "\"" + name + "\":\""
        val i = json.indexOf(k); if (i < 0) return null
        val s = i + k.length; val e = json.indexOf('"', s); if (e < 0) return null
        return unescape(json.substring(s, e))
    }

    /**
     * JSON 字符串反转义。
     *
     * 【真机踩过的坑】电脑端 System.Text.Json 的默认编码器会把 base64 里的 `+` 写成 `\u002B`
     * （HTML 安全转义）。以前这里直接原样返回，base64 解码就失败 → 公钥变 0 字节 → 配对失败，
     * 而且只有约 76% 的概率命中（base64 里出现 `+` 才触发），表现就是"时好时坏"。
     * 电脑端也已改成不转义输出，这里是第二道保险。
     */
    private fun unescape(s: String): String {
        if (s.indexOf('\\') < 0) return s
        val sb = StringBuilder(s.length)
        var i = 0
        while (i < s.length) {
            val c = s[i]
            if (c != '\\' || i == s.length - 1) { sb.append(c); i++; continue }
            when (val n = s[i + 1]) {
                'u' -> {
                    if (i + 5 < s.length) {
                        val hex = s.substring(i + 2, i + 6)
                        val cp = hex.toIntOrNull(16)
                        if (cp != null) { sb.append(cp.toChar()); i += 6 } else { sb.append(n); i += 2 }
                    } else { sb.append(n); i += 2 }
                }
                'n' -> { sb.append('\n'); i += 2 }
                't' -> { sb.append('\t'); i += 2 }
                'r' -> { sb.append('\r'); i += 2 }
                else -> { sb.append(n); i += 2 }   // \" \\ \/ 等：去掉反斜杠
            }
        }
        return sb.toString()
    }
    private fun intField(json: String, name: String): Int? {
        val k = "\"" + name + "\":"
        val i = json.indexOf(k); if (i < 0) return null
        var s = i + k.length; val sb = StringBuilder()
        while (s < json.length && (json[s].isDigit() || json[s] == '-')) { sb.append(json[s]); s++ }
        return sb.toString().toIntOrNull()
    }
    private fun jsonEscape(s: String) = s.replace("\\", "\\\\").replace("\"", "\\\"")
    fun b64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)
    fun b64d(s: String): ByteArray? = try { Base64.getDecoder().decode(s) } catch (e: Exception) { null }
}