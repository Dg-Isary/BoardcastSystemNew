package com.broadcast.vntlite

/**
 * vnt 12 字节报文头 —— 与本仓库 C# 实现（BroadcastSystemNet\VntLite\VntPacket.cs）逐字节一致。
 * [0] 高4位: e(加密)|gw(网关/服务包)|x(扩展)|u(未用)；低4位: 版本(必须 >= 2)
 * [1] 外层协议  [2] 上层协议  [3] 高4位 初始TTL | 低4位 生存TTL
 * [4..8) 源 IP（大端）  [8..12) 目的 IP（大端）
 */
object VntOuterProto { const val Service = 1; const val Error = 2; const val Control = 3; const val IpTurn = 4; const val OtherTurn = 5 }
object VntServiceProto {
    const val RegistrationRequest = 1; const val RegistrationResponse = 2
    const val PullDeviceList = 3; const val PushDeviceList = 4
    const val HandshakeRequest = 5; const val HandshakeResponse = 6
    const val SecretHandshakeRequest = 7; const val SecretHandshakeResponse = 8
    const val ClientStatusInfo = 9
}
object VntControlProto { const val Ping = 1; const val Pong = 2; const val PunchRequest = 3; const val PunchResponse = 4; const val AddrRequest = 5; const val AddrResponse = 6 }
object VntOtherTurnProto { const val Punch = 1 }
object VntIpTurnProto { const val Ipv4 = 4; const val WGIpv4 = 5; const val Ipv4Broadcast = 201 }

object VntErrorProto {
    const val TokenError = 1; const val Disconnect = 2; const val AddressExhausted = 3
    const val IpAlreadyExists = 4; const val InvalidIp = 5; const val NoKey = 6
    fun text(v: Int): String = when (v) {
        TokenError -> "组网令牌错误（token 不对或不在服务端白名单）"
        Disconnect -> "被服务端断开（可能同 token 重复登录）"
        AddressExhausted -> "服务端地址池耗尽（虚拟网 IP 用完了）"
        IpAlreadyExists -> "虚拟 IP 冲突（device_id 重复）"
        InvalidIp -> "虚拟 IP 非法"
        NoKey -> "缺少密钥（服务端要求加密通信）"
        else -> "未知错误码 $v"
    }
}

class VntPacket {
    var encrypted = false
    var gateway = false
    var extended = false
    var version = 2
    var outer = 0
    var inner = 0
    var initTtl = 15
    var ttl = 15
    var srcIp = 0
    var dstIp = 0
    var payload = ByteArray(0)

    fun build(): ByteArray {
        val buf = ByteArray(HEAD + payload.size)
        var flags = version and 0x0F
        if (encrypted) flags = flags or 0x80
        if (gateway) flags = flags or 0x40
        if (extended) flags = flags or 0x20
        buf[0] = flags.toByte()
        buf[1] = outer.toByte()
        buf[2] = inner.toByte()
        buf[3] = (((initTtl and 0x0F) shl 4) or (ttl and 0x0F)).toByte()
        writeU32(buf, 4, srcIp)
        writeU32(buf, 8, dstIp)
        System.arraycopy(payload, 0, buf, HEAD, payload.size)
        return buf
    }

    override fun toString(): String =
        "vnt ${intToIp(srcIp)}→${intToIp(dstIp)} outer=$outer inner=$inner" +
        (if (encrypted) " 加密" else "") + (if (gateway) " 服务包" else "") + " ${payload.size}B"

    companion object {
        const val HEAD = 12
        const val SELF_IP = 0x00000002
        const val GATEWAY_IP = 0x00000001
        const val MAX_TTL = 15

        fun parse(buf: ByteArray): VntPacket? {
            if (buf.size < HEAD) return null
            val v = buf[0].toInt() and 0x0F
            if (v < 2) return null
            val p = VntPacket()
            p.version = v
            p.encrypted = (buf[0].toInt() and 0x80) != 0
            p.gateway = (buf[0].toInt() and 0x40) != 0
            p.extended = (buf[0].toInt() and 0x20) != 0
            p.outer = buf[1].toInt() and 0xFF
            p.inner = buf[2].toInt() and 0xFF
            p.initTtl = (buf[3].toInt() shr 4) and 0x0F
            p.ttl = buf[3].toInt() and 0x0F
            p.srcIp = readU32(buf, 4)
            p.dstIp = readU32(buf, 8)
            p.payload = buf.copyOfRange(HEAD, buf.size)
            return p
        }

        fun ipToInt(ip: String): Int {
            val parts = ip.trim().split('.')
            require(parts.size == 4) { "IP 格式不对: $ip" }
            var v = 0
            for (s in parts) {
                val b = s.toInt()
                require(b in 0..255) { "IP 段越界: $ip" }
                v = (v shl 8) or b
            }
            return v
        }

        fun intToIp(v: Int): String =
            "${(v ushr 24) and 0xFF}.${(v ushr 16) and 0xFF}.${(v ushr 8) and 0xFF}.${v and 0xFF}"

        fun writeU32(b: ByteArray, off: Int, v: Int) {
            b[off] = (v ushr 24).toByte(); b[off + 1] = (v ushr 16).toByte()
            b[off + 2] = (v ushr 8).toByte(); b[off + 3] = v.toByte()
        }

        fun readU32(b: ByteArray, off: Int): Int =
            ((b[off].toInt() and 0xFF) shl 24) or ((b[off + 1].toInt() and 0xFF) shl 16) or
            ((b[off + 2].toInt() and 0xFF) shl 8) or (b[off + 3].toInt() and 0xFF)
    }
}