#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 纯数字输入框：只保留「直接输入」和「滚轮调节」，两侧不加加减键 —— 时分秒/时长这类字段
    /// 本来就靠输入和滚轮就够用，加步进键反而把那一行挤爆。
    /// 值变化（含手打与滚轮）后触发 Changed。
    /// </summary>
    public class NumberInput : TextBox
    {
        public int Min { get; private set; }
        public int Max { get; private set; }
        public int Step { get; private set; }

        public event Action Changed;

        public NumberInput(int min, int max, int initial, double width = 68, string tip = null, int step = 1)
        {
            Min = min; Max = max; Step = Math.Max(1, step);
            TextAlignment = TextAlignment.Center;
            FontSize = 13.5;
            Width = width;
            MinHeight = Theme.ControlHeight;
            VerticalAlignment = VerticalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;
            ToolTip = string.IsNullOrEmpty(tip) ? $"直接输入，或在框上滚动滚轮调节（{min}-{max}）" : tip;

            PreviewMouseWheel += (s, e) =>
            {
                int v = Value + (e.Delta > 0 ? Step : -Step);
                if (v < Min) v = Max;      // 循环
                if (v > Max) v = Min;
                SetValueInternal(v);
                e.Handled = true;
            };
            LostFocus += (s, e) => SetValueInternal(Value);
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) SetValueInternal(Value); };

            SetValueInternal(initial < min ? min : (initial > max ? max : initial));
        }

        public int Value
        {
            get { int v; return int.TryParse((Text ?? "").Trim(), out v) ? Clamp(v) : Min; }
            set { SetValueInternal(value); }
        }

        int Clamp(int v) => v < Min ? Min : (v > Max ? Max : v);

        void SetValueInternal(int v)
        {
            Text = Clamp(v).ToString();
            try { Changed?.Invoke(); } catch { }
        }
    }
}
