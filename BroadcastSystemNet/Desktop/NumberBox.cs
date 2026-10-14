#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontWeights = System.Windows.FontWeights;
using Cursors = System.Windows.Input.Cursors;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 数字步进框：左边「−」、中间可直接输入、右边「+」，滚轮同样能调。
    /// 用在时分秒、时长、音量、提歌数这类数值上——它们以前是可输入下拉，
    /// 但"0-599"这种几百项的下拉列表根本没法用（滚半天找不到值）。
    /// </summary>
    public class NumberBox : Grid
    {
        private readonly TextBox _box;
        private readonly Button _minus;
        private readonly Button _plus;
        private bool _syncing;

        public int Min { get; private set; }
        public int Max { get; private set; }
        public int Step { get; private set; }

        /// <summary>值变化（含手打与滚轮）后触发。</summary>
        public event Action Changed;

        public NumberBox(int min, int max, int initial, double width = 120, string tip = null, int step = 1)
        {
            Min = min; Max = max; Step = Math.Max(1, step);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Width = width;
            MinHeight = Theme.ControlHeight;
            VerticalAlignment = VerticalAlignment.Center;

            _minus = MakeStepButton("−");
            _minus.Click += (s, e) => Nudge(-Step);
            SetColumn(_minus, 0);
            Children.Add(_minus);

            _box = Theme.TextBox("");
            _box.TextAlignment = TextAlignment.Center;
            _box.FontSize = 13.5;
            _box.Padding = new Thickness(2, 6, 2, 6);
            _box.MinWidth = 34;
            _box.BorderThickness = new Thickness(0);   // 外框由本控件统一画，内部输入框不再描边
            _box.Background = Brushes.Transparent;
            _box.LostFocus += (s, e) => Commit();
            _box.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            SetColumn(_box, 1);
            Children.Add(_box);

            _plus = MakeStepButton("+");
            _plus.Click += (s, e) => Nudge(Step);
            SetColumn(_plus, 2);
            Children.Add(_plus);

            Background = Brushes.White;
            ToolTip = string.IsNullOrEmpty(tip) ? $"可输入，或点两侧加减键、滚轮调节（{min}-{max}）" : tip;

            // 外圈圆角描边：把「− 值 +」包成一个整体
            var frame = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false
            };
            SetColumnSpan(frame, 3);
            Children.Add(frame);

            PreviewMouseWheel += (s, e) => { Nudge(e.Delta > 0 ? Step : -Step); e.Handled = true; };

            SetValueInternal(initial, false);
        }

        /// <summary>当前值（越界自动夹紧）。</summary>
        public int Value
        {
            get { int v; return int.TryParse(_box.Text, out v) ? Clamp(v) : Min; }
            set { SetValueInternal(value, true); }
        }

        static Button MakeStepButton(string text)
        {
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.TextDark,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 28,
                MinHeight = Theme.ControlHeight - 2,
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = text == "+" ? "加" : "减"
            };
            Theme.ApplyModernButtonStyle(btn);
            btn.Padding = new Thickness(0);
            btn.Margin = new Thickness(0);
            btn.MinHeight = Theme.ControlHeight - 2;
            return btn;
        }

        int Clamp(int v) => v < Min ? Min : (v > Max ? Max : v);

        void Nudge(int delta)
        {
            int v = Value + delta;
            if (v < Min) v = Max;      // 循环
            if (v > Max) v = Min;
            SetValueInternal(v, true);
        }

        void Commit() => SetValueInternal(Value, true);

        void SetValueInternal(int v, bool notify)
        {
            if (_syncing) return;
            _syncing = true;
            _box.Text = Clamp(v).ToString();
            _syncing = false;
            if (notify) { try { Changed?.Invoke(); } catch { } }
        }
    }
}
