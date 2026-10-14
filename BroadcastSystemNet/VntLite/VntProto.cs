using System;
using System.Collections.Generic;
using System.Text;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>
    /// 手写 protobuf 线格式（只需要 varint / length-delimited / fixed32 三种 wire type）。
    /// 为什么不用 Google.Protobuf：本仓库既定约定是"零新 NuGet 依赖"，而 vnt 的消息量很小（10 个）。
    /// 契约来源：third_party\vnt-1.2.16\vnt\proto\message.proto（逐字段对应）。
    /// </summary>
    public static class Pb
    {
        public const int WireVarint = 0, WireFixed64 = 1, WireBytes = 2, WireFixed32 = 5;

        // ---------------- 写 ----------------

        public static void Varint(List<byte> b, ulong v)
        {
            while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
            b.Add((byte)v);
        }

        static void Tag(List<byte> b, int field, int wire) => Varint(b, (ulong)((field << 3) | wire));

        public static void WriteString(List<byte> b, int field, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var d = Encoding.UTF8.GetBytes(s);
            Tag(b, field, WireBytes); Varint(b, (ulong)d.Length); b.AddRange(d);
        }

        public static void WriteBytes(List<byte> b, int field, byte[] d)
        {
            if (d == null || d.Length == 0) return;
            Tag(b, field, WireBytes); Varint(b, (ulong)d.Length); b.AddRange(d);
        }

        public static void WriteBool(List<byte> b, int field, bool v)
        {
            if (!v) return;                       // proto3：默认值不写
            Tag(b, field, WireVarint); Varint(b, 1);
        }

        public static void WriteUInt32(List<byte> b, int field, uint v)
        {
            if (v == 0) return;
            Tag(b, field, WireVarint); Varint(b, v);
        }

        /// <summary>fixed32：小端 4 字节（protobuf 规范）。</summary>
        public static void WriteFixed32(List<byte> b, int field, uint v)
        {
            if (v == 0) return;
            Tag(b, field, WireFixed32);
            b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF));
            b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF));
        }

        // ---------------- 读 ----------------

        public sealed class Reader
        {
            readonly byte[] _d; int _i;
            public Reader(byte[] d) { _d = d ?? Array.Empty<byte>(); }

            public bool Eof => _i >= _d.Length;

            public ulong Varint()
            {
                ulong r = 0; int shift = 0;
                while (_i < _d.Length)
                {
                    byte c = _d[_i++];
                    r |= (ulong)(c & 0x7F) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                    if (shift > 63) break;
                }
                return r;
            }

            public (int field, int wire) ReadTag()
            {
                if (Eof) return (0, -1);
                ulong t = Varint();
                return ((int)(t >> 3), (int)(t & 0x7));
            }

            public string String()
            {
                int n = (int)Varint();
                if (n < 0 || _i + n > _d.Length) { _i = _d.Length; return ""; }
                var s = Encoding.UTF8.GetString(_d, _i, n); _i += n; return s;
            }

            public byte[] Bytes()
            {
                int n = (int)Varint();
                if (n < 0 || _i + n > _d.Length) { _i = _d.Length; return Array.Empty<byte>(); }
                var r = new byte[n]; Array.Copy(_d, _i, r, 0, n); _i += n; return r;
            }

            public uint Fixed32()
            {
                if (_i + 4 > _d.Length) { _i = _d.Length; return 0; }
                uint v = (uint)(_d[_i] | (_d[_i + 1] << 8) | (_d[_i + 2] << 16) | (_d[_i + 3] << 24));
                _i += 4; return v;
            }

            public void Skip(int wire)
            {
                switch (wire)
                {
                    case WireVarint: Varint(); break;
                    case WireFixed32: _i += 4; break;
                    case WireFixed64: _i += 8; break;
                    case WireBytes: { int n = (int)Varint(); _i += n; break; }
                    default: _i = _d.Length; break;
                }
                if (_i > _d.Length) _i = _d.Length;
                if (_i < 0) _i = 0;
            }
        }
    }

    // ================= 报文（字段号严格对应 message.proto） =================

    /// <summary>message HandshakeRequest { version=1, secret=2, key_finger=3 }</summary>
    public sealed class HandshakeRequest
    {
        public string Version = "";
        public bool Secret;
        public string KeyFinger = "";

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteString(b, 1, Version);
            Pb.WriteBool(b, 2, Secret);
            Pb.WriteString(b, 3, KeyFinger);
            return b.ToArray();
        }

        public static HandshakeRequest Parse(byte[] d)
        {
            var m = new HandshakeRequest(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.Version = r.String(); break;
                    case 2: m.Secret = r.Varint() != 0; break;
                    case 3: m.KeyFinger = r.String(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }

    /// <summary>message HandshakeResponse { version=1, secret=2, public_key=3, key_finger=4 }</summary>
    public sealed class HandshakeResponse
    {
        public string Version = "";
        public bool Secret;
        public byte[] PublicKey = Array.Empty<byte>();
        public string KeyFinger = "";

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteString(b, 1, Version);
            Pb.WriteBool(b, 2, Secret);
            Pb.WriteBytes(b, 3, PublicKey);
            Pb.WriteString(b, 4, KeyFinger);
            return b.ToArray();
        }

        public static HandshakeResponse Parse(byte[] d)
        {
            var m = new HandshakeResponse(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.Version = r.String(); break;
                    case 2: m.Secret = r.Varint() != 0; break;
                    case 3: m.PublicKey = r.Bytes(); break;
                    case 4: m.KeyFinger = r.String(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }

    /// <summary>
    /// message SecretHandshakeRequest { token=1, key=2 }（vnt 的"服务端加密" -W 用的第二次握手）
    ///
    /// 依据：`vnt\proto\message.proto` + `vnt\src\handle\handshaker.rs` L85-111
    /// —— 整个报文用**服务端 RSA 公钥**（PKCS#1 v1.5）加密后作为 NetPacket 载荷发出；
    /// 服务端解出后用 `key` 作为对称密钥、以 `token` 参与指纹，加密后续所有报文。
    /// </summary>
    public sealed class SecretHandshakeRequest
    {
        public string Token = "";
        public byte[] Key = Array.Empty<byte>();

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteString(b, 1, Token);
            Pb.WriteBytes(b, 2, Key);
            return b.ToArray();
        }

        public static SecretHandshakeRequest Parse(byte[] d)
        {
            var m = new SecretHandshakeRequest(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.Token = r.String(); break;
                    case 2: m.Key = r.Bytes(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }

    /// <summary>
    /// message RegistrationRequest { token=1, device_id=2, name=3, is_fast=4, version=5,
    ///                               virtual_ip=6(fixed32), allow_ip_change=7, client_secret=8, client_secret_hash=9 }
    /// </summary>
    public sealed class RegistrationRequest
    {
        public string Token = "";
        public string DeviceId = "";
        public string Name = "";
        public bool IsFast;
        public string Version = "1.2.16";
        public uint VirtualIp;
        public bool AllowIpChange;
        public bool ClientSecret;
        public byte[] ClientSecretHash = Array.Empty<byte>();

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteString(b, 1, Token);
            Pb.WriteString(b, 2, DeviceId);
            Pb.WriteString(b, 3, Name);
            Pb.WriteBool(b, 4, IsFast);
            Pb.WriteString(b, 5, Version);
            Pb.WriteFixed32(b, 6, VirtualIp);
            Pb.WriteBool(b, 7, AllowIpChange);
            Pb.WriteBool(b, 8, ClientSecret);
            Pb.WriteBytes(b, 9, ClientSecretHash);
            return b.ToArray();
        }

        public static RegistrationRequest Parse(byte[] d)
        {
            var m = new RegistrationRequest(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.Token = r.String(); break;
                    case 2: m.DeviceId = r.String(); break;
                    case 3: m.Name = r.String(); break;
                    case 4: m.IsFast = r.Varint() != 0; break;
                    case 5: m.Version = r.String(); break;
                    case 6: m.VirtualIp = r.Fixed32(); break;
                    case 7: m.AllowIpChange = r.Varint() != 0; break;
                    case 8: m.ClientSecret = r.Varint() != 0; break;
                    case 9: m.ClientSecretHash = r.Bytes(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }

    /// <summary>
    /// message PunchInfo { public_ip_list=2(repeated fixed32), public_port=3, public_port_range=4,
    ///                    nat_type=5(enum), reply=6, local_ip=7, local_port=8, ipv6=9, ipv6_port=10,
    ///                    tcp_port=11, udp_ports=12(repeated), public_ports=13(repeated),
    ///                    public_tcp_port=14, punch_model=15(enum) }
    /// 注意：字段号从 2 开始（上游 proto 里没有 1）。
    /// nat_type: Symmetric=0 / Cone=1；punch_model: All=0 / IPv4=1 / IPv6=2 / IPv4Tcp=3 / IPv4Udp=4 / IPv6Tcp=5 / IPv6Udp=6
    /// </summary>
    public sealed class PunchInfo
    {
        public readonly List<uint> PublicIpList = new List<uint>();
        public uint PublicPort;
        public uint PublicPortRange;
        public uint NatType;          // 0=Symmetric, 1=Cone
        public bool Reply;
        public uint LocalIp;
        public uint LocalPort;
        public byte[] Ipv6 = Array.Empty<byte>();
        public uint Ipv6Port;
        public uint TcpPort;
        public readonly List<uint> UdpPorts = new List<uint>();
        public readonly List<uint> PublicPorts = new List<uint>();
        public uint PublicTcpPort;
        public uint PunchModel;       // 0=All, 1=IPv4, ...

        public string NatTypeName => NatType == 1 ? "Cone" : "Symmetric";

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            foreach (var ip in PublicIpList) Pb.WriteFixed32(b, 2, ip);
            Pb.WriteUInt32(b, 3, PublicPort);
            Pb.WriteUInt32(b, 4, PublicPortRange);
            if (NatType != 0) Pb.WriteUInt32(b, 5, NatType);
            Pb.WriteBool(b, 6, Reply);
            Pb.WriteFixed32(b, 7, LocalIp);
            Pb.WriteUInt32(b, 8, LocalPort);
            Pb.WriteBytes(b, 9, Ipv6);
            Pb.WriteUInt32(b, 10, Ipv6Port);
            Pb.WriteUInt32(b, 11, TcpPort);
            foreach (var p in UdpPorts) Pb.WriteUInt32(b, 12, p);
            foreach (var p in PublicPorts) Pb.WriteUInt32(b, 13, p);
            Pb.WriteUInt32(b, 14, PublicTcpPort);
            if (PunchModel != 0) Pb.WriteUInt32(b, 15, PunchModel);
            return b.ToArray();
        }

        public static PunchInfo Parse(byte[] d)
        {
            var m = new PunchInfo(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 2: m.PublicIpList.Add(r.Fixed32()); break;
                    case 3: m.PublicPort = (uint)r.Varint(); break;
                    case 4: m.PublicPortRange = (uint)r.Varint(); break;
                    case 5: m.NatType = (uint)r.Varint(); break;
                    case 6: m.Reply = r.Varint() != 0; break;
                    case 7: m.LocalIp = r.Fixed32(); break;
                    case 8: m.LocalPort = (uint)r.Varint(); break;
                    case 9: m.Ipv6 = r.Bytes(); break;
                    case 10: m.Ipv6Port = (uint)r.Varint(); break;
                    case 11: m.TcpPort = (uint)r.Varint(); break;
                    case 12: m.UdpPorts.Add((uint)r.Varint()); break;
                    case 13: m.PublicPorts.Add((uint)r.Varint()); break;
                    case 14: m.PublicTcpPort = (uint)r.Varint(); break;
                    case 15: m.PunchModel = (uint)r.Varint(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }

        public string Describe()
            => $"公网端口 {PublicPort}（范围 {PublicPortRange}），NAT {NatTypeName}，reply={Reply}，"
             + $"本地 {VntPacket.UintToIp(LocalIp)}:{LocalPort}，公网 IP 数 {PublicIpList.Count}，模型 {PunchModel}";
    }

    /// <summary>message DeviceInfo { name=1, virtual_ip=2, device_status=3, client_secret=4, client_secret_hash=5, wireguard=6 }</summary>
    public sealed class DeviceInfo
    {
        public string Name = "";
        public uint VirtualIp;
        public uint DeviceStatus;
        public bool ClientSecret;
        public byte[] ClientSecretHash = Array.Empty<byte>();
        public bool Wireguard;

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteString(b, 1, Name);
            Pb.WriteFixed32(b, 2, VirtualIp);
            Pb.WriteUInt32(b, 3, DeviceStatus);
            Pb.WriteBool(b, 4, ClientSecret);
            Pb.WriteBytes(b, 5, ClientSecretHash);
            Pb.WriteBool(b, 6, Wireguard);
            return b.ToArray();
        }

        public static DeviceInfo Parse(Pb.Reader r)
        {
            var m = new DeviceInfo();
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.Name = r.String(); break;
                    case 2: m.VirtualIp = r.Fixed32(); break;
                    case 3: m.DeviceStatus = (uint)r.Varint(); break;
                    case 4: m.ClientSecret = r.Varint() != 0; break;
                    case 5: m.ClientSecretHash = r.Bytes(); break;
                    case 6: m.Wireguard = r.Varint() != 0; break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }

    /// <summary>
    /// message RegistrationResponse { virtual_ip=1, virtual_gateway=2, virtual_netmask=3, epoch=4,
    ///                                device_info_list=5(repeated), public_ip=6, public_port=7, public_ipv6=8 }
    /// </summary>
    public sealed class RegistrationResponse
    {
        public uint VirtualIp, VirtualGateway, VirtualNetmask;
        public uint Epoch;
        public readonly List<DeviceInfo> Devices = new List<DeviceInfo>();
        public uint PublicIp, PublicPort;
        public byte[] PublicIpv6 = Array.Empty<byte>();

        public byte[] ToBytes()
        {
            var b = new List<byte>();
            Pb.WriteFixed32(b, 1, VirtualIp);
            Pb.WriteFixed32(b, 2, VirtualGateway);
            Pb.WriteFixed32(b, 3, VirtualNetmask);
            Pb.WriteUInt32(b, 4, Epoch);
            foreach (var d in Devices) Pb.WriteBytes(b, 5, d.ToBytes());
            Pb.WriteFixed32(b, 6, PublicIp);
            Pb.WriteUInt32(b, 7, PublicPort);
            Pb.WriteBytes(b, 8, PublicIpv6);
            return b.ToArray();
        }

        public static RegistrationResponse Parse(byte[] d)
        {
            var m = new RegistrationResponse(); var r = new Pb.Reader(d);
            while (!r.Eof)
            {
                var (f, w) = r.ReadTag(); if (w < 0) break;
                switch (f)
                {
                    case 1: m.VirtualIp = r.Fixed32(); break;
                    case 2: m.VirtualGateway = r.Fixed32(); break;
                    case 3: m.VirtualNetmask = r.Fixed32(); break;
                    case 4: m.Epoch = (uint)r.Varint(); break;
                    case 5: { var sub = r.Bytes(); m.Devices.Add(DeviceInfo.Parse(new Pb.Reader(sub))); break; }
                    case 6: m.PublicIp = r.Fixed32(); break;
                    case 7: m.PublicPort = (uint)r.Varint(); break;
                    case 8: m.PublicIpv6 = r.Bytes(); break;
                    default: r.Skip(w); break;
                }
            }
            return m;
        }
    }
}
