#nullable disable

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using MessageBox = System.Windows.MessageBox;
using Microsoft.Win32;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 系统设置（最小必要）：开机自启、Web 端口、数据/日志目录入口。
    /// 端口冲突的处理策略是"临时切换、不写回配置"，此处的保存是唯一的落盘入口。
    /// </summary>
    public class SettingsPage : PageBase
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "CampusBroadcastSystem";
        private const string ExeName = "BroadcastSystemNet.exe";

        private readonly TextBox _port = Theme.TextBox("", 90);
        private readonly TextBlock _autoState = Theme.Label("", 12.5);
        private readonly TextBlock _autoDetail = Theme.Label("", 11.5, Theme.TextGray);
        private readonly TextBlock _portState = Theme.Label("", 12);

        public SettingsPage(MainWindow host) : base(host, "系统设置", "最小必要项：开机自启、Web 端口、数据目录") { }

        protected override void Build()
        {
            // 开机自启
            var auto = new StackPanel();
            auto.Children.Add(Theme.Title("开机自启"));
            auto.Children.Add(_autoState);
            auto.Children.Add(_autoDetail);
            auto.Children.Add(Theme.Row(
                Theme.Button("启用开机自启", Theme.Success, Brushes.White, "\uf00c", () => SetAutostart(true)),
                Theme.Button("关闭开机自启", Theme.Danger, Brushes.White, "\uf00d", () => SetAutostart(false))));
            // 静默启动 / 显示悬浮球
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var silentLabel = Theme.Label("静默启动", 12.5, Theme.TextGray);
            silentLabel.ToolTip = "开机自启时用：勾选后开机进系统不显示主界面，只在托盘与悬浮球里运行（手动双击程序仍会正常打开界面）";
            var silentToggle = new SegmentedToggle(new[] { "关", "开" }, 92, "开机自启时用：勾选后开机进系统不显示主界面，只在托盘与悬浮球里运行", Program.GetSilentStart() ? 1 : 0);
            silentToggle.Changed += () =>
            {
                SaveGlobalBool("silent_start", silentToggle.SelectedIndex == 1);
                // 只更新"已经存在"的启动项，别因为切了个显示开关就顺手把开机自启打开
                if (IsAutostartEnabled())
                {
                    SetAutostart(true);   // 重写注册表项，把 --silent 加/去掉
                    SetStatus(silentToggle.SelectedIndex == 1 ? "已开启静默启动，启动项已更新（下次开机不显示主界面）。" : "已关闭静默启动，启动项已更新（下次开机会显示主界面）。", Theme.Success);
                }
                else
                {
                    SetStatus(silentToggle.SelectedIndex == 1
                        ? "已开启静默启动；当前还没启用开机自启，等启用后启动项会自动带 --silent。"
                        : "已关闭静默启动。", Theme.Success);
                }
            };
            toggles.Children.Add(silentLabel);
            toggles.Children.Add(silentToggle);
            var ballLabel = Theme.Label("桌面悬浮球", 12.5, Theme.TextGray);
            ballLabel.Margin = new Thickness(18, 0, 0, 0);
            ballLabel.ToolTip = "显示一个常驻小球：红方块=停用、蓝圆=待机、绿三角=播放中，中间显示距下一个响铃的剩余时间；左键自检、右键菜单";
            var ballToggle = new SegmentedToggle(new[] { "关", "开" }, 92, "显示一个常驻小球：红方块=停用、蓝圆=待机、绿三角=播放中，中间显示距下一个响铃的剩余时间", Program.GetShowBall() ? 1 : 0);
            ballToggle.Changed += () =>
            {
                SaveGlobalBool("show_ball", ballToggle.SelectedIndex == 1);
                SetStatus(ballToggle.SelectedIndex == 1 ? "已开启桌面悬浮球（马上显示）。" : "已关闭桌面悬浮球（马上消失）。", Theme.Success);
            };
            toggles.Children.Add(ballLabel);
            toggles.Children.Add(ballToggle);
            auto.Children.Add(toggles);
            auto.Children.Add(Theme.Label(
                "本程序与安装包可能各自留下一条启动项，这里会同时识别并统一管理：\n" +
                "· 注册表：HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run → CampusBroadcastSystem\n" +
                "· 启动文件夹：安装包默认创建的「智能校园广播系统」快捷方式\n" +
                "点「启用」会合并成一条（注册表），点「关闭」会把两种都清掉；点完可回到本页确认只剩一条。",
                12, Theme.TextGray));
            Root.Children.Add(Card(auto));

            // Web 端口
            var port = new StackPanel();
            port.Children.Add(Theme.Title("Web 服务端口"));
            port.Children.Add(Theme.Row(
                Theme.Label("端口：", 12.5, Theme.TextGray), _port,
                Theme.Button("保存端口", Theme.Primary, Brushes.White, "\uf0c7", SavePort)));
            port.Children.Add(_portState);
            // 日志保留天数（用户要求）：默认 30 天，0 = 永久保留。
            // 启动时自动清理过期日志 —— **只删 logs 目录里的日志文件**，业务数据一律不碰。
            var logKeep = Theme.TextBox("", 70);
            logKeep.Text = VntManager.LogKeepDays.ToString();
            var logState = Theme.Label("", 11.5, Theme.TextGray);
            port.Children.Add(Theme.Row(
                Theme.Label("日志保留天数（0 = 永久）：", 12.5, Theme.TextGray), logKeep,
                Theme.Button("应用", Theme.Primary, System.Windows.Media.Brushes.White, "\uf00c", () =>
                {
                    if (!int.TryParse(logKeep.Text.Trim(), out int d) || d < 0) { logState.Text = "请输入 0 或更大的整数"; return; }
                    VntManager.LogKeepDays = d;
                    logState.Text = d == 0 ? "已保存：永久保留（不再自动清理）" : $"已保存：只保留最近 {d} 天的日志（下次启动时清理）";
                    logState.Foreground = Theme.Success;
                    PlayLog.Write("系统", "日志保留", "info", detail: $"日志保留天数已设为 {(d == 0 ? "永久" : d + " 天")}");
                })));
            port.Children.Add(logState);
            port.Children.Add(Theme.Label(
                "说明：端口被占用时程序会临时改用其它端口继续启动，且不会写回配置（避免设置写错后网页端再也起不来）。这里保存后需重启程序生效。",
                12, Theme.TextGray));
            port.Children.Add(Theme.Row(Theme.Button("立即重启程序", null, null, "\uf021", () => Host.RestartApp())));
            Root.Children.Add(Card(port));

            // 目录与运行信息
            var info = new StackPanel();
            info.Children.Add(Theme.Title("目录与运行信息"));
            info.Children.Add(Theme.Label($"程序目录：{Program.BaseDir}", 12));
            info.Children.Add(Theme.Label($"数据目录：{Program.AppDataDir}", 12));
            info.Children.Add(Theme.Label($"日志目录：{Program.LogDir}", 12));
            info.Children.Add(Theme.Label($"当前实际端口：{Program.ListenPort} · 看板地址 {Program.StartupUrl}", 12));
            info.Children.Add(Theme.Label("图标字体：" + Theme.IconFontStatus, 12));
            info.Children.Add(Theme.Row(
                Theme.Button("打开数据目录", null, null, "\uf07b", () => Host.OpenPath(Program.AppDataDir)),
                Theme.Button("打开日志目录", null, null, "\uf0ae", () => Host.OpenPath(Program.LogDir)),
                Theme.Button("复制看板地址", null, null, "\uf0c5", () =>
                {
                    try { System.Windows.Clipboard.SetText(Program.StartupUrl); SetStatus("已复制看板地址。", Theme.Success); } catch { }
                })));
            Root.Children.Add(Card(info));

            RefreshState();
        }

        private void RefreshState()
        {
            bool enabled = IsAutostartEnabled();
            string reg = RegistryEntry();
            var lnks = StartupShortcuts();
            int total = (reg != null ? 1 : 0) + lnks.Count;

            if (!enabled) _autoState.Text = "当前状态：未启用开机自启";
            else if (total > 1) _autoState.Text = $"当前状态：已启用，但检测到 {total} 条重复启动项";
            else if (reg != null) _autoState.Text = "当前状态：已启用 · 注册表 Run 项";
            else _autoState.Text = "当前状态：已启用 · 启动文件夹快捷方式";
            _autoState.Foreground = !enabled ? Theme.TextGray : (total > 1 ? Theme.Warn : Theme.Success);

            var lines = new System.Collections.Generic.List<string>();
            if (reg != null) lines.Add("· 注册表：" + reg);
            foreach (var l in lnks) lines.Add("· 启动文件夹：" + l);
            if (total > 1) lines.Add("↑ 有重复，点「启用开机自启」即可合并成一条；点「关闭」可全部清除。");
            _autoDetail.Text = lines.Count == 0 ? "当前没有任何启动项。" : string.Join("\n", lines);

            _port.Text = Program.ListenPort.ToString();
            _portState.Text = $"当前实际使用端口：{Program.ListenPort} · 配置文件中的端口需重启后生效";
            _portState.Foreground = Theme.TextGray;
        }

        // ================= 开机自启 =================
        //
        // 历史问题：安装包默认会在「启动」文件夹建一条快捷方式，而程序自己是写注册表 Run 项，
        // 两边互不认识 —— 结果是程序显示"未启用"，用户再点一次「启用」就变成两条启动项。
        // 现在两种来源都识别、都归本页统一管理，任何一次操作后都只保留一条（或零条）。

        /// <summary>注册表 Run 项里的值（没有则返回 null）。</summary>
        private static string RegistryEntry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var v = key?.GetValue(RunValueName)?.ToString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch { return null; }
        }

        /// <summary>找出「启动」文件夹里指向本程序的快捷方式（用户级 + 公共级）。</summary>
        private static System.Collections.Generic.List<string> StartupShortcuts()
        {
            var found = new System.Collections.Generic.List<string>();
            var dirs = new System.Collections.Generic.List<string>();
            try
            {
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup)));
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)));
            }
            catch { }

            foreach (var dir in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                    {
                        if (ShortcutIsOurs(lnk)) found.Add(lnk);
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>
        /// .lnk 里目标路径以明文存放（LinkInfo 段），所以直接按字节找可执行文件名即可，
        /// 不必依赖 WScript.Shell 的 COM（COM 在受限环境里可能不可用）。
        /// </summary>
        private static bool ShortcutIsOurs(string lnkPath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(lnkPath) ?? "";
                if (name.IndexOf("智能校园广播", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("CampusBroadcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                var bytes = File.ReadAllBytes(lnkPath);
                string ansi = System.Text.Encoding.GetEncoding(936).GetString(bytes);
                if (ansi.IndexOf(ExeName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string uni = System.Text.Encoding.Unicode.GetString(bytes);
                return uni.IndexOf(ExeName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>只要注册表项或启动文件夹快捷方式任一存在，就算已启用。</summary>
        /// <summary>
        /// 开机自启启动项的完整命令行（纯函数，便于自检）：
        /// 静默启动靠的就是这里的 --silent，缺了它开机就会弹主界面。
        /// 可执行文件路径带空格时要加引号。
        /// </summary>
        public static string AutostartCommand(string exe, bool silent)
            => "\"" + (exe ?? "") + "\"" + (silent ? " --silent" : "");

        /// <summary>
        /// 自愈：程序启动时若发现"设置里开了静默启动、但已有启动项里没带 --silent"（老版本写下的），
        /// 就地重写成带 --silent 的版本，免得用户还得手动再切一次开关。
        /// </summary>
        public static void SyncAutostartSilentFlag()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(RunValueName) is not string cur || string.IsNullOrWhiteSpace(cur)) return;
                bool silent = Program.GetSilentStart();
                bool hasSilent = cur.IndexOf("--silent", StringComparison.OrdinalIgnoreCase) >= 0;
                if (silent == hasSilent) return;
                string exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                key.SetValue(RunValueName, AutostartCommand(exe, silent));
                PlayLog.Write("系统", "开机自启", "info",
                    detail: (silent ? "启动项补上 --silent（静默启动生效）" : "启动项去掉 --silent（恢复显示主界面）") + "：" + AutostartCommand(exe, silent));
            }
            catch { }
        }

        public static bool IsAutostartEnabled()
        {
            if (RegistryEntry() != null) return true;
            return StartupShortcuts().Count > 0;
        }


        /// <summary>把布尔开关写进 setting.jsonl 的 global 记录。</summary>
        private static void SaveGlobalBool(string key, bool value)
        {
            try
            {
                var all = Program.ReadSettingMerged();
                foreach (var n in all)
                {
                    if (n is not System.Text.Json.Nodes.JsonObject o || o["_type"]?.ToString() != "global") continue;
                    o[key] = value;
                    break;
                }
                Program.WriteSettingMerged( all);
            }
            catch { }
        }

        // ---- 占位：保持原有方法顺序 ----
        private void SetAutostartKeep()
        { }


        private void SetAutostart(bool enable)
        {
            try
            {
                string exe = Environment.ProcessPath;
                var shortcuts = StartupShortcuts();
                int removed = 0;

                if (enable)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                    if (key == null) { SetStatus("无法写入注册表 Run 项。", Theme.Danger); return; }
                    // 静默启动就是靠启动项里的 --silent 实现的：原来这里只写 "exe"，没带参数，
                    // 结果开机启动照样弹主界面 → 用户反馈"静默启动无效"。现在按设置写入。
                    bool silent = Program.GetSilentStart();
                    key.SetValue(RunValueName, AutostartCommand(exe, silent));
                    // 已经有注册表项就够了，把启动文件夹里重复的快捷方式清掉，避免出现两条启动项
                    foreach (var lnk in shortcuts)
                    {
                        try { File.Delete(lnk); removed++; } catch { }
                    }
                    PlayLog.Write("系统", "开机自启", "info",
                        detail: "已启用：" + AutostartCommand(exe, silent) + (removed > 0 ? $"，并清理了 {removed} 条重复的启动文件夹快捷方式" : ""));
                    SetStatus(removed > 0 ? $"已启用开机自启，并清理了 {removed} 条重复启动项（现在只剩注册表一条）。" : "已启用开机自启。", Theme.Success);
                }
                else
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                    if (key?.GetValue(RunValueName) != null) { key.DeleteValue(RunValueName, false); removed++; }
                    foreach (var lnk in shortcuts)
                    {
                        try { File.Delete(lnk); removed++; } catch { }
                    }
                    PlayLog.Write("系统", "开机自启", "warn", detail: removed > 0 ? $"已关闭，清理 {removed} 条启动项" : "已关闭（本来就没有启动项）");
                    SetStatus(removed > 0 ? $"已关闭开机自启，清理了 {removed} 条启动项。" : "已关闭开机自启（本来就没有启动项）。", Theme.Warn);
                }
                RefreshState();
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus("设置开机自启失败：注册表写入被拒绝。常见原因是安全软件防护了启动项，请把本程序加入信任区后重试（或直接手动把快捷方式放进“启动”文件夹）。", Theme.Danger);
                PlayLog.Write("系统", "开机自启失败", "error", detail: "注册表写入被拒绝（可能被安全软件拦截）");
            }
            catch (Exception ex) { SetStatus("设置开机自启失败：" + ex.Message, Theme.Danger); }
        }

        // ================= 端口 =================

        private void SavePort()
        {
            if (!int.TryParse(_port.Text.Trim(), out int p) || p < 1 || p > 65535)
            {
                SetStatus("端口必须是 1-65535 之间的整数。", Theme.Danger);
                return;
            }
            try
            {
                var all = Program.ReadSettingMerged();
                JsonObject globalItem = null;
                foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "global") { globalItem = o; break; }
                if (globalItem == null)
                {
                    globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" };
                    all.Insert(0, globalItem);
                }
                globalItem["web_port"] = p;
                if (globalItem["sys_volume"] != null) globalItem.Remove("sys_volume");
                Program.WriteSettingMerged( all);
                PlayLog.Write("系统", "端口设置", "warn", detail: $"已保存端口 {p}（重启后生效）");
                _portState.Text = $"已保存 {p}，重启程序后生效。";
                _portState.Foreground = Theme.Success;
                if (AppDialog.Confirm(Host, "需要重启",
                    $"端口已保存为 {p}。\n\n是否立即重启程序使其生效？",
                    "立即重启", "稍后手动重启"))
                {
                    Host.RestartApp();
                }
            }
            catch (Exception ex) { SetStatus("保存端口失败：" + ex.Message, Theme.Danger); }
        }
    }
}
