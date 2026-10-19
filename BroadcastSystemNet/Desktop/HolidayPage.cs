#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 调休管理：标记用下拉框选择（0 = 全局；1-10 = 只影响绑定该标记的任务），
    /// 语义与后端/网页端完全一致：分标记优先于全局，同优先级内后写入覆盖先写入。
    /// 默认标记不是 0：非周一的调休通常只需要影响"升旗"这类带标记的任务，全默认全局会把当天所有铃声都关掉。
    /// </summary>
    public class HolidayPage : PageBase
    {
        private readonly DateField _start = new DateField { Width = 128 };
        private readonly DateField _end = new DateField { Width = 128 };
        private readonly ComboBox _marker = Theme.Combo(62);
        private Button _actionToggle;
        private TextBlock _actionText;
        private bool _actionEnable;   // false = 禁用（不播音），true = 启用（照常打铃）
        private readonly TextBox _note = Theme.TextBox("", 150);
        private readonly StackPanel _list = new StackPanel();
        private readonly StackPanel _invalid = new StackPanel();
        private JsonArray _records = new JsonArray();
        private bool _markerInitialized;

        /// <summary>记住上一次用过的标记，下次打开就沿用，不必每次手选。</summary>
        private static int _lastUsedMarker = -1;

        public HolidayPage(MainWindow host) : base(host, "调休管理",
            "调休与特殊日期规则") { }

        protected override void Build()
        {
            // 标记下拉：只显示数字，含义在悬浮提示里
            _marker.ToolTip = "调休标记：0 = 全局（当天所有任务都受影响）；1-10 = 只影响绑定了同标记的任务。点开下拉选择";
            for (int i = 0; i <= 10; i++) _marker.Items.Add(MarkerLabel(i));
            _marker.SelectedIndex = 0;
            // 动作：点击切换按钮（禁用 / 启用）
            BuildActionToggle();

            // 新增表单
            // 一行放完的新增表单：字段自带提示（悬浮可见），不再用文字标签占宽度，
            // 备注列用星号自适应收缩，窗口再窄也不会折行、不会被裁掉。
            var formRow = new Grid();
            for (int ci = 0; ci < 8; ci++) formRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            formRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 备注
            formRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // 按钮

            _start.Width = 140; _start.Margin = new Thickness(0, 0, 10, 0);
            _end.Width = 140; _end.Margin = new Thickness(0, 0, 14, 0);
            _marker.Width = 62; _marker.Margin = new Thickness(0, 0, 8, 0);

            _note.MinWidth = 70; _note.Margin = new Thickness(10, 0, 10, 0);
            _note.ToolTip = "备注（可留空），例如“国庆放假”";

            Grid.SetColumn(_start, 0); formRow.Children.Add(_start);
            Grid.SetColumn(_end, 1); formRow.Children.Add(_end);
            Grid.SetColumn(_marker, 2); formRow.Children.Add(_marker);
            Grid.SetColumn(_actionToggle, 3); formRow.Children.Add(_actionToggle);
            Grid.SetColumn(_note, 4); formRow.Children.Add(_note);

            var formButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            formButtons.Children.Add(Theme.Button("添加/覆盖", Theme.Success, Brushes.White, "\uf0fe", AddRecord));
            var todayBtn = Theme.Button("", null, null, "\uf073", () => { _start.SelectedDate = DateTime.Today; _end.SelectedDate = DateTime.Today; });
            todayBtn.ToolTip = "把开始/结束日期都设为今天";
            todayBtn.Padding = new Thickness(10, 6, 10, 6);
            formButtons.Children.Add(todayBtn);
            var clearBtn = Theme.Button("", null, null, "\uf12d", () => { _start.SelectedDate = null; _end.SelectedDate = null; });
            clearBtn.ToolTip = "清空两个日期";
            clearBtn.Padding = new Thickness(10, 6, 10, 6);
            clearBtn.Margin = new Thickness(0);
            formButtons.Children.Add(clearBtn);
            Grid.SetColumn(formButtons, 5); formRow.Children.Add(formButtons);

            var formCard = Card(formRow);
            Grid.SetRow(formCard, 0);
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 新增表单（固定）
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 规则列表（滚动）
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 底部按钮（固定）
            root.Children.Add(formCard);

            // 规则列表：滚动条只属于这里；无效数据接在列表末尾，一起滚动
            var listGrid = new Grid();
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var rulesTitle = Theme.Title("调休规则");
            rulesTitle.ToolTip = "优先级：分标记规则(1-10) 高于全局规则(0)；同优先级内后写入的覆盖先写入";
            Grid.SetRow(rulesTitle, 0);
            listGrid.Children.Add(rulesTitle);

            var scrollBody = new StackPanel();
            scrollBody.Children.Add(_list);
            var invalidTitle = Theme.Title("云端同步的无效数据");
            invalidTitle.ToolTip = "云端订阅里无法解析的记录，已忽略、不参与判定，确认无用后可点底部「清除无效项」";
            invalidTitle.Margin = new Thickness(0, 14, 0, 6);
            scrollBody.Children.Add(invalidTitle);
            scrollBody.Children.Add(_invalid);

            var scroller = new ScrollViewer
            {
                Content = scrollBody,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(scroller, 1);
            listGrid.Children.Add(scroller);

            var listCard = Card(listGrid);
            Grid.SetRow(listCard, 1);
            root.Children.Add(listCard);

            // 底部固定操作栏
            var bottomBar = new WrapPanel { Orientation = Orientation.Horizontal };
            bottomBar.Children.Add(Theme.Button("保存全部规则", Theme.Primary, Brushes.White, "\uf0c7", SaveAll));
            bottomBar.Children.Add(Theme.Button("立即同步云端订阅", null, null, "\uf0ed", () => _ = SyncAsync()));
            bottomBar.Children.Add(Theme.Button("刷新", null, null, "\uf021", () => _ = ReloadAsync()));
            bottomBar.Children.Add(Theme.Button("清除无效项", Theme.Danger, Brushes.White, "\uf00d", () => _ = ClearInvalidAsync()));
            var bottomCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 6),
                Margin = new Thickness(0, 10, 0, 0),
                Child = bottomBar
            };
            Grid.SetRow(bottomCard, 2);
            root.Children.Add(bottomCard);

            FillHost.Children.Add(root);

            _ = ReloadAsync();
        }

        // ================= 启用/禁用点击切换 =================

        /// <summary>
        /// 动作只有两种状态，用点击切换按钮比下拉框更直接：文字始终可见，点一下换一次。
        /// （下拉框这里曾经出现"选中项文字不显示"的问题，切换按钮没有这个毛病。）
        /// </summary>
        private void BuildActionToggle()
        {
            _actionText = new TextBlock { FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            _actionToggle = new Button
            {
                Content = _actionText,
                Width = 132,
                Background = Theme.Danger,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "点一下在「禁用（不播音）」和「启用（照常打铃）」之间切换"
            };
            Theme.ApplyModernButtonStyle(_actionToggle);
            _actionToggle.Click += (s, e) => { _actionEnable = !_actionEnable; UpdateActionVisual(); };
            UpdateActionVisual();
        }

        private void UpdateActionVisual()
        {
            if (_actionToggle == null) return;
            _actionToggle.Background = _actionEnable ? Theme.Success : Theme.Danger;
            if (_actionText != null) _actionText.Text = _actionEnable ? "启用 · 照常打铃" : "禁用 · 不播音";
        }
        // ================= 标记下拉框 =================

        private static TextBlock EndDateLabel() { var l = Theme.Label("结束日期：", 12.5, Theme.TextGray); l.ToolTip = "留空表示只影响开始日期当天；填了就按日期区间生效"; return l; }

        // 标记只显示数字：0 = 全局、1-10 = 分标记（完整含义在悬浮提示里）
        private static string MarkerLabel(int m) => m.ToString();

        private static string MarkerLabelWithCount(int m, int count) => m.ToString();

        private int MarkerFromCombo()
        {
            string t = _marker.SelectedItem?.ToString() ?? "0";
            int m;
            return int.TryParse(t.Split(' ')[0], out m) ? m : 0;
        }

        /// <summary>按已有记录刷新下拉项（显示每个标记已用了几条），并把上次用过的标记选回来。</summary>
        private void RefreshMarkerItems()
        {
            int cur = _markerInitialized ? MarkerFromCombo() : _lastUsedMarker;
            var counts = new Dictionary<int, int>();
            foreach (var n in _records)
                if (n is JsonObject o && int.TryParse(o["marker"]?.ToString(), out int m))
                    counts[m] = counts.TryGetValue(m, out int c) ? c + 1 : 1;

            _marker.Items.Clear();
            for (int i = 0; i <= 10; i++) _marker.Items.Add(MarkerLabelWithCount(i, counts.TryGetValue(i, out int c2) ? c2 : 0));

            if (cur < 0)
            {
                // 首次打开：沿用最近一条规则用过的标记；没有历史则用 1（不是 0，避免默认把整天铃声全关掉）
                int seed = 1;
                for (int i = _records.Count - 1; i >= 0; i--)
                {
                    if (_records[i] is JsonObject o && int.TryParse(o["marker"]?.ToString(), out int m)) { seed = m; break; }
                }
                cur = seed;
                _lastUsedMarker = seed;
            }
            if (cur < 0 || cur > 10) cur = 0;
            _marker.SelectedIndex = cur;
            _markerInitialized = true;
        }

        // ================= 数据 =================

        private async Task ReloadAsync()
        {
            try
            {
                _records = await DesktopApi.GetTiaoxiu();
                RefreshMarkerItems();
                RenderList();
                await LoadInvalidAsync();
                var markers = new HashSet<int>();
                foreach (var n in _records) if (n is JsonObject o && int.TryParse(o["marker"]?.ToString(), out int mk)) markers.Add(mk);
                SetStatus($"共 {_records.Count} 条规则；已用到的标记：" + (markers.Count == 0 ? "无" : string.Join("、", markers.OrderBy(x => x))));
            }
            catch (Exception ex) { SetStatus("读取失败：" + ex.Message, Theme.Danger); }
        }

        private void RenderList()
        {
            _list.Children.Clear();
            var sorted = new List<JsonObject>();
            foreach (var n in _records) if (n is JsonObject o) sorted.Add(o);
            sorted.Sort((a, b) => string.Compare(a["start_date"]?.ToString() ?? a["date"]?.ToString(), b["start_date"]?.ToString() ?? b["date"]?.ToString(), StringComparison.Ordinal));

            if (sorted.Count == 0) { _list.Children.Add(Theme.Label("暂无调休规则。", 12.5, Theme.TextGray)); return; }

            foreach (var o in sorted)
            {
                string start = o["start_date"]?.ToString() ?? o["date"]?.ToString() ?? "";
                string end = o["end_date"]?.ToString() ?? start;
                string marker = o["marker"]?.ToString() ?? "0";
                string action = o["action"]?.ToString();
                bool enable = action == "enable";
                bool isSub = o["source"]?.ToString() == "sub";

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });   // 日期
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });   // 标记
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });    // 动作
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });   // 备注
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });    // 来源
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dateText = Theme.Label(start == end ? start : $"{start} ~ {end}", 12.5);
                var markerText = Theme.Label(marker == "0" ? "全局" : "标记 " + marker, 12.5, marker == "0" ? Theme.Primary : Theme.TextDark); markerText.ToolTip = "0 = 全局（当天所有任务都受影响）；1-10 = 只影响绑定同标记的任务";
                var actionText = Theme.Label(enable ? "启用" : "禁用", 12.5, enable ? Theme.Success : Theme.Danger);
                var noteText = Theme.Label(o["note"]?.ToString() ?? "-", 12);
                var srcText = Theme.Label(isSub ? "云端订阅" : "手动", 12, Theme.TextGray);

                Grid.SetColumn(dateText, 0); Grid.SetColumn(markerText, 1); Grid.SetColumn(actionText, 2);
                Grid.SetColumn(noteText, 3); Grid.SetColumn(srcText, 4);
                row.Children.Add(dateText); row.Children.Add(markerText); row.Children.Add(actionText);
                row.Children.Add(noteText); row.Children.Add(srcText);

                var del = Theme.Button("删除", Theme.Danger, Brushes.White, "\uf1f8", () =>
                {
                    _records.Remove(o);
                    SaveAll();
                });
                Grid.SetColumn(del, 5);
                row.Children.Add(del);

                _list.Children.Add(row);
            }
        }

        private void AddRecord()
        {
            if (_start.SelectedDate == null) { SetStatus("请先选择开始日期。", Theme.Danger); return; }
            var ds = _start.SelectedDate.Value;
            var de = _end.SelectedDate ?? ds;
            if (de < ds) { SetStatus("结束日期不能早于开始日期。", Theme.Danger); return; }
            int marker = MarkerFromCombo();
            _lastUsedMarker = marker;
            string action = _actionEnable ? "enable" : "disable";

            string s = ds.ToString("yyyy-MM-dd"), e = de.ToString("yyyy-MM-dd");
            JsonObject hit = null;
            foreach (var n in _records)
            {
                if (n is not JsonObject o) continue;
                string os = o["start_date"]?.ToString() ?? o["date"]?.ToString() ?? "";
                string oe = o["end_date"]?.ToString() ?? os;
                if (os == s && oe == e && (o["marker"]?.ToString() ?? "0") == marker.ToString()) { hit = o; break; }
            }
            if (hit != null)
            {
                hit["action"] = action;
                hit["note"] = _note.Text.Trim();
                hit["source"] = "manual";
                SetStatus("已覆盖同日期同标记的既有规则（后写入覆盖先写入）。", Theme.Warn);
            }
            else
            {
                _records.Add(new JsonObject
                {
                    ["_type"] = "tiaoxiu",
                    ["source"] = "manual",
                    ["start_date"] = s,
                    ["end_date"] = e,
                    ["action"] = action,
                    ["marker"] = marker,
                    ["note"] = _note.Text.Trim()
                });
                SetStatus("已添加规则（记得点保存）。", Theme.Success);
            }
            RefreshMarkerItems();
            RenderList();
        }

        private void SaveAll()
        {
            try
            {
                Program.WriteSettingMerged( ComposeSetting());
                PlayLog.Write("调休", "保存", "warn", detail: $"共 {_records.Count} 条调休规则");
                RenderList();
                SetStatus("已保存。规则在新的一天生效时会按“分标记优先于全局”判定。", Theme.Success);
            }
            catch (Exception ex) { SetStatus("保存失败：" + ex.Message, Theme.Danger); }
        }

        /// <summary>把调休规则与其它设置项（global、无效数据清单）合并后整体写回。</summary>
        private JsonArray ComposeSetting()
        {
            var all = Program.ReadSettingMerged();
            var result = new JsonArray();
            foreach (var n in all)
            {
                if (n is not JsonObject o) continue;
                if (o["_type"]?.ToString() == "tiaoxiu") continue;
                result.Add(o.DeepClone());
            }
            foreach (var n in _records) result.Add(n.DeepClone());
            return result;
        }

        private async Task SyncAsync()
        {
            SetStatus("正在向云端拉取调休数据…");
            var r = await DesktopApi.SyncSubscription();
            bool ok = r?["status"]?.ToString() == "success";
            if (ok)
            {
                int invalid = 0; try { invalid = r?["invalid"]?.GetValue<int>() ?? 0; } catch { }
                SetStatus(r?["message"]?.ToString() ?? "云端同步完成。", invalid > 0 ? Theme.Warn : Theme.Success);
                await ReloadAsync();
            }
            else
            {
                SetStatus(r?["message"]?.ToString() ?? "云端同步失败，请检查订阅地址（菜单 → 订阅）。", Theme.Danger);
            }
        }

        private async Task LoadInvalidAsync()
        {
            _invalid.Children.Clear();
            try
            {
                var groups = await DesktopApi.GetInvalidTiaoxiu();
                int total = 0;
                foreach (var g in groups)
                {
                    if (g is not JsonObject go) continue;
                    string at = go["synced_at"]?.ToString() ?? "";
                    if (go["items"] is not JsonArray items) continue;
                    foreach (var it in items)
                    {
                        if (it is not JsonObject io) continue;
                        total++;
                        var line = new TextBlock
                        {
                            Text = $"[{at}] {io["reason"]}　原始：{io["raw"]}",
                            FontSize = 12,
                            Foreground = Theme.Danger,
                            TextDecorations = TextDecorations.Strikethrough,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 2)
                        };
                        _invalid.Children.Add(line);
                    }
                }
                if (total == 0) _invalid.Children.Add(Theme.Label("没有无效数据。", 12.5, Theme.TextGray));
            }
            catch { _invalid.Children.Add(Theme.Label("读取无效数据失败。", 12.5, Theme.Danger)); }
        }

        private async Task ClearInvalidAsync()
        {
            if (!AppDialog.Confirm(Host, "确认清除",
                "确定清除无效的云端数据记录吗？（只删除无效项，不影响任何调休规则）",
                "确定清除", "取消", AppDialogKind.Warning)) return;
            await DesktopApi.ClearInvalidTiaoxiu();
            await LoadInvalidAsync();
            SetStatus("已清除无效数据记录。", Theme.Success);
        }
    }
}
