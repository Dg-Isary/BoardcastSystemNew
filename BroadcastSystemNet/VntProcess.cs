using System;
using System.Collections.Generic;

namespace BroadcastSystemNet
{
    /// <summary>
    /// vnt 组网管理 —— **已改为纯 C# vnt-lite 实现，不再启动任何 vnt 二进制、不建虚拟网卡、不需要管理员权限**。
    /// 公开 API 与旧版（管理 vnt-cli 子进程）保持一致，因此调用点无需改动：
    /// Start / Stop / IsRunning / TryQueryStatus / StatusSummary / OutputTail / ResolveCliPath。
    /// </summary>
    public static class VntProcess
    {
        public static string LastError = "";
        public static DateTime StartedAt = DateTime.MinValue;
        static VntLite.VntLiteClient _client;

        /// <summary>当前 vnt-lite 实例（为空表示未启动）。</summary>
        public static VntLite.VntLiteClient Client => _client;
        public static bool IsRunning => _client != null && _client.Running;

        /// <summary>最近的诊断信息（报错提示与排错用）。</summary>
        public static string OutputTail()
        {
            if (_client == null) return "";
            var s = (_client.LastError ?? "") + " ｜ " + (_client.LastPacketInfo ?? "");
            return s.Length > 400 ? s.Substring(s.Length - 400) : s;
        }

        /// <summary>
        /// 排错用：最近收到的数据面报文 + 话筒侧"认不出的包"，各取几条。
        /// 真机上"手机发了但电脑没反应"时，看这里就能判断是**没收到**还是**收到了但认不出**。
        /// </summary>
        public static string DataPlaneDiag()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var c = _client;
                if (c != null && c.DataPlaneDiagLog.Count > 0)
                {
                    sb.Append("最近收到的数据包（最多 5 条）：");
                    for (int i = 0; i < Math.Min(5, c.DataPlaneDiagLog.Count); i++) sb.Append("\n· " + c.DataPlaneDiagLog[i]);
                }
                else sb.Append("最近没有收到任何对端数据包");
                if (MicServer.BadHeaderDiag.Count > 0)
                {
                    sb.Append("\n话筒侧认不出的包：");
                    for (int i = 0; i < Math.Min(5, MicServer.BadHeaderDiag.Count); i++) sb.Append("\n· " + MicServer.BadHeaderDiag[i]);
                }
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>兼容旧调用：vnt-lite 是内置实现，不需要外部可执行文件，永远返回空串。</summary>
        public static string ResolveCliPath() => "";

        /// <summary>启动 vnt-lite（内置协议实现）。</summary>
        public static bool Start(out string error)
        {
            error = null;
            if (IsRunning) return true;
            if (!VntManager.VntConfigured)
            {
                error = "还没配置组网：请先打开「vnt 设置」，填写服务器地址与组网令牌";
                LastError = error;
                return false;
            }
            var c = new VntLite.VntLiteClient
            {
                Password = VntManager.DataPassword ?? "",
                Model = string.IsNullOrWhiteSpace(VntManager.Encrypt) ? "aes_gcm" : VntManager.Encrypt,
                // 中继优先：经服务端中继且不加 vnt 层加密（内容仍由话筒自己的 AEAD 保护）
                RelayPlaintext = VntManager.RelayFirst,
                // 服务端加密（-W）：三态策略（auto/on/off）→ 自动适配默认。
                //   auto：第一次握手明文，服务端带 RSA 公钥就自动切到加密（两种服务器都能连）；
                //   on  ：服务端不支持时直接报错；off：永不使用。
                // 但**中继优先时必须关**：vnts 只转发明文包（e=0），开了 -W 后所有控制面报文都是 e=1，
                //   中继数据面也会被误加密 → 中继收不到（内容仍由话筒每包 AEAD 保护）。
                ServerEncrypt = VntManager.ServerEncryptMode != "off" && !VntManager.RelayFirst,
                ForceServerEncrypt = VntManager.ServerEncryptMode == "on" && !VntManager.RelayFirst
            };
            if (!c.Start(VntManager.Server, VntManager.Token, VntManager.DeviceId, Environment.MachineName, out error))
            {
                LastError = error;
                try { c.Stop(); } catch { }
                return false;
            }
            // W1：把经 vnt-lite 收到的对端数据（中继或 P2P）喂进话筒管线；源地址用【内层虚拟 IP】
            c.OnPeerDataFrom = (payload, srcVirtualIp) =>
            {
                try { MicServer.HandleAudioPacket(payload, srcVirtualIp); }
                catch (Exception ex) { PlayLog.Write("vnt-lite", "话筒包处理异常", "error", detail: ex.Message); }
            };
            _client = c;
            // 用户填了虚拟 IP 就固定用它（与官方 App 一致）：注册时请求这个 IP 且不允许服务器改
            try
            {
                string want = VntManager.RequestedVirtualIp;
                c.RequestedIp = string.IsNullOrWhiteSpace(want) ? 0u : VntLite.VntPacket.IpToUint(want);
                if (c.RequestedIp != 0) PlayLog.Write("vnt-lite", "固定虚拟 IP", "info", detail: "已请求固定虚拟 IP " + want);
            }
            catch { }
            StartedAt = DateTime.UtcNow;
            LastError = "";
            PlayLog.Write("vnt-lite", "启动", "info",
                detail: $"vnt-lite（内置实现，无需 vnt-cli）已启动：服务器 {VntManager.Server}，组网令牌 {VntManager.MaskToken(VntManager.Token)}"
                        + (VntManager.RelayFirst ? "，传输模式 中继优先" : "，传输模式 P2P 优先"));
            // 数据面模式必须与手机端一致：vnts 只在「目标注册的 client_secret == 数据包 e 位」时转发；
            // 带 -f 指纹校验的服务端还要求数据包是**密文**（finger.rs 对明文直接 not encrypt 丢掉）。
            PlayLog.Write("vnt-lite", "数据面模式", "info",
                detail: (string.IsNullOrEmpty(VntManager.DataPassword)
                            ? "没填数据密码：数据面只能是明文 e=0、注册 client_secret=false。"
                              + "若服务端带 -f（指纹校验），数据面会被服务端直接丢掉 —— 请在「vnt 设置」填一个数据密码并让手机扫同一个码。"
                            : "数据面 = 加密 e=1 + 12 字节指纹，注册 client_secret=true（与官方客户端一致）。"));
            return true;
        }

        /// <summary>停止 vnt-lite。</summary>
        public static void Stop()
        {
            if (_client == null) return;
            try { _client.Stop(); } catch { }
            _client = null;
            StartedAt = DateTime.MinValue;
            PlayLog.Write("vnt-lite", "停止", "info", detail: "vnt-lite 已停止");
        }

        /// <summary>查询组网状态与设备表（旧接口签名保持不变）。</summary>
        public static bool TryQueryStatus(out VntManager.VntInfo info, out List<VntManager.VntPeer> peers, out string error)
        {
            info = new VntManager.VntInfo();
            peers = new List<VntManager.VntPeer>();
            error = null;
            var c = _client;
            if (c == null)
            {
                info.Status = "Disconnected";
                error = "vnt-lite 未启动：请先在「vnt 设置」里启动，或从菜单选「启动 vnt」";
                return false;
            }
            error = string.IsNullOrEmpty(c.LastError) ? null : c.LastError;
            info.Status = c.State == VntLite.VntLiteState.Registered ? "Connected" : c.State.ToString();
            info.VirtualIp = c.VirtualIp != 0 ? VntLite.VntPacket.UintToIp(c.VirtualIp) : "";
            info.Gateway = c.VirtualGateway != 0 ? VntLite.VntPacket.UintToIp(c.VirtualGateway) : "";
            info.Netmask = c.VirtualNetmask != 0 ? VntLite.VntPacket.UintToIp(c.VirtualNetmask) : "";
            info.PublicIp = c.PublicIp ?? "";
            info.LocalIp = "";
            info.NatType = "Cone";
            info.Relay = VntManager.RelayFirst ? "中继优先" : "P2P 优先";
            foreach (var d in c.Devices)
            {
                peers.Add(new VntManager.VntPeer
                {
                    Name = d.Name,
                    VirtualIp = VntLite.VntPacket.UintToIp(d.VirtualIp),
                    Kind = d.Wireguard ? "wg" : "",
                    Rt = -1,
                    Status = d.DeviceStatus == 0 ? "online" : "offline"
                });
            }
            return true;
        }

        /// <summary>
        /// 用户可读的一句话状态（W4）：把 vnt-lite 的内部状态 + 错误码翻成人话，并覆盖"未配置/未启动/握手中/重连中/失败"等场景。
        /// </summary>
        public static string StateText()
        {
            if (!VntManager.VntConfigured)
                return "还没配置组网：请在下面填写【服务器】与【组网令牌】，再点「启动 vnt」。";
            var c = _client;
            if (c == null || !c.Running)
                return "vnt-lite 未启动：点「启动 vnt」即可组网（内置实现，不装 vnt、不建网卡、不需要管理员权限）。";
            switch (c.State)
            {
                case VntLite.VntLiteState.Registered:
                    return "已入网：虚拟 IP " + VntLite.VntPacket.UintToIp(c.VirtualIp)
                           + (string.IsNullOrEmpty(c.PublicIp) ? "" : "，公网 " + c.PublicIp + ":" + c.PublicPort)
                           + "，设备 " + c.Devices.Count + " 台。";
                case VntLite.VntLiteState.HandshakeDone:
                    return "握手完成，正在注册…（若长时间停在这里，检查服务器地址与组网令牌）";
                case VntLite.VntLiteState.Handshaking:
                    // 卡在握手时，把已经诊断出的原因直接说出来（以前这里永远只显示"握手中"，用户只能猜）
                    if (!string.IsNullOrEmpty(c.LastError))
                        return "连不上：" + c.LastError;
                    return "正在连接服务器（握手中）…（服务端不可达或地址填错时会一直重试）";
                case VntLite.VntLiteState.Failed:
                    return "组网失败：" + (string.IsNullOrEmpty(c.LastError) ? "未知原因" : c.LastError)
                           + "（令牌错误请核对组网令牌；服务端开了指纹校验时本机也要开指纹认证）";
                default:
                    return "vnt-lite 空闲。";
            }
        }

        /// <summary>
        /// 【统一发送入口】把一个话筒（MicProtocol）包发给指定虚拟 IP 的对端：
        /// · 中继优先 → 交给服务端转发（`IpTurn/Ipv4`，vnt 层明文，内容仍是话筒自身 AEAD 密文）；
        /// · P2P 优先 → 直发已协商到的对端公网端点；若还没拿到端点则回退到服务端转发。
        /// </summary>
        public static bool SendToPeer(string peerVirtualIp, byte[] micPacket, out string error)
        {
            error = null;
            var c = _client;
            if (c == null || !c.Running) { error = "vnt-lite 未启动：请先点「启动 vnt」"; return false; }
            if (string.IsNullOrWhiteSpace(peerVirtualIp)) { error = "对端虚拟 IP 为空"; return false; }
            if (micPacket == null || micPacket.Length == 0) { error = "要发送的包为空"; return false; }
            uint vip;
            try { vip = VntLite.VntPacket.IpToUint(peerVirtualIp.Trim()); }
            catch { error = "对端虚拟 IP 无法解析：" + peerVirtualIp; return false; }

            bool relay = VntManager.RelayFirst;
            if (!relay)
            {
                var pi = c.P2PInfo;
                if (pi != null && pi.PublicIpList != null && pi.PublicIpList.Count > 0 && pi.PublicPort > 0)
                {
                    try
                    {
                        c.SendRawToPeer(micPacket, VntLite.VntPacket.UintToIp(pi.PublicIpList[0]), (int)pi.PublicPort);
                        return true;
                    }
                    catch (Exception ex) { error = "P2P 直发失败：" + ex.Message; }
                }
                // 没协商到端点 → 回退中继（不报错，保证"能用优先"）
            }
            try
            {
                c.SendPeerViaIp(micPacket, vip);
                if (!string.IsNullOrEmpty(c.LastError)) { error = c.LastError; return false; }
                return true;
            }
            catch (Exception ex) { error = "经服务端转发失败：" + ex.Message; return false; }
        }

        /// <summary>状态摘要（菜单与状态灯用）。</summary>
        public static string StatusSummary()
        {
            var c = _client;
            if (c == null || !c.Running)
                return $"vnt-lite 未运行（内置实现，无需 vnt-cli）｜服务器 {VntManager.Server}｜token {VntManager.MaskToken(VntManager.Token)}";
            string state = c.State switch
            {
                VntLite.VntLiteState.Registered => "已入网",
                VntLite.VntLiteState.HandshakeDone => "握手完成",
                VntLite.VntLiteState.Handshaking => "握手中",
                VntLite.VntLiteState.Failed => "失败",
                _ => "未启动"
            };
            string ip = c.VirtualIp != 0 ? VntLite.VntPacket.UintToIp(c.VirtualIp) : "-";
            string pub = string.IsNullOrEmpty(c.PublicIp) ? "-" : c.PublicIp + ":" + c.PublicPort;
            return $"vnt-lite {state}｜虚拟 IP {ip}｜公网 {pub}｜设备 {c.Devices.Count} 台｜收 {c.RecvPackets}/发 {c.SentPackets} 包"
                   + $"｜token {VntManager.MaskToken(VntManager.Token)}｜传输模式 {(VntManager.RelayFirst ? "中继优先" : "P2P 优先")}"
                   + (string.IsNullOrEmpty(c.LastError) ? "" : "｜最近错误：" + c.LastError);
        }
    }
}