package com.broadcast.vntlite

import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 话筒（远程喊话）音频包 —— 与 C# 侧 BroadcastSystemNet\MicProtocol.cs 逐字节一致。
 * **4.0.0 起为 20 字节包头**：magic(2)='M''C'  version(1)=2  type(1)  sid(4)  **seq(8)**  frame_ms(2)  payload_len(2)，全部小端
 * AEAD：AES-GCM，nonce = sid(4B 小端)‖seq(8B 小端)，**AAD = 20 字节包头**，报文 = 包头‖密文‖tag(16)
 * type：1=音频帧  2=静音帧（payload 必须为空）  3=Ogg 页（安卓端推荐，PC 端直接转 ffplay）
 *
 * 【为什么 4.0.0 把 seq 从 4 字节扩到 8 字节】原先后 4 字节恒为 0，nonce 实际只有 32 位可变空间；
 * 而数据通道（sid=0）用的是**由数据密码派生的静态密钥**（所有手机共用一把），32 位空间下多次重启的
 * 生日碰撞不可忽略 = AES-GCM nonce 复用（可还原明文、甚至伪造合法报文）。扩到 8 字节后 nonce 用满
 * 12 字节，且数据通道的 seq 改为**每安装持久化的 64 位单调计数器**（见 DataSeqStore）。
 * 这是**不兼容改动**：两端必须同时更新（电脑端 MicProtocol.Version 同步为 2）。
 */
object MicProtocol {
    const val VERSION: Byte = 2
    const val TYPE_AUDIO: Byte = 1
    const val TYPE_SILENCE: Byte = 2
    const val TYPE_OGG: Byte = 3
    /** 接口请求/响应（cbs-api）：载荷是 UTF-8 JSON，走与音频同一条已配对加密通道。 */
    const val TYPE_API: Byte = 4
    const val HEADER_SIZE = 20
    const val TAG_SIZE = 16
    /**
     * 单包载荷上限（与电脑端 MicProtocol.MaxPayload 保持一致）。
     * 加入 cbs-api 后从 1024 放大到 60000：接口响应动辄几 KB，而"不做分片"能省掉两端一整套拼接逻辑。
     */
    const val MAX_PAYLOAD = 60000
    val MAGIC = byteArrayOf(0x4D, 0x43)

    /**
     * 会话内**统一**的包序号（音频帧与接口请求共用同一个递增空间）。
     *
     * 电脑端按会话做防重放（窗口内序号必须递增）。如果音频与接口各用一套计数器，
     * 两组序号互相穿插就会让其中一组整批被判成"重放"丢弃 —— 真机踩过：手机显示已发上千帧，
     * 电脑端 `Ogg 0 页`、播放器从未启动、状态里的"重放"数字一直涨。
     */
    // 【nonce 复用 · 4.0.0 正式根治】AEAD 的 nonce = sid‖seq。会话通道（sid>0）的密钥含
    // nc‖ns‖动态码、**每会话唯一**，所以那里的 seq 只需会话内递增；而**数据通道（sid=0）**用的是
    // 由数据密码派生的**静态密钥**（所有手机共用一把），必须靠"seq 空间跨重启、跨设备都不重叠"
    // 来保证 nonce 不重复 —— 见下面的 DataSeqStore。
    private val sessionSeq = java.util.concurrent.atomic.AtomicLong(randomSeqStart())
    fun nextSessionSeq(): Long = sessionSeq.incrementAndGet()

    /** 62 位正随机起点（留出递增余量，且永不为负）。 */
    private fun randomSeqStart(): Long = java.security.SecureRandom().nextLong() ushr 2

    /**
     * 数据通道（sid=0）专用的**持久化** 64 位单调计数器。
     *
     * 为什么必须持久化：数据通道的密钥是静态的（同一数据密码 → 同一把密钥），若 seq 每次启动都从
     * 同一处开始，App 重启后就会与上次的 (key, nonce) 重复 = AES-GCM nonce 复用（可还原明文、
     * 甚至伪造合法报文）。三件事一起才成立：
     *   · 跨重启不重叠 —— 计数器落盘、只增不减；
     *   · 跨手机不重叠 —— 每台安装的随机起点不同；
     *   · 重装/清数据也不重叠 —— 会得到一个新的随机起点。
     *
     * 持久化由宿主注入（安卓端写 SharedPreferences；verify 等纯 JVM 场景不注入则退化为内存计数器）。
     */
    object DataSeqStore {
        private var counter: Long = 0
        private var save: ((Long) -> Unit)? = null

        /** 宿主启动时调用：[persisted] = 上次落盘的值（没有传 null）；[save] = 写回函数。 */
        fun init(persisted: Long?, save: (Long) -> Unit) {
            this.save = save
            counter = if (persisted != null && persisted > 0) persisted else randomSeqStart()
            save(counter)
        }

        /** 下一条数据通道消息的 seq：单调递增并立刻写回。 */
        fun next(): Long {
            if (counter <= 0) counter = randomSeqStart()   // 未 init 时也要有随机起点，绝不能从 0 开始
            counter += 1
            save?.invoke(counter)
            return counter
        }

        val current: Long get() = counter
    }

    data class Packet(val type: Byte, val sid: Int, val seq: Long, val frameMs: Int, val payload: ByteArray)

    /** 解析出来的包头。seq 是 64 位、放不进 IntArray，所以用数据类而不是原来的 IntArray。 */
    data class Header(val type: Int, val sid: Int, val seq: Long, val frameMs: Int, val payloadLen: Int)

    fun buildHeader(type: Byte, sid: Int, seq: Long, frameMs: Int, payloadLen: Int): ByteArray {
        val h = ByteArray(HEADER_SIZE)
        h[0] = MAGIC[0]; h[1] = MAGIC[1]; h[2] = VERSION; h[3] = type
        le32(h, 4, sid); le64(h, 8, seq); le16(h, 16, frameMs); le16(h, 18, payloadLen)
        return h
    }

    fun parseHeader(buf: ByteArray): Pair<Header?, String?> {
        if (buf.size < HEADER_SIZE) return Pair(null, "包太短")
        if (buf[0] != MAGIC[0] || buf[1] != MAGIC[1]) return Pair(null, "magic 不符")
        if (buf[2] != VERSION) return Pair(null, "协议版本不符")
        val type = buf[3].toInt()
        if (type != TYPE_AUDIO.toInt() && type != TYPE_SILENCE.toInt() && type != TYPE_OGG.toInt() && type != TYPE_API.toInt()) return Pair(null, "类型非法")
        val sid = le32r(buf, 4); val seq = le64r(buf, 8)
        val frameMs = le16r(buf, 16); val len = le16r(buf, 18)
        if (len > MAX_PAYLOAD) return Pair(null, "载荷过长")
        if (type == TYPE_SILENCE.toInt() && len != 0) return Pair(null, "静音帧不该带载荷")
        if (frameMs != 0 && frameMs != 10 && frameMs != 20 && frameMs != 40 && frameMs != 60) return Pair(null, "帧长非法")
        return Pair(Header(type, sid, seq, frameMs, len), null)
    }

    fun buildPacket(type: Byte, sid: Int, seq: Long, frameMs: Int, payload: ByteArray?, key: ByteArray): ByteArray {
        val body = payload ?: ByteArray(0)
        val header = buildHeader(type, sid, seq, frameMs, body.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_SIZE * 8, nonce(sid, seq)))
        cipher.updateAAD(header)
        val ct = cipher.doFinal(body)
        val out = ByteArray(HEADER_SIZE + ct.size)
        System.arraycopy(header, 0, out, 0, HEADER_SIZE)
        System.arraycopy(ct, 0, out, HEADER_SIZE, ct.size)
        return out
    }

    fun tryOpenPacket(packet: ByteArray?, key: ByteArray): Pair<Packet?, String?> {
        if (packet == null) return Pair(null, "空包")
        val (h, err) = parseHeader(packet)
        if (h == null) return Pair(null, err ?: "包头解析失败")
        if (packet.size != HEADER_SIZE + h.payloadLen + TAG_SIZE) return Pair(null, "长度与头不符")
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_SIZE * 8, nonce(h.sid, h.seq)))
            cipher.updateAAD(packet.copyOfRange(0, HEADER_SIZE))
            val plain = cipher.doFinal(packet, HEADER_SIZE, h.payloadLen + TAG_SIZE)
            Pair(Packet(h.type.toByte(), h.sid, h.seq, h.frameMs, plain), null)
        } catch (e: Exception) { Pair(null, "AEAD 校验失败（伪造或篡改）") }
    }

    fun nonce(sid: Int, seq: Long): ByteArray {
        val n = ByteArray(12)
        le32(n, 0, sid)
        le64(n, 4, seq)
        return n
    }

    /** HKDF-SHA256（Extract+Expand），用于把 X25519 共享密钥派生成会话密钥 */
    fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray, len: Int): ByteArray {
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

    private fun le16(b: ByteArray, off: Int, v: Int) { b[off] = v.toByte(); b[off + 1] = (v ushr 8).toByte() }
    private fun le32(b: ByteArray, off: Int, v: Int) { for (i in 0 until 4) b[off + i] = (v ushr (8 * i)).toByte() }
    private fun le64(b: ByteArray, off: Int, v: Long) { for (i in 0 until 8) b[off + i] = ((v ushr (8 * i)) and 0xFF).toByte() }
    private fun le16r(b: ByteArray, off: Int) = (b[off].toInt() and 0xFF) or ((b[off + 1].toInt() and 0xFF) shl 8)
    private fun le32r(b: ByteArray, off: Int) = (b[off].toInt() and 0xFF) or ((b[off + 1].toInt() and 0xFF) shl 8) or
            ((b[off + 2].toInt() and 0xFF) shl 16) or ((b[off + 3].toInt() and 0xFF) shl 24)
    private fun le64r(b: ByteArray, off: Int): Long {
        var r = 0L
        for (i in 0 until 8) r = r or ((b[off + i].toLong() and 0xFFL) shl (8 * i))
        return r
    }
}