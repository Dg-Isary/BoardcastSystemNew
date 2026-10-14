#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
// WinForms 与 WPF 同时启用，公共类型名必须显式消歧
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontStyle = System.Windows.FontStyle;
using FontStyles = System.Windows.FontStyles;
using FontStretch = System.Windows.FontStretch;
using FontStretches = System.Windows.FontStretches;
using FontWeight = System.Windows.FontWeight;
using FontWeights = System.Windows.FontWeights;
using MessageBox = System.Windows.MessageBox;
// 拖入导入用到的三个类型在 WinForms 里同名，必须显式消歧
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 桌面客户端主窗口：单窗口 + 左侧导航 + 底部常驻状态条 + 桌面绘制的置顶提示横幅。
    /// 播控仍由网页端负责（远程更跟手），桌面端只做本地管理与状态指示。
    /// </summary>
    public class MainWindow : Window
    {
        private readonly Grid _contentHost = new Grid();
        private readonly List<Button> _navButtons = new List<Button>();
        private readonly Dictionary<string, Func<UIElement>> _pages = new Dictionary<string, Func<UIElement>>();

        private Border _stateButton;
        private TextBlock _stateIcon, _stateText, _stateSwitchText;
        private TextBlock _playInfo, _nextInfo, _statsInfo, _portInfo;
        private Border _banner;
        private TextBlock _bannerBody;
        private DispatcherTimer _timer;
        private Forms.NotifyIcon _tray;
        private TrayMenuWindow _trayMenu;
        private FloatingBall _ball;          // 桌面悬浮球（可在系统设置里关闭）
        private Border _webEntry;
        private TrayState? _trayState;
        private string _currentPage = "overview";
        private long _alertCursor = 0;
        private bool _busy;

        private static readonly Brush WebEntryBg = new SolidColorBrush(Color.FromRgb(0xEA, 0xF4, 0xFB));
        private static readonly Brush WebEntryHover = new SolidColorBrush(Color.FromRgb(0xDA, 0xEB, 0xF9));
        private static readonly Brush WebEntryLine = new SolidColorBrush(Color.FromRgb(0xBF, 0xDD, 0xF5));

        public MainWindow()
        {

            Title = "智能校园广播系统 - 控制核心";
            // 【高缩放修复·正确做法】窗口尺寸**保持设计尺寸 1080x720，绝不按工作区缩小**：
            // 本窗口有 ApplyAutoScale()：scale = min(窗口宽/1080, 窗口高/720)（下限 0.75），
            // 窗口一变小，整个界面就等比缩小 —— 真机反馈"文字与所有元素都变小"就是上一版把窗口改小导致的。
            // 标题栏跑到屏幕外的问题改用**位置钳制**解决（见下方 Loaded），尺寸不动。
            // 【界面偏小修复】初始窗口不再写死 1080x720，而是按工作区比例给（上限 1800x1200）。
            // 原因：本窗口的 ApplyAutoScale() 用 scale = min(宽/1080, 高/720) 缩放整个界面，
            // 窗口写死 1080x720 时 scale 恒为 1.0，在 4K/高 DPI 大屏上界面就显得很小（用户反馈）。
            // 窗口按工作区放大后 scale 会大于 1，界面元素随之整体变大。
            var _wa0 = System.Windows.SystemParameters.WorkArea;
            Width = Math.Max(1080, Math.Min(_wa0.Width * 0.82, 1800));
            Height = Math.Max(720, Math.Min(_wa0.Height * 0.86, 1200));
            MinWidth = 900; MinHeight = 620;
            Background = Theme.Bg;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // 【高缩放修复】把窗口位置拉回屏幕内：工作区比 720 DIP 矮时，CenterScreen 会让标题栏跑到屏幕上方，
            // 导致最小化/最大化/关闭按钮点不到。这里只挪位置、不改尺寸，所以界面不会被缩小。
            Loaded += (s2, e2) =>
            {
                try
                {
                    var wa = System.Windows.SystemParameters.WorkArea;
                    if (Top < wa.Top) Top = wa.Top;
                    if (Left < wa.Left) Left = wa.Left;
                }
                catch { }
            };
            try { Icon = Program.AppIcon != null ? System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(Program.AppIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()) : null; } catch { }

            _pages["overview"] = () => new OverviewPage(this).RenderPage();
            _pages["tasks"] = () => new TasksPage(this).RenderPage(true);      // 工具栏固定，仅任务列表滚动
            _pages["player"] = () => new PlayerPage(this).RenderPage(true);    // 播放器与队列固定，仅列表滚动
            _pages["music"] = () => new MusicPage(this).RenderPage(true);   // 撑满高度：工具栏与底部按钮固定，仅列表滚动
            _pages["holidays"] = () => new HolidayPage(this).RenderPage(true);   // 表单与底部按钮固定，仅规则列表滚动
            _pages["warn"] = () => new WarnPage(this).RenderPage();               // 气象预警订阅与预报节点（整页滚动）
            _pages["logs"] = () => new LogPage(this).RenderPage();
            _pages["settings"] = () => new SettingsPage(this).RenderPage();

            BuildLayout();
            SetupTray();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _timer.Tick += async (s, e) =>
            {
                // 拖动悬浮球时让路：这轮刷新会走接口并把续体排回 UI 线程，正好和拖动抢线程，
                // 表现就是"球跟不上鼠标"。松手后下一拍（≤0.9 秒）会立刻补上。
                if (FloatingBall.Dragging) return;
                await RefreshStatusAsync();
            };
            _timer.Start();

            Loaded += async (s, e) =>
            {
                ShowPage("overview");
                ApplyAutoScale();
                await RefreshStatusAsync();
            };
            Closing += (s, e) =>
            {
                // 关闭窗口 = 最小化到托盘，后台继续打铃
                e.Cancel = true;
                Hide();
                _tray?.ShowBalloonTip(1500, "智能校园广播系统", "已最小化到托盘，后台继续运行。", Forms.ToolTipIcon.Info);
            };
        }

        // ================= 布局 =================

        private void BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // 菜单
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 主体
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // 状态条

            var menuBar = BuildMenu();
            Grid.SetRow(menuBar, 0);
            root.Children.Add(menuBar);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var nav = BuildNav();
            Grid.SetColumn(nav, 0);
            body.Children.Add(nav);
            _contentHost.Margin = new Thickness(14, 14, 14, 8);
            Grid.SetColumn(_contentHost, 1);
            body.Children.Add(_contentHost);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var status = BuildStatusBar();
            Grid.SetRow(status, 2);
            root.Children.Add(status);

            _banner = BuildBanner();
            Grid.SetRow(_banner, 0);
            Grid.SetRowSpan(_banner, 3);
            _banner.Visibility = Visibility.Collapsed;
            root.Children.Add(_banner);

            _rootLayout = root;
            Content = root;

            // 窗口大小变化时整体等比缩放（设计基准 1080×720）
            SizeChanged += (s, e) => ApplyAutoScale();
            StateChanged += (s, e) => ApplyAutoScale();
        }

        // ================= 界面随窗口自动缩放 =================

        private FrameworkElement _rootLayout;
        private double _appliedScale = 1.0;

        /// <summary>设计基准尺寸：默认窗口就是这个大小，此时缩放系数正好为 1。</summary>
        private const double DesignWidth = 1080;
        private const double DesignHeight = 720;

        /// <summary>
        /// 按窗口尺寸对整界面做等比缩放（LayoutTransform，矢量缩放不发虚）：
        /// 窗口变大时整体放大、变小时整体缩小，始终保持在设计比例下的观感，
        /// 高 DPI 屏幕（例如 150%/200%）上也不会出现"字小得看不清"。
        /// 缩放系数限制在 0.75~1.6，避免极端窗口尺寸下界面被拉得过大或过小。
        /// </summary>
        private void ApplyAutoScale()
        {
            if (_rootLayout == null) return;
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double scale = Math.Min(w / DesignWidth, h / DesignHeight);
            scale = Math.Max(0.75, Math.Min(2.4, scale));   // 上限 1.6 -> 2.4：大屏上允许界面继续放大（用户反馈元素偏小）
            // 变化小于 1% 时不重排，避免 SizeChanged 里反复触发
            if (Math.Abs(scale - _appliedScale) < 0.01) return;
            _appliedScale = scale;
            _rootLayout.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
        }

        private Menu BuildMenu()
        {
            var menu = new Menu { Background = Brushes.White, Padding = new Thickness(6, 3, 6, 3) };

            var mSystem = new MenuItem { Header = "系统(_S)" };
            mSystem.Items.Add(MenuAction("运行总览", () => ShowPage("overview")));
            mSystem.Items.Add(new Separator());
            var miMulti = new MenuItem { Header = "单实例检测：已启用", IsEnabled = false };
            mSystem.Items.Add(miMulti);
            mSystem.Items.Add(MenuAction("打开数据目录", () => OpenPath(Program.AppDataDir)));
            mSystem.Items.Add(MenuAction("打开日志目录", () => OpenPath(Program.LogDir)));
            mSystem.Items.Add(MenuAction("系统设置", () => ShowPage("settings")));
            mSystem.Items.Add(new Separator());
            mSystem.Items.Add(MenuAction("退出程序", ExitApp));
            menu.Items.Add(mSystem);

            // 说明：原先的「任务排程(_T)」「调休(_H)」两个主项已移除——它们的功能左侧导航里都有
            //（任务排程 / 播控台 / 调休管理），「打开网页看板 / 复制看板地址」在「运行总览」页也有按钮。
            // 菜单栏现在只保留侧栏没有的功能：数据备份恢复、订阅导入、关于。
            var mData = new MenuItem { Header = "数据(_D)" };
            // 「导入 / 导出」走 ConfigTransfer（按项、带校验与旧格式迁移）；「从备份中恢复」保留为整包入口，但同样走导入器
            mData.Items.Add(MenuAction("导入 / 导出…", OpenTransferWindow));
            mData.Items.Add(MenuAction("备份系统数据…", BackupConfig));
            mData.Items.Add(MenuAction("从备份中恢复…", RestoreConfig));
            menu.Items.Add(mData);

            var mSub = new MenuItem { Header = "订阅(_U)" };
            mSub.Items.Add(MenuAction("订阅配置与立即同步…", () => new SubscriptionWindow().ShowDialog()));
            mSub.Items.Add(MenuAction("本地导入调休数据…", () => new ImportWindow { Owner = this }.ShowDialog()));
            menu.Items.Add(mSub);

            // 话筒功能：按用户要求**只放菜单、不进侧栏**
            var mMic = new MenuItem { Header = "远程(_M)" };
            mMic.Items.Add(MenuAction("vnt 设置（组网与二维码）…", () => ShowChildDialog(new VntSettingsWindow())));
            mMic.Items.Add(MenuAction("话筒开关（开麦与动态配对码）…", () => ShowChildDialog(new MicSwitchWindow())));
            // 管理员赋权收进「远程」菜单（一级项，用户要求）；点它直接出赋权窗口
            mMic.Items.Add(MenuAction("管理员（设备赋权）…", () => ShowChildDialog(new AdminWindow())));
            // 「管理员」子菜单（用户要求）：给设备赋权（管理员/普通两档）；凭证自动生成、界面不显示
            mMic.Items.Add(new Separator());
            mMic.Items.Add(MenuAction("停止 vnt", () => VntProcess.Stop()));
            menu.Items.Add(mMic);
			
			var mHelp = new MenuItem { Header = "帮助(_H)" };
            mHelp.Items.Add(MenuAction("关于 / 版本信息", () => new AboutDialog { Owner = this }.ShowDialog()));
            menu.Items.Add(mHelp);

            return menu;
        }

        private MenuItem MenuAction(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => { try { action(); } catch (Exception ex) { ShowError(ex); } };
            return mi;
        }

        private Border BuildNav()
        {
            var panel = new StackPanel { Margin = new Thickness(12, 14, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "功能导航",
                FontSize = 12,
                Foreground = Theme.TextGray,
                Margin = new Thickness(4, 0, 0, 8)
            });
            AddNav(panel, "overview", "运行总览", "\uf015");
            AddNav(panel, "tasks", "任务排程", "\uf017");
            AddNav(panel, "player", "播控台", "\uf144");
            AddNav(panel, "music", "音乐库", "\uf001", "本地音频库：编号一经分配不再变化，删除只留空缺、新增顶替最小空缺");
            AddNav(panel, "holidays", "调休管理", "\uf073");
            AddNav(panel, "warn", "预警播报", "\uf071", "气象预警订阅：编号 1000 保留给预警播报，排程到点时按预报节点决定播不播");
            AddNav(panel, "logs", "执行日志", "\uf0ae");
            AddNav(panel, "settings", "系统设置", "\uf013");
            return new Border { Background = Brushes.Transparent, Child = panel };
        }

        private void AddNav(Panel host, string key, string text, string icon, string tip = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Theme.Icon(icon, 14, Theme.TextDark));
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            var btn = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 10, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 13
            };
            btn.Tag = key;
            if (!string.IsNullOrEmpty(tip)) btn.ToolTip = tip;
            btn.Click += (s, e) => ShowPage(key);
            _navButtons.Add(btn);
            host.Children.Add(btn);
        }

        private Border BuildStatusBar()
        {
            var grid = new Grid { Margin = new Thickness(14, 0, 14, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 状态大按钮
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 播放信息
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 资源与端口
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 网页看板入口（最右）

            // 左侧：系统状态大按钮
            var stateContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _stateIcon = Theme.Icon("\uf011", 24, Brushes.White);
            _stateIcon.Margin = new Thickness(0, 0, 10, 0);
            stateContent.Children.Add(_stateIcon);
            var stateTexts = new StackPanel();
            _stateText = new TextBlock { Text = "读取中", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            _stateSwitchText = new TextBlock { Text = "点击切换", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255)) };
            stateTexts.Children.Add(_stateText);
            stateTexts.Children.Add(_stateSwitchText);
            stateContent.Children.Add(stateTexts);

            _stateButton = new Border
            {
                Background = Theme.TextGray,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 10, 22, 10),
                Child = stateContent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 14, 0)
            };
            _stateButton.MouseLeftButtonUp += async (s, e) => await ToggleSwitchAsync();
            Grid.SetColumn(_stateButton, 0);
            grid.Children.Add(_stateButton);

            // 中间：当前播放 / 下一次排程
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) };
            _playInfo = new TextBlock { Text = "待机中...", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, TextTrimming = TextTrimming.CharacterEllipsis };
            _nextInfo = new TextBlock { Text = "下一次排程：读取中…", FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(0, 3, 0, 0) };
            mid.Children.Add(_playInfo);
            mid.Children.Add(_nextInfo);
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            // 中部：CPU / RAM / 端口
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            _statsInfo = new TextBlock { Text = "CPU 0%  RAM 0%", FontSize = 11.5, Foreground = Theme.TextGray, FontFamily = new FontFamily("Consolas") };
            _portInfo = new TextBlock { Text = "", FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(14, 0, 0, 0), FontFamily = new FontFamily("Consolas") };
            right.Children.Add(_statsInfo);
            right.Children.Add(_portInfo);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            // 最右：网页看板入口（重要接口，不能只藏在"系统"菜单里）
            Grid.SetColumn(BuildWebEntry(), 3);
            grid.Children.Add(_webEntry);

            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Margin = new Thickness(14, 0, 14, 12),
                Child = grid
            };
        }

        /// <summary>
        /// 底栏中部的"网页看板"入口：白底浅蓝描边 + 主题蓝图标与文字，与右侧灰字、左侧彩色大按钮形成层次，
        /// 既不抢状态按钮的视觉重心，又能让人一眼看到这个最重要的远程控制通道。
        /// </summary>
        private UIElement BuildWebEntry()
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(Theme.Icon("\uf0ac", 16, Theme.Primary));
            content.Children.Add(new TextBlock
            {
                Text = "打开网页看板",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Primary,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            _webEntry = new Border
            {
                Background = WebEntryBg,
                BorderBrush = WebEntryLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 8, 18, 8),
                Child = content,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0),
                ToolTip = "在浏览器中打开控制看板（远程控制请用手机或其他电脑访问同一地址）"
            };
            _webEntry.MouseLeftButtonUp += (s, e) => OpenWebDashboard();
            _webEntry.MouseEnter += (s, e) => _webEntry.Background = WebEntryHover;
            _webEntry.MouseLeave += (s, e) => _webEntry.Background = WebEntryBg;
            return _webEntry;
        }

        /// <summary>置顶提示横幅：由桌面程序绘制，浏览器没开也不会漏报。</summary>
        private Border BuildBanner()
        {
            _bannerBody = new TextBlock { Text = "", FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), TextWrapping = TextWrapping.Wrap };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(Theme.Icon("\uf071", 20, Theme.Danger));
            left.Children.Add(new TextBlock { Text = "系统提示", FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0), Foreground = Theme.Danger, VerticalAlignment = VerticalAlignment.Center });

            var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(left);
            body.Children.Add(_bannerBody);

            var close = new Button
            {
                Content = Theme.Icon("\uf00d", 12, Brushes.White),
                Background = Theme.Danger,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };
            close.Click += (s, e) => _banner.Visibility = Visibility.Collapsed;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(body);
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            var banner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xF4)),
                BorderBrush = Theme.Danger,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 12, 12),
                Margin = new Thickness(14, 8, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid
            };
            Grid.SetZIndex(banner, 100);
            return banner;
        }

        // ================= 页面切换 =================

        public void ShowPage(string key)
        {
            if (!_pages.ContainsKey(key)) return;
            _currentPage = key;
            _contentHost.Children.Clear();
            var page = _pages[key]();
            _contentHost.Children.Add(page);
            AttachDropImport(key, page);
            foreach (var b in _navButtons)
            {
                bool active = (string)b.Tag == key;
                b.Background = active ? Theme.Neutral : Brushes.Transparent;
                b.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        // ================= 拖入导入（规格 §1.3） =================
        // 只在对应页面拖：排程页拖排程配置、调休页拖调休配置。
        // 与导入窗口、整包还原共用同一批导入器（ConfigTransfer）与同一份校验规则。

        private void AttachDropImport(string pageKey, UIElement page)
        {
            string kind = pageKey == "tasks" ? ConfigTransfer.Tasks
                        : pageKey == "holidays" ? ConfigTransfer.Tiaoxiu
                        : null;
            if (kind == null || page == null) return;

            page.AllowDrop = true;
            page.DragOver += (s, e) =>
            {
                e.Effects = DropAcceptable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            page.Drop += async (s, e) =>
            {
                string path = DropFirstFile(e.Data);
                if (string.IsNullOrEmpty(path)) return;
                e.Handled = true;
                await ImportDroppedAsync(path, kind);
            };
        }

        static string DropFirstFile(IDataObject data)
        {
            try
            {
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
                var files = data.GetData(DataFormats.FileDrop) as string[];
                return (files != null && files.Length > 0) ? files[0] : null;
            }
            catch { return null; }
        }

        /// <summary>只接受 .jsonl 配置文件与 .zip 备份包，其它一律拒绝（拖拽光标显示禁止）。</summary>
        static bool DropAcceptable(IDataObject data)
        {
            string path = DropFirstFile(data);
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext == ".jsonl" || ext == ".zip";
        }

        private async Task ImportDroppedAsync(string path, string kind)
        {
            string label = ConfigTransfer.Label(kind);
            try
            {
                // 先只读预览：非法文件在这里就被挡住，绝不会写坏配置
                var pre = await DesktopApi.PostJson("/api/transfer/preview",
                    new { path, items = new[] { kind }, overwrite = false, kind });
                if (pre == null) { AppDialog.Warn(this, "拖入导入", "本机服务没有响应（请确认程序仍在运行）。"); return; }
                if (pre["status"]?.ToString() == "error")
                {
                    AppDialog.Warn(this, "这个文件不能用", pre["message"]?.ToString() ?? "无法解析该文件。");
                    return;
                }
                int willIn = pre["imported"]?.GetValue<int>() ?? 0;
                int bad = pre["invalid"]?.GetValue<int>() ?? 0;
                int skip = pre["skipped"]?.GetValue<int>() ?? 0;
                if (willIn == 0 && bad == 0)
                {
                    AppDialog.Info(this, "没有可导入的内容",
                        "这个文件里没有可用的" + label + "（可能已被导入过，或类别不匹配）。");
                    return;
                }
                if (!AppDialog.Confirm(this, "确认导入" + label,
                        Path.GetFileName(path) + "\n\n将写入 " + willIn + " 条"
                        + (skip > 0 ? "，跳过 " + skip + " 条（本机已有相同内容）" : "")
                        + (bad > 0 ? "，非法 " + bad + " 条（会被跳过）" : "")
                        + "\n\n导入只做追加合并，不会清空本机现有" + label + "。确定继续吗？",
                        "导入", "取消", bad > 0 ? AppDialogKind.Warning : AppDialogKind.Question)) return;

                var r = await DesktopApi.PostJson("/api/transfer/import",
                    new { path, items = new[] { kind }, overwrite = false, kind });
                string st = r?["status"]?.ToString();
                string msg = r?["message"]?.ToString() ?? "";
                if (st == "error") AppDialog.Error(this, "导入失败", msg);
                else AppDialog.Success(this, "导入完成", msg);
                ShowPage(_currentPage);   // 重载当前页，立刻看到新数据
            }
            catch (Exception ex) { ShowError(ex); }
        }

        public void ShowError(Exception ex)
        {
            PlayLog.Write("系统", "界面异常", "error", detail: ex.Message);
            AppDialog.Warn(this, "操作失败", ex.Message);
        }

        /// <summary>每次 /api/sys 刷新成功后广播给页面（播控台据此实时更新进度与队列，避免各页面重复打接口）。</summary>
        public event Action<JsonObject> SysUpdated;

        /// <summary>供页面在发出播控指令后立刻刷新一次状态。</summary>
        public async Task RefreshNowAsync() => await RefreshStatusAsync();

        public void Notify(string message) => AlertCenter.Publish("桌面端", message, "info");

        // ================= 状态刷新 =================

        private async Task RefreshStatusAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var sys = await DesktopApi.GetSys();
                bool on = Program.IsGlobalSwitchOn();

                // 状态大按钮
                bool playing = sys?["is_playing"]?.GetValue<bool>() ?? false;
                if (!on)
                {
                    ApplySwitchVisual(false, "已停用");
                    _stateSwitchText.Text = "点击恢复运行";
                }
                else
                {
                    ApplySwitchVisual(true, playing ? "运行中 · 正在播放" : "运行中 · 待机");
                    _stateSwitchText.Text = "点击停用广播";
                    if (playing) _stateIcon.Text = Theme.HasIconFont ? "\uf519" : "●";
                }

                string now = sys?["current_playing_name"]?.ToString() ?? "";
                _playInfo.Text = playing && !string.IsNullOrEmpty(now)
                    ? "当前播放：" + now.Replace("[定时] ", "").Replace("[手动] ", "").Trim()
                    : "待机中...";

                var st = sys?["sys_stats"] as JsonObject;
                _statsInfo.Text = $"CPU {st?["cpu"]?.GetValue<double>() ?? 0:F1}%   RAM {st?["ram"]?.GetValue<double>() ?? 0:F1}%";
                _portInfo.Text = $"Web :{Program.ListenPort}";

                // 悬浮球开关立即生效：开→建出来；关→直接把球窗口关掉（原来只处理"开"，
                // 关掉必须重启程序，用户反馈过）。
                if (Program.GetShowBall())
                {
                    if (_ball == null) SetupFloatingBall();
                }
                else if (_ball != null)
                {
                    try { _ball.Close(); } catch { }
                    _ball = null;
                }
            try { _ball?.Refresh(); } catch { }
            _nextInfo.Text = NextTaskText(out bool hasTaskToday);

                // 托盘小圆点：红=已停用，蓝=正常但今日无排程，绿=正常且今日有排程
                var trayState = !on ? TrayState.Disabled : (hasTaskToday ? TrayState.Scheduled : TrayState.NoSchedule);
                if (_trayState != trayState) { _trayState = trayState; ApplyTrayState(trayState, on); }

                try { SysUpdated?.Invoke(sys); } catch { }

                await PollAlertsAsync();
            }
            catch { }
            finally { _busy = false; }
        }

        // ================= 桌面悬浮球 / 配置自检 =================

        /// <summary>创建悬浮球：状态与托盘图标一致（红方块=停用、蓝圆=今日无排程、绿三角=今日有排程），
        /// 中间显示距下一个响铃的剩余时间；左键配置自检、右键弹与托盘相同的菜单。</summary>
        private void SetupFloatingBall()
        {
            try
            {
                _ball = new FloatingBall(BallStateOf, RunSelfCheck, ShowTrayMenuAtBall, OpenMainFromBall);
                _ball.Show();
            }
            catch (Exception ex) { PlayLog.Write("系统", "悬浮球", "warn", detail: "创建失败：" + ex.Message); }
        }

        /// <summary>把主窗口的状态翻译成悬浮球要的形状与倒计时。</summary>
        private BallState BallStateOf()
        {
            bool on = Program.IsGlobalSwitchOn();
            var st = _trayState ?? (on ? TrayState.NoSchedule : TrayState.Disabled);
            var state = new BallState
            {
                Enabled = on,
                Scheduled = on && st == TrayState.Scheduled,   // 绿底：今日有排程（与托盘同一套语义）

            };
            try
            {
                bool hasTask;
                string text = NextTaskText(out hasTask);
                if (hasTask && !string.IsNullOrEmpty(text))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2}):(\d{2})");
                    if (m.Success)
                    {
                        var today = DateTime.Today;
                        state.NextRing = today.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
                    }
                }
            }
            catch { }
            return state;
        }

        /// <summary>
        /// 把主界面调出来（托盘双击/托盘菜单/悬浮球双击都走这里）。
        /// 关键：**恢复任务栏按钮**（ShowInTaskbar = true）——静默启动时窗口是"隐藏且不进任务栏"的，
        /// 若不恢复，用户会遇到"调出主窗口却看不到任务栏图标"的问题（用户反馈过）。
        /// 另外从后台唤到最前：Windows 不允许后台进程随便抢焦点，所以"Topmost 抬一下再取消"最稳，
        /// 再补一次 SetForegroundWindow（拿不到焦点权限时它只是失败，不抛异常）。
        /// </summary>
        public void ShowMainWindow(string reason = "")
        {
            try
            {
                SelfCheckPopup.CloseCurrent();      // 气泡是 Topmost，不关掉会浮在主界面上方
                ShowInTaskbar = true;               // 从托盘/悬浮球启动过的进程：恢复任务栏图标
                if (!IsVisible) Show();
                WindowState = WindowState.Normal;
                Topmost = true;
                Activate();
                Topmost = false;
                Focus();
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    try { NativeShowWindow(h, 9); } catch { }          // 9 = SW_RESTORE
                    // 任务栏按钮：WPF 的 ShowInTaskbar 在窗口创建后再改**不一定生效** → 直接改 ex-style
                    //（去掉 TOOLWINDOW、加上 APPWINDOW），这样"从托盘调出来的主窗口"必定有任务栏图标。
                    try
                    {
                        long ex = NativeGetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                        ex = (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
                        NativeSetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
                    }
                    catch { }
                    try { NativeSetForegroundWindow(h); } catch { }
                }
                if (!string.IsNullOrEmpty(reason))
                    PlayLog.Write("系统", "打开主界面", "info", detail: reason);
            }
            catch (Exception ex) { try { LogHelper.Log("ShowMainWindow Failed", ex); } catch {} }
        }

        /// <summary>悬浮球双击：把主界面调出来（与托盘"显示主界面"同一套动作）。</summary>
        private void OpenMainFromBall() => ShowMainWindow("悬浮球双击");


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // 任务栏按钮修正用：从托盘/悬浮球把主窗口调回来时，WPF 的 ShowInTaskbar 在窗口创建后再改**不一定生效**，
        // 所以直接改窗口的 ex-style（去掉 TOOLWINDOW、加上 APPWINDOW），保证任务栏一定出现图标。
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x00000080L;
        const long WS_EX_APPWINDOW = 0x00040000L;
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr NativeGetWindowLongPtr(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr NativeSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private static bool NativeSetForegroundWindow(IntPtr h) => SetForegroundWindow(h);
        private static bool NativeShowWindow(IntPtr h, int cmd) => ShowWindow(h, cmd);
        /// <summary>悬浮球右键：在球的位置弹出与托盘完全相同的菜单。</summary>
        private void ShowTrayMenuAtBall()
        {
            try
            {
                if (_ball != null) { _ball.Activate(); }
                ShowTrayMenu();
            }
            catch { }
        }

        /// <summary>配置自检：只读检查本地配置，结果用动画气泡展示并写入日志。</summary>
        private void RunSelfCheck()
        {
            try
            {
                var items = ConfigSelfCheck.Run();
                PlayLog.Write("系统", "配置自检", "info", detail: ConfigSelfCheck.ToText(items).Replace("\r\n", "；").Replace("\n", "；"));
                double ax = _ball != null ? _ball.Left : SystemParameters.WorkArea.Right - FloatingBall.BallSize - 24;
                double ay = _ball != null ? _ball.Top : SystemParameters.WorkArea.Bottom - 160;
                new SelfCheckPopup(items, ax, ay) { Owner = null }.Show();
            }
            catch (Exception ex)
            {
                try { AppDialog.Error(this, "自检失败", ex.Message); } catch { }
            }
        }

        private string NextTaskText(out bool hasTaskToday)
        {
            hasTaskToday = false;
            try
            {
                var tasks = Program.ReadJsonl("tasks.jsonl");
                var now = DateTime.Now; int nowSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
                int best = int.MaxValue; string bestText = null;
                foreach (var t in tasks)
                {
                    var o = t as JsonObject; if (o == null) continue;
                    if (!Program.ShouldPlayTodayForUi(o)) continue;
                    hasTaskToday = true;
                    string st = o["start_time"]?.ToString() ?? ""; if (st.Length == 5) st += ":00";
                    var parts = st.Split(':'); if (parts.Length != 3) continue;
                    if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m) || !int.TryParse(parts[2], out int s2)) continue;
                    int sec = h * 3600 + m * 60 + s2;
                    int delta = sec - nowSec; if (delta < 0) delta += 86400;
                    if (delta < best) { best = delta; bestText = $"{h:00}:{m:00}"; }
                }
                if (bestText == null) return "下一次排程：今日无有效排程";
                return $"下一次排程：{bestText}（约 {best / 60} 分钟后）";
            }
            catch { return "下一次排程：读取失败"; }
        }

        private async Task PollAlertsAsync()
        {
            try
            {
                var arr = await DesktopApi.GetAlerts(_alertCursor);
                if (arr == null || arr.Count == 0) return;
                var lines = new List<string>();
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    lines.Add($"· [{o["time"]}] {o["message"]}");
                    long id = o["id"]?.GetValue<long>() ?? 0;
                    if (id > _alertCursor) _alertCursor = id;
                }
                if (lines.Count > 0)
                {
                    _bannerBody.Text = string.Join("\n", lines);
                    _banner.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        /// <summary>
        /// 一键停用/恢复广播：**不做二次确认**（紧急停用要的就是即时响应），
        /// 点了先立刻把按钮状态改成目标状态给足反馈，再发指令、再拉一次真实状态校准。
        /// </summary>
        private async Task ToggleSwitchAsync()
        {
            bool on = Program.IsGlobalSwitchOn();
            bool target = !on;

            // 乐观更新：先让界面立刻响应
            ApplySwitchVisual(target, target ? "运行中 · 待机" : "已停用");
            _stateSwitchText.Text = target ? "点击停用广播" : "点击恢复运行";

            await DesktopApi.ToggleSwitch(target);
            PlayLog.Write("系统", "开关", "warn", detail: target ? "桌面端恢复广播系统运行" : "桌面端停用广播系统");
            AlertCenter.Publish("系统", target ? "广播系统已恢复运行，定时打铃照常执行。" : "广播系统已停用，所有音频已静音，定时打铃不再执行。", target ? "info" : "warn");
            await RefreshStatusAsync();
        }

        /// <summary>把底栏状态大按钮刷成指定状态（停用=红，正常=绿）。</summary>
        private void ApplySwitchVisual(bool on, string text)
        {
            _stateButton.Background = on ? Theme.Success : Theme.Danger;
            _stateText.Text = text;
            _stateIcon.Text = Theme.HasIconFont ? "\uf011" : "●";
        }

        // ================= 托盘 =================

        private void SetupTray()
        {
            _tray = new Forms.NotifyIcon
            {
                Icon = Program.AppIcon,
                Text = "智能校园广播",
                Visible = true
            };
            // 右键菜单用 WPF 自绘（TrayMenuWindow），不再用 WinForms 的 ContextMenuStrip
            _tray.ContextMenuStrip = null;
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right)
                    Dispatcher.Invoke(new Action(ShowTrayMenu));
            };
            _tray.DoubleClick += (s, e) => Dispatcher.Invoke(new Action(() => ShowMainWindow("托盘双击")));
        }

        /// <summary>弹出托盘的现代风格右键菜单（圆角卡片 + FA 图标 + 圆角悬停）。</summary>
        private void ShowTrayMenu()
        {
            try
            {
                if (_trayMenu != null && _trayMenu.IsVisible) { _trayMenu.Close(); return; }
                bool on = Program.IsGlobalSwitchOn();
                var menu = new TrayMenuWindow(_trayState ?? (on ? TrayState.NoSchedule : TrayState.Disabled), on);
                menu.ShowMainRequested += () => ShowMainWindow("托盘菜单");
                menu.WebRequested += OpenWebDashboard;
                menu.ToggleRequested += () => { _ = ToggleSwitchAsync(); };
                menu.ExitRequested += ExitApp;
        menu.SelfCheckRequested += RunSelfCheck;
                _trayMenu = menu;
                menu.ShowAtCursor();
            }
            catch { }
        }


        /// <summary>
        /// 根据状态刷新托盘图标右上角的小圆点与右键菜单里的启用/停用项：
        /// 红 = 系统已停用；蓝 = 正常但今天没有排程；绿 = 正常且今天有排程。
        /// </summary>
        private void ApplyTrayState(TrayState state, bool switchOn)
        {
            try
            {
                var old = _tray.Icon;
                _tray.Icon = TrayIcons.BuildStateIcon(state);
                if (old != null && !ReferenceEquals(old, Program.AppIcon)) { try { old.Dispose(); } catch { } }
            }
            catch { }

            // 注意：状态圆点只画在托盘图标上；主窗口/任务栏图标保持程序原图标，不叠加圆点
            string tip;
            switch (state)
            {
                case TrayState.Disabled: tip = "智能校园广播 · 已停用（不会打铃）"; break;
                case TrayState.NoSchedule: tip = "智能校园广播 · 运行中（今日无排程）"; break;
                default: tip = "智能校园广播 · 运行中（今日有排程）"; break;
            }
            try { _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; } catch { }
        }

        private void ExitApp()
        {
            try
            {
                _tray.Visible = false;
                StopAllAndExit();
            }
            catch { }
            System.Windows.Application.Current.Shutdown();
            Environment.Exit(0);
        }

        private void StopAllAndExit()
        {
            try { Program.StopAllAudio("用户退出程序"); } catch { }
            try { Program.ReleaseSingleInstance(); } catch { }
        }

        /// <summary>重启本程序（端口等设置变更后使用）。</summary>
        public void RestartApp()
        {
            try
            {
                PlayLog.Write("系统", "重启", "warn", detail: "由桌面端触发重启");
                Program.StopAllAudio("重启前停止播放");
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--desktop" });
                Program.ReleaseSingleInstance();
                System.Windows.Application.Current.Shutdown();
                Environment.Exit(0);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        /// <summary>
        /// 打开子窗口时**把主窗口虚化**（用户要求：子窗口弹出后要与主窗口区分开，而不是子窗口自己糊）；关掉自动恢复。
        /// </summary>
        public void ShowChildDialog(Window dlg)
        {
            try
            {
                // 【真模糊】给主窗口**内容**套 BlurEffect。
                // 说明：以前用的是 WCA 毛玻璃（WindowBlur），它只虚化"窗口背后"的东西，
                // 而本窗口背景不透明 → 用户看到的是"完全没效果"。这里改成 WPF 自身的模糊，肉眼可见。
                var rootEl = this.Content as System.Windows.UIElement;
                var oldEffect = rootEl?.Effect;
                if (rootEl != null)
                    rootEl.Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 9,
                        KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                    };
                dlg.Owner = this;
                try { dlg.ShowDialog(); }
                finally { if (rootEl != null) rootEl.Effect = oldEffect; }
            }
            finally { try { WindowBlur.Remove(this); } catch { } }   // 兼容保留（WCA 对自身内容无效，真模糊见上）
        }

        public void OpenWebDashboard()
        {
            try { Process.Start(new ProcessStartInfo(Program.StartupUrl) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError(ex); }
        }

        public void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError(ex); }
        }

        // ================= 备份 / 恢复 =================

        /// <summary>
        /// 打开「导入 / 导出」窗口。窗口内不含业务逻辑，全部交给 /api/transfer/*（ConfigTransfer），
        /// 与拖入导入、整包还原共用同一份校验规则。
        /// </summary>
        private void OpenTransferWindow()
        {
            var win = new TransferWindow { Owner = this };
            win.OnFinished = () => ShowPage(_currentPage);   // 导入成功后重载当前页，避免列表还是旧数据
            win.ShowDialog();
        }

        private void BackupConfig()
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                FileName = $"BroadcastBackup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            };
            if (sfd.ShowDialog() != true) return;
            // 【导出选项】默认不导出隐私数据（密码/令牌）；需要时才勾选，跨电脑导入并不需要它。
            bool withPrivacy = false;
            try
            {
                withPrivacy = AppDialog.Confirm(this, "导出选项 · 隐私数据",
                    "是否把隐私数据一并导出？\n\n隐私数据包括：网页口令、组网令牌、数据密码、凭证密钥。\n\n默认不导出（推荐）：排程、调休、预警配置、音乐库、铃声都能正常还原；vnt 只带服务器地址，\n跨电脑导入时令牌与密码以本机现有为准，不会互相污染。",
                    "导出隐私数据", "不导出（推荐）", AppDialogKind.Warning);
            }
            catch { withPrivacy = false; }
            try
            {
                if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                System.IO.Compression.ZipFile.CreateFromDirectory(Program.AppDataDir, sfd.FileName, System.IO.Compression.CompressionLevel.Optimal, false);
                if (!withPrivacy)
                {
                    int cleared = Program.StripPrivacyFromBackup(sfd.FileName);
                    PlayLog.Write("数据", "备份", "info", detail: $"{sfd.FileName}（已按默认策略清空隐私字段 {cleared} 处）");
                }
                else
                {
                    PlayLog.Write("数据", "备份", "warn", detail: $"{sfd.FileName}（含隐私数据，明文，请妥善保管）");
                }
                PlayLog.Write("数据", "备份", "info", detail: sfd.FileName);
                AppDialog.Success(this, "备份成功", "数据备份成功！\n此压缩包包含您的全部排程、配置与本地铃声。");
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void RestoreConfig()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip" };
            if (ofd.ShowDialog() != true) return;
            if (!AppDialog.Confirm(this, "警告 · 数据覆盖",
                "整包还原会把备份包里的配置按同一批导入器写入本机，而不是盲目解压覆盖：\n\n"
                + "· 每一类都先做合法性校验与旧格式迁移，非法条目跳过并报告；\n"
                + "· 不导入预警历史与运行日志；\n"
                + "· vnt 的设备 ID / 令牌 / 数据密码 / 凭证密钥 / 动态码种子一律以本机现有为准，绝不会被覆盖；\n"
                + "· 音乐库只导入编号不冲突的条目，铃声同名覆盖。\n\n"
                + "程序随后会自动重启以加载新配置。确定继续吗？",
                "继续还原", "取消", AppDialogKind.Warning)) return;
            try
            {
                Program.StopAllAudio("还原数据前停止播放");
                // 走同一批导入器（ConfigTransfer）：逐类校验 + 旧格式迁移 + 按规则合并，
                // 不再盲目解压覆盖；vnt 的密钥类字段由导入器保证以本机现有为准。
                var rep = ConfigTransfer.ImportWholePackage(ofd.FileName);
                if (rep["status"]?.ToString() == "error")
                {
                    AppDialog.Error(this, "还原失败", rep["message"]?.ToString() ?? "无法解析该备份包。");
                    return;
                }
                                string msg = rep["message"]?.ToString() ?? "";
                PlayLog.Write("数据", "整包还原", "warn", detail: ofd.FileName + "；" + msg);
                AppDialog.Success(this, "还原完成", msg + "\n\n程序将重新启动。");
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Arguments = "--desktop" });
                ExitApp();
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }
}
