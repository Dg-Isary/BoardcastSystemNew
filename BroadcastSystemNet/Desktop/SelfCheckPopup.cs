#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 配置自检结果气泡：
    ///   · 顶部显示【问题总数】（错误 N · 警告 M），全通过时显示绿色"全部通过"；
    ///   · 下面**默认只列有问题的项**（不通过=红色图标，通过=绿色图标）；
    ///   · 有「展开全部检查项 / 只看问题」按钮可切换；
    ///   · 鼠标移开（或 12 秒无操作）自动淡出收起，点击也立即收起。
    /// 图标一律用 Font Awesome，不使用任何 Emoji/符号字符。
    /// </summary>
    public class SelfCheckPopup : Window
    {
        private readonly List<ConfigSelfCheck.Item> _items;
        private readonly StackPanel _body = new StackPanel();
        private readonly Button _toggle;
        private readonly TextBlock _toggleText;
        private bool _showAll;
        private readonly double _anchorX;
        private readonly double _anchorY;
        private readonly DispatcherTimer _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        private readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private DateTime _opened;
        private int _outsideMs;
        private bool _closing;
        private static SelfCheckPopup _current;   // 连点悬浮球时只保留最新一张

        public SelfCheckPopup(List<ConfigSelfCheck.Item> items, double anchorX, double anchorY)
        {
            _items = items ?? new List<ConfigSelfCheck.Item>();
            _anchorX = anchorX;
            _anchorY = anchorY;
            try { var old = _current; _current = this; if (old != null && !ReferenceEquals(old, this)) old.Close2(); } catch { }
            Closed += (s, e) => { if (ReferenceEquals(_current, this)) _current = null; };

            int err = _items.Count(i => i.Level == "错误");
            int warn = _items.Count(i => i.Level == "警告");
            bool allOk = err == 0 && warn == 0;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 480;
            Width = 420;

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE3, 0xEC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.22, Color = Colors.Black }
            };

            var stack = new StackPanel();

            // ---------- 顶部：问题总数 ----------
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Theme.Icon(allOk ? "\uf00c" : "\uf071", 15,
                allOk ? new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)) : new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C))));
            head.Children.Add(new TextBlock
            {
                Text = allOk
                    ? "配置自检：全部通过"
                    : $"配置自检：错误 {err} 项 · 警告 {warn} 项（共 {err + warn} 项问题）",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = allOk ? new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)) : new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C))
            });
            stack.Children.Add(head);

            // ---------- 中间：结果列表（默认只列问题） ----------
            _body.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(new ScrollViewer
            {
                Content = _body,
                MaxHeight = Math.Max(200, System.Windows.SystemParameters.WorkArea.Height - 240),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            // ---------- 底部：展开/收起 + 说明 ----------
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _toggleText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _toggle = Theme.Button("", null, null, "\uf078", null);
            _toggle.Content = _toggleText;
            _toggle.Padding = new Thickness(10, 4, 10, 4);
            // 展开/收起会改变高度：重新摆位，避免顶出屏幕或钻到任务栏下面
            _toggle.Click += (s, e) => { _showAll = !_showAll; Render(); RepositionToAnchor(); };
            footer.Children.Add(_toggle);
            footer.Children.Add(new TextBlock
            {
                Text = "只读检查，未修改配置；鼠标移开自动收起，明细已写入日志",
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x99, 0xA6))
            });
            stack.Children.Add(footer);

            card.Child = stack;
            Content = card;
            Render();

            // ---------- 位置与动画 ----------
            Loaded += (s, e) =>
            {
                RepositionToAnchor();

                Opacity = 0;
                var scale = new ScaleTransform(0.94, 0.94);
                card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                card.RenderTransform = scale;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(200)));
                _opened = DateTime.Now;
                _autoClose.Start();
                _watch.Start();
            };

            // 鼠标移开 → 收起。用全局光标轮询判定：
            // 气泡开在球的旁边，鼠标常常一次都没进入过气泡，只靠 WPF 的 MouseLeave（必须先 MouseEnter）是不会触发的。
            _watch.Tick += (s, e) =>
            {
                bool inside = CursorInside();
                if (inside) { _outsideMs = 0; return; }
                if ((DateTime.Now - _opened).TotalSeconds < 2.0) return;   // 开窗 2 秒内先不动，留时间把鼠标移上来
                _outsideMs += 150;
                if (_outsideMs >= 700) Close2();
            };
            _autoClose.Tick += (s, e) => { if (!CursorInside()) Close2(); };
            MouseRightButtonUp += (s, e) => Close2();
        }

        /// <summary>光标是否停在气泡（含 6 像素容差）内；坐标要按 DPI 从物理像素换算回 WPF 逻辑像素。</summary>
        private bool CursorInside()
        {
            try
            {
                var p = System.Windows.Forms.Control.MousePosition;
                var dpi = VisualTreeHelper.GetDpi(this);
                double x = p.X / dpi.DpiScaleX;
                double y = p.Y / dpi.DpiScaleY;
                return x >= Left - 6 && x <= Left + ActualWidth + 6
                    && y >= Top - 6 && y <= Top + ActualHeight + 6;
            }
            catch { return true; }   // 判定不了就当在气泡内，别误关
        }

        /// <summary>
        /// 贴着悬浮球摆位（展开/收起改变高度后必须重算，否则会顶出屏幕或被任务栏遮住）。
        /// 判定范围用"球所在显示器的工作区"（排除任务栏），优先摆球左侧，放不下翻右侧，
        /// 两侧都放不下就取空间较大的一侧并夹紧在屏幕内。
        /// </summary>
        public void RepositionToAnchor()
        {
            try
            {
                UpdateLayout();
                double w = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) ? 420 : Width);
                double h = ActualHeight > 0 ? ActualHeight : 320;
                var wa = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)_anchorX, (int)_anchorY)).WorkingArea;
                const double gap = 12, edge = 8;

                double xLeft = _anchorX - w - gap;                       // 球左侧
                double xRight = _anchorX + FloatingBall.BallSize + gap;  // 球右侧
                double x;
                if (xLeft >= wa.Left + edge) x = xLeft;
                else if (xRight + w <= wa.Right - edge) x = xRight;
                else x = Math.Max(wa.Left + edge, Math.Min(xLeft, wa.Right - w - edge));

                double y = _anchorY;
                if (y + h > wa.Bottom - edge) y = wa.Bottom - h - edge;
                if (y < wa.Top + edge) y = wa.Top + edge;

                Left = x; Top = y;
            }
            catch { }
        }

        /// <summary>按当前模式重建列表：默认只列问题项，展开后列全部（通过=绿、不通过=红）。</summary>
        private void Render()
        {
            _body.Children.Clear();
            var problems = _items.Where(i => i.Level != "通过").ToList();
            var shown = _showAll ? _items : problems;

            if (shown.Count == 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Theme.Icon("\uf00c", 13, new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71))));
                row.Children.Add(new TextBlock
                {
                    Text = "没有发现任何问题，全部检查项都通过。",
                    FontSize = 12,
                    Margin = new Thickness(7, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71))
                });
                _body.Children.Add(row);
            }
            else
            {
                foreach (var it in shown)
                {
                    bool ok = it.Level == "通过";
                    Brush color = ok
                        ? new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71))
                        : new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                    string icon = ok ? "\uf00c" : (it.Level == "错误" ? "\uf00d" : "\uf071");

                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                    var ico = Theme.Icon(icon, 12, color);
                    ico.VerticalAlignment = VerticalAlignment.Top;
                    ico.Margin = new Thickness(0, 2, 0, 0);
                    row.Children.Add(ico);
                    row.Children.Add(new TextBlock
                    {
                        Text = it.Text,
                        FontSize = 12,
                        Margin = new Thickness(7, 0, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x3C, 0x4A, 0x56)) : color
                    });
                    _body.Children.Add(row);
                }
            }

            int problemCount = problems.Count;
            _toggleText.Text = _showAll
                ? "只看问题（" + problemCount + " 项）"
                : "展开全部检查项（" + _items.Count + " 项）";
        }

        /// <summary>关掉当前那张气泡（悬浮球双击打开主界面时调用；它是 Topmost，不关会浮在主界面上方）。</summary>
        public static void CloseCurrent()
        {
            try { _current?.Close2(); } catch { }
        }
        /// <summary>淡出后关闭（重复调用安全）。</summary>
        private void Close2()
        {
            if (_closing) return;
            _closing = true;
            _autoClose.Stop();
            _watch.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (a, b) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
