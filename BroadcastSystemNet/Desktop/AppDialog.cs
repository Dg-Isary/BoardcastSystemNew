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
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontWeights = System.Windows.FontWeights;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>对话框语气：决定图标、主色与默认按钮配色。</summary>
    public enum AppDialogKind { Info, Success, Warning, Error, Question }

    /// <summary>
    /// 统一风格的应用内对话框，替代系统 MessageBox。
    /// 系统 MessageBox 是 Windows 经典灰白外观，和本程序"白卡片 + 浅蓝描边 + Font Awesome"的设计语言完全不搭，
    /// 因此所有提示都走这里：圆角卡片、语气图标徽章、右下角按钮、Enter/Esc 快捷键、按住标题区可拖动。
    /// </summary>
    public static class AppDialog
    {
        // ================= 对外入口 =================

        public static void Info(Window owner, string title, string message)
            => Show(owner, title, message, AppDialogKind.Info, "知道了", null);

        public static void Success(Window owner, string title, string message)
            => Show(owner, title, message, AppDialogKind.Success, "好的", null);

        public static void Warn(Window owner, string title, string message)
            => Show(owner, title, message, AppDialogKind.Warning, "知道了", null);

        public static void Error(Window owner, string title, string message)
            => Show(owner, title, message, AppDialogKind.Error, "关闭", null);

        /// <summary>确认框：确定返回 true；取消 / Esc / 右上角关闭返回 false。</summary>
        public static bool Confirm(Window owner, string title, string message,
            string okText = "确定", string cancelText = "取消", AppDialogKind kind = AppDialogKind.Question)
            => Show(owner, title, message, kind, okText, cancelText);

        // ================= 实现 =================

        private static bool Show(Window owner, string title, string message, AppDialogKind kind,
            string okText, string cancelText)
        {
            Color accent = AccentOf(kind);

            var win = new Window
            {
                Title = title ?? "提示",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.Height,
                Width = 470,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };
            if (owner != null && owner.IsLoaded) win.Owner = owner;

            // ---------- 卡片 ----------
            var card = new Border
            {
                Margin = new Thickness(18),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.18,
                    Color = Color.FromRgb(0x2C, 0x3E, 0x50)
                }
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题行
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 正文
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮行

            // ---------- 标题行：图标徽章 + 标题 + 关闭 ----------
            var head = new Grid { Margin = new Thickness(20, 18, 16, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Theme.Icon(IconOf(kind), 19, new SolidColorBrush(accent))
            };
            Grid.SetColumn(badge, 0);
            head.Children.Add(badge);

            var titleBlock = new TextBlock
            {
                Text = title ?? "",
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 1);
            head.Children.Add(titleBlock);

            var closeBtn = new Button
            {
                Content = Theme.Icon("\uf00d", 12, Theme.TextGray),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "关闭 (Esc)"
            };
            Grid.SetColumn(closeBtn, 2);
            head.Children.Add(closeBtn);
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---------- 正文 ----------
            var bodyText = new TextBlock
            {
                Text = message ?? "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x55, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            };
            var bodyScroll = new ScrollViewer
            {
                Content = bodyText,
                // 保底值 200 → 120：175% 的矮工作区（约 420 DIP）上，Math.Max 的保底值会把"正文上限"
                // 抬到超过实际可用高度，窗口底部按钮就会被挤出屏幕（TaskEditorWindow / AboutDialog 都踩过）。
                MaxHeight = Math.Max(120, System.Windows.SystemParameters.WorkArea.Height - 220),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(20, 12, 20, 0)
            };
            Grid.SetRow(bodyScroll, 1);
            grid.Children.Add(bodyScroll);

            // ---------- 按钮行 ----------
            var sep = new Border
            {
                Height = 1,
                Background = Theme.Border,
                Margin = new Thickness(20, 0, 20, 0)
            };
            var footGrid = new Grid { Margin = new Thickness(20, 14, 20, 0) };
            footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(buttons, 1);
            footGrid.Children.Add(buttons);

            if (!string.IsNullOrEmpty(cancelText))
            {
                var cancel = MakeButton(cancelText, Theme.Neutral, Theme.TextDark, false);
                cancel.Margin = new Thickness(0, 0, 10, 0);
                cancel.Click += (s, e) => { win.DialogResult = false; win.Close(); };
                buttons.Children.Add(cancel);
            }
            var ok = MakeButton(string.IsNullOrEmpty(okText) ? "确定" : okText, new SolidColorBrush(accent), Brushes.White, true);
            ok.Click += (s, e) => { win.DialogResult = true; win.Close(); };
            buttons.Children.Add(ok);

            var footerHost = new StackPanel { Margin = new Thickness(0, 16, 0, 18) };
            footerHost.Children.Add(sep);
            footerHost.Children.Add(footGrid);
            Grid.SetRow(footerHost, 2);
            grid.Children.Add(footerHost);

            card.Child = grid;
            win.Content = card;

            // 右上角关闭：确认框按"取消"处理
            closeBtn.Click += (s, e) => { win.DialogResult = false; win.Close(); };
            win.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { win.DialogResult = false; win.Close(); }
                else if (e.Key == Key.Enter) { win.DialogResult = true; win.Close(); }
            };
            // 按住卡片任何空白处都能拖动窗口
            card.MouseLeftButtonDown += (s, e) => { try { win.DragMove(); } catch { } };

            bool result = false;
            try
            {
                var r = win.ShowDialog();
                result = r == true;
            }
            catch
            {
                // 极端情况下（例如非 UI 线程）退回系统对话框，保证提示不丢
                var r = MessageBox.Show(message, title,
                    string.IsNullOrEmpty(cancelText) ? MessageBoxButton.OK : MessageBoxButton.OKCancel,
                    kind == AppDialogKind.Warning || kind == AppDialogKind.Error ? MessageBoxImage.Warning : MessageBoxImage.Information);
                result = r == MessageBoxResult.OK;
            }
            return result;
        }

        private static Button MakeButton(string text, Brush bg, Brush fg, bool bold)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    Foreground = fg,
                    FontSize = 13,
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
                },
                Background = bg,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 86,
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
        }

        private static Color AccentOf(AppDialogKind kind)
        {
            switch (kind)
            {
                case AppDialogKind.Success: return Color.FromRgb(0x2E, 0xCC, 0x71);
                case AppDialogKind.Warning: return Color.FromRgb(0xE6, 0x7E, 0x22);
                case AppDialogKind.Error: return Color.FromRgb(0xE7, 0x4C, 0x3C);
                default: return Color.FromRgb(0x34, 0x98, 0xDB);
            }
        }

        private static string IconOf(AppDialogKind kind)
        {
            switch (kind)
            {
                case AppDialogKind.Success: return "\uf058";   // circle-check
                case AppDialogKind.Warning: return "\uf071";   // triangle-exclamation
                case AppDialogKind.Error: return "\uf057";     // circle-xmark
                case AppDialogKind.Question: return "\uf059";  // circle-question
                default: return "\uf05a";                      // circle-info
            }
        }
    }
}
