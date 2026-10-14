package com.broadcast.vntlite

/** 极简 IPv4 + UDP 帧（中继路径用；与 C# 侧 VntIpFrame.cs 一致：20 字节 IP 头 + 8 字节 UDP 头） */
object VntIpFrame {
    const val OVERHEAD = 28

    fun build(srcIp: Int, dstIp: Int, srcPort: Int, dstPort: Int, payload: ByteArray): ByteArray {
        val udpLen = 8 + payload.size
        val totalLen = 20 + udpLen
        val b = ByteArray(totalLen)
        b[0] = 0x45; b[1] = 0; b[2] = (totalLen ushr 8).toByte(); b[3] = totalLen.toByte()
        b[4] = 0; b[5] = 0; b[6] = 0x40; b[7] = 0          // 不分片
        b[8] = 64; b[9] = 17                                // TTL=64, 协议=UDP
        b[10] = 0; b[11] = 0
        VntPacket.writeU32(b, 12, srcIp); VntPacket.writeU32(b, 16, dstIp)
        val ipCk = checksum(b, 0, 20)
        b[10] = (ipCk ushr 8).toByte(); b[11] = ipCk.toByte()
        b[20] = (srcPort ushr 8).toByte(); b[21] = srcPort.toByte()
        b[22] = (dstPort ushr 8).toByte(); b[23] = dstPort.toByte()
        b[24] = (udpLen ushr 8).toByte(); b[25] = udpLen.toByte()
        b[26] = 0; b[27] = 0                                // UDP 校验和 0 = 不校验（IPv4 允许）
        System.arraycopy(payload, 0, b, 28, payload.size)
        return b
    }

    data class Parsed(val srcIp: Int, val dstIp: Int, val srcPort: Int, val dstPort: Int, val payload: ByteArray)

    fun tryParse(b: ByteArray): Parsed? {
        if (b.size < OVERHEAD) return null
        val ver = (b[0].toInt() shr 4) and 0x0F
        if (ver != 4) return null
        val ihl = (b[0].toInt() and 0x0F) * 4
        if (ihl < 20 || b.size < ihl + 8) return null
        if ((b[9].toInt() and 0xFF) != 17) return null
        val total = ((b[2].toInt() and 0xFF) shl 8) or (b[3].toInt() and 0xFF)
        val end = minOf(if (total == 0) b.size else total, b.size)
        val sp = ((b[ihl].toInt() and 0xFF) shl 8) or (b[ihl + 1].toInt() and 0xFF)
        val dp = ((b[ihl + 2].toInt() and 0xFF) shl 8) or (b[ihl + 3].toInt() and 0xFF)
        val udpLen = ((b[ihl + 4].toInt() and 0xFF) shl 8) or (b[ihl + 5].toInt() and 0xFF)
        val dataLen = if (udpLen >= 8) minOf(udpLen - 8, end - ihl - 8) else end - ihl - 8
        if (dataLen <= 0) return null
        return Parsed(VntPacket.readU32(b, 12), VntPacket.readU32(b, 16), sp, dp, b.copyOfRange(ihl + 8, ihl + 8 + dataLen))
    }

    fun checksum(b: ByteArray, off: Int, len: Int): Int {
        var sum = 0; var i = off
        while (i + 1 < off + len) { sum += ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF); i += 2 }
        if (i < off + len) sum += (b[i].toInt() and 0xFF) shl 8
        while (sum shr 16 != 0) sum = (sum and 0xFFFF) + (sum shr 16)
        return sum.inv() and 0xFFFF
    }
}