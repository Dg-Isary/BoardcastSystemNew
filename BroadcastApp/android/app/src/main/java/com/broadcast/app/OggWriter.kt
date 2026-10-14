package com.broadcast.app

/**
 * 极简 Ogg 页封装（按 docs\安卓端-Ogg封装参考实现.md）：
 *  · 第一页 OpusHead（BOS 标志），随后每个 Opus 包一页，granule 每页 +960（20ms@48k）
 *  · CRC32 用 Ogg 的多项式 0x04C11DB7（不反射、初值 0、不取反）
 * 电脑端把收到的页原样喂给 ffplay，无需解码代码。
 */
class OggWriter(private val sampleRate: Int = 48000, private val channels: Int = 1) {
    private var serial = (System.currentTimeMillis() and 0x7FFFFFFF).toInt()
    private var pageSeq = 0
    private var granule = 0L
    private var headSent = false

    /** OpusHead 页（每帧先发一次） */
    fun opusHead(preSkip: Int = 312): ByteArray {
        val head = ByteArray(19)
        head[0] = 'O'.code.toByte(); head[1] = 'p'.code.toByte(); head[2] = 'u'.code.toByte(); head[3] = 's'.code.toByte()
        head[4] = 'H'.code.toByte(); head[5] = 'e'.code.toByte(); head[6] = 'a'.code.toByte(); head[7] = 'd'.code.toByte()
        head[8] = 1                                   // 版本
        head[9] = channels.toByte()
        head[10] = preSkip.toByte(); head[11] = (preSkip ushr 8).toByte()
        // 输入采样率（原始率，这里写 48000）
        head[12] = (sampleRate and 0xFF).toByte(); head[13] = ((sampleRate ushr 8) and 0xFF).toByte()
        head[14] = ((sampleRate ushr 16) and 0xFF).toByte(); head[15] = ((sampleRate ushr 24) and 0xFF).toByte()
        head[16] = 0; head[17] = 0                     // 输出增益
        head[18] = 0                                   // 声道映射
        headSent = true
        return page(head, 0x02, 0L)                    // BOS
    }

    /** 一个 Opus 包 → 一页 */
    /**
     * OpusTags 页（注释头）。
     *
     * 【为什么必须有】按 Ogg Opus 规范，BOS 页（OpusHead）之后紧跟着的必须是 OpusTags；
     * ffmpeg 的 ogg 解复用器会把"下一包"当 OpusTags 解析 —— 没有它时，第一个音频包被当成注释头，
     * **ffplay 会直接报错退出**（真机现象：播了几十秒后 `ffplay 退出，已自动重启`，
     * 重启后的进程又没有 OpusHead，于是彻底没声音）。
     */
    fun opusTags(vendor: String = "BroadcastApp"): ByteArray {
        val v = vendor.toByteArray(Charsets.UTF_8)
        val body = ByteArray(8 + 4 + v.size + 4)
        "OpusTags".toByteArray(Charsets.US_ASCII).copyInto(body, 0)
        body[8] = (v.size and 0xFF).toByte(); body[9] = ((v.size ushr 8) and 0xFF).toByte()
        body[10] = ((v.size ushr 16) and 0xFF).toByte(); body[11] = ((v.size ushr 24) and 0xFF).toByte()
        v.copyInto(body, 12)
        val z = 12 + v.size                        // 用户注释条数 = 0
        body[z] = 0; body[z + 1] = 0; body[z + 2] = 0; body[z + 3] = 0
        return page(body, 0x00, 0L)                // 注释头 granule 仍为 0
    }

    fun opusPage(opusPacket: ByteArray, samplesPerFrame: Int = 960): ByteArray {
        if (!headSent) opusHead()
        granule += samplesPerFrame.toLong()
        return page(opusPacket, 0x00, granule)
    }

    private fun page(data: ByteArray, headerType: Int, granulePos: Long): ByteArray {
        val segments = ArrayList<Int>()
        var left = data.size
        while (left >= 255) { segments.add(255); left -= 255 }
        segments.add(left)
        val body = ByteArray(27 + segments.size + data.size)
        body[0] = 'O'.code.toByte(); body[1] = 'g'.code.toByte(); body[2] = 'g'.code.toByte(); body[3] = 'S'.code.toByte()
        body[4] = 0
        body[5] = headerType.toByte()
        for (i in 0 until 8) body[6 + i] = ((granulePos ushr (8 * i)) and 0xFF).toByte()
        for (i in 0 until 4) body[14 + i] = ((serial ushr (8 * i)) and 0xFF).toByte()
        for (i in 0 until 4) body[18 + i] = ((pageSeq ushr (8 * i)) and 0xFF).toByte()
        body[26] = segments.size.toByte()
        var p = 27
        for (s in segments) body[p++] = s.toByte()
        System.arraycopy(data, 0, body, p, data.size)
        val crc = crc32(body)
        for (i in 0 until 4) body[22 + i] = ((crc ushr (8 * i)) and 0xFF).toByte()
        pageSeq++
        return body
    }

    companion object {
        private val TABLE = IntArray(256).also { t ->
            for (i in 0 until 256) {
                var r = i shl 24
                for (j in 0 until 8) r = if (r and 0x80000000.toInt() != 0) (r shl 1) xor 0x04C11DB7 else r shl 1
                t[i] = r
            }
        }
        fun crc32(b: ByteArray): Int {
            var crc = 0
            for (x in b) crc = (crc shl 8) xor TABLE[((crc ushr 24) xor (x.toInt() and 0xFF)) and 0xFF]
            return crc
        }
    }
}
