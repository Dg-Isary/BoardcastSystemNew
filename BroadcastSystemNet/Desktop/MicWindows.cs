using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using PixelFormats = System.Windows.Media.PixelFormats;
using Size = System.Windows.Size;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>两个话筒窗口的离屏渲染（不显示窗口、不联网、不改数据），用于人工核对版式。</summary>
    public static class MicPreview
    {
        /// <summary>which: "vnt" = vnt 设置窗；"mic" = 话筒开关窗。</summary>
        public static string Render(string which, string outPath, int width = 620)
        {
            try
            {
                Window win = string.Equals(which, "mic", StringComparison.OrdinalIgnoreCase)
                    ? new MicSwitchWindow()
                    : (Window)new VntSettingsWindow();
                if (width <= 0) width = 620;
                win.Width = width;
                var root = win.Content as FrameworkElement;
                if (root == null) return "窗口没有内容，无法渲染";
                root.Measure(new Size(width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
                root.UpdateLayout();
                int h = (int)Math.Ceiling(root.DesiredSize.Height);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                if (string.IsNullOrWhiteSpace(outPath))
                    outPath = System.IO.Path.Combine(Program.AppDataDir, which + "-preview.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath) ?? Program.AppDataDir);
                using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                return outPath + $"（{width}×{h}）";
            }
            catch (Exception ex) { return "渲染失败：" + ex.Message; }
        }
    }

    /// <summary>「vnt 设置」窗：服务器/token/设备ID/协议/加密 + 状态 + 在线设备 + 二维码文本。</summary>
    public sealed class VntSettingsWindow : Window
    {
        readonly TextBox _server = Theme.TextBox(""), _token = Theme.TextBox(""), _device = Theme.TextBox("");
        readonly SegmentedToggle _relay = new SegmentedToggle(new[] { "P2P 优先", "中继优先" }, 200,
            "传输方式：P2P 优先 = 先打洞直连（延迟最低）；中继优先 = 经 vnts 服务端转发（网络对 P2P 不友好时用）。中继优先时经服务端的数据包不加 vnt 层加密，但音频内容仍由话筒自身的每包 AEAD 保护");
        readonly ComboBox _protocol = Theme.Combo(120);
        readonly TextBox _pass = Theme.TextBox("");
        readonly SegmentedToggle _finger = new SegmentedToggle(new[] { "关", "开" }, 92,
            "数据指纹校验（vnt --finger）：服务端开了指纹校验时，客户端也必须开，否则连不上");
        // 服务端加密（-W）三态：自动适配（默认）/ 强制开 / 强制关。
        //   自动 = 第一次握手明文，服务端带 RSA 公钥就自动切加密 → 带不带 -W 的服务器都能连。
        readonly SegmentedToggle _srvEnc = new SegmentedToggle(new[] { "自动", "强制开", "强制关" }, 150,
            "服务端通信加密（vnt -W）：「自动」= 第一次握手明文，服务端提供 RSA 公钥就自动切到加密通道（推荐，带不带 -W 的服务器都能连）；"
            + "「强制开」= 服务端不支持就直接报错；「强制关」= 始终明文。注意：「中继优先」模式下会自动关掉它");
                readonly TextBox _prefix = Theme.TextBox("");
        /// <summary>
        /// vnt 组网开关（用户要求：启动/停止合成一个开关，**下次启动按这个状态保持**）。
        /// 开 = 立即启动 + 记住"程序启动时自动开"；关 = 立即停止 + 记住"下次也别开"（写进 VntManager.AutoStart）。
        /// </summary>
        readonly SegmentedToggle _vntOn = new SegmentedToggle(new[] { "关", "开" }, 92,
            "vnt 组网开关：打开 = 立即组网并记住（下次启动程序自动开）；关闭 = 立即停止并记住（下次也不自动开）");
        /// <summary>正在按真实状态回写开关（此时不触发开关自己的逻辑）。</summary>
        bool _syncingVntToggle;
        /// <summary>输入框改动后的"防抖落盘"计时器（即时应用用）。</summary>
        readonly System.Windows.Threading.DispatcherTimer _applyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                readonly TextBlock _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextGray, Margin = new Thickness(0, 10, 0, 0) };
        /// <summary>数据密码是否成功回填过。见构造里的说明：没回填成功就不许把空值写回配置。</summary>
        bool _passLoaded;
        readonly TextBlock _qr = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextDark, Margin = new Thickness(0, 6, 0, 0) };
        // 组网连接状态：彩色指示灯 + 自动刷新（每 2 秒；状态查询每 6 秒一次，避免频繁刷新）
        readonly System.Windows.Shapes.Ellipse _dot = new System.Windows.Shapes.Ellipse
        { Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center, Fill = Brushes.Gray };
        readonly TextBlock _state = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        /// <summary>设备 ID 缩短显示（前 8 位 + 省略号），列表里一眼能认。</summary>
        internal static string Short(string deviceId)
            => string.IsNullOrWhiteSpace(deviceId) ? "(无ID)"
               : (deviceId.Length <= 10 ? deviceId : deviceId.Substring(0, 8) + "…");
        /// <summary>刷新两组设备列表（当前设备/信任设备）。</summary>
        Action _refreshTrust;
        DateTime _lastVntQuery = DateTime.MinValue;
        string _cachedInfoStatus = "", _cachedIp = "";

        /// <summary>服务端加密三态 ↔ 下拉框索引（0=自动 1=强制开 2=强制关）。</summary>
        static int SrvEncIndex(string mode) => mode == "on" ? 1 : mode == "off" ? 2 : 0;
        static string SrvEncMode(int index) => index == 1 ? "on" : index == 2 ? "off" : "auto";
        static string SrvEncLabel(string mode) => mode == "on" ? "强制开" : mode == "off" ? "强制关" : "自动";

        public VntSettingsWindow()
        {

            Title = "vnt 设置";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; SizeToContent = SizeToContent.Height;
            // 不加 MaxHeight：本窗口是 SizeToContent.Height，限高会把内容裁掉（真机反馈：vnt/话筒窗显示不全）
            Width = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var stack = new StackPanel();
            stack.Children.Add(Theme.Title("vnt 虚拟局域网设置", 15));
            stack.Children.Add(Theme.Label("程序不内置任何默认值：填好这里才能组网；token 等同于虚拟网的钥匙，别外发。", 12, Theme.TextGray));

            foreach (var cb in new[] { "udp", "tcp", "ws", "wss" }) _protocol.Items.Add(cb);
            // vnt 自带的链路加密（对应 vnt-cli -e）：第一层防护；第二层是我们自己的每包 AEAD。
            // 【说明】本机 vnt-lite 引擎**只实现了 AES-GCM**，所以"加密方式"不再作为设置项暴露
            //（以前的下拉里列了 chacha20/sm4 等，但引擎并未实现，属于"假设置"；而且这个字符串会进入
            //  client_secret_hash = SHA256(model+password+token)，改错会直接连不上）。固定值见 VntManager.Encrypt。
            _server.Text = VntManager.Server; _token.Text = VntManager.Token;
            _device.Text = VntManager.DeviceId;
            _protocol.Text = string.IsNullOrWhiteSpace(VntManager.Protocol) ? "udp" : VntManager.Protocol;

            // 关键：把已保存的密码/开关回填到控件里。否则打开窗口点「保存」会把数据密码清空（指纹与服务端加密同理）。
            _pass.Text = VntManager.DataPassword;
            // 再兜一层：**只有真的回填成功过，才允许把输入框的值写回配置**。
            // 否则一旦控件是空的（主题/初始化时序异常、配置读取失败等），点「保存」或「启动 vnt」
            // 就会把已存的数据密码清成空字符串 —— 这正是"设置里密码老是没了"的原因。
            _passLoaded = !string.IsNullOrEmpty(_pass.Text);
            _finger.SelectedIndex = VntManager.Fingerprint ? 1 : 0;
            _srvEnc.SelectedIndex = SrvEncIndex(VntManager.ServerEncryptMode);
            _token.ToolTip = "组网令牌（vnt-cli -k，vnt 官方也叫组网编号）：同一个令牌的设备才在同一个虚拟局域网里";

            // 字段排版：左标签 + 右控件，两列两行严格对齐
            //   第一行：服务器 ｜ 协议
            //   第二行：密码（组网令牌）+随机 ｜ 加密方式
            //   第三行：设备ID（+重新生成）　第四行：vnt-cli 路径（占满）
            var form = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            for (int i = 0; i < 8; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock Lbl(string t) => new TextBlock
            {
                Text = t, FontSize = 11.5, Foreground = Theme.TextGray,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 9)
            };
            // ===== 高级设置（默认收起，点按钮展开）=====
            // 基础设置（服务器 / 组网令牌 / 数据密码）留在这张主网格里常显；其余项搬进 advGrid。
            var advGrid = new Grid { Visibility = System.Windows.Visibility.Collapsed };
            for (int ci = 0; ci < 4; ci++) advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = ci % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            for (int ri = 0; ri < 8; ri++) advGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void PlaceAdv(UIElement el, int r, int c, int span = 1)
            {
                Grid.SetRow(el, r); Grid.SetColumn(el, c); Grid.SetColumnSpan(el, span);
                advGrid.Children.Add(el);
            }
            // vnt 窗的连接状态必须**定时刷新**（用户反馈"状态还不改"）：每秒重算一次状态行
            _timer.Tick += (s, e) => { try { RefreshStatus(); } catch { } };

            void Place(UIElement el, int r, int c, int span = 1)
            {
                if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 9);
                Grid.SetRow(el, r); Grid.SetColumn(el, c);
                if (span > 1) Grid.SetColumnSpan(el, span);
                form.Children.Add(el);
            }

            _server.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            _protocol.Width = 150;
            _relay.SelectedIndex = VntManager.RelayFirst ? 1 : 0;
            _relay.Changed += () =>
            {
                VntManager.RelayFirst = _relay.SelectedIndex == 1;
                PlayLog.Write("vnt", "传输模式", "info", detail: VntManager.RelayFirst ? "中继优先（经服务端）" : "P2P 优先（打洞直连）");
                RefreshStatus();
            };

            Place(Lbl("服务器"), 0, 0);
            Place(_server, 0, 1);
            PlaceAdv(Lbl("协议"), 0, 0);
            PlaceAdv(_protocol, 0, 1);

            Place(Lbl("组网令牌"), 1, 0);
            var tokenRow = new StackPanel { Orientation = Orientation.Horizontal };
            _token.Width = 118;
            tokenRow.Children.Add(_token);
            var randBtn = Theme.Button("随机", null, null, "\uf074", () =>
            {
                _token.Text = VntManager.NewToken();
                _token.Focus();
            });
            randBtn.Margin = new Thickness(6, 0, 0, 0);
            randBtn.ToolTip = "生成一个随机组网令牌（对应 vnt-cli -k，等同 vnt 官方说的组网编号）：同一个令牌的设备才在同一个虚拟局域网里";
            tokenRow.Children.Add(randBtn);
            Place(tokenRow, 1, 1);

            // 数据加密密码（vnt -w）+ 随机；服务端也无法解密，两端必须一致
            Place(Lbl("数据密码"), 2, 0);
            var passRow = new StackPanel { Orientation = Orientation.Horizontal };
            _pass.Width = 118;
            _pass.ToolTip = "数据加密密码（等同 vnt-cli -w）：用它生成的密钥加密客户端数据，服务端也无法解密；手机端必须填同一个";
            passRow.Children.Add(_pass);
            var randPassBtn = Theme.Button("随机", null, null, "\uf074", () =>
            {
                _pass.Text = VntManager.NewDataPassword();
                _pass.Focus();
            });
            randPassBtn.Margin = new Thickness(6, 0, 0, 0);
            randPassBtn.ToolTip = "生成一个随机数据加密密码（16 位，a-z/A-Z/0-9）。手机端要填同一个，否则彼此收不到数据";
            passRow.Children.Add(randPassBtn);
            Place(passRow, 2, 1);
            PlaceAdv(Lbl("指纹认证"), 1, 0);
            _finger.SelectedIndex = VntManager.Fingerprint ? 1 : 0;
            _finger.Changed += () => { VntManager.Fingerprint = _finger.SelectedIndex == 1; RefreshStatus(); };
            PlaceAdv(_finger, 1, 1);

            // 服务端通信加密（vnt -W）：三态自动适配（详见 VntManager.ServerEncryptMode 的注释）
            PlaceAdv(Lbl("服务端加密"), 1, 2);
            _srvEnc.Changed += () =>
            {
                string mode = SrvEncMode(_srvEnc.SelectedIndex);
                VntManager.ServerEncryptMode = mode;
                // 帮助里 -W 提醒"请留意服务端指纹是否正确"：选「强制开」时顺手把指纹认证也打开（可手动关掉）
                if (mode == "on" && _finger.SelectedIndex == 0) { _finger.SelectedIndex = 1; VntManager.Fingerprint = true; }
                if (mode == "auto")
                {
                    AppDialog.Info(this, "服务端加密：自动适配",
                        "已设为「自动」（推荐）。首次握手用明文，服务端在握手响应里提供 RSA 公钥时，"
                        + "客户端会自动补发密钥握手并切换到加密通道；服务端没有这个能力就一直走明文。\n\n"
                        + "也就是说：服务器带不带 -W（server_encrypt）都能连，你不必再关心这个开关。\n\n"
                        + "唯一例外：传输模式选「中继优先」时，本项会被自动关掉 —— "
                        + "vnts 只原样转发明文数据包，控制面加密会让中继收不到包。\n"
                        + "（话筒音频与配对通道始终有各自的每包 AEAD 加密，不受这里影响。）");
                }
                RefreshStatus();
            };
            PlaceAdv(_srvEnc, 1, 3);
            _srvEnc.ToolTip = "服务端通信加密（vnt -W）三态：「自动」（推荐）= 服务端提供 RSA 公钥就自动切加密，"
                + "带不带 -W 的服务器都能连；「强制开」= 服务端不支持就报错；「强制关」= 始终明文。"
                + "「中继优先」模式下会强制关（vnts 只转发明文包）";

            PlaceAdv(Lbl("设备ID"), 3, 0);
            var devRow = new StackPanel { Orientation = Orientation.Horizontal };
            _device.Width = 210;
            _device.IsReadOnly = false;
            devRow.Children.Add(_device);
            var newDevBtn = Theme.Button("重新生成", null, null, "\uf021", () => { _device.Text = VntManager.NewDeviceId(); });
            newDevBtn.Margin = new Thickness(6, 0, 0, 0);
            newDevBtn.ToolTip = "重新生成设备ID（换一个虚拟 IP）。device_id 固定才能让虚拟 IP 固定，掉线重连后手机端无需重新扫码";
            devRow.Children.Add(newDevBtn);
            PlaceAdv(devRow, 3, 1, 3);

            PlaceAdv(Lbl("传输模式"), 2, 0);
            PlaceAdv(_relay, 2, 1);   // 跨度收成 1 列：紧贴"传输模式"标签（用户反馈太远）

            // 第七行：启动时是否自动组网
            // 「启动时自动组网」已合并进上面的「组网」开关（开关本身就写 AutoStart），不再单独占一行（用户要求）

            // 第七行：vnt 由谁管理（你自己已装 vnt 时选「自己装」：不启动 vnt、不需要管理员权限）
            // 「自带 / 自己装」已取消（用户确认：内置 vnt-lite 已运行良好，不再需要另一套）（用户要求）
            PlaceAdv(Lbl("虚拟 IP"), 2, 2);
            _prefix.Text = VntManager.VnetPrefix;
            _prefix.Width = 120;
            _prefix.ToolTip = "虚拟 IP：**填了就用这个固定 IP**（如 10.8.0.5）；**留空 = 由服务器自动分配**（推荐，和官方 App 一致）";
            _prefix.TextChanged += (s, e) => VntManager.VnetPrefix = _prefix.Text;
            PlaceAdv(_prefix, 2, 3);
            // 输入框：停止输入 0.8 秒自动落盘（连接相关字段改动还会自动重连）
            _applyTimer.Tick += ApplyTimerTick;
            // 配置自动保存的**兜底**（用户反馈"配置不自动保存"）：不依赖某个控件有没有挂事件，
            // 每秒比对一次"控件值 vs 已存配置"，只要不一致就立刻应用（落盘 + 按需重连）。
            var autoApplyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            autoApplyTimer.Tick += (s, e) =>
            {
                try
                {
                    if (_syncingVntToggle) return;
                    bool diff =
                        (_server.Text ?? "").Trim() != (VntManager.Server ?? "").Trim() ||
                        (_token.Text ?? "").Trim() != (VntManager.Token ?? "").Trim() ||
                        (_passLoaded && (_pass.Text ?? "") != (VntManager.DataPassword ?? "")) ||   // 未加载完不判差异，避免清空密码
                        (_device.Text ?? "").Trim() != (VntManager.DeviceId ?? "").Trim() ||
                        (_prefix.Text ?? "").Trim() != (VntManager.VnetPrefix ?? "").Trim() ||
                        (_finger.SelectedIndex == 1) != VntManager.Fingerprint ||
                        (_relay.SelectedIndex == 1) != VntManager.RelayFirst;
                    if (diff)
                    {
                        ApplyVntFromControls();
                        // 明确记一条：以后"到底有没有保存"在日志里能直接看到
                        PlayLog.Write("vnt", "配置已自动保存", "info",
                            detail: $"服务器={_server.Text.Trim()} 令牌={(string.IsNullOrWhiteSpace(_token.Text) ? "空" : "有值")} "
                                  + $"数据密码={(string.IsNullOrWhiteSpace(_pass.Text) ? "空" : "有值")} 协议={_protocol.Text} 加密=aes_gcm "
                                  + $"指纹={(_finger.SelectedIndex == 1 ? "开" : "关")} 传输={(_relay.SelectedIndex == 1 ? "中继优先" : "P2P优先")} "
                                  + $"虚拟IP={(_prefix.Text.Trim().Length == 0 ? "（留空=服务器分配）" : _prefix.Text.Trim())}");
                    }
                }
                catch { }
            };
            // autoApplyTimer.Start();   // 轮询已关闭：它会把界面正在输入的值刷回旧值（用户反馈"虚拟 IP 每次都被改回去"），改用"关窗/失焦必存 + 原有事件"
            // 关窗、失焦时强制保存一次：用户"改完就关窗"是最常见的操作，绝不能丢（用户反馈"还是不保存"）
            this.Closing += (s, e) => { try { ApplyVntFromControls(); } catch { } };
            this.Deactivated += (s, e) => { try { ApplyVntFromControls(); } catch { } };
            foreach (var tb in new TextBox[] { _server, _token, _device, _pass })
                tb.TextChanged += (s, e) => ScheduleAutoApply(restartIfRunning: true);
            // 协议/加密是下拉框（ComboBox）：用 TextBox 基类的 TextChanged 不可用，改用 SelectionChanged + LostFocus
            _protocol.SelectionChanged += (s, e) => ScheduleAutoApply(restartIfRunning: true);
            _protocol.LostFocus += (s, e) => ScheduleAutoApply(restartIfRunning: true);

            stack.Children.Add(form);
            // 「高级设置」开关：默认收起，点开才显示（用户要求：基础设置常显、高级设置不常显）
            // 「高级设置」开关：默认收起，点开才显示（用户要求：基础常显、高级不常显）。同样用两个按钮互切。
            System.Windows.Controls.Button advShow = Theme.Button("显示高级设置", null, null, "\uf078", null);
            System.Windows.Controls.Button advHide = Theme.Button("收起高级设置", null, null, "\uf077", null);
            advShow.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            advHide.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            advShow.Margin = new Thickness(0, 8, 0, 4);
            advHide.Margin = new Thickness(0, 8, 0, 4);
            advHide.Visibility = System.Windows.Visibility.Collapsed;
            advShow.Click += (s, e) =>
            {
                advGrid.Visibility = System.Windows.Visibility.Visible;
                advShow.Visibility = System.Windows.Visibility.Collapsed;
                advHide.Visibility = System.Windows.Visibility.Visible;
            };
            advHide.Click += (s, e) =>
            {
                advGrid.Visibility = System.Windows.Visibility.Collapsed;
                advShow.Visibility = System.Windows.Visibility.Visible;
                advHide.Visibility = System.Windows.Visibility.Collapsed;
            };
            advGrid.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(advShow);
            stack.Children.Add(advHide);
            stack.Children.Add(advGrid);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            // 【即时应用】用户要求：不要"填完再点保存"。现在输入框改动会在停止输入 0.8 秒后自动落盘，
            // 开关（协议/加密/指纹/中继/自带-自己装）改一下立刻生效并自动重连；这个按钮只保留"立刻重连"。
            row.Children.Add(Theme.Button("立即重连（应用改动）", Theme.Primary, Brushes.White, "\uf021", () =>
            {
                ApplyVntFromControls();
                PlayLog.Write("vnt", "立即重连", "info", detail: "手动触发：保存当前配置并重启组网");
                if (VntProcess.IsRunning) VntProcess.Stop();
                if (VntManager.AutoStart && VntManager.VntConfigured) VntProcess.Start(out _);
                RefreshStatus();
            }));
            // vnt 组网开关（用户要求：启动/停止合成一个开关，并且**下次启动保持这个状态**）。
            // 开 = 立即启动 + 记住"程序启动时自动开组网"；关 = 立即停止 + 记住"下次也别开"。
            _vntOn.Margin = new Thickness(8, 0, 0, 0);
            _vntOn.SelectedIndex = VntProcess.IsRunning ? 1 : 0;      // 先设初值再订阅，避免误触发
            _vntOn.Changed += () =>
            {
                if (_syncingVntToggle) return;
                if (_vntOn.SelectedIndex == 1)
                {
                    VntManager.AutoStart = true;                       // 记住：下次启动程序自动开
                    // 与老控件「自动启动」保持一致，避免"保存"时按旧值把开关又写回关
                    if (!VntManager.ManagedByApp)
                    {
                        RefreshStatus();
                        string detected = MicServer.DetectLocalVntIp();
                        AppDialog.Info(this, "vnt 由你自己管理",
                            "当前是「自己装」模式：本程序不会启动 vnt。\n\n"
                            + "请确认本机已入网（看上面的状态与虚拟 IP），"
                            + "然后直接去「话筒开关」开麦即可。\n\n"
                            + "当前检测结果：" + (string.IsNullOrWhiteSpace(detected) ? "没检测到虚拟网卡地址（检查网段前缀）" : detected));
                        return;
                    }
                    if (VntProcess.IsRunning) { RefreshStatus(); return; }
                    // 先自动保存输入框里的值：避免"填了没点保存 → 启动报未配置"这种坑
                    VntManager.Server = _server.Text.Trim(); VntManager.Token = _token.Text.Trim();
                    VntManager.DeviceId = _device.Text.Trim(); VntManager.RelayFirst = _relay.SelectedIndex == 1;
                    VntManager.Protocol = string.IsNullOrWhiteSpace(_protocol.Text) ? "udp" : _protocol.Text.Trim();
                    string e0 = "aes_gcm";
                    VntManager.Encrypt = (e0.Length == 0 || e0.StartsWith("(")) ? "" : e0;
                    if (_passLoaded) VntManager.DataPassword = _pass.Text.Trim();
                    VntManager.Fingerprint = _finger.SelectedIndex == 1;
                    VntManager.ServerEncryptMode = SrvEncMode(_srvEnc.SelectedIndex);
                    if (VntProcess.Start(out string err))
                    {
                        _status.Text = "vnt-lite 已启动。\n" + VntProcess.StatusSummary();
                        PlayLog.Write("vnt-lite", "开关打开", "info", detail: "已启动组网，并记住下次启动程序时自动开");
                    }
                    else
                    {
                        _status.Text = err;
                        AppDialog.Warn(this, "启动 vnt 失败", err);
                        _syncingVntToggle = true; _vntOn.SelectedIndex = 0; _syncingVntToggle = false;
                    }
                }
                else
                {
                    VntProcess.Stop();
                    VntManager.AutoStart = false;                      // 记住：下次启动不要自动开
                    PlayLog.Write("vnt-lite", "开关关闭", "info", detail: "已停止组网，并记住下次启动程序时不自动开");
                }
                RefreshStatus();
            };
            row.Children.Add(_vntOn);
            var refreshBtn = Theme.Button("刷新状态", null, null, "\uf021", RefreshStatus);
            refreshBtn.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(refreshBtn);
            var qrBtn = Theme.Button("显示二维码（浏览器打开）", Theme.Primary, Brushes.White, "\uf029", () =>
            {
                // 真正的二维码由本地页面用随包的 qrcode.min.js 渲染（手写编码器风险高，这里复成熟库）
                try
                {
                    string url = $"http://127.0.0.1:{Program.ListenPort}/res/mic-qr.html";
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    _qr.Text = "已在浏览器打开二维码页面：" + url + "\n（手机扫那个二维码即可入网并使用网页全部功能）";
                }
                catch (Exception ex) { _qr.Text = "打开二维码页面失败：" + ex.Message; }
            });
            qrBtn.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(qrBtn);
            stack.Children.Add(row);
            // （vnt 窗里的【当前设备/管理员设备】列表已移除：用户确认 vnt 层拿不到设备 ID、做不到按 ID 赋权，赋权统一放【远程 → 管理员】）

            var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            stateRow.Children.Add(_dot);
            stateRow.Children.Add(_state);
            stack.Children.Add(stateRow);

            stack.Children.Add(new TextBlock { Text = "当前状态", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, Margin = new Thickness(0, 12, 0, 4) });
            // 当前状态太长（用户反馈）：**默认收起**，点按钮展开/收起；展开时把这份状态写进日志备查。
            _status.Visibility = System.Windows.Visibility.Collapsed;
            // 当前状态太长（用户反馈）：默认收起。用**两个按钮互相切换**（不是重建按钮 —— 重建后
            // 原按钮已被移出面板、Parent 为 null，第二次点就更新不了，真机踩过）。
            _status.Visibility = System.Windows.Visibility.Collapsed;
            var statusShow = Theme.Button("显示当前状态", null, null, "\uf078", null);
            var statusHide = Theme.Button("收起当前状态", null, null, "\uf077", null);
            statusShow.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            statusHide.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            statusShow.Margin = new Thickness(0, 4, 0, 4);
            statusHide.Margin = new Thickness(0, 4, 0, 4);
            statusHide.Visibility = System.Windows.Visibility.Collapsed;
            statusShow.Click += (s, e) =>
            {
                _status.Visibility = System.Windows.Visibility.Visible;
                statusShow.Visibility = System.Windows.Visibility.Collapsed;
                statusHide.Visibility = System.Windows.Visibility.Visible;
                try { PlayLog.Write("界面", "当前状态", "info", detail: _status.Text); } catch { }
            };
            statusHide.Click += (s, e) =>
            {
                _status.Visibility = System.Windows.Visibility.Collapsed;
                statusShow.Visibility = System.Windows.Visibility.Visible;
                statusHide.Visibility = System.Windows.Visibility.Collapsed;
            };
            stack.Children.Add(statusShow);
            stack.Children.Add(statusHide);
            stack.Children.Add(_status);
            stack.Children.Add(new TextBlock { Text = "二维码：点上面的「显示二维码」按钮在浏览器里打开（手机扫码用，也可复制下面内容手动导入）\n扫码一次即可：① 用这里的服务器/密码入网　② 用虚拟IP+网页端口使用网页全部功能（任务/播控/调休/预警/日志，沿用网页口令）\n话筒喊话不在这张码里授权：每次打开 App 都要在手机上重新输入「话筒开关」窗里的 6 位动态码（密钥只在内存，退出即失效）", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextGray, Margin = new Thickness(0, 14, 0, 4) });
            stack.Children.Add(_qr);

            var close = Theme.Button("关闭", null, null, "\uf00c", Close);
            close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(close);

            Content = Theme.Card2(stack, 0);
            RefreshStatus();                                   // 构造时就取一次（离屏预览也能看到状态）
            Loaded += (s, e) => { _timer.Start(); RefreshStatus(); };
            Closed += (s, e) => _timer.Stop();
        }

        /// <summary>
        /// 把界面上的值写回配置（**即时应用**的核心）。
        /// 关键安全点：只有 [_passLoaded]（数据密码成功回填过）时才写密码，绝不用空值覆盖已保存的密码。
        /// </summary>
        void ApplyVntFromControls()
        {
            try
            {
                VntManager.Server = _server.Text.Trim(); VntManager.Token = _token.Text.Trim();
                VntManager.DeviceId = _device.Text.Trim();
                VntManager.RelayFirst = _relay.SelectedIndex == 1;
                VntManager.Protocol = string.IsNullOrWhiteSpace(_protocol.Text) ? "udp" : _protocol.Text.Trim();
                string enc = "aes_gcm";
                VntManager.Encrypt = (enc.Length == 0 || enc.StartsWith("(")) ? "" : enc;
                if (_passLoaded) VntManager.DataPassword = _pass.Text.Trim();
                VntManager.Fingerprint = _finger.SelectedIndex == 1;
                VntManager.ServerEncryptMode = SrvEncMode(_srvEnc.SelectedIndex);
            }
            catch (Exception ex) { PlayLog.Write("vnt", "配置写入异常", "error", detail: ex.Message); }
        }

        /// <summary>输入框改动后的自动落盘（防抖：停止输入 0.8 秒后才写，避免每敲一个字都写配置/重连）。</summary>
        void ScheduleAutoApply(bool restartIfRunning)
        {
            _applyTimer.Stop();
            _applyTimer.Tag = restartIfRunning;
            _applyTimer.Start();
        }

        void ApplyTimerTick(object sender, EventArgs e)
        {
            _applyTimer.Stop();
            bool restart = _applyTimer.Tag is bool b && b;
            ApplyVntFromControls();
            if (restart && VntProcess.IsRunning && VntManager.ManagedByApp)
            {
                // 连接相关字段变了：自动断开重连（用户要求"即时应用"，不该再让人手点保存/重启）
                PlayLog.Write("vnt", "配置即时生效", "info", detail: "连接参数已改动：自动重连组网");
                try { VntProcess.Stop(); } catch { }
                try { VntProcess.Start(out string err2); if (err2 != null) PlayLog.Write("vnt", "自动重连失败", "warn", detail: err2); } catch { }
            }
            else
            {
                PlayLog.Write("vnt", "配置即时生效", "info",
                    detail: $"服务器 {VntManager.Server}，token {VntManager.MaskToken(VntManager.Token)}，协议 {VntManager.Protocol}"
                          + $"，数据密码 {VntManager.MaskToken(VntManager.DataPassword)}");
            }
            RefreshStatus();
        }

        void RefreshStatus()        {
            try
            {
                // 状态查询每 6 秒一次（要拉起 vnt-cli，别每 2 秒一次）；其余时间用缓存
                if ((DateTime.Now - _lastVntQuery).TotalSeconds >= 6)
                {
                    _lastVntQuery = DateTime.Now;
                    if (VntProcess.TryQueryStatus(out var info, out _, out _))
                    {
                        _cachedInfoStatus = info.Status;
                        _cachedIp = info.VirtualIp;
                    }
                    else { _cachedInfoStatus = ""; _cachedIp = ""; }
                }
                // 虚拟 IP **优先用引擎实际分配到的那个**（VntManager.LocalVirtualIp 读的是 vnt-lite 客户端的注册结果）；
// 以前只用 _cachedIp（从旧 vnt-cli 的状态文本里解析）→ 用 vnt-lite 时它一直是空 → 界面误报"还没拿到虚拟 IP"。
                string ipNow = VntManager.LocalVirtualIp;
                if (string.IsNullOrWhiteSpace(ipNow)) ipNow = _cachedIp;
                var st = VntManager.ConnectionState(VntManager.VntConfigured, VntProcess.IsRunning, _cachedInfoStatus, ipNow);
                // 开关始终反映"真实是否在跑"（用户手动从菜单停掉 vnt 时，开关也要跟着变）
                _syncingVntToggle = true;
                _vntOn.SelectedIndex = VntProcess.IsRunning ? 1 : 0;
                _syncingVntToggle = false;
                _state.Text = st.label;
                _dot.Fill = st.colorKey == "green"
                    ? new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60))
                    : st.colorKey == "amber"
                        ? new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12))
                        : new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                _status.Text = VntProcess.StateText() + "\n" + VntProcess.StatusSummary()
                    + "\n地址：内置 vnt-lite " + (string.IsNullOrWhiteSpace(VntManager.LocalVirtualIp) ? "(还没入网)" : VntManager.LocalVirtualIp)
                    + "　｜　你自己装的 vnt " + (string.IsNullOrWhiteSpace(VntManager.OsVnetIp) ? "(没检测到网卡)" : VntManager.OsVnetIp)
                    + "　｜　话筒监听 " + (MicServer.Running ? MicServer.BoundAddress + ":" + MicServer.AudioPort + "/" + MicServer.CtlPort : "(未启动)")
                    + "\n（同一台电脑跑两套 vnt 客户端时，这两个 10.x 地址都对：二维码里两个都会带上，手机依次尝试，不用你判断）"
                    + "\n" + VntProcess.DataPlaneDiag();
            }
            catch (Exception ex) { _status.Text = "取状态失败：" + ex.Message; }
        }

        static StackPanel Field(string label, UIElement control)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Theme.TextGray, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(control);
            return sp;
        }
    }

    /// <summary>「话筒开关」窗：总开关 + 动态配对码 + 当前会话 + 闪避状态 + 上限设置。</summary>
    public sealed class MicSwitchWindow : Window
    {
        readonly TextBlock _code = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Primary };
        readonly TextBlock _codeTip = new TextBlock { FontSize = 11.5, Foreground = Theme.TextGray, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextGray };
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        /// <summary>刷新两组设备列表（当前设备 / 信任设备）。</summary>
        Action _refreshTrust;
        readonly SegmentedToggle _on = new SegmentedToggle(new[] { "关", "开" }, 92, "话筒总开关：关闭时一律拒绝接入（这是最高优先级的开关）");
        readonly NumberBox _maxMin = new NumberBox(0, 240, VntManager.MicMaxMinutes, 110,
            "单次喊话最长分钟数，到点自动断开；填 0 = 不自动关闭（一直开到你手动断开或对方退出）");
        readonly NumberBox _duck = new NumberBox(0, 100, VntManager.MicDuckPercent, 110, "话筒说话时把音乐压低到百分之几");
        // 迟到帧阈值（用户要求"给我自己设置"）：默认 1500ms，0 = 不丢（只统计）
        readonly NumberBox _lateDrop = new NumberBox(0, 10000, VntManager.MicLateDropMs, 130,
            "音频迟到帧丢弃阈值（毫秒）：网络卡顿后，比最新帧晚这么多毫秒才到的帧直接丢掉（避免播放「旧话」）；0 = 不丢，只统计。默认 1500");

        public MicSwitchWindow()
        {

            Title = "话筒";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; SizeToContent = SizeToContent.Height;
            // 不加 MaxHeight：本窗口是 SizeToContent.Height，限高会把内容裁掉（真机反馈：vnt/话筒窗显示不全）
            // 宽度按"最长一行"给足：以前 560 时，"单次上限(分)" 后面的输入框被挤出窗口（用户反馈）。
            // 控件也顺手排成两行，避免高 DPI 缩放下再次挤爆。
            Width = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var stack = new StackPanel();
            stack.Children.Add(Theme.Title("话筒（远程喊话）", 15));
            stack.Children.Add(Theme.Label("手机端采集 → 电脑播放。扫码只负责入网；**话筒每次打开 App 都要重新输入下面这个 6 位数字动态码**（每 60 秒换一次、用一次即作废，密钥只在内存、退出即失效）。", 12, Theme.TextGray));
            stack.Children.Add(Theme.Label("手机端配对走本机 ctl_port 的 UDP 加密通道（cbs-pair）：**请务必在「vnt 设置」里填一个【数据密码】**，它就是这条通道的加密钥匙；没填时 UDP 配对不可用（日志会提示），手机端只能走虚拟网卡里的 TCP 配对。", 11.5, Theme.TextGray));

            TextBlock RowLabel(string text, double leftMargin = 0) => new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = Theme.TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(leftMargin, 0, 8, 0)
            };

            // 第一行：总开关
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            top.Children.Add(RowLabel("话筒开关"));
            _on.SelectedIndex = VntManager.MicEnabled ? 1 : 0;
            _on.Changed += () =>
            {
                VntManager.MicEnabled = _on.SelectedIndex == 1;
                if (!VntManager.MicEnabled) { MicServer.Stop(); MicDuck.ForceRestore(); }
                else StartMic();
                Update();
            };
            top.Children.Add(_on);
            stack.Children.Add(top);

            // 第二行：两个数值（放得下，不会被窗口边缘切掉）
            var limits = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            limits.Children.Add(RowLabel("压低音乐到 %"));
            limits.Children.Add(_duck);
            limits.Children.Add(_lateDrop);   // 迟到帧阈值（用户要求可自己设置）
            limits.Children.Add(RowLabel("单次上限(分)", 22));
            limits.Children.Add(_maxMin);
            limits.Children.Add(new TextBlock
            {
                Text = "（填 0 = 不自动断开）",
                FontSize = 11.5,
                Foreground = Theme.TextGray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            stack.Children.Add(limits);

            stack.Children.Add(new TextBlock { Text = "动态配对码（在手机上输入）", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, Margin = new Thickness(0, 16, 0, 4) });
            stack.Children.Add(_code);
            stack.Children.Add(_codeTip);

            // 两组列表（用户要求）：
            //   当前设备 = 正在跟本机通话的设备（id + 信任开关 + 踢出）
            //   信任设备 = 免动态码的那份凭据（id + 踢出）
            stack.Children.Add(new TextBlock { Text = "当前设备（正在连接的话筒）", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, Margin = new Thickness(0, 16, 0, 4) });
            var curHost = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            stack.Children.Add(curHost);

            stack.Children.Add(new TextBlock { Text = "信任设备（配对免动态码 · 可踢出）", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, Margin = new Thickness(0, 12, 0, 4) });
            var trustHost = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            stack.Children.Add(trustHost);   // 【修复】以前只建了面板、忘了挂到界面上 → 信任设备列表永远不显示（用户反馈）
            void RebuildTrustList()
            {
                // —— 当前设备 ——
                curHost.Children.Clear();
                var sessions = MicServer.Registry.All.ToList();
                if (sessions.Count == 0)
                {
                    curHost.Children.Add(new TextBlock
                    {
                        Text = "（当前没有设备连着）",
                        FontSize = 11.5, Foreground = Theme.TextGray, TextWrapping = TextWrapping.Wrap
                    });
                }
                foreach (var s in sessions)
                {
                    string dev = s.DeviceId ?? "";
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    bool trustedNow = VntManager.IsTrustedDevice(dev);
                    // 【可读性】主行显示**机型**（用户要求：UUID 太长不适合人读），ID 放 ToolTip。
                    string model = "";
                    try { model = VntManager.TrustedDeviceName(dev); } catch { }
                    string shown = string.IsNullOrWhiteSpace(model) ? "未知机型" : model.Trim();
                    line.Children.Add(new TextBlock
                    {
                        Text = $"{shown}　会话{s.Sid}　{s.SourceIp}" + (s.MaxMinutes > 0 ? $"　上限{s.MaxMinutes}分" : ""),
                        FontSize = 11.5, Foreground = Theme.TextDark, VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300,
                        ToolTip = "完整设备 ID：" + dev
                    });
                    var trustBtn = Theme.Button(trustedNow ? "已信任" : "信任", trustedNow ? Theme.Success : (Brush)null,
                        trustedNow ? Brushes.White : null, trustedNow ? "\uf00c" : "\uf0c7", () =>
                        {
                            if (VntManager.IsTrustedDevice(dev)) VntManager.RemoveTrustedDevice(dev);
                            else VntManager.AddTrustedDevice(dev);
                            PlayLog.Write("话筒", "信任开关", "info",
                                detail: $"设备 {dev} 现在{(VntManager.IsTrustedDevice(dev) ? "已信任（下次免动态码）" : "未信任（下次要输动态码）")}");
                            RebuildTrustList();
                        });
                    trustBtn.Margin = new Thickness(10, 0, 0, 0);
                    line.Children.Add(trustBtn);
                    var kick = Theme.Button("踢出", null, null, "\uf00d", () =>
                    {
                        try { MicServer.Registry.Remove(s.Sid); } catch { }
                        // 踢出 = 断开 + **解除信任**（用户反馈：只断开会话的话，下次连接又免码自动连）
                        try { if (!string.IsNullOrWhiteSpace(dev)) VntManager.RemoveTrustedDevice(dev); } catch { }
                        try { if (!string.IsNullOrWhiteSpace(dev)) VntManager.RemoveAdminDevice(dev); } catch { }   // 踢出也解除管理员
                        MicDuck.ForceRestore();
                        PlayLog.Write("话筒", "踢出当前设备", "warn", detail: $"会话 {s.Sid}（设备 {dev}）已被管理员踢出");
                        RebuildTrustList();
                    });
                    kick.Margin = new Thickness(6, 0, 0, 0);
                    if (!VntManager.IsAdminDevice(dev)) line.Children.Add(kick);   // 管理员设备不显示踢出（用户要求：取消管理员后才显示）
                    curHost.Children.Add(line);
                }

                // 机型名（手机上报过才有）：显示成"机型名 + ID前12位"，离线设备也能认出来
                string nameOf(string id) { try { return VntManager.TrustedDeviceName(id); } catch { return ""; } }                // —— 信任设备 ——
                trustHost.Children.Clear();
                var ids = VntManager.TrustedDeviceList();
                if (ids.Count == 0)
                {
                    trustHost.Children.Add(new TextBlock
                    {
                        Text = "（暂无信任设备：配对成功一次后自动记下，下次同一设备免动态码）",
                        FontSize = 11.5, Foreground = Theme.TextGray, TextWrapping = TextWrapping.Wrap
                    });
                }
                foreach (var id in ids)
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    line.Children.Add(new TextBlock
                    {
                        // 主行只显示**机型**（用户要求：UUID 太长不适合人读）+ 管理员标记；
                        // 完整设备 ID 放 ToolTip（核对/复制时悬停可见），不再顶到主行。
                        Text = (string.IsNullOrWhiteSpace(nameOf(id)) ? "未知机型" : nameOf(id).Trim())
                               + (VntManager.IsAdminDevice(id) ? "　[管理员]" : ""),
                        FontSize = 11.5, Foreground = Theme.TextDark, VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "完整设备 ID：" + id
                    });
                    var kick = Theme.Button("踢出", null, null, "\uf00d", () =>
                    {
                        VntManager.RemoveTrustedDevice(id);
                        PlayLog.Write("话筒", "信任设备踢出", "warn", detail: $"已把设备 {id} 从信任列表踢出（下次必须重新输动态码）");
                        RebuildTrustList();
                    });
                    kick.Margin = new Thickness(10, 0, 0, 0);
                    if (!VntManager.IsAdminDevice(id)) line.Children.Add(kick);   // 管理员设备不显示踢出（同上）
                    trustHost.Children.Add(line);
                }
                var clearAll = Theme.Button("全部清除", null, null, "\uf2ed", () =>
                {
                    VntManager.ClearTrustedDevices();
                    PlayLog.Write("话筒", "信任设备清除", "info", detail: "已清除全部信任设备");
                    RebuildTrustList();
                });
                clearAll.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                clearAll.Margin = new Thickness(0, 6, 0, 0);
                trustHost.Children.Add(clearAll);
            }
            RebuildTrustList();
            _refreshTrust = RebuildTrustList;

            stack.Children.Add(new TextBlock { Text = "当前状态", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, Margin = new Thickness(0, 16, 0, 4) });
            // 当前状态太长（用户反馈）：**默认收起**，点按钮展开/收起；展开时把这份状态写进日志备查。
            _status.Visibility = System.Windows.Visibility.Collapsed;
            // 当前状态太长（用户反馈）：默认收起。用**两个按钮互相切换**（不是重建按钮 —— 重建后
            // 原按钮已被移出面板、Parent 为 null，第二次点就更新不了，真机踩过）。
            _status.Visibility = System.Windows.Visibility.Collapsed;
            var statusShow = Theme.Button("显示当前状态", null, null, "\uf078", null);
            var statusHide = Theme.Button("收起当前状态", null, null, "\uf077", null);
            statusShow.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            statusHide.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            statusShow.Margin = new Thickness(0, 4, 0, 4);
            statusHide.Margin = new Thickness(0, 4, 0, 4);
            statusHide.Visibility = System.Windows.Visibility.Collapsed;
            statusShow.Click += (s, e) =>
            {
                _status.Visibility = System.Windows.Visibility.Visible;
                statusShow.Visibility = System.Windows.Visibility.Collapsed;
                statusHide.Visibility = System.Windows.Visibility.Visible;
                try { PlayLog.Write("界面", "当前状态", "info", detail: _status.Text); } catch { }
            };
            statusHide.Click += (s, e) =>
            {
                _status.Visibility = System.Windows.Visibility.Collapsed;
                statusShow.Visibility = System.Windows.Visibility.Visible;
                statusHide.Visibility = System.Windows.Visibility.Collapsed;
            };
            stack.Children.Add(statusShow);
            stack.Children.Add(statusHide);
            stack.Children.Add(_status);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(Theme.Button("保存上限与闪避", Theme.Primary, Brushes.White, "\uf0c7", () =>
            {
                VntManager.MicMaxMinutes = _maxMin.Value; VntManager.MicDuckPercent = _duck.Value;
            // 迟到帧阈值也即时生效（不用保存）
            _lateDrop.Changed += () => VntManager.MicLateDropMs = _lateDrop.Value;
                MicDuck.DuckPercent = _duck.Value;
                Update();
            }));
            var restoreBtn = Theme.Button("立即恢复音乐", null, null, "\uf028", () => { MicDuck.ForceRestore(); Update(); });
            restoreBtn.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(restoreBtn);
            var kickBtn = Theme.Button("断开当前话筒", null, null, "\uf00d", () =>
            {
                MicServer.Registry.Clear(); MicDuck.ForceRestore();
                PlayLog.Write("话筒", "手动断开", "warn", detail: "管理员在菜单里断开了当前话筒会话");
                Update();
            });
            kickBtn.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(kickBtn);
            var close = Theme.Button("关闭", null, null, "\uf00c", Close);
            close.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(close);
            stack.Children.Add(row);

            Content = Theme.Card2(stack, 0);
            _timer.Tick += (s, e) => { Update(); try { _refreshTrust?.Invoke(); } catch { } };
            Loaded += (s, e) => { _timer.Start(); Update(); };
            Closed += (s, e) => { _timer.Stop(); };
        }

        /// <summary>
        /// 按开关状态启动话筒服务。
        ///
        /// 【绑定地址】直接用 `0.0.0.0` + 虚拟网来源过滤（网段由虚拟 IP 推出 /24）：
        /// 程序内置的 vnt-lite 是**用户态**组网，它的虚拟 IP 在本机没有对应网卡，bind 不了；
        /// 而机器上那个"像虚拟网"的地址（用户另装的 vnt，比如 10.8.0.105）属于另一个网，
        /// 手机永远发不到 —— 以前拿它 bind，界面上就一直显示 105，纯属误导。
        /// </summary>
        void StartMic()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(VntManager.EffectiveVnetPrefix))
                {
                    AppDialog.Warn(this, "无法开麦",
                        "本机还没拿到虚拟 IP（服务器尚未分配）：通常一两秒内会自动完成；"
                        + "它会作为话筒的来源过滤条件（绑 0.0.0.0 时只接受该网段内的包）。");
                    VntManager.MicEnabled = false; _on.SelectedIndex = 0;
                    return;
                }
                if (!MicServer.Start("0.0.0.0", VntManager.MicPort, VntManager.MicCtlPort, VntManager.MicCodeSeed, out string err))
                    AppDialog.Warn(this, "开麦失败", err);
            }
            catch (Exception ex) { PlayLog.Write("话筒", "开麦失败", "error", detail: ex.Message); }
        }

        void Update()
        {
            try
            {
                string seed = VntManager.MicCodeSeed;
                // 与 /api/mic/code、运行总览同一入口（内部统一 UTC），三处显示的码必然一致。
                string code = MicProtocol.CurrentCodeUtc(seed);
                long idx = MicProtocol.WindowIndex(DateTime.UtcNow);
                int left = MicProtocol.SecondsLeftUtc();
                _code.Text = code;
                _codeTip.Text = $"剩余 {left} 秒后自动换码；同一窗口只能配对一次（换码后旧码立即失效）。{MicDuck.StatusText()}";
                MicDuck.DuckPercent = _duck.Value;
                _status.Text = MicServer.StatusText() + "\n" + VntProcess.StatusSummary();
            }
            catch (Exception ex) { _status.Text = "取状态失败：" + ex.Message; }
        }
    }
}
