#nullable disable

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>运行总览：状态、当前播放、下一次排程、看板入口、数据位置。</summary>
    public class OverviewPage : PageBase
    {
        private readonly TextBlock _stateText = Theme.Label("读取中…", 14);
        private readonly TextBlock _playText = Theme.Label("—", 12.5);
        private readonly TextBlock _nextText = Theme.Label("—", 12.5);
        private readonly TextBlock _statsText = Theme.Label("—", 12.5);
        private readonly TextBlock _webText = Theme.Label("—", 12.5);
        private readonly TextBlock _pathText = Theme.Label("—", 12.5);
        private readonly TextBlock _micText = Theme.Label("—", 12, Theme.TextGray);
        private TextBlock _micCode;
        private System.Windows.Controls.Image _qrImage;
        private readonly TextBlock _qrState = new TextBlock();
        /// <summary>上次画二维码用的载荷：变了就自动重画（用户要求"重连信息有变自动刷新"）。</summary>
        private string _lastQrPayload = "";

        /// <summary>
        /// 画/刷新连接二维码。
        ///
        /// 规则（用户要求）：
        ///   · **组网成功（内置 vnt-lite 拿到虚拟 IP）才生成**；没入网就只显示一行提示；
        ///   · 界面上不放配置信息（都在设置窗口里），只留一行组网状态提示；
        ///   · 服务器/令牌/密码/虚拟IP/端口/主机名任一变化 → 载荷变 → 自动重画；
        ///   · 也能手动点「重新生成」强制重画。
        /// </summary>
        private void RefreshQr(bool force = false)
        {
            try
            {
                string vip = VntManager.LocalVirtualIp;
                bool online = VntProcess.IsRunning && !string.IsNullOrWhiteSpace(vip);
                if (!online)
                {
                    _qrImage.Source = null;
                    _qrState.Text = VntProcess.IsRunning
                        ? "组网中：拿到虚拟地址后自动生成二维码"
                        : "尚未组网：组网成功后自动生成二维码";
                    _qrState.Foreground = Theme.Danger;
                    _lastQrPayload = "";
                    return;
                }
                string payload = VntManager.BuildQrPayloadFromConfig(
                    MicServer.AdvertisedHost, VntManager.MicPort, VntManager.MicCtlPort,
                    Environment.MachineName, Program.ListenPort);
                if (!force && payload == _lastQrPayload) return;
                _qrImage.Source = QrCode.Render(payload);
                _lastQrPayload = payload;
                _qrState.Text = "组网成功：手机扫码即可入网";
                _qrState.Foreground = Theme.Success;
            }
            catch (Exception ex)
            {
                _qrState.Text = "二维码生成失败：" + ex.Message;
                _qrState.Foreground = Theme.Danger;
            }
        }
        private readonly TextBlock _iconText = Theme.Label(
            Theme.HasIconFont ? "已加载 Font Awesome 图标字体" : "未找到 fa-solid-900.ttf，界面使用文字回退（不影响功能）", 12, Theme.TextGray);

        /// <summary>刷新「话筒配对」卡片：6 位动态码 + 服务状态。</summary>
        private void UpdateMicLine()
        {
            try
            {
                string code = MicProtocol.CurrentCode(VntManager.MicCodeSeed, DateTime.UtcNow);
                int left = (int)(MicProtocol.CodePeriodSeconds - (DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % MicProtocol.CodePeriodSeconds));
                _micCode.Text = code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code;
                _micText.Text = $"手机上输这 6 位即可配对；剩余 {left} 秒自动换码，一码只能用一次"
                    + $"\n话筒服务：{(MicServer.Running ? "在跑" : "未启动")}　已接入：{MicServer.Registry.Count} 台";
            }
            catch (Exception ex) { _micText.Text = "读取话筒状态失败：" + ex.Message; }
        }

        public OverviewPage(MainWindow host) : base(host, "运行总览", "系统状态、播放情况与入口") { }

        protected override void Build()
        {
            // 布局（用户要求）：**连接二维码放右侧**，左边四张卡片（系统状态 / 网页看板 / 数据与日志 / 话筒配对）。
            // 左边用星号宽度自适应窗口，右边固定 330；左列卡片限宽 560，窗口拉宽时不会变得又扁又长。
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var right = new StackPanel();

            var info = new StackPanel();
            info.Children.Add(Theme.Title("系统状态"));
            info.Children.Add(_stateText);
            info.Children.Add(_playText);
            info.Children.Add(_nextText);
            info.Children.Add(_statsText);
            left.Children.Add(Card(info));

            // 话筒配对（新增卡片）：**直接显示 6 位动态码**（用户要求：话筒配对码就是这 6 位，没别的）
            var mic = new StackPanel();
            mic.Children.Add(Theme.Title("话筒配对"));
            _micCode = new TextBlock
            {
                Text = "—— —— ——", FontSize = 34, FontWeight = FontWeights.Bold,
                Foreground = Theme.Primary,
                // 【底部留白】大字号文本的"墨迹"会略微溢出它自己的行框，而下一行是**后绘制**的，
                // 会把溢出的下边缘盖住几个像素（用户反馈"配对码下边缘被遮挡"）。
                // 原来只留 2px，不够；这里加到 10px，给字形底部留出余量。
                Margin = new Thickness(0, 2, 0, 10)
            };
            // 【字形平滑】主窗口整体用 TextFormattingMode.Display（按像素对齐，小字更实），
            // 但 34px 粗体数字在 Display 下竖笔画会落在半像素上，边缘出现粗细不匀的台阶。
            // 这一处单独用 Ideal（亚像素定位）+ 关闭布局取整，边缘过渡更平滑；
            // 注意透明窗口上 WPF 会禁用 ClearType，所以仍是灰度抗锯齿，这是本方案的平滑上限。
            System.Windows.Media.TextOptions.SetTextFormattingMode(
                _micCode, System.Windows.Media.TextFormattingMode.Ideal);
            _micCode.UseLayoutRounding = false;
            mic.Children.Add(_micCode);
            mic.Children.Add(_micText);
            mic.Children.Add(Theme.Row(
                Theme.Button("配置话筒", Theme.Primary, Brushes.White, "\uf130", () =>
                {
                    try { Host.ShowChildDialog(new MicSwitchWindow()); } catch { }
                    UpdateMicLine();
                })));
            left.Children.Add(Card(mic));

            var web = new StackPanel();
            var webTitle = Theme.Title("网页看板"); webTitle.ToolTip = "远程控制入口：手机或其他电脑打开同一地址即可操作，桌面端只做本机管理"; web.Children.Add(webTitle);
            web.Children.Add(_webText);
            var webBtns = Theme.Row(
                Theme.Button("打开网页看板", Theme.Primary, Brushes.White, "\uf0ac", () => Host.OpenWebDashboard()),
                Theme.Button("复制看板地址", null, null, "\uf0c5", () =>
                {
                    try { System.Windows.Clipboard.SetText(Program.StartupUrl); SetStatus("已复制：" + Program.StartupUrl, Theme.Success); } catch { }
                }),
                Theme.Button("刷新状态", null, null, "\uf021", () => _ = RefreshAsync()));
            web.Children.Add(webBtns);
            left.Children.Add(Card(web));

            var data = new StackPanel();
            data.Children.Add(Theme.Title("数据与日志位置"));
            data.Children.Add(_pathText);
            data.Children.Add(Theme.Row(
                Theme.Button("打开数据目录", null, null, "\uf07b", () => Host.OpenPath(Program.AppDataDir)),
                Theme.Button("打开日志目录", null, null, "\uf0ae", () => Host.OpenPath(Program.LogDir)),
                Theme.Button("复制数据目录路径", null, null, "\uf0c5", () =>
                {
                    try { System.Windows.Clipboard.SetText(Program.AppDataDir); SetStatus("已复制数据目录路径。", Theme.Success); } catch { }
                })));
            data.Children.Add(_iconText);
            left.Children.Add(Card(data));


            // 右侧：连接二维码（用户要求：只放码 + 一行组网状态，配置项都在设置窗口里；可重新生成；信息变了自动刷新）
            var qrPanel = new StackPanel();
            qrPanel.Children.Add(Theme.Title("连接二维码"));
            _qrState.Text = "尚未组网：组网成功后自动生成";
            _qrState.FontSize = 12; _qrState.TextWrapping = TextWrapping.Wrap;
            _qrState.Foreground = Theme.TextGray;
            qrPanel.Children.Add(_qrState);
            // 二维码用 Viewbox 包住：**随窗口缩放**（正方形保持比例，窗口拉大就变大，不会留死尺寸）
            _qrImage = new System.Windows.Controls.Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch
            };
            // 最近邻放大：模块边缘不会被插值糊掉（糊了扫码识别率会掉）
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(_qrImage, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
            var qrBox = new System.Windows.Controls.Viewbox
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            qrBox.Child = _qrImage;
            qrPanel.Children.Add(qrBox);
            var qrBtns = Theme.Row(
                Theme.Button("重新生成", Theme.Primary, Brushes.White, "\uf021", () => RefreshQr(force: true)),
                Theme.Button("配置窗口", null, null, "\uf013", () =>
                {
                    try { Host.ShowChildDialog(new VntSettingsWindow()); } catch { }
                    RefreshQr(force: true);
                }));
            qrBtns.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;   // 两个按钮居中对齐（用户要求）
            qrPanel.Children.Add(qrBtns);
            right.Children.Add(Card(qrPanel));

            Grid.SetColumn(left, 0); Grid.SetColumn(right, 1);
            grid.Children.Add(left); grid.Children.Add(right);
            Root.Children.Add(grid);

            RefreshQr(force: true);
            _ = RefreshAsync();

            // 系统状态实时更新：复用主窗口现成的 900ms 状态广播（不新增接口、不新增轮询者、网页端零改动）。
            // 页面每次导航都是新实例，必须在 Unloaded 解绑，否则事件会累积。
            Host.SysUpdated += OnSys;
            Root.Unloaded += (s, e) => Host.SysUpdated -= OnSys;
        }

        /// <summary>主窗口广播的系统状态：直接套用到界面。</summary>
        private void OnSys(JsonObject sys)
        {
            try
            {
                ApplySys(sys);
                UpdateMicLine();      // 6 位动态码每秒都在变，顺手刷新
                RefreshQr();          // 组网信息变了就自动重画二维码
            }
            catch { }
        }

        /// <summary>把 /api/sys 的返回套到界面文本上（手动刷新与实时广播共用）。</summary>
        private void ApplySys(JsonObject sys)
        {
            bool on = Program.IsGlobalSwitchOn();
            bool playing = sys?["is_playing"]?.GetValue<bool>() ?? false;
            _stateText.Text = on ? (playing ? "运行中 · 正在播放" : "运行中 · 待机") : "已停用 · 全局静态";
            _stateText.Foreground = on ? Theme.Success : Theme.Danger;
            _playText.Text = "当前播放：" + (playing ? (sys?["current_playing_name"]?.ToString() ?? "未知") : "无");
            _nextText.Text = "下一次排程：" + NextTask();
            var st = sys?["sys_stats"] as JsonObject;
            _statsText.Text = $"资源占用：CPU {st?["cpu"]?.GetValue<double>() ?? 0:F1}%　RAM {st?["ram"]?.GetValue<double>() ?? 0:F1}%";
            _webText.Text = $"本机访问：{Program.StartupUrl}　　局域网：http://<本机IP>:{Program.ListenPort}";
            _pathText.Text = $"数据目录：{Program.AppDataDir}";
        }

        private async Task RefreshAsync()
        {
            try
            {
                var sys = await DesktopApi.GetSys();
                ApplySys(sys);
                SetStatus("状态已更新：" + DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception ex) { SetStatus("刷新失败：" + ex.Message, Theme.Danger); }
        }

        private string NextTask()
        {
            try
            {
                var tasks = Program.ReadJsonl("tasks.jsonl");
                var now = DateTime.Now; int nowSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
                int best = int.MaxValue; string bestText = null;
                foreach (var t in tasks)
                {
                    var o = t as JsonObject; if (o == null) continue;
                    if (!Program.ShouldPlayTodayForUi(o)) continue;
                    string s = o["start_time"]?.ToString() ?? ""; if (s.Length == 5) s += ":00";
                    var p = s.Split(':'); if (p.Length != 3) continue;
                    if (!int.TryParse(p[0], out int h) || !int.TryParse(p[1], out int m) || !int.TryParse(p[2], out int sec)) continue;
                    int at = h * 3600 + m * 60 + sec;
                    int delta = at - nowSec; if (delta < 0) delta += 86400;
                    if (delta < best) { best = delta; bestText = $"{h:00}:{m:00}"; }
                }
                return bestText == null ? "今日无有效排程" : $"{bestText} · 约 {best / 60} 分钟后";
            }
            catch { return "读取失败"; }
        }
    }
}
