#nullable disable

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 「本地导入调休数据」独立窗口：网络拉不到订阅时，用文件或粘贴的 JSON 把节假日写进调休文件。
    /// 与订阅同步共用同一套解析与合并逻辑（后台 /api/tiaoxiu/import），所以结果完全一致：
    /// 手动规则优先、区间自动合并、非法数据单独统计。
    /// </summary>
    public class ImportWindow : Window
    {
        private readonly TextBox _paste = Theme.TextBox("");
        private readonly TextBlock _status = Theme.Label("", 12.5, Theme.TextGray);
        private readonly TextBlock _preview = Theme.Label("", 11.5, Theme.TextGray);

        public ImportWindow()
        {
            Title = "本地导入调休数据";
            Width = 720;
            Height = Math.Min(620, Math.Max(320, System.Windows.SystemParameters.WorkArea.Height - 120));   // 高缩放修复
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var body = new StackPanel { Margin = new Thickness(18) };

            body.Children.Add(new TextBlock
            {
                Text = "本地导入调休数据",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark
            });
            body.Children.Add(new TextBlock
            {
                Text = "网络拉不到云端订阅时用这里：把已经下载好的节假日数据导进来。"
                     + "与云端订阅是同一套自动识别：小米节假日接口、苹果 iCloud 日历（ICS）、holiday-cn、程序原生 JSON 四种都能认。"
                     + "写入规则与云端同步完全一致——手动规则优先、日期区间自动合并。",
                FontSize = 12,
                Foreground = Theme.TextGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 12)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var pickFile = Theme.Button("从文件导入…", Theme.Primary, Brushes.White, "\uf15b", () => _ = ImportFromFile());
            pickFile.ToolTip = "选择本地的节假日文件：holiday-cn / 原生 JSON，或苹果 iCloud 的 .ics 日历";
            buttons.Children.Add(pickFile);
            var doPaste = Theme.Button("导入粘贴内容", Theme.Success, Brushes.White, "\uf0ea", () => _ = ImportPasted());
            doPaste.ToolTip = "把下面框里的内容导入（JSON 或 ICS 文本都可以）";
            doPaste.Margin = new Thickness(0);
            buttons.Children.Add(doPaste);
            body.Children.Add(buttons);

            body.Children.Add(new TextBlock
            {
                Text = "或者把内容全文粘到下面（Ctrl+A 全选、Ctrl+V 粘贴，然后点上面的「导入粘贴内容」）：",
                FontSize = 11.5,
                Foreground = Theme.TextGray,
                Margin = new Thickness(0, 12, 0, 4)
            });

            _paste.AcceptsReturn = true;
            _paste.TextWrapping = TextWrapping.Wrap;
            _paste.VerticalContentAlignment = VerticalAlignment.Top;
            _paste.FontSize = 11.5;
            _paste.Height = 260;
            _paste.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _paste.ToolTip = "节假日内容全文；支持 holiday-cn / 程序原生 JSON / 小米接口 / 苹果 iCloud ICS";
            body.Children.Add(_paste);

            _status.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(_status);
            _preview.Margin = new Thickness(0, 4, 0, 0);
            _preview.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(_preview);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var close = Theme.Button("关闭", null, null, "\uf00d", () => Close());
            close.Margin = new Thickness(0);
            footer.Children.Add(close);
            body.Children.Add(footer);

            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(14),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = body
                }
            };
        }

        /// <summary>从本地文件导入。</summary>
        private async Task ImportFromFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择节假日 JSON 文件",
                Filter = "JSON 文件|*.json;*.txt|所有文件|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            string text;
            try { text = await System.IO.File.ReadAllTextAsync(dlg.FileName); }
            catch (Exception ex)
            {
                _status.Text = "读取文件失败：" + ex.Message;
                _status.Foreground = Theme.Danger;
                return;
            }
            await ImportText(text, "文件 " + System.IO.Path.GetFileName(dlg.FileName));
        }

        /// <summary>导入粘贴框里的内容。</summary>
        private async Task ImportPasted()
        {
            string text = _paste.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _status.Text = "粘贴框是空的，请先把 JSON 内容粘进去。";
                _status.Foreground = Theme.Warn;
                return;
            }
            await ImportText(text, "粘贴内容");
        }

        /// <summary>交给后台解析并写入（与云端同步同一套逻辑）。</summary>
        private async Task ImportText(string text, string sourceName)
        {
            try
            {
                _status.Text = "正在解析并写入…";
                _status.Foreground = Theme.TextGray;
                _preview.Text = "";
                var r = await DesktopApi.PostJson("/api/tiaoxiu/import", new { text });
                string msg = r?["message"]?.ToString() ?? "导入完成";
                bool ok = r?["status"]?.ToString() == "success";
                _status.Text = (ok ? "已导入（" + sourceName + "）：" : "导入失败（" + sourceName + "）：") + msg;
                _status.Foreground = ok ? Theme.Success : Theme.Danger;
                if (ok)
                {
                    string fmt = r?["format"]?.ToString() ?? "自动";
                    _preview.Text = $"识别格式：{fmt}　·　写入记录：{r?["applied"]} 条　·　覆盖天数：{r?["applied_days"]} 天"
                                  + $"　·　被手动规则优先覆盖：{r?["overridden_days"]} 天　·　非法：{r?["invalid"]} 条\n"
                                  + "提示：导入完成后可回到「调休管理」页查看与微调这些规则。";
                }
            }
            catch (Exception ex)
            {
                _status.Text = "导入失败：" + ex.Message;
                _status.Foreground = Theme.Danger;
            }
        }
    }
}
