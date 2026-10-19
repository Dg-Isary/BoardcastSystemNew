using System;
using System.Net;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>外层协议号（vnt\src\protocol\mod.rs）</summary>
    public static class VntOuterProtocol
    {
        public const byte Service = 1;      // 与 service 报文（注册/握手/状态）
        public const byte Error = 2;
        public const byte Control = 3;
        public const byte IpTurn = 4;
        public const byte OtherTurn = 5;
    }

    /// <summary>控制层协议号（外层 = Control(3)）：vnt\src\protocol\control_packet.rs</summary>
    public static class VntControlProtocol
    {
        public const byte Ping = 1;
        public const byte Pong = 2;
        public const byte PunchRequest = 3;
        public const byte PunchResponse = 4;
        public const byte AddrRequest = 5;
        public const byte AddrResponse = 6;

        public static string Name(byte v) => v switch
        {
            1 => "Ping", 2 => "Pong", 3 => "打洞请求", 4 => "打洞响应",
            5 => "地址请求", 6 => "地址响应", _ => "未知(" + v + ")"
        };
    }

    /// <summary>OtherTurn 层（外层 = OtherTurn(5)）：vnt\src\protocol\other_turn_packet.rs</summary>
    public static class VntOtherTurnProtocol
    {
        public const byte Punch = 1;      // 服务端把对端的 PunchInfo 转发过来
    }

    /// <summary>错误层（外层 = Error(2)）：vnt\src\protocol\error_packet.rs</summary>
    public static class VntErrorProtocol
    {
        public const byte TokenError = 1;
        public const byte Disconnect = 2;
        public const byte AddressExhausted = 3;
        public const byte IpAlreadyExists = 4;
        public const byte InvalidIp = 5;
        public const byte NoKey = 6;

        /// <summary>错误码 → 人话（这些码解释了"连不上/注册失败"的真实原因）。</summary>
        public static string Text(byte v) => v switch
        {
            1 => "组网令牌（token）错误：服务端不接受这个令牌",
            2 => "服务端主动断开：可能被顶号或被服务端踢出",
            3 => "地址池耗尽：这个虚拟网段没 IP 可分配了",
            4 => "虚拟 IP 冲突：这个 IP 已被别的设备占用",
            5 => "虚拟 IP 非法：指定的 IP 不在服务端网段内",
            6 => "缺少密钥：服务端要求节点间密钥（-w）/服务端加密（-W），客户端没带",
            _ => "未知错误码 " + v
        };
    }

    /// <summary>服务层协议号（vnt\src\protocol\service_packet.rs）</summary>
    public static class VntServiceProtocol
    {
        public const byte RegistrationRequest = 1;
        public const byte RegistrationResponse = 2;
        public const byte PullDeviceList = 3;
        public const byte PushDeviceList = 4;
        public const byte HandshakeRequest = 5;
        public const byte HandshakeResponse = 6;
        public const byte SecretHandshakeRequest = 7;
        public const byte SecretHandshakeResponse = 8;
        public const byte ClientStatusInfo = 9;

        public static string Name(byte v) => v switch
        {
            1 => "注册请求", 2 => "注册响应", 3 => "拉设备表", 4 => "推设备表",
            5 => "握手请求", 6 => "握手响应", 7 => "密钥握手请求", 8 => "密钥握手响应",
            9 => "状态上报", _ => "未知(" + v + ")"
        };
    }

    /// <summary>
    /// vnt 的 NetPacket 12 字节头（vnt\src\protocol\mod.rs，HEAD_LEN = 12）：
    ///   [0] e(加密)|s(服务端通信包)|x(扩展)|u(未用)  高4位 + 版本 低4位（版本必须 ≥ 2）
    ///   [1] 外层协议  [2] 上层协议  [3] 初始TTL 高4位 | 生存时间 低4位
    ///   [4..8) 源IP  [8..12) 目的IP        之后是数据体
    /// 服务包的源/目的固定用哨兵地址：SELF_IP = 0.0.0.2，GATEWAY_IP = 0.0.0.1
    /// </summary>
    public sealed class VntPacket
    {
        public const int HeadLen = 12;
        public const byte VersionV2 = 2;          // 版本从 2 起，用于与 STUN binding 响应区分
        public const byte MaxTtl = 0x0F;

        public static readonly uint SelfIp = 0x00000002;      // 0.0.0.2
        public static readonly uint GatewayIp = 0x00000001;   // 0.0.0.1

        public bool Encrypted;        // e
        public bool GatewayFlag;      // s：服务端通信包
        public bool Extended;         // x
        public byte Version = VersionV2;
        public byte Protocol;         // 外层
        public byte Transport;        // 上层（外层=Service 时为服务层协议号）
        public byte InitTtl = MaxTtl;
        public byte Ttl = MaxTtl;
        public uint SourceIp = SelfIp;
        public uint DestIp = GatewayIp;
        public byte[] Payload = Array.Empty<byte>();

        /// <summary>构造一个"发往服务端"的服务包（头部字段按 vnt 客户端的写法固定）。</summary>
        public static VntPacket NewService(byte serviceProtocol, byte[] payload)
            => new VntPacket
            {
                GatewayFlag = true,
                Protocol = VntOuterProtocol.Service,
                Transport = serviceProtocol,
                SourceIp = SelfIp,
                DestIp = GatewayIp,
                InitTtl = MaxTtl,
                Ttl = MaxTtl,
                Payload = payload ?? Array.Empty<byte>()
            };

        /// <summary>序列化为字节（12 字节头 + 数据体）。</summary>
        public byte[] Build()
        {
            var buf = new byte[HeadLen + Payload.Length];
            byte flags = (byte)((Version & 0x0F));
            if (Encrypted) flags |= 0x80;
            if (GatewayFlag) flags |= 0x40;
            if (Extended) flags |= 0x20;
            buf[0] = flags;
            buf[1] = Protocol;
            buf[2] = Transport;
            buf[3] = (byte)(((InitTtl & 0x0F) << 4) | (Ttl & 0x0F));
            WriteU32Be(buf, 4, SourceIp);
            WriteU32Be(buf, 8, DestIp);
            Array.Copy(Payload, 0, buf, HeadLen, Payload.Length);
            return buf;
        }

        public static bool TryParse(byte[] buf, out VntPacket p, out string error)
        {
            p = null; error = null;
            if (buf == null || buf.Length < HeadLen) { error = "长度不足 12 字节"; return false; }
            var m = new VntPacket
            {
                Encrypted = (buf[0] & 0x80) != 0,
                GatewayFlag = (buf[0] & 0x40) != 0,
                Extended = (buf[0] & 0x20) != 0,
                Version = (byte)(buf[0] & 0x0F),
                Protocol = buf[1],
                Transport = buf[2],
                InitTtl = (byte)(buf[3] >> 4),
                Ttl = (byte)(buf[3] & 0x0F),
                SourceIp = ReadU32Be(buf, 4),
                DestIp = ReadU32Be(buf, 8)
            };
            if (m.Version < VersionV2) { error = "版本 < 2（可能是 STUN 响应，不是 vnt）"; return false; }
            m.Payload = new byte[buf.Length - HeadLen];
            Array.Copy(buf, HeadLen, m.Payload, 0, m.Payload.Length);
            p = m;
            return true;
        }

        // ---- IP 用点分十进制表示（便于日志/断言；线格式按网络字节序写入） ----

        public static uint IpToUint(string ip)
            => (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(IPAddress.Parse(ip).GetAddressBytes(), 0));

        public static string UintToIp(uint v)
            => new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)v))).ToString();

        static void WriteU32Be(byte[] b, int off, uint v)
        {
            b[off] = (byte)((v >> 24) & 0xFF); b[off + 1] = (byte)((v >> 16) & 0xFF);
            b[off + 2] = (byte)((v >> 8) & 0xFF); b[off + 3] = (byte)(v & 0xFF);
        }

        static uint ReadU32Be(byte[] b, int off)
            => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

        public override string ToString()
            => $"[vnt {UintToIp(SourceIp)}→{UintToIp(DestIp)} outer={Protocol} svc={VntServiceProtocol.Name(Transport)}"
             + (Encrypted ? " 加密" : "") + (GatewayFlag ? " 服务包" : "") + $" {Payload.Length}B]";
    }
}
