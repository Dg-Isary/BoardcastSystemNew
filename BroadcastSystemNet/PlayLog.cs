#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BroadcastSystemNet
{
    /// <summary>一条播放/系统执行记录。</summary>
    public sealed class PlayLogEntry
    {
        public string time { get; set; } = "";
        public string category { get; set; } = "";
        public string action { get; set; } = "";
        public string level { get; set; } = "info";
        public string task { get; set; } = "";
        public string name { get; set; } = "";
        public string path { get; set; } = "";
        public string detail { get; set; } = "";
    }

    /// <summary>一条待提示消息（引用失效、云端数据异常等），供桌面端与网页端拉取。</summary>
    public sealed class PendingAlert
    {
        public long id { get; set; }
        public string time { get; set; } = "";
        public string level { get; set; } = "warn";
        public string category { get; set; } = "";
        public string message { get; set; } = "";
    }

    /// <summary>
    /// 执行日志：业务线程只做一次无锁入队，后台单线程批量落盘，绝不阻塞播放与 HTTP 请求。
    /// 同时维护最近 N 条的内存快照，供网页与桌面端快速读取。
    /// </summary>
    public static class PlayLog
    {
        private const int MemoryCapacity = 800;
        private const int QueueCapacity = 20000;
        private const int BatchSize = 64;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(800);

        private static readonly BlockingCollection<PlayLogEntry> _queue = new BlockingCollection<PlayLogEntry>(QueueCapacity);
        private static readonly LinkedList<PlayLogEntry> _memory = new LinkedList<PlayLogEntry>();
        private static readonly object _memoryLock = new object();

        private static readonly ConcurrentDictionary<string, byte> _onceGuards = new ConcurrentDictionary<string, byte>();

        private static string _logDir;
        private static long _dropped;
        private static bool _started;

        public static long DroppedCount => Interlocked.Read(ref _dropped);

        public static void Start(string logDir)
        {
            if (_started) return;
            _started = true;
            _logDir = logDir;
            try { Directory.CreateDirectory(_logDir); } catch { }
            var t = new Thread(WriterLoop) { IsBackground = true, Name = "PlayLogWriter" };
            t.Start();
        }

        /// <summary>写入一条记录。任何异常都被吞掉，绝不影响主流程。</summary>
        public static void Write(string category, string action, string level = "info",
                                 string task = "", string name = "", string path = "", string detail = "")
        {
            try
            {
                var e = new PlayLogEntry
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    category = category, action = action, level = level,
                    task = task, name = name, path = path, detail = detail
                };
                lock (_memoryLock)
                {
                    _memory.AddLast(e);
                    while (_memory.Count > MemoryCapacity) _memory.RemoveFirst();
                }
                if (!_queue.IsAddingCompleted && !_queue.TryAdd(e)) Interlocked.Increment(ref _dropped);
            }
            catch { }
        }

        /// <summary>同样的 key 只记一次，避免同一故障每 5 秒刷屏。</summary>
        public static bool WriteOnce(string onceKey, string category, string action, string level = "info",
                                     string task = "", string name = "", string path = "", string detail = "")
        {
            if (!string.IsNullOrEmpty(onceKey) && !_onceGuards.TryAdd(onceKey, 0)) return false;
            Write(category, action, level, task, name, path, detail);
            return true;
        }

        public static JsonArray RecentJson(int limit)
        {
            if (limit <= 0) limit = 200;
            if (limit > MemoryCapacity) limit = MemoryCapacity;
            var arr = new JsonArray();
            List<PlayLogEntry> snapshot;
            lock (_memoryLock) snapshot = _memory.Reverse().Take(limit).Reverse().ToList();
            foreach (var e in snapshot) arr.Add(ToNode(e));
            return arr;
        }

        private static JsonNode ToNode(PlayLogEntry e) => new JsonObject
        {
            ["time"] = e.time,
            ["category"] = e.category,
            ["action"] = e.action,
            ["level"] = e.level,
            ["task"] = e.task,
            ["name"] = e.name,
            ["path"] = e.path,
            ["detail"] = e.detail
        };

        private static void WriterLoop()
        {
            var buffer = new List<PlayLogEntry>(BatchSize);
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            while (true)
            {
                try
                {
                    buffer.Clear();
                    if (_queue.TryTake(out var first, FlushInterval)) buffer.Add(first);
                    while (buffer.Count < BatchSize && _queue.TryTake(out var more)) buffer.Add(more);
                    if (buffer.Count == 0) continue;
                    Flush(buffer, options);
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch { try { Thread.Sleep(500); } catch { } }
            }
        }

        private static void Flush(List<PlayLogEntry> batch, JsonSerializerOptions options)
        {
            try
            {
                if (string.IsNullOrEmpty(_logDir)) return;
                Directory.CreateDirectory(_logDir);
                string file = Path.Combine(_logDir, $"play-{DateTime.Now:yyyy-MM-dd}.jsonl");
                var sb = new StringBuilder();
                foreach (var e in batch) sb.AppendLine(JsonSerializer.Serialize(e, options));
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                TrimOldFiles();
            }
            catch { }
        }

        private static DateTime _lastTrim = DateTime.MinValue;

        private static void TrimOldFiles()
        {
            if ((DateTime.Now - _lastTrim).TotalMinutes < 30) return;
            _lastTrim = DateTime.Now;
            try
            {
                var files = new DirectoryInfo(_logDir).GetFiles("play-*.jsonl").OrderByDescending(f => f.Name).ToList();
                foreach (var f in files.Skip(30)) { try { f.Delete(); } catch { } }
            }
            catch { }
        }
    }

    /// <summary>待提示消息队列：消息可被读取标记，桌面端据此绘制置顶横幅。</summary>
    public static class AlertCenter
    {
        private const int Capacity = 200;
        private static readonly object _lock = new object();
        private static readonly List<PendingAlert> _all = new List<PendingAlert>();
        private static readonly HashSet<string> _dedup = new HashSet<string>();
        private static long _nextId = 1;

        public static PendingAlert Publish(string category, string message, string level = "warn", string dedupKey = null)
        {
            try
            {
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(dedupKey))
                    {
                        if (!_dedup.Add(dedupKey)) return null;
                    }
                    var a = new PendingAlert
                    {
                        id = _nextId++,
                        time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        level = level, category = category, message = message
                    };
                    _all.Add(a);
                    while (_all.Count > Capacity) _all.RemoveAt(0);
                    return a;
                }
            }
            catch { return null; }
        }

        public static JsonArray PendingJson(long afterId)
        {
            var arr = new JsonArray();
            lock (_lock)
            {
                foreach (var a in _all)
                {
                    if (a.id <= afterId) continue;
                    arr.Add(new JsonObject
                    {
                        ["id"] = a.id,
                        ["time"] = a.time,
                        ["level"] = a.level,
                        ["category"] = a.category,
                        ["message"] = a.message
                    });
                }
            }
            return arr;
        }

        public static void Clear()
        {
            lock (_lock) { _all.Clear(); _dedup.Clear(); }
        }
    }
}
