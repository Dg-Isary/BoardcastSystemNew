#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontWeights = System.Windows.FontWeights;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 任务排程编辑窗（新增 / 编辑）。
    /// 字段与网页端完全对齐：时间、时长、音量、星期、特殊日期、标记、列队模式、文件夹模式、单次提歌数、音乐序列。
    /// </summary>
    public class TaskEditorWindow : Window
    {
        private readonly JsonObject _task;          // 编辑对象（新建时是空对象）
        private readonly JsonArray _music;          // 音乐库快照
        private readonly List<string> _selectedIds = new List<string>();

        private TextBox _dates, _search;
        private DateField _pickDate;
        private NumberInput _hour, _minute, _second, _durMin, _durSec;
        private NumberBox _folderCount;      // 提歌数保留加减键
        private System.Windows.Controls.Slider _volume;                // 音量用音量条
        private TextBlock _volumeText;
        private System.Windows.Controls.ComboBox _marker;
        private readonly Dictionary<int, CheckBox> _dayBoxes = new Dictionary<int, CheckBox>();
        private SegmentedToggle _seqMode, _folderMode, _stopMode;
        private ListBox _available, _selected;
        private TextBlock _warn;

        public bool Saved { get; private set; }

        private static readonly Brush BgSoft = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xFD));
        private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF3));

        public TaskEditorWindow(JsonObject task, JsonArray music)
        {
            _task = task ?? new JsonObject();
            _music = music ?? new JsonArray();

            Title = IsNew ? "新增任务排程" : "编辑任务排程";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;   // 允许调整大小：高缩放下也能把窗口拉大
            ShowInTaskbar = false;
            // 【高缩放修复 · 175% 下看不到"保存"】
            // 以前是 SizeToContent.Height + MaxHeight(WorkArea-40)，正文用
            // ScrollViewer.MaxHeight = Max(220, WorkArea-260) 限高。
            // 问题：那个 220 的**保底值**在"工作区很矮"时会超过实际可用高度 ——
            // 175% 的笔记本上 WorkArea 只有约 420 DIP，正文被保底撑到 220，
            // 加上页眉与底部按钮约 199，合计 419 > 窗口上限 376 → 底部"保存"被裁掉（用户实测反馈）。
            // 现在改成**不依赖算术**：固定初始高度 + Grid 三行(Auto/*/Auto)，
            // 正文在星号行里滚动，底部按钮永远可见；窗口始终落在工作区内，用户仍可拉伸。
            SizeToContent = SizeToContent.Manual;
            double waW = SystemParameters.WorkArea.Width, waH = SystemParameters.WorkArea.Height;
            Width = Math.Max(560, Math.Min(840, waW - 40));    // 175% 下屏幕 DIP 宽度很小，必须一起夹住
            Height = Math.Max(320, Math.Min(720, waH - 40));
            MinWidth = 520;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            LoadModel();
            BuildUi();
        }

        /// <summary>把保存下来的字符串数值转回 int，失败时用兜底值。</summary>
        private static int ParseIntOr(string s, int fallback) { int v; return int.TryParse(s, out v) ? v : fallback; }

        private bool IsNew => _task["id"] == null;

        // ================= 取值 / 赋值 =================

        private void LoadModel()
        {
            string st = _task["start_time"]?.ToString() ?? "08:00:00";
            var p = st.Split(':');
            Set(p, 0, "08"); Set(p, 1, "00"); Set(p, 2, "00");
            void Set(string[] parts, int i, string fallback)
            {
                string v = (parts.Length > i && int.TryParse(parts[i], out _)) ? parts[i] : fallback;
                _pendingTime[i] = v;
            }

            _pendingDurMin = (_task["duration_m"]?.ToString() ?? "0");
            _pendingDurSec = (_task["duration_s"]?.ToString() ?? "0");
            _pendingVolume = (_task["volume"]?.ToString() ?? "100");
            _pendingMarker = (_task["tiaoxiu_marker"]?.ToString() ?? "1");
            _pendingFolderCount = (_task["folder_play_count"]?.ToString() ?? "1");
            _pendingStopMode = (_task["stop_mode"]?.ToString() == "finish_track") ? "finish_track" : "immediate";
            _pendingDates = string.Join("\n", (_task["specific_dates"] as JsonArray)?.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)) ?? Enumerable.Empty<string>());
            _pendingDays = new HashSet<int>();
            foreach (var d in (_task["days"] as JsonArray) ?? new JsonArray())
                if (int.TryParse(d?.ToString(), out int v)) _pendingDays.Add(v);
            foreach (var m in (_task["music_ids"] as JsonArray) ?? new JsonArray())
            { string s = m?.ToString(); if (!string.IsNullOrEmpty(s)) _selectedIds.Add(s); }
        }

        private readonly Dictionary<int, string> _pendingTime = new Dictionary<int, string>();
        private string _pendingDurMin, _pendingDurSec, _pendingVolume, _pendingMarker, _pendingFolderCount, _pendingDates, _pendingStopMode;
        private HashSet<int> _pendingDays;

        // ================= 界面 =================

        private void BuildUi()
        {
            // 三行布局：标题(Auto) / 正文(星号，内部滚动) / 底部按钮(Auto)。
            // 这样"保存"永远在可视区内 —— 不再靠"正文限高"的算术去猜（见构造函数里的说明）。
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---------- 标题行 ----------
            var head = new Grid { Margin = new Thickness(20, 18, 16, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0x34, 0x98, 0xDB)),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Theme.Icon("\uf017", 19, Theme.Primary)
            };
            Grid.SetColumn(badge, 0); head.Children.Add(badge);

            var titleStack = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = IsNew ? "新增任务排程" : "编辑任务排程 #" + _task["id"],
                FontSize = 15.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "到点自动抢占当前播放并打铃；任务在网页端、手机端、桌面端共用同一份配置",
                FontSize = 11, Foreground = Theme.TextGray, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(titleStack, 1); head.Children.Add(titleStack);

            var close = new Button
            {
                Content = Theme.Icon("\uf00d", 12, Theme.TextGray),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(6), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "关闭 (Esc)"
            };
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 2); head.Children.Add(close);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ---------- 表单 ----------
            var form = new StackPanel { Margin = new Thickness(20, 14, 20, 0) };
            form.Children.Add(Card(BuildTimeRow()));
            form.Children.Add(Card(BuildCycleRow()));
            form.Children.Add(Card(BuildDatesAndModesRow()));
            form.Children.Add(BuildMusicRow());

            var scroll = new ScrollViewer
            {
                Content = form,
                // 不再设 MaxHeight：正文放在星号行里，由 Grid 分配剩余高度后自己滚动。
                // （原来的 MaxHeight 带 220 的保底值，在矮工作区上会把底部"保存"挤出窗口 —— 175% 实测。）
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // ---------- 底部（整组放进 Auto 行，保证"保存"永远可见）----------
            var footer = new StackPanel();
            _warn = new TextBlock { FontSize = 11.5, Foreground = Theme.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 10, 20, 0) };
            footer.Children.Add(_warn);

            var sep = new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(20, 12, 20, 0) };
            footer.Children.Add(sep);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 12, 20, 18) };
            var cancel = Theme.Button("取消", Theme.Neutral, Theme.TextDark, "\uf00d", Close);
            cancel.Padding = new Thickness(18, 8, 18, 8);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            buttons.Children.Add(cancel);
            var save = Theme.Button(IsNew ? "新增任务" : "保存修改", Theme.Primary, Brushes.White, "\uf0c7", Save);
            save.Padding = new Thickness(20, 8, 20, 8);
            save.Margin = new Thickness(0);
            buttons.Children.Add(save);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            var card = new Border
            {
                Margin = new Thickness(18),
                Background = Brushes.White,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = root,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = 0.18, Color = Color.FromRgb(0x2C, 0x3E, 0x50) }
            };
            Content = card;

            card.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            UpdateWarn();
        }

        private Border Card(UIElement child) => new Border
        {
            Background = BgSoft,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = child
        };

        private TextBlock FieldLabel(string text) => new TextBlock
        {
            Text = text, FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(0, 0, 0, 5)
        };

        private TextBox Num(string value, double width, int min, int max, string tip)
        {
            var tb = Theme.TextBox(value, width);
            tb.TextAlignment = TextAlignment.Center;
            tb.ToolTip = tip;
            tb.PreviewMouseWheel += (s, e) =>
            {
                int cur; if (!int.TryParse(tb.Text, out cur)) cur = min;
                cur += e.Delta > 0 ? 1 : -1;
                if (cur < min) cur = max;       // 滚轮循环，和网页端一致
                if (cur > max) cur = min;
                tb.Text = cur.ToString();
                e.Handled = true;
            };
            return tb;
        }

        private UIElement BuildTimeRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _hour = new NumberInput(0, 23, ParseIntOr(_pendingTime[0], 8), 66, "开始时间-时：直接输入，或在框上滚动滚轮调节（0-23）");
            _minute = new NumberInput(0, 59, ParseIntOr(_pendingTime[1], 0), 66, "开始时间-分：直接输入，或在框上滚动滚轮调节（0-59）");
            _second = new NumberInput(0, 59, ParseIntOr(_pendingTime[2], 0), 66, "开始时间-秒：直接输入，或在框上滚动滚轮调节（0-59）");
            var timeStack = new StackPanel();
            var tLabel = FieldLabel("开始时间"); tLabel.ToolTip = "时 : 分 : 秒，可用鼠标滚轮在输入框上直接调"; timeStack.Children.Add(tLabel);
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
            timeRow.Children.Add(_hour);
            timeRow.Children.Add(Colon());
            timeRow.Children.Add(_minute);
            timeRow.Children.Add(Colon());
            timeRow.Children.Add(_second);
            timeStack.Children.Add(timeRow);
            Grid.SetColumn(timeStack, 0); grid.Children.Add(timeStack);
            Grid.SetColumnSpan(timeStack, 4);

            _durMin = new NumberInput(0, 599, ParseIntOr(_pendingDurMin, 0), 66, "播放时长-分：0 分 0 秒表示完整播放；直接输入或滚轮调节（0-599）");
            _durSec = new NumberInput(0, 59, ParseIntOr(_pendingDurSec, 0), 66, "播放时长-秒：0 分 0 秒表示完整播放；直接输入或滚轮调节（0-59）");
            var durStack = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            var dLabel = FieldLabel("播放时长"); dLabel.ToolTip = "分 : 秒；填 0 分 0 秒表示完整播放整首/整个目录"; durStack.Children.Add(dLabel);
            var durRow = new StackPanel { Orientation = Orientation.Horizontal };
            durRow.Children.Add(_durMin);
            durRow.Children.Add(Colon());
            durRow.Children.Add(_durSec);
            durStack.Children.Add(durRow);
            Grid.SetColumn(durStack, 4); grid.Children.Add(durStack);

            _volume = new System.Windows.Controls.Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = ParseIntOr(_pendingVolume, 100),
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 5,
                IsSnapToTickEnabled = false,
                ToolTip = "本条任务的播放音量百分比（0-100），拖动即可"
            };
            _volumeText = new TextBlock
            {
                Text = ((int)_volume.Value) + "%",
                FontSize = 12.5,
                Foreground = Theme.TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 42,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _volume.ValueChanged += (s, e) => { if (_volumeText != null) _volumeText.Text = ((int)_volume.Value) + "%"; };
            var volStack = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            volStack.Children.Add(FieldLabel("音量 %"));
            var volRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            volRow.Children.Add(_volume);
            volRow.Children.Add(_volumeText);
            volStack.Children.Add(volRow);
            Grid.SetColumn(volStack, 5); grid.Children.Add(volStack);

            return grid;
        }

        /// <summary>星期快捷选择按钮：统一高度与圆角，悬停/按下有反馈。</summary>
        private static Button DayQuickButton(string text, string tip, Action onClick)
        {
            var btn = Theme.Button(text, Theme.Neutral, Theme.TextDark, null, onClick);
            btn.FontSize = 12;
            btn.MinHeight = 28;
            btn.Padding = new Thickness(11, 4, 11, 4);
            btn.Margin = new Thickness(4, 0, 6, 0);
            btn.ToolTip = tip;
            return btn;
        }
        private static TextBlock Colon() => new TextBlock
        {
            Text = ":", FontSize = 14, Foreground = Theme.TextGray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
        };

        private UIElement BuildCycleRow()
        {
            var panel = new StackPanel();
            var cLabel = FieldLabel("常规播放周期"); cLabel.ToolTip = "勾选星期几执行；与「特殊覆盖日期」是或的关系，任一命中即执行"; panel.Children.Add(cLabel);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            for (int i = 0; i < 7; i++)
            {
                int day = i + 1;
                var cb = new CheckBox
                {
                    Content = names[i],
                    FontSize = 12.5,
                    Foreground = Theme.TextDark,
                    IsChecked = _pendingDays.Contains(day),
                    Margin = new Thickness(0, 2, 16, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                cb.Checked += (s, e) => UpdateWarn();
                cb.Unchecked += (s, e) => UpdateWarn();
                _dayBoxes[day] = cb;
                row.Children.Add(cb);
            }
            // 快捷选择：做成一组同高、圆角、悬停有反馈的小按钮，和整体风格一致（原来是裸灰底小方块）
            row.Children.Add(DayQuickButton("工作日", "周一至周五打铃", () => { for (int d = 1; d <= 7; d++) _dayBoxes[d].IsChecked = d <= 5; UpdateWarn(); }));
            row.Children.Add(DayQuickButton("每天", "一周七天都打铃", () => { for (int d = 1; d <= 7; d++) _dayBoxes[d].IsChecked = true; UpdateWarn(); }));
            row.Children.Add(DayQuickButton("全不选", "取消所有星期勾选（只靠特定日期或调休规则触发）", () => { for (int d = 1; d <= 7; d++) _dayBoxes[d].IsChecked = false; UpdateWarn(); }));

            panel.Children.Add(row);

            // 调休标记：决定这条任务受哪一组调休规则控制（0 = 跟随全局规则）
            var markRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var markLabel = FieldLabel("调休标记"); markLabel.ToolTip = "决定这条任务受哪一组调休规则控制：只能选 1-10，只受「同标记」的调休规则影响。（0 = 全局规则，只在「调休管理」页里用）";
            markLabel.Margin = new Thickness(0, 0, 8, 4);
            markLabel.VerticalAlignment = VerticalAlignment.Bottom;
            markRow.Children.Add(markLabel);
            _marker = Theme.Combo(76);   // 只显示数字，含义在悬浮提示里
            _marker.ToolTip = "决定这条任务受哪一组调休规则控制：只能选 1-10，只受「同标记」的调休规则影响。（0 = 全局规则，只在「调休管理」页里用）";
            for (int i = 1; i <= 10; i++)          // 只给 1-10：0（全局规则）留给「调休管理」页用
                _marker.Items.Add(i.ToString());
            int preset = 1;
            if (!int.TryParse(_pendingMarker, out preset) || preset < 1 || preset > 10) preset = 1;
            _marker.SelectedIndex = preset - 1;     // 下拉里第 0 项是「1」
            _marker.VerticalAlignment = VerticalAlignment.Center;
            markRow.Children.Add(_marker);
            panel.Children.Add(markRow);
            return panel;
        }

        private UIElement BuildDatesAndModesRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            var sLabel = FieldLabel("特殊覆盖日期"); sLabel.ToolTip = "每行一个日期，格式 2026-05-01；也可以逗号分隔。右侧可点日历挑一个日期再点「加入」"; left.Children.Add(sLabel);
            _dates = Theme.TextBox(_pendingDates);
            _dates.AcceptsReturn = true;
            _dates.TextWrapping = TextWrapping.Wrap;
            _dates.VerticalContentAlignment = VerticalAlignment.Top;
            _dates.Height = 193;
            _dates.FontSize = 12.5;
            left.Children.Add(_dates);
            // 日历点选 + 「加入」：不擅长手打日期的也能用
            var pickRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pickDate = new DateField { Width = 212 };
            Grid.SetColumn(_pickDate, 0);
            pickRow.Children.Add(_pickDate);
            var addDate = Theme.Button("加入", Theme.Success, Brushes.White, "\uf0fe", () =>
            {
                var dt = _pickDate.SelectedDate;
                if (!dt.HasValue) { _warn.Text = "提示：请先在上面的日历里选一个日期，再点「加入」。"; return; }
                string one = dt.Value.ToString("yyyy-MM-dd");
                var existing = new List<string>((_dates.Text ?? "").Split(new[] { '\n', '\r', ',', '，' }, StringSplitOptions.RemoveEmptyEntries));
                for (int i = 0; i < existing.Count; i++) existing[i] = existing[i].Trim();
                if (!existing.Contains(one)) existing.Add(one);
                _dates.Text = string.Join("\r\n", existing);
                UpdateWarn();
            });
            addDate.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(addDate, 1);
            pickRow.Children.Add(addDate);
            left.Children.Add(pickRow);
            // 说明：「到达时间后」原先挂在这一列，和右列的「单次提歌数」样式、宽度都不一样，看起来参差不齐；
            // 现在统一挪到右列，和另外三项排成同样的一列（同标签样式、同控件宽度、同间距）。
            Grid.SetColumn(left, 0); grid.Children.Add(left);

            var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            right.Children.Add(FieldLabel("列队与播放选项"));

            _seqMode = new SegmentedToggle(new[] { "顺序播放", "随机打乱" }, 152, "总列队模式：点一下在「顺序播放」和「随机打乱」之间切换");
            _folderMode = new SegmentedToggle(new[] { "接续上次", "随机播放" }, 152, "文件夹内模式：点一下在「接着上次断点继续」和「每次都随机」之间切换");
            _stopMode = new SegmentedToggle(new[] { "立即终止", "播完当前" }, 152, "本任务到达结束时间时怎么停：立即终止=到点立刻切掉正在播的这一首；播完当前=等这一首自然放完再结束本任务（第 n 首播完即停）。注意：任务与任务之间一律立即抢断，不受本设置影响");
            _folderCount = new NumberBox(1, 99, ParseIntOr(_pendingFolderCount, 1), 152, "单次提歌数：目录型音频一次取几首；可输入、加减键、滚轮（1-99）");

            // 竖排四项：横向空间不够，从上往下放，各自一行，不再互相挤压；宽度统一 152，左右边缘对齐
            // （布局已由使用者肉眼校正，勿再调整宽度与顺序）
            var modes = new StackPanel();
            modes.Children.Add(OptionRow("总列队模式", _seqMode, 8));
            modes.Children.Add(OptionRow("文件夹内模式", _folderMode, 8));
            modes.Children.Add(OptionRow("单次提歌数", _folderCount, 8));
            modes.Children.Add(OptionRow("到达时间后", _stopMode, 0));

            right.Children.Add(modes);
            Grid.SetColumn(right, 1); grid.Children.Add(right);

            _seqMode.SelectedIndex = (_task["sequence_mode"]?.ToString() == "random") ? 1 : 0;
            _folderMode.SelectedIndex = (_task["folder_mode"]?.ToString() == "random") ? 1 : 0;
            _stopMode.SelectedIndex = (_pendingStopMode == "finish_track") ? 1 : 0;
            return grid;
        }

        /// <summary>
        /// 离屏渲染任务编辑窗（不显示窗口、不改任何数据），用于人工核对布局。
        /// 用一条示例任务，并把「到达时间后」设成"播完当前"，便于一眼看到各控件是否对齐。
        /// </summary>
        /// <summary>预览 / 自检共用的示例任务（与原来 RenderPreview 里那份完全一致）。</summary>
        static JsonObject DemoTask() => new JsonObject
        {
            ["id"] = 1,
            ["start_time"] = "08:20:00",
            ["duration_m"] = 1,
            ["duration_s"] = 0,
            ["volume"] = 100,
            ["days"] = new JsonArray { 1, 2, 3, 4, 5 },
            ["specific_dates"] = new JsonArray(),
            ["music_ids"] = new JsonArray { "1" },
            ["sequence_mode"] = "sequential",
            ["folder_mode"] = "sequential",
            ["folder_play_count"] = 3,
            ["tiaoxiu_marker"] = 1,
            ["stop_mode"] = "finish_track"
        };

        public static string RenderPreview(string outPath, int width = 660, int height = 0)
        {
            try
            {
                var win = new TaskEditorWindow(DemoTask(), Program.ReadJsonl("music.jsonl"));
                if (width <= 0) width = 660;
                win.Width = width;
                var root = win.Content as FrameworkElement;
                if (root == null) return "编辑窗没有内容，无法渲染";
                // height > 0 时按**受限高度**布局：用来复现"高缩放 / 矮屏幕下底部按钮被挤出窗口"的场景
                // （175% 缩放的笔记本工作区只有约 420 DIP）。传 0 则按内容自然高度渲染，用于核对控件对齐。
                double measureH = height > 0 ? height : double.PositiveInfinity;
                root.Measure(new System.Windows.Size(width, measureH));
                double arrangeH = height > 0 ? height : root.DesiredSize.Height;
                root.Arrange(new System.Windows.Rect(0, 0, width, arrangeH));
                root.UpdateLayout();
                int h = (int)Math.Ceiling(arrangeH);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                if (string.IsNullOrWhiteSpace(outPath)) outPath = System.IO.Path.Combine(Program.AppDataDir, "task-editor-preview.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath) ?? Program.AppDataDir);
                using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                return outPath + $"（{width}×{h}）";
            }
            catch (Exception ex) { return "渲染失败：" + ex.Message; }
        }

        /// <summary>
        /// 自检用：在给定的**受限尺寸**下离屏布局一次，返回底部按钮行的底边 Y 与可用高度。
        /// 用来断言"保存"不会被挤出窗口 —— 175% 缩放的笔记本工作区只有约 420 DIP，
        /// 旧的 SizeToContent.Height + MaxHeight 组合会在这里把"保存"裁掉（用户实测反馈）。
        /// 纯布局计算：不进屏、不联网、不改数据。
        /// </summary>
        public static string CheckFooterVisibleForTest(double width, double height)
        {
            try
            {
                var win = new TaskEditorWindow(DemoTask(), Program.ReadJsonl("music.jsonl"));
                var root = win.Content as FrameworkElement;
                if (root == null) return "无内容";
                root.Measure(new System.Windows.Size(width, height));
                root.Arrange(new System.Windows.Rect(0, 0, width, height));
                root.UpdateLayout();
                var card = root as Border;
                var grid = card?.Child as System.Windows.Controls.Grid;
                if (grid == null) return "内容根不是 Grid（三行布局未生效）";
                if (grid.Children.Count < 3) return "Grid 子元素少于 3（应为 标题/正文/底部）";
                var footer = grid.Children[2] as FrameworkElement;
                if (footer == null) return "取不到底部行";
                if (footer.ActualHeight <= 0) return "底部行高度为 0（被压扁）";
                var bottom = footer.TransformToAncestor(root).Transform(new System.Windows.Point(0, footer.ActualHeight));
                bool ok = bottom.Y <= height + 0.5;
                return (ok ? "OK" : "FAIL")
                     + $" 底部行底边={bottom.Y:F1} 可用高度={height:F0} 底行高={footer.ActualHeight:F1}";
            }
            catch (Exception ex) { return "异常：" + ex.Message; }
        }

        /// <summary>
        /// 右列"小标题 + 控件"的一行：四项设置统一用同一个标签样式（11px 灰）、同宽控件、
        /// 同样的行间距，避免宽度与样式不一导致整列看起来参差不齐。
        /// </summary>
        static StackPanel OptionRow(string label, UIElement control, double bottomMargin)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, bottomMargin) };
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Theme.TextGray, Margin = new Thickness(0, 0, 0, 4) });
            row.Children.Add(control);
            return row;
        }

        private UIElement BuildMusicRow()
        {
            var panel = new StackPanel();
            var mLabel = FieldLabel("播放内容"); mLabel.ToolTip = "左侧双击或点「加入」加入右侧序列，按从上到下的顺序播放"; panel.Children.Add(mLabel);

            var grid = new Grid { Height = 176 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new DockPanel();
            var leftHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            leftHead.Children.Add(new TextBlock { Text = "音乐库", FontSize = 11.5, Foreground = Theme.TextGray, VerticalAlignment = VerticalAlignment.Center });
            _search = Theme.TextBox("", 120);
            _search.FontSize = 12;
            _search.Margin = new Thickness(10, 0, 0, 0);
            _search.ToolTip = "按名称或编号筛选";
            _search.TextChanged += (s, e) => FillAvailable();
            leftHead.Children.Add(_search);
            DockPanel.SetDock(leftHead, Dock.Top);
            left.Children.Add(leftHead);

            _available = new ListBox { FontSize = 12.5, BorderBrush = Theme.Border, BorderThickness = new Thickness(1) };
            _available.MouseDoubleClick += (s, e) => AddSelected();
            DockPanel.SetDock(_available, Dock.Top);
            left.Children.Add(_available);
           Grid.SetColumn(left, 0); grid.Children.Add(left);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var addBtn = Theme.Button("加入", Theme.Primary, Brushes.White, "\uf062", AddSelected);
            addBtn.Margin = new Thickness(0, 0, 0, 8);
            addBtn.Padding = new Thickness(10, 7, 10, 7);
            mid.Children.Add(addBtn);
            var removeBtn = Theme.Button("移除", Theme.Neutral, Theme.TextDark, "\uf063", RemoveSelected);
            removeBtn.Margin = new Thickness(0);
            removeBtn.Padding = new Thickness(10, 7, 10, 7);
            mid.Children.Add(removeBtn);
            Grid.SetColumn(mid, 1); grid.Children.Add(mid);

            var right = new DockPanel();
            var rightHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            rightHead.Children.Add(new TextBlock { Text = "已选播放序列", FontSize = 11.5, Foreground = Theme.TextGray, VerticalAlignment = VerticalAlignment.Center });
            var up = new Button { Content = new TextBlock { Text = "上移", FontSize = 11.5 }, Background = Theme.Neutral, BorderThickness = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 6, 0) };
            up.Click += (s, e) => MoveSelected(-1);
            rightHead.Children.Add(up);
            var down = new Button { Content = new TextBlock { Text = "下移", FontSize = 11.5 }, Background = Theme.Neutral, BorderThickness = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0) };
            down.Click += (s, e) => MoveSelected(1);
            rightHead.Children.Add(down);
            var clear = new Button { Content = new TextBlock { Text = "清空", FontSize = 11.5 }, Background = Theme.Neutral, BorderThickness = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand };
            clear.Click += (s, e) => { _selectedIds.Clear(); FillSelected(); UpdateWarn(); };
            rightHead.Children.Add(clear);
            DockPanel.SetDock(rightHead, Dock.Top);
            right.Children.Add(rightHead);

            _selected = new ListBox { FontSize = 12.5, BorderBrush = Theme.Border, BorderThickness = new Thickness(1) };
            DockPanel.SetDock(_selected, Dock.Top);
            right.Children.Add(_selected);
            Grid.SetColumn(right, 2); grid.Children.Add(right);

            panel.Children.Add(grid);
            FillAvailable();
            FillSelected();
            return panel;
        }

        // ================= 音乐选择 =================

        private void FillAvailable()
        {
            if (_available == null) return;
            string kw = (_search?.Text ?? "").Trim();
            _available.Items.Clear();
            foreach (var n in _music)
            {
                if (n is not JsonObject o) continue;
                string id = o["id"]?.ToString() ?? "";
                string name = o["name"]?.ToString() ?? "";
                string type = o["type"]?.ToString() ?? "file";
                if (_selectedIds.Contains(id)) continue;
                if (kw.Length > 0 && name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 && id != kw) continue;
                var item = new ListBoxItem
                {
                    Content = $"{name}　（编号 {id} · {MusicTypes.ToDisplay(type)}）",
                    Tag = id,
                    FontSize = 12.5,
                    Padding = new Thickness(6, 4, 6, 4)
                };
                _available.Items.Add(item);
            }
            if (_available.Items.Count == 0)
                _available.Items.Add(new ListBoxItem { Content = "没有可加入的音乐，先去「音乐库」导入", IsEnabled = false, FontSize = 12.5, Padding = new Thickness(6, 4, 6, 4) });
        }

        private void FillSelected()
        {
            if (_selected == null) return;
            _selected.Items.Clear();
            for (int i = 0; i < _selectedIds.Count; i++)
            {
                _selected.Items.Add(new ListBoxItem
                {
                    Content = $"{i + 1}. {NameOf(_selectedIds[i])}　（编号 {_selectedIds[i]}）",
                    Tag = _selectedIds[i],
                    FontSize = 12.5,
                    Padding = new Thickness(6, 4, 6, 4)
                });
            }
            if (_selectedIds.Count == 0)
                _selected.Items.Add(new ListBoxItem { Content = "还没有选择音乐", IsEnabled = false, FontSize = 12.5, Padding = new Thickness(6, 4, 6, 4) });
            UpdateWarn();
        }

        private string NameOf(string id)
        {
            foreach (var n in _music)
                if (n is JsonObject o && o["id"]?.ToString() == id) return o["name"]?.ToString() ?? id;
            return id;
        }

        private void AddSelected()
        {
            var item = _available?.SelectedItem as ListBoxItem;
            string id = item?.Tag?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            if (!_selectedIds.Contains(id)) _selectedIds.Add(id);
            FillAvailable();
            FillSelected();
        }

        private void RemoveSelected()
        {
            var item = _selected?.SelectedItem as ListBoxItem;
            string id = item?.Tag?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            _selectedIds.Remove(id);
            FillAvailable();
            FillSelected();
        }

        private void MoveSelected(int dir)
        {
            var item = _selected?.SelectedItem as ListBoxItem;
            string id = item?.Tag?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            int i = _selectedIds.IndexOf(id), t = i + dir;
            if (i < 0 || t < 0 || t >= _selectedIds.Count) return;
            _selectedIds.RemoveAt(i);
            _selectedIds.Insert(t, id);
            FillSelected();
            _selected.SelectedIndex = t;
        }

        // ================= 校验 / 保存 =================

        private void UpdateWarn()
        {
            if (_warn == null) return;
            var msgs = new List<string>();
            if (_selectedIds.Count == 0) msgs.Add("还没有选择播放内容");
            bool anyDay = _dayBoxes.Values.Any(c => c.IsChecked == true);
            bool anyDate = !string.IsNullOrWhiteSpace(_dates?.Text);
            if (!anyDay && !anyDate) msgs.Add("既没选星期也没有特殊日期 → 这条任务永远不会触发");
            _warn.Text = msgs.Count == 0 ? "" : "提示：" + string.Join("；", msgs) + "。";
        }

        private int IntOf(TextBox tb, int fallback, int min, int max)
        {
            int v; if (tb == null || !int.TryParse(tb.Text.Trim(), out v)) v = fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        private void Save()
        {
            int h = _hour.Value, mi = _minute.Value, s = _second.Value;
            var days = new JsonArray();
            foreach (var kv in _dayBoxes.OrderBy(k => k.Key))
                if (kv.Value.IsChecked == true) days.Add(kv.Key);

            var dates = new JsonArray();
            foreach (var line in (_dates.Text ?? "").Split(new[] { '\n', '\r', ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            { string t = line.Trim(); if (t.Length > 0) dates.Add(t); }

            var ids = new JsonArray();
            foreach (var id in _selectedIds) ids.Add(id);

            var payload = new JsonObject
            {
                ["start_time"] = $"{h:00}:{mi:00}:{s:00}",
                ["duration_m"] = _durMin.Value,
                ["duration_s"] = _durSec.Value,
                ["volume"] = (int)_volume.Value,
                ["days"] = days,
                ["specific_dates"] = dates,
                ["sequence_mode"] = _seqMode.SelectedIndex == 1 ? "random" : "sequential",
                ["folder_mode"] = _folderMode.SelectedIndex == 1 ? "random" : "sequential",
                ["music_ids"] = ids,
                ["tiaoxiu_marker"] = (_marker?.SelectedIndex ?? 0) + 1,   // 下拉第 0 项 = 标记 1
                ["folder_play_count"] = _folderCount.Value,
                ["stop_mode"] = _stopMode.SelectedIndex == 1 ? "finish_track" : "immediate"
            };
            // 标记输入框在下方构建，这里统一读取
            if (_marker != null) payload["tiaoxiu_marker"] = _marker.SelectedIndex + 1;   // 下拉第 0 项 = 标记 1

            Result = payload;
            Saved = true;
            DialogResult = true;
            Close();
        }

        /// <summary>保存后的任务载荷（调用方拿去 POST / PUT）。</summary>
        public JsonObject Result { get; private set; }
    }
}
