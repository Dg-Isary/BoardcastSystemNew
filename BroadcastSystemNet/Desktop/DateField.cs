#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 轻量日期字段：左边是可手打 yyyy-MM-dd 的输入框（圆角浅蓝描边，和下拉框同一套），
    /// 右边是 Font Awesome 日历按钮，点开弹出本站风格的日历。
    /// 不用 WPF 自带 DatePicker：它的模板必须先满足一堆 PART_ 部件要求，
    /// 而且自带的灰色小日历字形在整体风格里显得突兀（看着像 emoji）。
    /// 对外接口与 DatePicker 一致（SelectedDate 可读写，null = 未选）。
    /// </summary>
    public class DateField : Grid
    {
        private readonly TextBox _box = Theme.TextBox("");
        private readonly Calendar _cal = new Calendar();
        private readonly Popup _pop = new Popup { AllowsTransparency = true, Placement = PlacementMode.Bottom, StaysOpen = false };
        private bool _syncing;

        public event Action Changed;

        public DateField()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Background = Brushes.Transparent;
            MinHeight = Theme.ControlHeight;
            Height = Theme.ControlHeight;

            _box.FontSize = 13;
            _box.Padding = new Thickness(7, 4, 4, 4);   // 日期文字不裁
            _box.ToolTip = "可直接输入 yyyy-MM-dd，也可点右侧日历选择";
            _box.LostFocus += (s, e) => CommitText();
            _box.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) CommitText(); };
            SetColumn(_box, 0);
            Children.Add(_box);

            var iconButton = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = "点开日历选择日期",
                Child = Theme.Icon("\uf073", 14, Theme.TextGray)
            };
            iconButton.MouseLeftButtonUp += (s, e) =>
            {
                _syncing = true;
                _cal.SelectedDate = SelectedDate ?? DateTime.Today;
                _syncing = false;
                _pop.IsOpen = !_pop.IsOpen;
            };
            SetColumn(iconButton, 1);
            Children.Add(iconButton);

            _pop.Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 2, 0, 0),
                Child = _cal
            };
            Children.Add(_pop);

            _cal.SelectedDatesChanged += (s, e) =>
            {
                if (_syncing) return;
                SetText(_cal.SelectedDate);
                _pop.IsOpen = false;
                Raise();
            };
        }

        /// <summary>选中的日期；null 表示未选（例如"结束日期留空 = 只影响当天"）。</summary>
        public DateTime? SelectedDate
        {
            get { DateTime d; return DateTime.TryParse(_box.Text, out d) ? d : (DateTime?)null; }
            set { SetText(value); }
        }

        /// <summary>只用于校验手打内容是否合法（不合法时回退到上次的日历选择）。</summary>
        public bool HasValidText
        {
            get { DateTime d; return string.IsNullOrWhiteSpace(_box.Text) || DateTime.TryParse(_box.Text, out d); }
        }

        void SetText(DateTime? value)
        {
            _syncing = true;
            _box.Text = value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "";
            if (value.HasValue) _cal.SelectedDate = value;
            _syncing = false;
        }

        void CommitText()
        {
            if (string.IsNullOrWhiteSpace(_box.Text)) { SetText(null); Raise(); return; }
            DateTime d;
            if (DateTime.TryParse(_box.Text, out d)) SetText(d);
            else SetText(_cal.SelectedDate);
            Raise();
        }

        void Raise() { try { Changed?.Invoke(); } catch { } }
    }
}
