#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 双选/多选切换组：只有两三个选项时，按钮组比下拉框更直观（一眼看到全部选项，点一下就切）。
    /// 选中项用主题色底白字，未选中项用浅灰底深色字。
    /// </summary>
    public class SegmentedToggle : Grid
    {
        private readonly Button[] _buttons;
        private readonly string[] _options;
        private int _selected;

        public event Action Changed;

        public SegmentedToggle(string[] options, double width = double.NaN, string tip = null, int initial = 0, double buttonWidth = double.NaN)
        {
            _options = options ?? new string[0];
            _buttons = new Button[_options.Length];
            Height = Theme.ControlHeight;
            VerticalAlignment = VerticalAlignment.Center;
            if (!double.IsNaN(width)) Width = width;
            if (!string.IsNullOrEmpty(tip)) ToolTip = tip;

            for (int i = 0; i < _options.Length; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int idx = i;
                var btn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = _options[i],
                        FontSize = 12.5,
                        Foreground = Theme.TextDark,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE3, 0xEC)),
                    Background = Theme.Neutral,
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Focusable = false
                };
                Theme.ApplyModernButtonStyle(btn);
                btn.Padding = new Thickness(6, 6, 6, 6);
                if (!double.IsNaN(buttonWidth)) btn.Width = buttonWidth;
                btn.Click += (s, e) => { SelectedIndex = idx; };
                SetColumn(btn, i);
                Children.Add(btn);
                _buttons[i] = btn;
            }
            SelectedIndex = initial;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _buttons.Length) value = 0;
                if (_selected == value && _buttons[value] != null) { Paint(); return; }
                _selected = value;
                Paint();
                try { Changed?.Invoke(); } catch { }
            }
        }

        public string SelectedText => (_options != null && _selected >= 0 && _selected < _options.Length) ? _options[_selected] : "";

        void Paint()
        {
            for (int i = 0; i < _buttons.Length; i++)
            {
                bool on = i == _selected;
                _buttons[i].Background = on ? Theme.Primary : Theme.Neutral;
                _buttons[i].BorderBrush = on ? Theme.Primary : new SolidColorBrush(Color.FromRgb(0xD8, 0xE3, 0xEC));
                if (_buttons[i].Content is TextBlock tb) tb.Foreground = on ? Brushes.White : Theme.TextDark;
            }
        }
    }
}
