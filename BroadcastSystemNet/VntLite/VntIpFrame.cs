using System;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>IpTurn 层协议号（外层 = IpTurn(4)）：vnt\src\protocol\ip_turn_packet.rs</summary>
    public static class VntIpTurnProtocol
    {
        public const byte Ipv4 = 4;
        public const byte WGIpv4 = 5;
        public const byte Ipv4Broadcast = 201;
    }

    /// <summary>
    /// 自己造最小 IPv4 + UDP 帧，用来把我们的数据（话筒音频）承载在 vnt 的 <c>IpTurn/Ipv4</c> 通道上：
    /// 服务端会按目的虚拟 IP 把它转给对端；对端 vnt-lite 再剥掉 IP/UDP 头取出载荷。
    /// **两端都不需要虚拟网卡（TUN）**，也不需要管理员权限。
    /// 头部开销固定 28 字节（20 IP + 8 UDP，均无选项）。
    /// </summary>
    public static class VntIpFrame
    {
        public const int IpHeaderLen = 20;
        public const int UdpHeaderLen = 8;
        public const int Overhead = IpHeaderLen + UdpHeaderLen;
        public const byte ProtoUdp = 17;

        static int _id = 1;

        /// <summary>构造 IPv4+UDP 帧。IP 用 VntPacket 的 uint 表示（与 UintToIp/IpToUint 一致）。</summary>
        public static byte[] Build(uint srcIp, uint dstIp, ushort srcPort, ushort dstPort, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            int total = Overhead + payload.Length;
            var b = new byte[total];
            b[0] = 0x45;                                  // version 4 + IHL 5（20 字节头）
            b[1] = 0;                                     // TOS
            WriteU16(b, 2, (ushort)total);                // 总长度
            WriteU16(b, 4, (ushort)(System.Threading.Interlocked.Increment(ref _id) & 0xFFFF));
            WriteU16(b, 6, 0);                            // flags/fragment
            b[8] = 64;                                    // TTL
            b[9] = ProtoUdp;
            WriteU16(b, 10, 0);                           // 校验和先置 0
            WriteU32(b, 12, srcIp);
            WriteU32(b, 16, dstIp);
            WriteU16(b, 10, Checksum(b, 0, IpHeaderLen));
            WriteU16(b, 20, srcPort);
            WriteU16(b, 22, dstPort);
            WriteU16(b, 24, (ushort)(UdpHeaderLen + payload.Length));
            WriteU16(b, 26, 0);                           // UDP 校验和置 0：IPv4 下允许（省一次计算）
            Array.Copy(payload, 0, b, Overhead, payload.Length);
            return b;
        }

        /// <summary>解析 IPv4+UDP 帧，取出载荷。仅支持无分片、无 IP 选项、UDP 的普通帧。</summary>
        public static bool TryParse(byte[] f, out uint srcIp, out uint dstIp, out ushort srcPort, out ushort dstPort,
            out byte[] payload, out string error)
        {
            srcIp = dstIp = 0; srcPort = dstPort = 0; payload = Array.Empty<byte>(); error = null;
            if (f == null || f.Length < Overhead) { error = "帧太短"; return false; }
            if ((f[0] >> 4) != 4) { error = "不是 IPv4"; return false; }
            int ihl = (f[0] & 0x0F) * 4;
            if (ihl < IpHeaderLen || f.Length < ihl + UdpHeaderLen) { error = "IP 头长度不合法"; return false; }
            if (f[9] != ProtoUdp) { error = "不是 UDP 载荷（协议号 " + f[9] + "）"; return false; }
            ushort total = ReadU16(f, 2);
            int end = Math.Min(f.Length, total == 0 ? f.Length : total);
            int frag = ReadU16(f, 6) & 0x1FFF;
            if (frag != 0) { error = "分片帧不支持"; return false; }
            srcIp = ReadU32(f, 12); dstIp = ReadU32(f, 16);
            srcPort = ReadU16(f, ihl); dstPort = ReadU16(f, ihl + 2);
            int udpLen = ReadU16(f, ihl + 4);
            int dataLen = udpLen - UdpHeaderLen;
            if (dataLen < 0) { error = "UDP 长度不合法"; return false; }
            int start = ihl + UdpHeaderLen;
            if (start + dataLen > end) dataLen = Math.Max(0, end - start);
            payload = new byte[dataLen];
            Array.Copy(f, start, payload, 0, dataLen);
            return true;
        }

        /// <summary>16 位反码校验和（IP 头用）。</summary>
        public static ushort Checksum(byte[] d, int off, int len)
        {
            long sum = 0;
            for (int i = 0; i + 1 < len; i += 2) sum += (ushort)((d[off + i] << 8) | d[off + i + 1]);
            if ((len & 1) != 0) sum += (ushort)(d[off + len - 1] << 8);
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)(v & 0xFF); }
        static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        static uint ReadU32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    }
}
