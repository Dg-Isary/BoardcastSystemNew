#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>任务列表的一行（只读展示，编辑走弹窗）。</summary>
    public class TaskRow
    {
        public string Id { get; set; } = "";

        /// <summary>编号的数值形式：供 DataGrid 点表头排序用，否则字符串排序会排成 1、10、11、2…</summary>
        public int IdNum { get; set; }
        public string Start { get; set; } = "";        // HH:MM
        public string End { get; set; } = "";          // 计算出的结束时间
        public string TimeRange => Start + (End == "—" ? " → 完整" : " → " + End);
        public string Duration { get; set; } = "";
        public string Content { get; set; } = "";
        public string Marker { get; set; } = "";
        public string Volume { get; set; } = "";
        public string Cycle { get; set; } = "";
        public string Modes { get; set; } = "";
        public JsonObject Raw { get; set; }             // 原始任务对象（编辑时用）

        // ---- 排序键：列里显示的是给人看的文字，排序用这些数值/原始值 ----
        public int StartSec { get; set; }
        public int DurationSec { get; set; }
        public int MarkerNum { get; set; }
        public int VolumeNum { get; set; }
    }

    /// <summary>
    /// 任务排程：桌面端直接管理定时打铃任务（与网页端/手机端共用同一份 tasks.jsonl）。
    /// 布局沿用音乐库那套：顶部工具栏固定、中间列表滚动、底部不再放表单（编辑走弹窗，字段太多）。
    /// </summary>
    public class TasksPage : PageBase
    {
        private readonly ObservableCollection<TaskRow> _rows = new ObservableCollection<TaskRow>();
        private readonly DataGrid _grid = new DataGrid();
        private JsonArray _music = new JsonArray();
        private Style _headerStyle;

        private static readonly Brush AccentBg = new SolidColorBrush(Color.FromRgb(0xEA, 0xF4, 0xFB));
        private static readonly Brush LineBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xF8));

        public TasksPage(MainWindow host) : base(host, "任务排程",
            "到点自动抢占并打铃") { }

        protected override void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 工具栏
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 列表

            root.Children.Add(At(BuildToolbar(), 0));
            root.Children.Add(At(BuildListCard(), 1));
            FillHost.Children.Add(root);

            SetStatus("正在读取任务…");
            _ = ReloadAsync();
        }

        private static UIElement At(UIElement el, int row) { Grid.SetRow(el, row); return el; }

        private static Button Btn(string text, string icon, Action onClick, Brush bg = null, Brush fg = null)
        {
            var b = Theme.Button(text, bg, fg, icon, onClick);
            b.FontSize = 13;
            b.Padding = new Thickness(14, 8, 14, 8);
            b.Margin = new Thickness(0, 0, 8, 6);
            return b;
        }

        private UIElement BuildToolbar()
        {
            var tools = new WrapPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(Btn("新增任务…", "\uf0fe", () => Edit(null), Theme.Primary, Brushes.White));
            tools.Children.Add(Btn("编辑选中…", "\uf044", () => Edit(Selected())));
            tools.Children.Add(Btn("复制为新任务…", "\uf0c5", CopySelected));
            tools.Children.Add(Btn("删除选中", "\uf1f8", DeleteSelected, Theme.Danger, Brushes.White));
            tools.Children.Add(Btn("按时间重排编号", "\uf162", Renumber));
            tools.Children.Add(Btn("重新载入", "\uf021", () => _ = ReloadAsync()));
            tools.Children.Add(Btn("网页端编辑排程", "\uf0ac", Host.OpenWebDashboard));
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 4),
                Margin = new Thickness(0, 0, 0, 10),
                Child = tools
            };
        }

        private UIElement BuildListCard()
        {
            BuildGrid();
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(2),
                Child = _grid
            };
        }

        private void BuildGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = false;
            _grid.CanUserResizeColumns = true;
            _grid.CanUserReorderColumns = false;
            _grid.IsReadOnly = true;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.HorizontalGridLinesBrush = LineBg;
            _grid.RowHeight = 36;
            _grid.FontSize = 13.5;
            _grid.BorderThickness = new Thickness(0);
            _grid.Background = Brushes.White;
            _grid.RowBackground = Brushes.White;
            _grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0xFE));
            _grid.ColumnHeaderHeight = 36;
            _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _headerStyle = new Style(typeof(DataGridColumnHeader))
            {
                Setters =
                {
                    new Setter(DataGridColumnHeader.BackgroundProperty, AccentBg),
                    new Setter(DataGridColumnHeader.ForegroundProperty, Theme.TextDark),
                    new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold),
                    new Setter(DataGridColumnHeader.FontSizeProperty, 13.5),
                    new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(10, 0, 10, 0)),
                    new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0)),
                    new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Left)
                }
            };
            _grid.ColumnHeaderStyle = _headerStyle;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            _grid.MouseDoubleClick += (s, e) => Edit(Selected());

            _grid.Columns.Add(Text("编号", "Id", 56, true, "IdNum"));
            _grid.Columns.Add(Text("开始 → 结束", "TimeRange", 152, true, "StartSec"));
            _grid.Columns.Add(Text("时长", "Duration", 78, true, "DurationSec"));

            var content = new DataGridTextColumn
            {
                Header = "播放内容",
                Binding = new Binding("Content") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(2.2, DataGridLengthUnitType.Star)
            };
            content.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap),
                    new Setter(TextBlock.ToolTipProperty, new Binding("Content")),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x6A)))
                }
            };
            _grid.Columns.Add(content);

            _grid.Columns.Add(Text("标记", "Marker", 58, true, "MarkerNum"));
            _grid.Columns.Add(Text("音量", "Volume", 62, true, "VolumeNum"));
            _grid.Columns.Add(Text("列队", "Modes", 92, true));

            var cycle = new DataGridTextColumn
            {
                Header = "播放周期",
                Binding = new Binding("Cycle") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star)
            };
            cycle.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap),
                    new Setter(TextBlock.ToolTipProperty, new Binding("Cycle")),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0))
                }
            };
            _grid.Columns.Add(cycle);
        }

        private DataGridTextColumn Text(string header, string path, double width, bool centerHeader, string sortPath = null)
        {
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                IsReadOnly = true,
                Width = new DataGridLength(width),
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0))
                    }
                }
            };
            // 显示文字与排序依据分开：编号/时长/标记/音量都是"好看的文字 + 数值排序"
            if (!string.IsNullOrEmpty(sortPath)) col.SortMemberPath = sortPath;
            if (centerHeader)
            {
                col.HeaderStyle = new Style(typeof(DataGridColumnHeader))
                {
                    BasedOn = _headerStyle,
                    Setters = { new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) }
                };
            }
            return col;
        }

        // ================= 数据 =================

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            try
            {
                _music = await DesktopApi.GetMusic();
                var tasks = await DesktopApi.GetTasks();
                _rows.Clear();
                foreach (var n in tasks)
                {
                    if (n is not JsonObject o) continue;
                    _rows.Add(ToRow(o));
                }
                _grid.ItemsSource = new ObservableCollection<TaskRow>(_rows.OrderBy(r => r.Start));
                SetStatus("共 " + _rows.Count + " 条任务　·　双击一行可编辑　·　改完记得保存（自动写入 tasks.jsonl 并同步网页端）");
            }
            catch (Exception ex) { SetStatus("读取任务失败：" + ex.Message, Theme.Danger); }
        }

        private TaskRow ToRow(JsonObject o)
        {
            string st = o["start_time"]?.ToString() ?? "00:00:00";
            var parts = st.Split(':');
            int h = 0, mi = 0, s = 0;
            if (parts.Length == 3) { int.TryParse(parts[0], out h); int.TryParse(parts[1], out mi); int.TryParse(parts[2], out s); }
            int dm = 0, ds = 0; int.TryParse(o["duration_m"]?.ToString(), out dm); int.TryParse(o["duration_s"]?.ToString(), out ds);
            int total = dm * 60 + ds;
            string duration = total <= 0 ? "完整播放" : (dm > 0 ? dm + " 分 " + ds + " 秒" : ds + " 秒");
            string end = "—";
            if (total > 0)
            {
                var t = new DateTime(2000, 1, 1, h, mi, s).AddSeconds(total);
                end = t.ToString("HH:mm");
            }

            var ids = (o["music_ids"] as JsonArray) ?? new JsonArray();
            var names = new List<string>();
            foreach (var idn in ids)
            {
                string id = idn?.ToString() ?? "";
                names.Add(NameOf(id));
            }
            string content = names.Count == 0 ? "（未选择内容）" : string.Join("  →  ", names);

            string marker = o["tiaoxiu_marker"]?.ToString() ?? "1";
            string vol = (o["volume"]?.ToString() ?? "100") + "%";
            string seq = o["sequence_mode"]?.ToString() == "random" ? "随机" : "顺序";
            string folder = o["folder_mode"]?.ToString() == "random" ? "随机" : "接续";
            string modes = seq + " / " + folder;

            var days = (o["days"] as JsonArray) ?? new JsonArray();
            var dayNames = new List<string>();
            string[] week = { "", "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            foreach (var d in days) { if (int.TryParse(d?.ToString(), out int w) && w >= 1 && w <= 7) dayNames.Add(week[w]); }
            var dates = (o["specific_dates"] as JsonArray) ?? new JsonArray();
            var dateList = dates.Select(d => d?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            string cycle;
            bool workdays = new[] { 1, 2, 3, 4, 5 }.All(d => days.Any(x => x?.ToString() == d.ToString()));
            if (dayNames.Count == 7) cycle = "每天";
            else if (dayNames.Count == 5 && workdays) cycle = "周一~周五";
            else if (dayNames.Count == 0) cycle = "—";
            else cycle = string.Join("、", dayNames);
            if (dateList.Count > 0) cycle += "　+ " + dateList.Count + " 个特殊日期";
            int fc = 0; int.TryParse(o["folder_play_count"]?.ToString(), out fc);
            if (fc > 1) modes += " · 提 " + fc + " 首";

            return new TaskRow
            {
                Id = o["id"]?.ToString() ?? "",
                IdNum = ParseInt(o["id"]?.ToString()),
                Start = $"{h:00}:{mi:00}",
                StartSec = h * 3600 + mi * 60,
                Duration = duration,
                DurationSec = total,
                End = end,
                Content = content,
                Marker = marker == "0" ? "0 全局" : marker,
                MarkerNum = ParseInt(marker),
                Volume = vol,
                VolumeNum = ParseInt(o["volume"]?.ToString()),
                Cycle = cycle,
                Modes = modes,
                Raw = o
            };
        }

        private static int ParseInt(string s)
        {
            int v; return int.TryParse(s, out v) ? v : 0;
        }

        private string NameOf(string id)
        {
            foreach (var n in _music)
                if (n is JsonObject o && o["id"]?.ToString() == id) return o["name"]?.ToString() ?? ("编号 " + id);
            return "编号 " + id + "（已删除）";
        }

        private TaskRow Selected() => _grid.SelectedItem as TaskRow;

        // ================= 操作 =================

        private async void Edit(TaskRow row)
        {
            try
            {
                if (_music.Count == 0) _music = await DesktopApi.GetMusic();
                var editing = row?.Raw != null ? (JsonObject)row.Raw.DeepClone() : new JsonObject();
                int id = 0;
                bool isUpdate = int.TryParse(editing["id"]?.ToString(), out id);
                var dlg = new TaskEditorWindow(editing, _music) { Owner = Host };
                if (dlg.ShowDialog() != true || dlg.Result == null) return;

                var result = isUpdate
                    ? await DesktopApi.UpdateTask(id, dlg.Result)
                    : await DesktopApi.AddTask(dlg.Result);

                if (result == null) { SetStatus("保存失败：本机接口无响应。", Theme.Danger); return; }
                PlayLog.Write("排程", isUpdate ? "修改任务" : "新增任务", "info",
                    task: isUpdate ? "ID " + id : "", detail: "桌面端编辑");
                SetStatus(isUpdate ? "已保存任务修改（网页端已同步）。" : "已新增任务（网页端已同步）。", Theme.Success);
                await ReloadAsync();
            }
            catch (Exception ex) { SetStatus("保存任务失败：" + ex.Message, Theme.Danger); }
        }

        /// <summary>
        /// 按开始时间把所有任务重新编号为 1..N 并写回 tasks.jsonl。
        /// 编号是任务的对外标识（网页端改/删都用它），所以必须先明确告知需要刷新其他端。
        /// </summary>
        private async void Renumber()
        {
            if (_rows.Count == 0) { SetStatus("没有任务可重排。", Theme.Danger); return; }
            if (!AppDialog.Confirm(Host, "按时间重排编号",
                $"将把全部 {_rows.Count} 条任务按「开始时间」从小到大重新编号为 1 ~ {_rows.Count}，并立即写回 tasks.jsonl。\n\n" +
                "· 同一时间的任务保持原有先后；\n" +
                "· 正在播放的定时任务不受影响；\n" +
                "· 编号是任务的对外标识，若网页端/手机端正开着排程页，请刷新后再编辑，否则会拿着旧编号操作。\n\n确定要重排吗？",
                "重排编号", "取消", AppDialogKind.Warning)) return;

            var res = await DesktopApi.RenumberTasks();
            if (res == null) { SetStatus("重排失败：本机接口无响应。", Theme.Danger); return; }
            int n = 0; int.TryParse(res["count"]?.ToString(), out n);
            PlayLog.Write("排程", "重排编号", "warn", detail: $"桌面端触发，共 {n} 条任务按时间重新编号");
            SetStatus($"已按开始时间把 {n} 条任务重新编号为 1 ~ {n} 并写回配置；网页端请刷新后再编辑。", Theme.Success);
            await ReloadAsync();
        }

        /// <summary>复制选中任务：克隆内容但去掉 id，弹窗里确认后作为新任务保存。</summary>
        private void CopySelected()
        {
            var r = Selected();
            if (r == null || r.Raw == null) { SetStatus("请先选择一条要复制的任务。", Theme.Danger); return; }
            var clone = (JsonObject)r.Raw.DeepClone();
            clone.Remove("id");
            Edit(new TaskRow { Id = "", Raw = clone });
        }

        private async void DeleteSelected()
        {
            var r = Selected();
            if (r == null) { SetStatus("请先选择要删除的任务。", Theme.Danger); return; }
            if (!AppDialog.Confirm(Host, "删除任务",
                $"确定删除编号 {r.Id}（{r.Start} · {r.Content}）这条任务吗？\n\n删除后立即写入配置，无法撤销。",
                "删除", "取消", AppDialogKind.Warning)) return;
            var res = await DesktopApi.DeleteTask(int.Parse(r.Id));
            if (res == null) { SetStatus("删除失败：本机接口无响应。", Theme.Danger); return; }
            PlayLog.Write("排程", "删除任务", "warn", task: "ID " + r.Id, detail: "桌面端删除");
            SetStatus("已删除任务 " + r.Id + "。", Theme.Success);
            await ReloadAsync();
        }
    }
}
