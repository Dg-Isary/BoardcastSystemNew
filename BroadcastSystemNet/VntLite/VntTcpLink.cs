using System;
using System.Net.Sockets;
using System.Threading;

namespace BroadcastSystemNet.VntLite
{
    /// <summary>
    /// vnt 的 TCP 链路（T1）。分帧依据 vnts v1.2.12 `src\core\server\tcp.rs` L96-113：
    ///   [0]=0x00  [1..3]=长度(24 位大端)  之后是完整的 NetPacket 字节（长度 ≥12 且 ≤65536）
    /// 为什么需要它：服务端只有在客户端走 **TCP 链路**时才持有 `tcp_sender`，而中继转发
    /// （wire_guard::send_one）只有 `tcp_sender` 或 `wg_sender` 可用——纯 UDP 客户端两者都没有，
    /// 所以此前"发 38 收 0"。走 TCP 才有机会用上服务端自带的中继。
    /// </summary>
    public sealed class VntTcpLink : IDisposable
    {
        public const int MaxFrame = 65536;

        TcpClient _client;
        NetworkStream _stream;
        Thread _rx;
        volatile bool _running;
        readonly object _sendLock = new object();

        public long SentFrames, RecvFrames, BadFrames;
        public string LastError = "";
        public bool Connected => _running && _client != null && _client.Connected;

        /// <summary>收到一个完整 NetPacket 字节时的回调。</summary>
        public Action<byte[]> OnPacket;

        public bool Connect(string host, int port, out string error)
        {
            error = null;
            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;                     // 控制/数据都靠小包，关掉 Nagle 降延迟
                _client.Connect(host, port);
                _stream = _client.GetStream();
                _running = true;
                _rx = new Thread(RxLoop) { IsBackground = true, Name = "vntlite-tcp" };
                _rx.Start();
                PlayLog.Write("vnt-lite", "TCP 链路", "info", detail: $"已用 TCP 连接服务端 {host}:{port}（中继需要 TCP 链路）");
                return true;
            }
            catch (Exception ex)
            {
                error = "TCP 连接失败：" + ex.Message;
                LastError = error;
                Stop();
                return false;
            }
        }

        /// <summary>发送一个报文（自动加 4 字节分帧头）。</summary>
        public bool SendPacket(byte[] packet)
        {
            if (!_running || _stream == null) { LastError = "TCP 链路未建立"; return false; }
            if (packet == null || packet.Length < 12 || packet.Length > MaxFrame)
            { LastError = "报文长度不合法（" + (packet?.Length ?? 0) + "）"; return false; }
            var head = new byte[4];
            head[0] = 0;
            head[1] = (byte)((packet.Length >> 16) & 0xFF);
            head[2] = (byte)((packet.Length >> 8) & 0xFF);
            head[3] = (byte)(packet.Length & 0xFF);
            try
            {
                lock (_sendLock)
                {
                    _stream.Write(head, 0, 4);
                    _stream.Write(packet, 0, packet.Length);
                    _stream.Flush();
                }
                SentFrames++;
                return true;
            }
            catch (Exception ex) { LastError = "TCP 发送失败：" + ex.Message; _running = false; return false; }
        }

        void RxLoop()
        {
            var head = new byte[4];
            var buf = new byte[MaxFrame];
            while (_running)
            {
                try
                {
                    if (!ReadExact(head, 4)) break;
                    if (head[0] != 0) { BadFrames++; LastError = "TCP 分帧头第一字节非 0"; break; }
                    int len = (head[1] << 16) | (head[2] << 8) | head[3];
                    if (len < 12 || len > MaxFrame) { BadFrames++; LastError = "TCP 帧长度不合法：" + len; break; }
                    if (!ReadExact(buf, len)) break;
                    RecvFrames++;
                    var pkt = new byte[len];
                    Buffer.BlockCopy(buf, 0, pkt, 0, len);
                    try { OnPacket?.Invoke(pkt); } catch { }
                }
                catch (Exception ex) { LastError = "TCP 收包异常：" + ex.Message; break; }
            }
            _running = false;
        }

        bool ReadExact(byte[] dst, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n;
                try { n = _stream.Read(dst, got, len - got); } catch { return false; }
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null; _client = null;
            try { if (_rx != null && _rx.IsAlive) _rx.Join(500); } catch { }
            _rx = null;
        }

        public void Dispose() => Stop();

        public string StatusText()
            => Connected
                ? $"TCP 链路已连接（发 {SentFrames} / 收 {RecvFrames} 帧" + (BadFrames > 0 ? $"，坏帧 {BadFrames}" : "") + "）"
                : (string.IsNullOrEmpty(LastError) ? "TCP 链路未连接" : "TCP 链路断开（" + LastError + "）");
    }
}
