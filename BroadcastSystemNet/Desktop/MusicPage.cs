#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>音乐库列表的一行：可编辑字段直接绑定，保存时才写盘。</summary>
    public class MusicRow : INotifyPropertyChanged
    {
        private string _name = "", _path = "", _type = "file", _id = "";

        public string Id
        {
            get => _id;
            set
            {
                _id = value ?? "";
                int n; IdNum = int.TryParse(_id, out n) ? n : 0;
                Raise(nameof(Id));
            }
        }

        /// <summary>编号的数值形式：供 DataGrid 点表头排序用，否则字符串排序会排成 1、10、11、2…</summary>
        public int IdNum { get; private set; }
        public string Name { get => _name; set { _name = value ?? ""; Raise(nameof(Name)); } }
        public string Path
        {
            get => _path;
            set { _path = value ?? ""; Raise(nameof(Path)); Raise(nameof(Source)); }
        }
        public string Type
        {
            get => _type;
            set { _type = value ?? "file"; Raise(nameof(Type)); Raise(nameof(TypeDisplay)); }
        }

        /// <summary>类型的中文显示名（内部仍存 file/folder/hls/web）。</summary>
        public string TypeDisplay
        {
            get => MusicTypes.ToDisplay(_type);
            set => Type = MusicTypes.ToCode(value);
        }

        /// <summary>入库来源：内置库（AppData 相对路径）/ 本地绝对路径 / 网络源。</summary>
        public string Source
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_path)) return "-";
                if (_path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || _path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "网络";
                return System.IO.Path.IsPathRooted(_path) ? "本地" : "内置库";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public void RefreshDerived() { Raise(nameof(Source)); Raise(nameof(TypeDisplay)); }
    }

    /// <summary>类型代码 ↔ 中文显示名互转。</summary>
    public static class MusicTypes
    {
        public static readonly string[] DisplayNames = { "音频文件", "音频目录", "网络流", "网页音频" };

        private static readonly Dictionary<string, string> CodeToDisplay = new Dictionary<string, string>
        {
            ["file"] = "音频文件", ["folder"] = "音频目录", ["hls"] = "网络流", ["web"] = "网页音频",
            ["warn"] = "气象预警"          // 保留编号 1000 的虚拟类型，不出现在音乐库的"新增"下拉里
        };
        private static readonly Dictionary<string, string> DisplayToCode = new Dictionary<string, string>
        {
            ["音频文件"] = "file", ["音频目录"] = "folder", ["网络流"] = "hls", ["网页音频"] = "web"
        };

        public static string ToDisplay(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string d; return CodeToDisplay.TryGetValue(code, out d) ? d : code;
        }
        public static string ToCode(string display)
        {
            if (string.IsNullOrEmpty(display)) return "file";
            string c; return DisplayToCode.TryGetValue(display, out c) ? c : display;
        }
    }

    /// <summary>
    /// 音乐库（本地音频管理）。
    /// 布局：顶部工具栏与底部按钮固定不动，只有中间条目列表滚动。
    /// 保存：所有改动只写内存，点“保存到磁盘”才落盘。
    /// 编号：一经分配固定不变，删除只留空缺，新增顶替最小空缺。
    /// </summary>
    public class MusicPage : PageBase
    {
        private readonly ObservableCollection<MusicRow> _rows = new ObservableCollection<MusicRow>();
        private readonly DataGrid _grid = new DataGrid();
        private Style _headerStyle;
        private bool _loading;

        // 明亮配色
        private static readonly Brush AccentBg = new SolidColorBrush(Color.FromRgb(0xEA, 0xF4, 0xFB));
        private static readonly Brush LineBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xF8));

        public MusicPage(MainWindow host) : base(host, "音乐库（本地音频管理）",
            "本地音频库") { }

        protected override void Build()
        {

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 工具栏（固定）
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 列表（滚动）
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 表单+按钮（固定）

            root.Children.Add(At(BuildToolbar(), 0));
            root.Children.Add(At(BuildListCard(), 1));
            root.Children.Add(At(BuildBottom(), 2));

            FillHost.Children.Add(root);
            SetStatus("正在读取音乐库…");
            _ = ReloadAsync();
        }

        private static UIElement At(UIElement el, int row) { Grid.SetRow(el, row); return el; }

        private static Button Btn(string text, string icon, Action onClick, Brush bg = null, Brush fg = null)
        {
            var b = Theme.Button(text, bg, fg, icon, onClick);
            b.FontSize = 13;
            b.Padding = new Thickness(13, 6, 13, 6);   // 与全局按钮统一高度
            b.Margin = new Thickness(0, 0, 8, 6);
            return b;
        }

        private UIElement BuildToolbar()
        {
            var tools = new WrapPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(Btn("导入文件…", "\uf15b", ImportFile, Theme.Primary, Brushes.White));
            tools.Children.Add(Btn("导入整个文件夹…", "\uf07b", ImportFolder));
            tools.Children.Add(Btn("扫描默认铃声库", "\uf002", () => ScanDirectory(Program.RingDir)));
            tools.Children.Add(Btn("扫描自定义目录…", "\uf002", ScanCustom));
            tools.Children.Add(Btn("打开默认库", "\uf08e", () => Host.OpenPath(Program.RingDir)));
            tools.Children.Add(Btn("重新载入", "\uf021", () => _ = ReloadAsync()));
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
                Margin = new Thickness(0, 0, 0, 10),
                Child = _grid
            };
        }

        private UIElement BuildBottom()
        {
            // 底部固定区只有操作按钮（新增已改成独立窗口，这里不再占高度）；不随列表滚动
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(BuildActionBar());
            return panel;
        }


        private void BuildGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = false;
            _grid.CanUserResizeColumns = true;
            _grid.CanUserReorderColumns = false;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.HorizontalGridLinesBrush = LineBg;
            _grid.RowHeight = 34;
            _grid.FontSize = 13.5;
            _grid.BorderThickness = new Thickness(0);
            _grid.Background = Brushes.White;
            _grid.RowBackground = Brushes.White;
            _grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0xFE));
            _grid.ColumnHeaderHeight = 36;
            _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;   // 列宽自适应，不出横向滚动条
            _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;         // 只有条目区纵向滚动
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

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "编号",
                Binding = new Binding("Id"),
                SortMemberPath = "IdNum",   // 按数值排序，别按字符串
                IsReadOnly = true,
                Width = new DataGridLength(62),
                HeaderStyle = BuildCenteredHeaderStyle(),
                ElementStyle = BuildCenteredTextStyle()
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "显示名称",
                Binding = new Binding("Name") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
                ElementStyle = BuildCenteredTextStyle()
            });

            var pathCol = new DataGridTextColumn
            {
                Header = "物理路径 / 网络源 URL",
                Binding = new Binding("Path") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(2.4, DataGridLengthUnitType.Star)
            };
            // 过长路径缩略显示（悬停用 ToolTip 看完整值）
            pathCol.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap),
                    new Setter(TextBlock.ToolTipProperty, new Binding("Path")),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x6A)))
                }
            };
            _grid.Columns.Add(pathCol);

            _grid.Columns.Add(BuildTypeColumn());
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "来源",
                Binding = new Binding("Source"),
                IsReadOnly = true,
                Width = new DataGridLength(84),
                HeaderStyle = BuildCenteredHeaderStyle(),
                ElementStyle = BuildCenteredTextStyle()
            });
        }

        /// <summary>
        /// 类型列：用模板列而不是 DataGridComboBoxColumn。
        /// 教训：DataGridComboBoxColumn 非编辑态的元素是内部类 TextBlockComboBox（ComboBox 的子类），
        /// 给它套 TargetType=TextBlock 的 ElementStyle 会每次布局都抛
        /// "TextBlock TargetType 与元素 TextBlockComboBox 的类型不匹配"，界面直接刷不出内容、
        /// 日志一秒几十条地涨。模板列里显示与编辑分开，显示用居中的 TextBlock，编辑用亮色 ComboBox，行为完全可控。
        /// </summary>
        private DataGridTemplateColumn BuildTypeColumn()
        {
            // 非编辑态：居中的中文类型名
            var cell = new FrameworkElementFactory(typeof(TextBlock));
            cell.SetBinding(TextBlock.TextProperty, new Binding("TypeDisplay"));
            cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cell.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cell.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            cell.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0));

            // 编辑态：白底浅蓝边、内容居中的下拉框
            var edit = new FrameworkElementFactory(typeof(ComboBox));
            edit.SetValue(ComboBox.ItemsSourceProperty, MusicTypes.DisplayNames);
            edit.SetBinding(ComboBox.SelectedItemProperty, new Binding("TypeDisplay")
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                Mode = BindingMode.TwoWay
            });
            edit.SetValue(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
            edit.SetValue(System.Windows.Controls.Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            edit.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            edit.SetValue(System.Windows.Controls.Control.FontSizeProperty, 13.5);
            edit.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is ComboBox cb) Theme.ApplyLightComboStyle(cb);
            }));

            return new DataGridTemplateColumn
            {
                Header = "类型",
                Width = new DataGridLength(132),
                HeaderStyle = BuildCenteredHeaderStyle(),
                CellTemplate = new DataTemplate { VisualTree = cell },
                CellEditingTemplate = new DataTemplate { VisualTree = edit }
            };
        }

        /// <summary>窄列的表头也居中，避免"表头靠左、内容居中"的错位感。</summary>
        private Style BuildCenteredHeaderStyle()
        {
            return new Style(typeof(DataGridColumnHeader))
            {
                BasedOn = _headerStyle,
                Setters = { new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) }
            };
        }

        /// <summary>单元格文字垂直居中（默认会顶到上沿，看起来"上下不居中"）。</summary>
        private static Style BuildCenteredTextStyle()
        {
            return new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0))
                }
            };
        }

        private UIElement BuildActionBar()
        {
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(Btn("上移", "\uf062", () => Move(-1)));
            bar.Children.Add(Btn("下移", "\uf063", () => Move(1)));
            bar.Children.Add(Btn("置于页首", "\uf102", () => MoveTo(true)));
            bar.Children.Add(Btn("置于页尾", "\uf103", () => MoveTo(false)));
            bar.Children.Add(Btn("删除选中", "\uf1f8", DeleteSelected, Theme.Danger, Brushes.White));
            bar.Children.Add(Btn("打开所在目录", "\uf07c", OpenSelectedFolder));
            var save = Btn("保存到磁盘并通知网页端", "\uf0c7", SaveAll, Theme.Primary, Brushes.White);
            save.Margin = new Thickness(26, 0, 0, 6);
            save.Padding = new Thickness(20, 9, 20, 9);
            save.FontWeight = FontWeights.Bold;
            bar.Children.Add(save);
            return bar;
        }

        // ================= 数据 =================

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            _loading = true;
            try
            {
            int repaired = 0;
                var all = Program.ReadJsonl("music.jsonl");
                _rows.Clear();
                foreach (var n in all)
                {
                    if (n is not JsonObject o) continue;
                    if (o["_type"]?.ToString() == "settings") continue;
                    if (o["id"] == null) continue;
                    string rawPath = o["path"]?.ToString() ?? "";
                    string fixedPath = Program.NormalizeMusicPath(rawPath);
                    if (!string.Equals(fixedPath, rawPath, StringComparison.Ordinal))
                    {
                        repaired++;
                        PlayLog.Write("音乐库", "路径规范化", "warn", name: o["name"]?.ToString(), path: rawPath, detail: "内置铃声改为相对路径 " + fixedPath);
                    }
                    _rows.Add(new MusicRow
                    {
                        Id = o["id"]?.ToString() ?? "",
                        Name = o["name"]?.ToString() ?? "",
                        Path = fixedPath,
                        Type = o["type"]?.ToString() ?? "file"
                    });
                }
                _grid.ItemsSource = _rows;
                SetStatus("共 " + _rows.Count + " 个条目　·　双击单元格可直接改名称/路径/类型　·　改完记得点保存" + (repaired > 0 ? "　·　已把 " + repaired + " 条内置铃声的绝对路径规范为相对路径，保存后生效" : ""), repaired > 0 ? Theme.Warn : Theme.TextDark);
            }
            catch (Exception ex) { SetStatus("读取失败：" + ex.Message, Theme.Danger); }
            finally { _loading = false; }
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private MusicRow Selected() => _grid.SelectedItem as MusicRow;


        private void ImportFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "音频/列表|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.m3u8;*.m3u|所有文件|*.*" };
            if (dlg.ShowDialog() != true) return;
            var row = new MusicRow { Id = NextFreeId(), Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Path = Program.NormalizeMusicPath(dlg.FileName), Type = "file" };
            _rows.Add(row); SelectRow(row);
            SetStatus("已导入并分配编号 " + row.Id + "（点“保存到磁盘”后生效）", Theme.Warn);
        }

        private void ImportFolder()
        {
            var dir = new FolderBrowserDialogWrapper().Pick("选择要导入的音频文件夹");
            if (string.IsNullOrEmpty(dir)) return;
            var row = new MusicRow { Id = NextFreeId(), Name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')), Path = Program.NormalizeMusicPath(dir), Type = "folder" };
            _rows.Add(row); SelectRow(row);
            SetStatus("已导入目录并分配编号 " + row.Id + "（点“保存到磁盘”后生效）", Theme.Warn);
        }

        private void ScanCustom()
        {
            var dir = new FolderBrowserDialogWrapper().Pick("选择要批量扫描的文件夹");
            if (!string.IsNullOrEmpty(dir)) ScanDirectory(dir);
        }

        private void ScanDirectory(string dir)
        {
            if (!Directory.Exists(dir)) { SetStatus("目录不存在：" + dir, Theme.Danger); return; }
            var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac" };
            var pending = new List<MusicRow>();
            int added = 0, updated = 0;
            foreach (var fp in Directory.GetFileSystemEntries(dir).OrderBy(f => f))
            {
                string name = System.IO.Path.GetFileName(fp);
                if (name.StartsWith(".")) continue;
                string entryName, entryType;
                if (Directory.Exists(fp)) { entryName = name; entryType = "folder"; }
                else if (exts.Contains(System.IO.Path.GetExtension(fp).ToLower())) { entryName = System.IO.Path.GetFileNameWithoutExtension(fp); entryType = "file"; }
                else continue;

                var existing = _rows.FirstOrDefault(r => r.Name == entryName);
                if (existing != null) { existing.Path = Program.NormalizeMusicPath(fp); existing.Type = entryType; updated++; }
                else { pending.Add(new MusicRow { Name = entryName, Path = Program.NormalizeMusicPath(fp), Type = entryType }); added++; }
            }
            if (pending.Count > 0)
            {
                var ids = NextFreeIds(pending.Count);
                for (int i = 0; i < pending.Count; i++) { pending[i].Id = ids[i]; _rows.Add(pending[i]); }
            }
            SetStatus("扫描完成：新增 " + added + " 个、更新 " + updated + " 个（点“保存到磁盘”后生效）", Theme.Warn);
        }

        // ================= 稳定编号 =================

        private HashSet<int> UsedIds()
        {
            var used = new HashSet<int>();
            foreach (var r in _rows) { int id; if (int.TryParse(r.Id, out id) && id > 0) used.Add(id); }
            // 保留编号 1000（气象预警播报）是虚拟条目、不在 music.jsonl 里，但也不能被新条目占用
            // —— 否则重号后调度侧永远走预警分支，那条真实音乐永远播不到。
            used.Add(WeatherWarn.MusicId);
            return used;
        }
        private string NextFreeId() { var u = UsedIds(); int c = 1; while (u.Contains(c)) c++; return c.ToString(); }
        private List<string> NextFreeIds(int count)
        {
            var u = UsedIds(); var res = new List<string>(); int c = 1;
            while (res.Count < count) { while (u.Contains(c)) c++; u.Add(c); res.Add(c.ToString()); c++; }
            return res;
        }

        private void SelectRow(MusicRow row)
        {
            _grid.SelectedItem = row;
            if (row != null) _grid.ScrollIntoView(row);
        }

        // ================= 操作 =================

        private void DeleteSelected()
        {
            var r = Selected();
            if (r == null) { SetStatus("请先选择要删除的条目。", Theme.Danger); return; }
            string id = r.Id, name = r.Name;
            _rows.Remove(r);
            SetStatus("已从列表移除编号 " + id + "《" + name + "》（编号留作空缺，点“保存到磁盘”后生效）", Theme.Warn);
        }

        private void Move(int dir)
        {
            var r = Selected(); if (r == null) return;
            int i = _rows.IndexOf(r), t = i + dir;
            if (t < 0 || t >= _rows.Count) return;
            _rows.Move(i, t); SelectRow(r);
            SetStatus("顺序已调整（编号未改变，保存后生效）", Theme.Warn);
        }

        private void MoveTo(bool top)
        {
            var r = Selected(); if (r == null) return;
            int i = _rows.IndexOf(r);
            if ((top && i == 0) || (!top && i == _rows.Count - 1)) return;
            _rows.Move(i, top ? 0 : _rows.Count - 1); SelectRow(r);
            SetStatus("顺序已调整（编号未改变，保存后生效）", Theme.Warn);
        }

        private void OpenSelectedFolder()
        {
            var r = Selected(); if (r == null) return;
            try
            {
                string p = Program.ResolveAudioPath(r.Path);
                string dir = Directory.Exists(p) ? p : System.IO.Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Host.OpenPath(dir);
                else SetStatus("目录不存在：" + p, Theme.Danger);
            }
            catch (Exception ex) { SetStatus("打开目录失败：" + ex.Message, Theme.Danger); }
        }

        private void SaveAll()
        {
            try
            {
                var arr = new JsonArray();
                foreach (var r in _rows)
                {
                    // R4：显式跳过保留编号 1000（气象预警播报）——它是 GET /api/music 实时合成的虚拟条目
                    // （带 "_virtual": true），不属于音乐库。以前只靠"路径为空"顺带把它滤掉，
                    // 用户若给那一行手填了路径就会被写进库，与虚拟条目重号（那条音乐将永远播不到）。
                    if (string.Equals(r.Id, WeatherWarn.MusicId.ToString(), StringComparison.Ordinal)) continue;
                    if (string.IsNullOrWhiteSpace(r.Path)) continue;
                    arr.Add(new JsonObject
                    {
                        ["id"] = r.Id,
                        ["name"] = r.Name ?? "",
                        ["path"] = Program.NormalizeMusicPath(r.Path),
                        ["type"] = MusicTypes.ToCode(MusicTypes.ToDisplay(r.Type))
                    });
                }
                Program.WriteJsonl("music.jsonl", arr);
                PlayLog.Write("音乐库", "保存", "info", detail: "共 " + arr.Count + " 个条目（编号保持稳定）");
                SetStatus("已保存 " + arr.Count + " 个条目到磁盘，网页端数据已同步。", Theme.Success);
                _ = ReloadAsync();
            }
            catch (Exception ex) { SetStatus("保存失败：" + ex.Message, Theme.Danger); }
        }
    }

    /// <summary>WPF 里没有内置文件夹选择框，用 WinForms 的对话框包一层。</summary>
    public class FolderBrowserDialogWrapper
    {
        public string Pick(string description)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = false };
            var result = dlg.ShowDialog();
            return result == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }
    }
}
