#nullable disable

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using DockPanel = System.Windows.Controls.DockPanel;
using Popup = System.Windows.Controls.Primitives.Popup;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using ControlTemplate = System.Windows.Controls.ControlTemplate;
using FrameworkElementFactory = System.Windows.FrameworkElementFactory;
using TemplateBindingExtension = System.Windows.TemplateBindingExtension;
using Geometry = System.Windows.Media.Geometry;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using System.Windows.Media;
// WinForms 与 WPF 同时启用，公共类型名必须显式消歧
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontStyle = System.Windows.FontStyle;
using FontStyles = System.Windows.FontStyles;
using FontStretch = System.Windows.FontStretch;
using FontStretches = System.Windows.FontStretches;
using FontWeight = System.Windows.FontWeight;
using FontWeights = System.Windows.FontWeights;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 桌面端统一样式与控件工厂。
    /// 图标：优先使用嵌入的 Font Awesome Solid（fa-solid-900.ttf，由 res\lib 提供），
    /// 缺字体时自动回退为文字标签，保证界面永远可用（不使用任何 emoji 字符）。
    /// </summary>
    public static class Theme
    {
        // 配色与网页看板保持一致
        public static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
        public static readonly Brush Card = Brushes.White;
        public static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB));
        public static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        public static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
        public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        public static readonly Brush TextDark = new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x50));
        public static readonly Brush TextGray = new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D));
        public static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0xED, 0xF2, 0xF7));
        public static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5));

        /// <summary>嵌入的 Font Awesome Solid 字体（缺失或不支持时为 null）。</summary>
        public static FontFamily IconFont { get; private set; }

        public static bool HasIconFont => IconFont != null;

        /// <summary>图标字体的可选字体族名（不同来源的 FA 打包，内部家族名略有差异）。</summary>
        private static readonly string[] IconFamilyNames =
        {
            "Font Awesome 7 Free", "Font Awesome 7 Free Solid", "Font Awesome 6 Free", "Font Awesome 6 Free Solid", "FontAwesome", "Font Awesome 5 Free"
        };

        static Theme()
        {
            IconFont = ResolveIconFont();
            // 记一条日志：部署到别的机器后，直接看日志就知道图标字体是从哪加载的
            try { BroadcastSystemNet.PlayLog.Write("系统", "图标字体来源", "info", detail: IconFontStatus); } catch { }
        }

        /// <summary>
        /// 解析可用的图标字体：
        ///   1) 嵌入的 res/lib/webfonts/fa-solid-900.ttf（package URI）；
        ///   2) 系统若已安装 Font Awesome；
        ///   3) 都没有则返回 null，界面自动回退。
        /// 用 TryGetGlyphTypeface 做真实校验——只 new FontFamily 而不校验的话，
        /// 资源名不对时会静默回退到默认字体，表现为"图标全变方块/黑点"。
        /// </summary>
        private static FontFamily ResolveIconFont()
        {
            // 顺序很重要：先用「程序自带的字体」，最后才用「系统已安装的」。
            // 早先的实现只按名字猜 pack URI，猜不中时静默退回系统字体——
            // 于是在装过 FA 的机器上看着正常，换台没装的机器图标就全没了。
            // 现在改成：真正枚举字体族，并用「某个已知图标码点是否存在」来校验。

            // 1) 程序目录旁的字体文件（开发版 = bin 下 res；发布版 = exe 旁的 res）
            string dir = System.IO.Path.Combine(BroadcastSystemNet.Program.ResDir, "lib", "webfonts");
            foreach (var file in new[] { "fa-solid-900.otf", "fa-solid-900.ttf" })
            {
                string path = System.IO.Path.Combine(dir, file);
                if (!System.IO.File.Exists(path)) continue;
                var picked = PickFrom(() => System.Windows.Media.Fonts.GetFontFamilies(path));
                if (picked != null) return picked;
            }

            // 2) 程序集里的嵌入资源（不依赖任何外部文件）
            foreach (var file in new[] { "fa-solid-900.otf", "fa-solid-900.ttf" })
            {
                var picked = PickFrom(() => System.Windows.Media.Fonts.GetFontFamilies(new Uri("pack://application:,,,/res/lib/webfonts/" + file)));
                if (picked != null) return picked;
            }

            // 3) 名字猜一遍（兼容历史资源命名）
            foreach (var file in new[] { "fa-solid-900.otf", "fa-solid-900.ttf" })
                foreach (var name in IconFamilyNames)
                {
                    var ff = TryFont("pack://application:,,,/res/lib/webfonts/" + file + "#" + name);
                    if (ff != null && HasGlyphs(ff)) return ff;
                }

            // 4) 系统已安装 Font Awesome（别人的机器上可能有，仅作最后兜底）
            foreach (var name in IconFamilyNames)
            {
                var ff = TryFont(name);
                if (ff != null && HasGlyphs(ff)) return ff;
            }
            return null;
        }

        /// <summary>从一个字体族集合里挑出真正含图标的那个（按已知码点校验，避免"加载成功但没图标"）。</summary>
        private static FontFamily PickFrom(Func<System.Collections.Generic.ICollection<FontFamily>> get)
        {
            try
            {
                foreach (var fam in get())
                {
                    if (fam == null) continue;
                    if (HasGlyphs(fam)) return fam;
                }
            }
            catch { }
            return null;
        }

        /// <summary>用真实码点校验：字体能加载 + 至少有一个已知 Font Awesome 图标字形。</summary>
        private static bool HasGlyphs(FontFamily ff)
        {
            try
            {
                var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
                if (!tf.TryGetGlyphTypeface(out var glyph) || glyph == null) return false;
                ushort idx;
                // 抽查几个必定存在的图标：square-plus / play / bell
                return glyph.CharacterToGlyphMap.TryGetValue(0xF0FE, out idx)
                    || glyph.CharacterToGlyphMap.TryGetValue(0xF04B, out idx)
                    || glyph.CharacterToGlyphMap.TryGetValue(0xF0F3, out idx);
            }
            catch { return false; }
        }

        private static FontFamily TryFont(string source)
        {
            try
            {
                var ff = new FontFamily(source);
                var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
                if (tf.TryGetGlyphTypeface(out var glyph) && glyph != null) return ff;
            }
            catch { }
            return null;
        }

        /// <summary>Font Awesome 图标（实心），codepoint 形如 "\uf028"。</summary>
        public static TextBlock Icon(string faCodepoint, double size = 14, Brush color = null)
        {
            if (HasIconFont)
            {
                return new TextBlock
                {
                    Text = faCodepoint,
                    FontFamily = IconFont,
                    FontWeight = FontWeights.Black,
                    FontSize = size,
                    Foreground = color ?? TextDark,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
            }
            // 回退：无图标字体时显示一个中性圆点，避免出现"豆腐块"
            return new TextBlock
            {
                Text = "•",
                FontSize = Math.Max(10, size * 0.8),
                Foreground = color ?? TextGray,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        /// <summary>图标是否可用的提示文案（用于设置页/关于框如实告知）。</summary>
        public static string IconFontStatus => HasIconFont
            ? "已加载 Font Awesome 图标字体（" + IconFont.Source + "）"
            : "未找到图标字体 res/lib/webfonts/fa-solid-900.ttf，界面图标暂用圆点替代（功能不受影响）";

        public static Border Card2(UIElement child, double marginBottom = 0)
        {
            return new Border
            {
                Background = Card,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, marginBottom),
                Child = child
            };
        }

        public static TextBlock Title(string text, double size = 15)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = TextDark,
                Margin = new Thickness(0, 0, 0, 10)
            };
        }

        public static TextBlock Label(string text, double size = 12.5, Brush color = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = color ?? TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }

        /// <summary>统一控件高度：文本框 / 下拉框 / 日期选择器都按这个高度对齐。</summary>
        public const double ControlHeight = 32;

        /// <summary>
        /// 现代按钮外观：圆角、悬停/按下有一层半透明遮罩（对任何底色都有效）、禁用变浅。
        /// 用遮罩而不是改颜色，是为了让"主题色 / 危险色 / 中性色"三种按钮共用同一套模板。
        /// </summary>
        public static void ApplyModernButtonStyle(Button btn)
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var bg = new FrameworkElementFactory(typeof(Border));
            bg.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            bg.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bg.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bg.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            root.AppendChild(bg);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            root.AppendChild(content);

            var overlay = new FrameworkElementFactory(typeof(Border));
            overlay.Name = "HoverOverlay";
            overlay.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            overlay.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brushes.Black);
            overlay.SetValue(UIElement.OpacityProperty, 0.0);
            overlay.SetValue(UIElement.IsHitTestVisibleProperty, false);
            root.AppendChild(overlay);

            var template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = root };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.08, "HoverOverlay"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.16, "HoverOverlay"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            btn.Template = template;
            btn.MinHeight = ControlHeight;
            btn.VerticalAlignment = VerticalAlignment.Center;  // 与同行输入框居中对齐
            btn.Padding = new Thickness(12, 6, 12, 6);
            btn.BorderThickness = new Thickness(0);
            btn.SnapsToDevicePixels = true;
            btn.Cursor = System.Windows.Input.Cursors.Hand;
        }

        /// <summary>文本框现代外观：圆角、白底、浅蓝描边，聚焦时描边变主题色。</summary>
        public static void ApplyModernTextBoxStyle(TextBox tb)
        {
            var root = new FrameworkElementFactory(typeof(Border));
            root.Name = "Root";
            root.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            root.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            root.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            root.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            root.AppendChild(host);

            var template = new ControlTemplate(typeof(System.Windows.Controls.TextBox)) { VisualTree = root };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Primary, "Root"));
            template.Triggers.Add(focus);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6));
            template.Triggers.Add(disabled);

            tb.Template = template;
            tb.MinHeight = ControlHeight;
            tb.VerticalAlignment = VerticalAlignment.Center;   // 和同行控件（按钮/下拉）居中对齐
            tb.Padding = new Thickness(9, 4, 9, 4);   // 32 高的框里留出足够行高，避免文字下半截被裁
            tb.BorderThickness = new Thickness(1);
            tb.SnapsToDevicePixels = true;
        }

        public static Button Button(string text, Brush bg = null, Brush fg = null, string faCodepoint = null, Action onClick = null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(faCodepoint)) panel.Children.Add(Icon(faCodepoint, 13, fg ?? TextDark));
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Margin = new Thickness(string.IsNullOrEmpty(faCodepoint) ? 0 : 6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = fg ?? TextDark
            });

            var btn = new Button
            {
                Content = panel,
                Background = bg ?? Neutral,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12.5
            };
            ApplyModernButtonStyle(btn);
            if (onClick != null) btn.Click += (s, e) => onClick();
            return btn;
        }

        public static TextBox TextBox(string text = "", double width = double.NaN)
        {
            var tb = new TextBox
            {
                Text = text ?? "",
                FontSize = 13.5,
                VerticalContentAlignment = VerticalAlignment.Center,   // 文字垂直居中
                HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)),
                Background = Brushes.White,
                Foreground = TextDark
            };
            ApplyModernTextBoxStyle(tb);
            if (!double.IsNaN(width)) tb.Width = width;
            return tb;
        }

        /// <summary>
        /// 数值型"点选 + 可输入"下拉框：既能点开列表选，也能直接键盘输入，滚轮同样可用。
        /// 针对"鼠标滚轮才能改"的字段 —— 不是每个人都会去滚那个框。
        /// </summary>
        public static ComboBox NumberCombo(int min, int max, double width = 78, string tip = null, int? initial = null, int? step = null)
        {
            var cb = Combo(width);
            cb.IsEditable = true;
            cb.IsTextSearchEnabled = false;
            for (int i = min; i <= max; i++) cb.Items.Add(i.ToString());
            int start = initial.HasValue && initial.Value >= min && initial.Value <= max ? initial.Value : min;
            cb.Text = start.ToString();
            cb.SelectedItem = start.ToString();
            cb.ToolTip = string.IsNullOrEmpty(tip) ? $"可点开下拉选择，也可以直接输入 {min}-{max}" : tip;
            int stepBy = step ?? 1;

            // 滚轮仍然可用（多数人习惯了），但不再是唯一方式
            cb.PreviewMouseWheel += (s, e) =>
            {
                int cur = ParseNumber(cb.Text, min);
                cur += e.Delta > 0 ? stepBy : -stepBy;
                if (cur < min) cur = max;      // 循环
                if (cur > max) cur = min;
                cb.Text = cur.ToString();
                e.Handled = true;
            };
            // 输入完/失去焦点时把值夹到范围内，避免手打越界
            cb.LostFocus += (s, e) => cb.Text = ParseNumber(cb.Text, min).ToString();
            cb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) cb.Text = ParseNumber(cb.Text, min).ToString();
            };
            return cb;
        }

        /// <summary>读数值下拉框的值（越界自动夹紧）。</summary>
        public static int NumberComboValue(ComboBox cb, int fallback, int min, int max)
        {
            if (cb == null) return fallback;
            int v = ParseNumber(cb.Text ?? cb.SelectedItem?.ToString(), fallback);
            return v < min ? min : (v > max ? max : v);
        }

        static int ParseNumber(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) ? v : fallback;
        }

        public static ComboBox Combo(double width = double.NaN)
        {
            var cb = new ComboBox { FontSize = 13.5, Padding = new Thickness(8, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(width)) cb.Width = width;
            ApplyLightComboStyle(cb);
            return cb;
        }

        /// <summary>
        /// 亮色下拉框样式：白底、浅蓝边框、内容垂直/水平居中、悬停与展开有清晰反馈。
        /// 默认模板在部分主题下会呈现偏灰的立体感，与整体明亮风格不协调，故统一替换。
        /// </summary>
        public static void ApplyLightComboStyle(ComboBox cb)
        {
            cb.Background = Brushes.White;
            cb.Foreground = TextDark;
            cb.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2));
            cb.BorderThickness = new Thickness(1);
            cb.HorizontalContentAlignment = HorizontalAlignment.Center;
            cb.VerticalContentAlignment = VerticalAlignment.Center;
            cb.SnapsToDevicePixels = true;

            var template = new ControlTemplate(typeof(ComboBox));
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Grid.BackgroundProperty, Brushes.White);

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(ComboBox.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(ComboBox.BorderBrushProperty));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(ComboBox.BorderThicknessProperty));
            root.AppendChild(border);

            // 关键：下拉框的展开必须靠一个 IsChecked 双向绑定到 IsDropDownOpen 的 ToggleButton
            // （WPF 默认模板就是这么做的；少了它点上去毫无反应，而且不会报错）。
            var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
            toggle.Name = "toggleButton";
            toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("IsDropDownOpen")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                    Mode = BindingMode.TwoWay
                });
            toggle.SetValue(Control.FocusableProperty, false);
            toggle.SetValue(System.Windows.Controls.Primitives.ButtonBase.ClickModeProperty, System.Windows.Controls.ClickMode.Press);
            toggle.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton))
            {
                VisualTree = MakeTransparentFill()
            });
            root.AppendChild(toggle);

            // 选中项文字：只负责显示，不参与命中测试（点击要落到上面的 ToggleButton 上）
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "ContentPresenter";
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(UIElement.IsHitTestVisibleProperty, false);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(content);

            // 右侧箭头：同样不参与命中测试
            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0 0 L 5 5 L 10 0"));
            arrow.SetValue(System.Windows.Shapes.Path.StrokeProperty, TextGray);
            arrow.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.6);
            arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
            arrow.SetValue(FrameworkElement.WidthProperty, 10.0);
            arrow.SetValue(FrameworkElement.HeightProperty, 6.0);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(arrow);

            // 可输入模式（IsEditable=true）用的编辑框：让"点选下拉 + 直接输入"两种方式都能用
            var editBox = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBox));
            editBox.Name = "PART_EditableTextBox";
            editBox.SetValue(System.Windows.Controls.TextBox.BackgroundProperty, Brushes.Transparent);
            editBox.SetValue(System.Windows.Controls.TextBox.BorderThicknessProperty, new Thickness(0));
            editBox.SetValue(System.Windows.Controls.TextBox.ForegroundProperty, TextDark);
            editBox.SetValue(System.Windows.Controls.TextBox.FontSizeProperty, 13.5);
            editBox.SetValue(System.Windows.Controls.TextBox.CaretBrushProperty, TextDark);
            editBox.SetValue(System.Windows.Controls.TextBox.TextAlignmentProperty, TextAlignment.Center);
            editBox.SetValue(System.Windows.Controls.TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            editBox.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 22, 0));
            editBox.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            root.AppendChild(editBox);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));
            popup.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brushes.White);
            popupBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)));
            popupBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(7));
            popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));

            var items = new FrameworkElementFactory(typeof(StackPanel));
            items.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            items.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
            items.SetValue(StackPanel.IsItemsHostProperty, true);
            popupBorder.AppendChild(items);
            popup.AppendChild(popupBorder);
            root.AppendChild(popup);

            template.VisualTree = root;

            // 可编辑 / 不可编辑两种模式切换显示哪个部件
            var editable = new Trigger { Property = ComboBox.IsEditableProperty, Value = true };
            editable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "ContentPresenter"));
            editable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "PART_EditableTextBox"));
            template.Triggers.Add(editable);

            cb.Template = template;
            cb.MinHeight = ControlHeight;
            cb.VerticalAlignment = VerticalAlignment.Center;   // 和同行控件（输入框/按钮）居中对齐
            cb.SnapsToDevicePixels = true;
        }

        /// <summary>指定可选值列表的数值下拉（例如轮询周期 1/2/4/6/12/24/48），同样可点选 + 可输入。</summary>
        public static ComboBox NumberCombo(int[] presets, double width = 110, string tip = null, int? initial = null)
        {
            var cb = Combo(width);
            cb.IsEditable = true;
            cb.IsTextSearchEnabled = false;
            if (presets != null) foreach (var v in presets) cb.Items.Add(v.ToString());
            int start = initial ?? (presets != null && presets.Length > 0 ? presets[0] : 0);
            cb.Text = start.ToString();
            cb.ToolTip = string.IsNullOrEmpty(tip) ? "可点开下拉选择，也可以直接输入" : tip;
            int lo = presets != null && presets.Length > 0 ? presets[0] : 0;
            int hi = presets != null && presets.Length > 0 ? presets[presets.Length - 1] : 9999;
            cb.PreviewMouseWheel += (s, e) =>
            {
                int cur = ParseNumber(cb.Text, lo);
                int idx = presets == null ? -1 : Array.IndexOf(presets, cur);
                if (idx < 0)
                {
                    cur += e.Delta > 0 ? 1 : -1;
                }
                else
                {
                    idx = ((idx + (e.Delta > 0 ? 1 : -1)) % presets.Length + presets.Length) % presets.Length;
                    cur = presets[idx];
                }
                if (cur < lo) cur = hi;
                if (cur > hi) cur = lo;
                cb.Text = cur.ToString();
                e.Handled = true;
            };
            cb.LostFocus += (s, e) => { int v = ParseNumber(cb.Text, start); if (v < 1) v = start; cb.Text = v.ToString(); };
            return cb;
        }

        /// <summary>从（可输入的）下拉框里读一个整数。</summary>
        public static int ComboIntValue(ComboBox cb, int fallback, int min = 1, int max = 100000)
        {
            if (cb == null) return fallback;
            int v = ParseNumber(cb.Text ?? cb.SelectedItem?.ToString(), fallback);
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>
        /// 日期选择器现代外观：圆角 + 浅蓝描边（聚焦变主题色），右侧按钮换成本程序在用的 Font Awesome 日历图标，
        /// 不再用 WPF 自带那个灰色小日历字形。
        /// </summary>
        public static void ApplyModernDatePickerStyle(System.Windows.Controls.DatePicker dp)
        {
            var root = new FrameworkElementFactory(typeof(Grid));

            var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.Name = "Bd";
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brushes.White);
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));

            var inner = new FrameworkElementFactory(typeof(Grid));
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            inner.AppendChild(c0); inner.AppendChild(c1);

            var box = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.DatePickerTextBox));
            box.Name = "PART_TextBox";
            box.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            box.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            box.SetValue(Control.ForegroundProperty, TextDark);
            box.SetValue(Control.FontSizeProperty, 13.0);
            box.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(Control.PaddingProperty, new Thickness(6, 0, 2, 0));
            box.SetValue(Grid.ColumnProperty, 0);
            inner.AppendChild(box);

            var btn = new FrameworkElementFactory(typeof(Button));
            btn.Name = "PART_Button";
            btn.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            btn.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            btn.SetValue(Control.ForegroundProperty, TextGray);
            btn.SetValue(Control.FontFamilyProperty, IconFont ?? new FontFamily("Segoe UI Symbol"));
            btn.SetValue(Control.FontSizeProperty, 14.0);
            btn.SetValue(Control.PaddingProperty, new Thickness(8, 0, 10, 0));
            btn.SetValue(ContentControl.ContentProperty, "\uf073");
            btn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            btn.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);
            btn.SetValue(Grid.ColumnProperty, 1);
            inner.AppendChild(btn);

            border.AppendChild(inner);
            root.AppendChild(border);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            var popupBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            popupBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brushes.White);
            popupBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)));
            popupBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
            var cal = new FrameworkElementFactory(typeof(System.Windows.Controls.Calendar));
            cal.Name = "PART_Calendar";
            popupBorder.AppendChild(cal);
            popup.AppendChild(popupBorder);
            root.AppendChild(popup);

            var template = new ControlTemplate(typeof(System.Windows.Controls.DatePicker)) { VisualTree = root };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Primary, "Bd"));
            template.Triggers.Add(focus);

            dp.Template = template;
            dp.MinHeight = ControlHeight;
            dp.FontSize = 13;
            dp.SnapsToDevicePixels = true;
        }
        /// <summary>透明铺满的一块区域（给 ToggleButton 用，只为接住点击）。</summary>
        static FrameworkElementFactory MakeTransparentFill()
        {
            var b = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            b.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brushes.Transparent);
            b.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            return b;
        }

        /// <summary>一行水平控件，自动换行。</summary>
        public static WrapPanel Row(params UIElement[] children)
        {
            var wp = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var c in children) wp.Children.Add(c);
            return wp;
        }
    }
}
