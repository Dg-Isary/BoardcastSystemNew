#nullable disable

using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>订阅配置窗口：订阅地址、格式、轮询周期、预览解析、立即同步。</summary>
    public class SubscriptionWindow : Window
    {
        private readonly TextBox _url = Theme.TextBox("", 520);   // 多行：一行一条订阅地址（第一条是主源，其余是备用）
        private bool _allowEmpty;      // 只有点了「清空订阅组」才允许把订阅组写成空
        private readonly ComboBox _interval = Theme.NumberCombo(new[] { 1, 2, 4, 6, 12, 24, 48, 168, 720 }, 116, "多久自动同步一次（小时）：可点开选常用值，也可直接输入其它值");
        private readonly ComboBox _markerOff = Theme.Combo(112);   // 禁用记录的标记
        private readonly ComboBox _markerOn = Theme.Combo(112);    // 启用记录的标记
        private readonly SegmentedToggle _actOff;                  // 放假日写成什么（两态，点击切换）
        private readonly SegmentedToggle _actOn;                   // 调休上班日写成什么（两态，点击切换）
        private readonly TextBlock _status = Theme.Label("", 12, Theme.TextGray);
        private readonly TextBlock _preview = Theme.Label("", 11.5, Theme.TextGray);


        public SubscriptionWindow()
        {
            Title = "云端调休订阅设置";
            Width = 760; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Bg;
            ResizeMode = ResizeMode.CanResize;   // 高缩放修复
            foreach (var cb in new[] { _markerOff, _markerOn })
            {
                cb.Items.Add("0 · 全局");
                for (int i = 1; i <= 10; i++) cb.Items.Add(i + " · 标记 " + i);
                cb.SelectedIndex = 0;
                cb.ToolTip = "0 = 全局，当天所有任务都受影响；1-10 = 只影响绑定了同标记的任务";
                cb.MaxDropDownHeight = 260;
            }
            _actOff = new SegmentedToggle(new[] { "禁用", "启用" }, 108, "云端「放假日」写成什么规则：点一下在禁用与启用之间切换");
            _actOn = new SegmentedToggle(new[] { "启用", "禁用" }, 108, "云端「调休上班日」写成什么规则：点一下在启用与禁用之间切换");

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(Theme.Title("云端调休订阅", 16));
            panel.Children.Add(Theme.Label(
                "格式始终自动识别（按内容判断，无需设置），支持四种订阅源：\n" +
                "· 小米节假日接口：api.comm.miui.com/holiday/holiday.jsp\n" +
                "· 苹果 iCloud 中国大陆节假日日历（ICS）：p10-calendars.icloud.com/holidays/CN_ZH.ics\n" +
                "· holiday-cn 节假日数据：{\"year\":2026,\"days\":[{\"name\":\"元旦\",\"date\":\"2026-01-01\",\"isOffDay\":true}]}\n" +
                "   放假日（isOffDay=true）→ 不播音；调休上班日（false）→ 照常打铃；节日名会写进备注。\n" +
                "· 原生记录数组：[{\"date\":\"2026-10-01\",\"action\":\"disable\",\"marker\":0,\"note\":\"国庆\"}]\n" +
                "地址里可以用 {year} 占位符（例如 .../holiday-cn/master/{year}.json），每年自动换成当年年份；\n" +
                "小米与 iCloud 是多年接口，地址里不需要占位符。\n" +
                "订阅组：一行填一条，从上往下试，任意一条拉取并解析成功即算成功（例如第 1 行填 jsDelivr 镜像、第 2 行填 GitHub 原始地址）。",
                11.5, Theme.TextGray));
            panel.Children.Add(new TextBlock { Height = 10 });

            var urlLabel = Theme.Label("订阅地址", 12.5, Theme.TextGray); urlLabel.ToolTip = "订阅组：一行一条，从上往下试，任意一条拉取并解析成功即算成功；全部删空 = 关闭云端订阅"; panel.Children.Add(urlLabel);
            _url.AcceptsReturn = true;
            _url.TextWrapping = TextWrapping.NoWrap;
            _url.VerticalContentAlignment = VerticalAlignment.Top;
            _url.Height = 84;
            _url.FontSize = 12.5;
            _url.Margin = new Thickness(0, 2, 0, 8);
            panel.Children.Add(_url);
            panel.Children.Add(Theme.Row(PeriodLabel(), _interval));
            var clearSub = Theme.Button("清空订阅组", Theme.Danger, Brushes.White, "\uf1f8", () =>
            {
                _url.Text = "";
                _allowEmpty = true;   // 显式授权：这次允许把订阅组写成空
                if (!Persist()) return;   // 保存没成功就不同步，避免显示"没有订阅地址"误导
            });
            clearSub.ToolTip = "关闭云端订阅：把订阅地址清空并保存。清空后不再自动拉取，随时可点「恢复默认订阅组」恢复。";

var amLabel = Theme.Label("云端记录的动作与标记", 12.5, Theme.TextGray); amLabel.ToolTip = "holiday-cn 只提供日期，这两项决定写进调休文件的规则内容"; panel.Children.Add(amLabel);
            panel.Children.Add(Theme.Row(
                Theme.Label("禁用标记", 12.5, Theme.TextGray), _markerOff,
                Theme.Label("启用标记", 12.5, Theme.TextGray), _markerOn,
                Theme.Label("放假日", 12.5, Theme.TextGray), _actOff,
                Theme.Label("上班日", 12.5, Theme.TextGray), _actOn));
            panel.Children.Add(Theme.Label("合并策略：手动记录优先 —— 手动规则覆盖的整个日期区间都不会被云端记录改写。", 12, Theme.TextGray));
            panel.Children.Add(new TextBlock { Height = 8 });
            panel.Children.Add(Theme.Row(
                Theme.Button("恢复默认订阅组", null, null, "\uf01e", RestoreDefaults),
                Theme.Button("预览解析结果", null, null, "\uf002", () => _ = PreviewAsync()),
                Theme.Button("保存并立即同步", Theme.Primary, Brushes.White, "\uf0ed", () => _ = SaveAndSyncAsync()),
                clearSub,
                Theme.Button("关闭", null, null, "\uf00d", Close)));
            panel.Children.Add(_status);
            panel.Children.Add(_preview);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Loaded += async (s, e) => await LoadAsync();
        }

        private string FormatCode => "auto";   // 格式始终自动识别（native / holiday-cn / miui）

        private async Task LoadAsync()
        {
            try
            {
                var all = Program.ReadSettingMerged();
                foreach (var n in all)
                {
                    if (n is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                    var arr = o["subscription_urls"] as JsonArray;
                    if (arr != null && arr.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var u in arr) { string s = u?.ToString(); if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s); }
                        _url.Text = sb.ToString().TrimEnd();
                    }
                    else _url.Text = o["subscription_url"]?.ToString() ?? "";
                    if (o["subscription_interval"] != null) _interval.Text = o["subscription_interval"].ToString();
                    int mk = 0; int.TryParse((o["subscription_marker_disable"] ?? o["subscription_marker"])?.ToString(), out mk);
                    _markerOff.SelectedIndex = (mk >= 0 && mk <= 10) ? mk : 0;
                    int mk2 = 0; int.TryParse((o["subscription_marker_enable"] ?? o["subscription_marker"])?.ToString(), out mk2);
                    _markerOn.SelectedIndex = (mk2 >= 0 && mk2 <= 10) ? mk2 : 0;
                    _actOff.SelectedIndex = (o["subscription_action_off"]?.ToString() == "enable") ? 1 : 0;
                    _actOn.SelectedIndex = (o["subscription_action_on"]?.ToString() == "disable") ? 1 : 0;
                    break;
                }
            }
            catch { }
            await Task.CompletedTask;
        }

        /// <summary>轮询周期标签。</summary>
        private static TextBlock PeriodLabel()
        {
            var l = Theme.Label("轮询周期", 12.5, Theme.TextGray);
            l.ToolTip = "多久自动向云端同步一次，单位：小时；可点开选常用值，也可直接输入";
            return l;
        }

        /// <summary>把内置的默认订阅组填进地址栏（还需点保存才生效）。</summary>
        private void RestoreDefaults()
        {
            _url.Text = string.Join("\r\n", Program.DefaultSubscriptionUrls);
            _allowEmpty = false;
            _status.Text = "已填入默认订阅组，点「保存并立即同步」即可生效。";
            _status.Foreground = Theme.TextGray;
        }
        private bool Persist()
        {
            var all = Program.ReadSettingMerged();
            JsonObject globalItem = null;
            foreach (var n in all) if (n is JsonObject o && o["_type"]?.ToString() == "global") { globalItem = o; break; }
            if (globalItem == null)
            {
                globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" };
                all.Insert(0, globalItem);
            }
            var urls = new JsonArray();
            foreach (var line in (_url.Text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim();
                if (!string.IsNullOrWhiteSpace(s)) urls.Add(s);
            }
            // 空地址栏 + 原来有订阅组 且不是显式点「清空订阅组」→ 拒绝保存，避免误清空
            var oldUrls = globalItem["subscription_urls"] as JsonArray;
            if (urls.Count == 0 && oldUrls != null && oldUrls.Count > 0 && !_allowEmpty)
            {
                _status.Text = "地址栏是空的，已忽略这次保存（以免误清空订阅组）。确实要关闭云端订阅请点「清空订阅组」。"; _status.Foreground = Theme.Warn;
                PlayLog.Write("订阅", "配置保存", "warn", detail: "地址栏为空且原订阅组非空，已忽略本次保存");
                return false;   // 没保存成功，调用方不要再往下走
            }
            globalItem["subscription_urls"] = urls;
            globalItem["subscription_url"] = urls.Count > 0 ? urls[0]?.ToString() : "";
            globalItem["subscription_marker_disable"] = _markerOff.SelectedIndex;
            globalItem["subscription_marker_enable"] = _markerOn.SelectedIndex;
            globalItem["subscription_marker"] = 0;
            globalItem["subscription_action_off"] = _actOff.SelectedIndex == 1 ? "enable" : "disable";
            globalItem["subscription_action_on"] = _actOn.SelectedIndex == 1 ? "disable" : "enable";
            int hours = int.TryParse(_interval.Text, out int h) && h > 0 ? h : 12;
            globalItem["subscription_interval"] = hours;
            _allowEmpty = false;   // 保存完复位，避免下次空提交被放行
            if (globalItem["sys_volume"] != null) globalItem.Remove("sys_volume");
            Program.WriteSettingMerged( all);
            Program.UpdateSubscriptionTimer();
            if (urls.Count == 0)
            {
                PlayLog.Write("订阅", "配置保存", "warn", detail: "订阅组已清空（用户主动关闭云端订阅）");
                AlertCenter.Publish("订阅", "订阅组已清空，云端订阅已关闭。如需恢复，点订阅设置里的「恢复默认订阅组」。", "warn", dedupKey: "sub-cleared");
            }
            else PlayLog.Write("订阅", "配置保存", "info", detail: $"订阅组 {urls.Count} 条，格式 {FormatCode}，周期 {hours} 小时");
            return true;
        }


        /// <summary>只拉取+转写，不写库：先看会被写成什么，再决定要不要同步。</summary>
        private async Task PreviewAsync()
        {
            _status.Text = "正在拉取并解析…"; _status.Foreground = Theme.TextGray;
            _preview.Text = "";
            var r = await DesktopApi.PreviewSubscription(null, FormatCode);
            if (r?["status"]?.ToString() != "success")
            {
                _status.Text = "解析失败：" + (r?["message"]?.ToString() ?? "无法访问订阅地址");
                _status.Foreground = Theme.Danger;
                return;
            }
            int total = 0, off = 0, on = 0;
            try { total = r["total"]?.GetValue<int>() ?? 0; off = r["off_days"]?.GetValue<int>() ?? 0; on = r["on_days"]?.GetValue<int>() ?? 0; } catch { }
            _status.Text = $"解析成功：识别为「{r["format"]}」，共 {total} 天 · 不播音 {off} 天 / 打铃 {on} 天。下面是要写入的样例：";
            _status.Foreground = Theme.Success;

            var sb = new StringBuilder();
            if (r["sample"] is JsonArray sample)
            {
                foreach (var n in sample)
                {
                    if (n is not JsonObject o) continue;
                    sb.AppendLine($"{o["date"]}　{(o["action"]?.ToString() == "disable" ? "不播音" : "照常打铃")}　{o["note"]}");
                }
                if (total > sample.Count) sb.AppendLine($"…（其余 {total - sample.Count} 天同理）");
            }
            _preview.Text = sb.ToString().TrimEnd();
        }



        private async Task SaveAndSyncAsync()
        {
            if (!Persist()) return;   // 保存没成功就不往下同步（避免"没有订阅地址"的误导提示）
            _status.Text = "正在向云端拉取并写入…"; _status.Foreground = Theme.TextGray;
            var r = await DesktopApi.SyncSubscription();
            bool ok = r?["status"]?.ToString() == "success";
            if (ok)
            {
                string msg = r?["message"]?.ToString();
                int invalid = 0; try { invalid = r?["invalid"]?.GetValue<int>() ?? 0; } catch { }
                _status.Text = string.IsNullOrEmpty(msg) ? "同步成功。" : msg;
                if (invalid > 0) _status.Text += "　（非法数据已在“调休管理”底部标红）";
                _status.Foreground = invalid > 0 ? Theme.Warn : Theme.Success;
            }
            else
            {
                _status.Text = "同步失败：" + (r?["message"]?.ToString() ?? "请检查订阅地址是否可访问、内容格式是否与所选类型一致。");
                _status.Foreground = Theme.Danger;
            }
        }
    }
}
