#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBlock = System.Windows.Controls.TextBlock;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontWeights = System.Windows.FontWeights;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 托盘图标右键菜单（WPF 自绘）。
    /// WinForms 的 ContextMenuStrip 就算换掉渲染器也还是方角 + 老式高亮，和主界面的白卡片风格不搭，
    /// 所以这里干脆自己画一个：圆角卡片 + 阴影 + Font Awesome 图标 + 圆角悬停高亮，光标处弹出、失焦自动关闭。
    /// </summary>
    public class TrayMenuWindow : Window
    {
        public event Action ShowMainRequested;
        public event Action WebRequested;
        public event Action ToggleRequested;
        public event Action ExitRequested;
        public event Action SelfCheckRequested;   // 配置自检

        private readonly TrayState _state;
        private readonly bool _switchOn;
        private bool _closing;

        private static readonly Brush ItemHover = new SolidColorBrush(Color.FromRgb(0xEA, 0xF4, 0xFB));
        private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xF2, 0xF7));
        private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D));

        public TrayMenuWindow(TrayState state, bool switchOn)
        {
            _state = state;
            _switchOn = switchOn;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = 236;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Opacity = 0;   // 定位完成后再显示，避免在 (0,0) 闪一下
            BuildUi();
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Margin = new Thickness(6) };

            // ---------- 状态头 ----------
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 8) };
            var marker = TrayIcons.BuildMarkerShape(_state, 9);
            marker.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(marker);
            head.Children.Add(new TextBlock
            {
                Text = TrayIcons.StatusText(_state),
                FontSize = 12,
                Foreground = GrayBrush,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(head);
            stack.Children.Add(Separator());

            // ---------- 功能项 ----------
            stack.Children.Add(Item("\uf2d0", "显示主界面", Theme.TextDark, () => Raise(ShowMainRequested)));
            stack.Children.Add(Item("\uf0ac", "打开网页看板", Theme.Primary, () => Raise(WebRequested)));
            stack.Children.Add(Separator());

            stack.Children.Add(_switchOn
                ? Item("\uf011", "停用广播系统", DangerBrush, () => Raise(ToggleRequested))
                : Item("\uf144", "启用广播系统", new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)), () => Raise(ToggleRequested)));
            stack.Children.Add(Separator());

            stack.Children.Add(Item("\uf002", "配置自检", Theme.TextDark, () => Raise(SelfCheckRequested)));
        stack.Children.Add(Item("\uf2f5", "完全退出系统", GrayBrush, () => Raise(ExitRequested)));

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(12),
                Child = stack,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.22,
                    Color = Color.FromRgb(0x2C, 0x3E, 0x50)
                }
            };
            Content = card;

            Deactivated += (s, e) => Close();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void Raise(Action handler)
        {
            Close();
            if (handler == null) return;
            // 等菜单真正关掉再执行动作（动作可能弹对话框或退出程序）
            Dispatcher.BeginInvoke(new Action(handler), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static Border Separator() => new Border
        {
            Height = 1,
            Background = LineBrush,
            Margin = new Thickness(8, 4, 8, 4)
        };

        private Border Item(string faCodepoint, string text, Brush color, Action onClick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Theme.Icon(faCodepoint, 14, color);
            icon.Width = 22;
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = color,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            var item = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 8, 8, 8),
                Child = row,
                Cursor = Cursors.Hand
            };
            item.MouseEnter += (s, e) => item.Background = ItemHover;
            item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (s, e) => onClick();
            return item;
        }

        /// <summary>在鼠标位置弹出：默认对齐到光标右下，靠近屏幕边缘时自动回缩。</summary>
        public void ShowAtCursor()
        {
            Show();
            UpdateLayout();

            var cursor = Forms.Cursor.Position;
            // 先落在 (0,0) 才能把屏幕坐标换算成本窗口的 DIP 坐标（高 DPI 下不能直接除 1.5）
            Left = 0; Top = 0;
            UpdateLayout();
            var pt = PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));

            var wa = SystemParameters.WorkArea;
            double left = pt.X - ActualWidth + 12;
            double top = pt.Y - ActualHeight - 10;
            if (top < wa.Top + 4)
            {
                top = pt.Y + 10;                       // 上方放不下就放到下方
                if (top + ActualHeight > wa.Bottom - 4) top = wa.Bottom - ActualHeight - 4;
            }
            Left = Math.Max(wa.Left + 4, Math.Min(left, wa.Right - ActualWidth - 4));
            Top = Math.Max(wa.Top + 4, top);

            Opacity = 1;
            Activate();
            Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            _closing = true;
            base.OnClosed(e);
        }

        public bool IsClosedSafe => _closing;

        /// <summary>
        /// 把菜单渲染成 PNG（不进屏、不依赖托盘），用于 `--tray-preview` 人工核对菜单样式。
        /// 走的是真实的菜单构建代码，所以图里什么样，实际弹出的就是什么样。
        /// </summary>
        public static string RenderPreview(TrayState state, bool switchOn, string outPath)
        {
            var win = new TrayMenuWindow(state, switchOn);
            var card = (FrameworkElement)win.Content;
            win.Content = null;               // 从窗口上摘下来，才能放进预览容器
            card.Margin = new Thickness(0);

            var host = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)),
                Padding = new Thickness(22),
                Child = card
            };
            double w = win.Width + 44;
            host.Measure(new System.Windows.Size(w, double.PositiveInfinity));
            host.Arrange(new System.Windows.Rect(0, 0, w, host.DesiredSize.Height));
            host.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);

            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(outPath)) encoder.Save(fs);
            return System.IO.Path.GetFullPath(outPath);
        }
    }
}
