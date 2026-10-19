#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 桌面端数据访问：统一走本机 HTTP 接口，保证桌面端与网页端/手机端看到同一份状态与同一套规则。
    /// 桌面上特有的操作（备份/恢复/开机自启/订阅配置）直接调用后端方法。
    /// </summary>
    public static class DesktopApi
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        static string Base => $"http://127.0.0.1:{Program.ListenPort}";

        public static async Task<JsonNode> GetJson(string path)
        {
            try
            {
                string s = await _http.GetStringAsync(Base + path);
                return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
            }
            catch { return null; }
        }

        public static async Task<JsonNode> PostJson(string path, object payload)
        {
            try
            {
                string body = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(Base + path, content);
                string s = await resp.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
            }
            catch { return null; }
        }

        public static async Task<JsonNode> DeleteJson(string path)
        {
            try
            {
                var resp = await _http.DeleteAsync(Base + path);
                string s = await resp.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
            }
            catch { return null; }
        }

        public static async Task<JsonNode> PutJson(string path, object payload)
        {
            try
            {
                string body = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await _http.PutAsync(Base + path, content);
                string s = await resp.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
            }
            catch { return null; }
        }

        public static async Task<JsonArray> GetArray(string path)
        {
            var n = await GetJson(path);
            return n as JsonArray ?? new JsonArray();
        }

        // ================= 供界面使用的强类型读取 =================

        public static async Task<JsonObject> GetSys() => await GetJson("/api/sys") as JsonObject;
        public static async Task<JsonArray> GetTasks() => await GetArray("/api/tasks");
        public static async Task<JsonArray> GetMusic() => await GetArray("/api/music");
        public static async Task<JsonArray> GetTiaoxiu() => await GetArray("/api/tiaoxiu");
        public static async Task<JsonArray> GetInvalidTiaoxiu() => await GetArray("/api/tiaoxiu/invalid");
        public static async Task<JsonArray> GetLog(int limit = 300) => await GetArray($"/api/log?limit={limit}");
        public static async Task<JsonArray> GetAlerts(long after) => await GetArray($"/api/alerts?after={after}");
        public static async Task<JsonObject> GetSettings() => await GetJson("/api/settings") as JsonObject;

        public static Task<JsonNode> ToggleSwitch(bool on) => PostJson("/api/settings", new { global_switch = on });
        public static Task<JsonNode> SaveTiaoxiu(JsonArray records) => PostJson("/api/tiaoxiu", records);
        public static Task<JsonNode> SyncSubscription() => PostJson("/api/tiaoxiu/sync", new { });
        /// <summary>只拉取 + 转写、不写入：返回 {status, format, total, off_days, on_days, sample}。</summary>
        public static Task<JsonNode> PreviewSubscription(string url = null, string format = null)
            => PostJson("/api/tiaoxiu/preview", new { url, format });
        public static Task<JsonNode> ClearInvalidTiaoxiu() => DeleteJson("/api/tiaoxiu/invalid");
        public static Task<JsonNode> AddMusic(string name, string path, string type) => PostJson("/api/music", new { name, path, type });
        public static Task<JsonNode> DeleteMusic(string id) => DeleteJson("/api/music/" + Uri.EscapeDataString(id ?? ""));

        /// <summary>直接写回音乐库（用于排序、改属性等批量操作）。</summary>
        public static void WriteMusic(JsonArray arr) => Program.WriteJsonl("music.jsonl", arr);

        // ================= 任务排程 =================
        public static Task<JsonNode> AddTask(object payload) => PostJson("/api/tasks", payload);
        public static Task<JsonNode> UpdateTask(int id, object payload) => PutJson("/api/tasks/" + id, payload);
        public static Task<JsonNode> DeleteTask(int id) => DeleteJson("/api/tasks/" + id);
        /// <summary>按开始时间把所有任务重新编号为 1..N 并写回 tasks.jsonl。</summary>
        public static Task<JsonNode> RenumberTasks() => PostJson("/api/tasks/renumber", new { });

        // ================= 播控（手动播放器） =================
        // 协议与网页端完全一致：/api/player/cmd { cmd, val }

        public static Task<JsonNode> PlayerPlay() => PostJson("/api/player/cmd", new { cmd = "play" });
        public static Task<JsonNode> PlayerPause() => PostJson("/api/player/cmd", new { cmd = "pause" });
        public static Task<JsonNode> PlayerStop() => PostJson("/api/player/cmd", new { cmd = "stop" });
        public static Task<JsonNode> PlayerNext() => PostJson("/api/player/cmd", new { cmd = "next" });
        public static Task<JsonNode> PlayerPrev() => PostJson("/api/player/cmd", new { cmd = "prev" });
        public static Task<JsonNode> PlayerPlayIndex(int index) => PostJson("/api/player/cmd", new { cmd = "play_idx", val = index });
        public static Task<JsonNode> PlayerSeek(double seconds) => PostJson("/api/player/cmd", new { cmd = "seek", val = seconds });
        public static Task<JsonNode> PlayerCycleMode() => PostJson("/api/player/cmd", new { cmd = "mode" });
        public static Task<JsonNode> PlayerVolume(int volume) => PostJson("/api/player/cmd", new { cmd = "volume", val = volume });
        public static Task<JsonNode> PlayerQueueAdd(string name, string path, string type)
            => PostJson("/api/player/cmd", new { cmd = "add", val = new { name, path, type } });
        public static Task<JsonNode> PlayerQueueRemove(int index) => PostJson("/api/player/cmd", new { cmd = "remove", val = index });
    }
}
