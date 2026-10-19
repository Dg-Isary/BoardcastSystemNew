package com.broadcast.vntlite

import java.io.ByteArrayOutputStream

/** protobuf 线格式手写实现（与 C# 侧 VntProto.cs 一致，字段号逐个对齐） */
object Pb {
    fun writeVarint(out: ByteArrayOutputStream, v: Long) {
        var x = v
        while (true) {
            val b = (x and 0x7F).toInt()
            x = x ushr 7
            if (x == 0L) { out.write(b); return }
            out.write(b or 0x80)
        }
    }
    private fun key(out: ByteArrayOutputStream, field: Int, wire: Int) = writeVarint(out, ((field shl 3) or wire).toLong())
    fun writeString(out: ByteArrayOutputStream, f: Int, s: String?) {
        if (s == null) return
        val b = s.toByteArray(Charsets.UTF_8); key(out, f, 2); writeVarint(out, b.size.toLong()); out.write(b, 0, b.size)
    }
    fun writeBytes(out: ByteArrayOutputStream, f: Int, b: ByteArray?) {
        if (b == null || b.isEmpty()) return
        key(out, f, 2); writeVarint(out, b.size.toLong()); out.write(b, 0, b.size)
    }
    fun writeBool(out: ByteArrayOutputStream, f: Int, v: Boolean) { key(out, f, 0); writeVarint(out, if (v) 1 else 0) }
    fun writeUInt(out: ByteArrayOutputStream, f: Int, v: Long) { if (v == 0L) return; key(out, f, 0); writeVarint(out, v) }
    fun writeFixed32(out: ByteArrayOutputStream, f: Int, v: Int) {
        if (v == 0) return
        key(out, f, 5)
        out.write(v and 0xFF); out.write((v ushr 8) and 0xFF); out.write((v ushr 16) and 0xFF); out.write((v ushr 24) and 0xFF)
    }

    class Reader(private val b: ByteArray, private var pos: Int = 0) {
        val exhausted: Boolean get() = pos >= b.size
        fun varint(): Long {
            var shift = 0; var r = 0L
            while (true) {
                if (pos >= b.size) throw IllegalStateException("varint 越界")
                val x = b[pos++].toInt() and 0xFF
                r = r or ((x and 0x7F).toLong() shl shift)
                if (x and 0x80 == 0) return r
                shift += 7
                if (shift > 63) throw IllegalStateException("varint 过长")
            }
        }
        fun bytes(): ByteArray { val n = varint().toInt(); if (pos + n > b.size) throw IllegalStateException("bytes 越界"); val r = b.copyOfRange(pos, pos + n); pos += n; return r }
        fun string(): String = String(bytes(), Charsets.UTF_8)
        fun fixed32(): Int {
            if (pos + 4 > b.size) throw IllegalStateException("fixed32 越界")
            val v = (b[pos].toInt() and 0xFF) or ((b[pos + 1].toInt() and 0xFF) shl 8) or
                    ((b[pos + 2].toInt() and 0xFF) shl 16) or ((b[pos + 3].toInt() and 0xFF) shl 24)
            pos += 4; return v
        }
        fun skip(wire: Int) {
            when (wire) {
                0 -> varint()
                1 -> pos += 8
                2 -> { val n = varint().toInt(); pos += n }
                5 -> pos += 4
                else -> throw IllegalStateException("不支持的 wire type $wire")
            }
        }
        fun nextField(): Pair<Int, Int>? {
            if (exhausted) return null
            val k = varint().toInt()
            return Pair(k ushr 3, k and 7)
        }
    }

    class Writer { private val o = ByteArrayOutputStream(); fun out(): ByteArrayOutputStream = o; fun toBytes(): ByteArray = o.toByteArray() }
}

class HandshakeRequest { var version = "1.2.16"; var secret = false; var keyFinger = ""
    fun toBytes(): ByteArray { val w = Pb.Writer(); Pb.writeString(w.out(), 1, version); Pb.writeBool(w.out(), 2, secret); Pb.writeString(w.out(), 3, keyFinger); return w.toBytes() }
    companion object { fun parse(b: ByteArray): HandshakeRequest { val m = HandshakeRequest(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) { 1 -> m.version = r.string(); 2 -> m.secret = r.varint() != 0L; 3 -> m.keyFinger = r.string(); else -> r.skip(f.second) } }
        return m } } }

class HandshakeResponse { var version = ""; var secret = false; var publicKey = ByteArray(0); var keyFinger = ""
    companion object { fun parse(b: ByteArray): HandshakeResponse { val m = HandshakeResponse(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) { 1 -> m.version = r.string(); 2 -> m.secret = r.varint() != 0L; 3 -> m.publicKey = r.bytes(); 4 -> m.keyFinger = r.string(); else -> r.skip(f.second) } }
        return m } } }

class SecretHandshakeRequest { var token = ""; var key = ByteArray(0)
    fun toBytes(): ByteArray { val w = Pb.Writer(); val o = w.out()
        Pb.writeString(o, 1, token); Pb.writeBytes(o, 2, key); return w.toBytes() }
    companion object { fun parse(b: ByteArray): SecretHandshakeRequest { val m = SecretHandshakeRequest(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) { 1 -> m.token = r.string(); 2 -> m.key = r.bytes(); else -> r.skip(f.second) } }
        return m } } }

class RegistrationRequest {
    var token = ""; var deviceId = ""; var name = ""; var isFast = false; var version = "1.2.16"
    var virtualIp = 0; var allowIpChange = true; var clientSecret = false; var clientSecretHash = ByteArray(0)
    fun toBytes(): ByteArray { val w = Pb.Writer(); val o = w.out()
        Pb.writeString(o, 1, token); Pb.writeString(o, 2, deviceId); Pb.writeString(o, 3, name)
        Pb.writeBool(o, 4, isFast); Pb.writeString(o, 5, version); Pb.writeFixed32(o, 6, virtualIp)
        Pb.writeBool(o, 7, allowIpChange); Pb.writeBool(o, 8, clientSecret); Pb.writeBytes(o, 9, clientSecretHash)
        return w.toBytes() }
}

class DeviceInfo { var name = ""; var virtualIp = 0; var deviceStatus = 0L; var clientSecret = false; var wireguard = false
    fun toBytes(): ByteArray { val w = Pb.Writer(); val o = w.out()
        Pb.writeString(o, 1, name); Pb.writeFixed32(o, 2, virtualIp); Pb.writeUInt(o, 3, deviceStatus)
        Pb.writeBool(o, 4, clientSecret); Pb.writeBool(o, 6, wireguard); return w.toBytes() }
    companion object { fun parse(b: ByteArray): DeviceInfo { val m = DeviceInfo(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) {
            1 -> m.name = r.string(); 2 -> m.virtualIp = r.fixed32(); 3 -> m.deviceStatus = r.varint()
            4 -> m.clientSecret = r.varint() != 0L; 5 -> r.bytes(); 6 -> m.wireguard = r.varint() != 0L; else -> r.skip(f.second) } }
        return m } } }

class RegistrationResponse { var virtualIp = 0; var virtualGateway = 0; var virtualNetmask = 0; var epoch = 0L
    var publicIp = 0; var publicPort = 0L; val devices = ArrayList<DeviceInfo>()
    companion object { fun parse(b: ByteArray): RegistrationResponse { val m = RegistrationResponse(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) {
            1 -> m.virtualIp = r.fixed32(); 2 -> m.virtualGateway = r.fixed32(); 3 -> m.virtualNetmask = r.fixed32()
            4 -> m.epoch = r.varint(); 5 -> m.devices.add(DeviceInfo.parse(r.bytes())); 6 -> m.publicIp = r.fixed32()
            7 -> m.publicPort = r.varint(); 8 -> r.bytes(); else -> r.skip(f.second) } }
        return m } } }

class PunchInfo {
    val publicIpList = ArrayList<Int>(); var publicPort = 0L; var publicPortRange = 0L; var natType = 0L
    var reply = false; var localIp = 0; var localPort = 0L; var ipv6 = ByteArray(0); var ipv6Port = 0L
    var tcpPort = 0L; val udpPorts = ArrayList<Long>(); val publicPorts = ArrayList<Long>()
    var publicTcpPort = 0L; var punchModel = 0L
    fun toBytes(): ByteArray { val w = Pb.Writer(); val o = w.out()
        for (ip in publicIpList) Pb.writeFixed32(o, 2, ip)
        Pb.writeUInt(o, 3, publicPort); Pb.writeUInt(o, 4, publicPortRange); Pb.writeUInt(o, 5, natType)
        Pb.writeBool(o, 6, reply); Pb.writeFixed32(o, 7, localIp); Pb.writeUInt(o, 8, localPort)
        Pb.writeBytes(o, 9, ipv6); Pb.writeUInt(o, 10, ipv6Port); Pb.writeUInt(o, 11, tcpPort)
        for (p in udpPorts) Pb.writeUInt(o, 12, p); for (p in publicPorts) Pb.writeUInt(o, 13, p)
        Pb.writeUInt(o, 14, publicTcpPort); Pb.writeUInt(o, 15, punchModel); return w.toBytes() }
    fun describe(): String = "公网=${publicIpList.joinToString(",") { VntPacket.intToIp(it) }}:$publicPort NAT=$natType reply=$reply 本地=${VntPacket.intToIp(localIp)}:$localPort"
    companion object { fun parse(b: ByteArray): PunchInfo { val m = PunchInfo(); val r = Pb.Reader(b)
        while (true) { val f = r.nextField() ?: break; when (f.first) {
            2 -> m.publicIpList.add(r.fixed32()); 3 -> m.publicPort = r.varint(); 4 -> m.publicPortRange = r.varint()
            5 -> m.natType = r.varint(); 6 -> m.reply = r.varint() != 0L; 7 -> m.localIp = r.fixed32()
            8 -> m.localPort = r.varint(); 9 -> m.ipv6 = r.bytes(); 10 -> m.ipv6Port = r.varint()
            11 -> m.tcpPort = r.varint(); 12 -> m.udpPorts.add(r.varint()); 13 -> m.publicPorts.add(r.varint())
            14 -> m.publicTcpPort = r.varint(); 15 -> m.punchModel = r.varint(); else -> r.skip(f.second) } }
        return m } } }