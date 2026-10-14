#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 预警播报：气象预警订阅（两步接口：列表拿编号 → 详情取全文）+ 预报节点设置 + 离线语音播报。
    ///
    /// 关键点：
    ///   · 保留音乐编号 1000 = 气象预警播报；排程里引用它，到点时按下面的规则判断播不播；
    ///   · 判断规则：正文算出的"预计时间"落在某个预报节点内、且还没过去 → 播；否则只记日志；
    ///   · 语音用 Windows 内置 SAPI 离线合成，不联网，不依赖在线 TTS。
    /// </summary>
    public class WarnPage : PageBase
    {
        class WindowRow
        {
            public readonly List<int> Days = new List<int>();
            public TextBox Start;
            public TextBox End;
            public StackPanel Panel;
        }

        private readonly Button _enableBtn;
        private readonly TextBlock _enableText;
        private bool _enabled;

        private readonly TextBox _listUrl = Theme.TextBox("", 620);
        private readonly TextBox _detailUrl = Theme.TextBox("", 620);
        private readonly TextBox _place = Theme.TextBox("", 80);
        private readonly TextBox _areaCode = Theme.TextBox("", 88);
        private readonly TextBox _town = Theme.TextBox("", 220);
        private readonly ComboBox _interval = Theme.NumberCombo(new[] { 0, 30, 60, 120 }, 88,
            "兜底轮询周期（分钟）：0 = 关闭（默认）——只在排程前按需拉取；填了就额外按这个周期拉一次。可点开选择，也可以直接输入");
        private readonly ComboBox _prefetch = Theme.NumberCombo(new[] { 5, 10, 15, 30 }, 78,
            "提前多少分钟拉取：在引用编号 1000 的排程时刻之前 N 分钟拉一次（默认 10 分钟）。可点开选择，也可以直接输入");
        private readonly ComboBox _rate = Theme.NumberCombo(new[] { -3, -2, -1, 0, 1, 2, 3 }, 78,
            "语速：0 为正常，负数更慢。可点开选择，也可以直接输入");

        private readonly TextBlock _cacheText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        private readonly TextBlock _judgeText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        private readonly TextBlock _bodyText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Foreground = Theme.TextGray };
        private readonly TextBlock _speechText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        private readonly ComboBox _voice = Theme.Combo(160);
        private readonly ComboBox _repeat = Theme.NumberCombo(new[] { 1, 2, 3, 4, 5 }, 78,
            "播报重复次数：一句话太短，学生可能没听到；默认念 2 遍。可点开选择，也可以直接输入");
        private readonly Button _gainBtn;
        private readonly TextBlock _gainText;
        private bool _gain;

        private readonly Button _followBtn;
        private readonly TextBlock _followText;
        private bool _follow;

        private const string VoiceAuto = "自动（优先中文女声）";

        private readonly StackPanel _windowList = new StackPanel();
        private readonly List<WindowRow> _rows = new List<WindowRow>();

        public WarnPage(MainWindow host) : base(host, "预警播报",
            "气象预警订阅 · 保留编号 1000", "气象台预警通过 Windows 内置离线语音播报；排程里引用编号 1000 即可定时播报") 
        {
            _enableText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _enableBtn = Theme.Button("", null, null, null, () => { _enabled = !_enabled; PaintEnable(); });
            _enableBtn.Content = _enableText;

            _gainText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _gainBtn = Theme.Button("", null, null, null, () => { _gain = !_gain; PaintGain(); });
            _gainBtn.Content = _gainText;

            _followText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _followBtn = Theme.Button("", null, null, null, () => { _follow = !_follow; PaintFollow(); });
            _followBtn.Content = _followText;
        }

        void PaintFollow()
        {
            _followText.Text = _follow ? "跟随调休规则：开" : "跟随调休规则：关";
            _followBtn.Background = _follow ? Theme.Success : new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB1));
            _followBtn.Foreground = Brushes.White;
            _followBtn.Padding = new Thickness(14, 6, 14, 6);
            _followBtn.ToolTip = "开着的时候（判定口径与排程任务一致）：\n"
                               + "· 调休判为放假的日子 → 预警一律不播报；\n"
                               + "· 调休判为补班（周末上班）的日子 → 按「周一~周五」的节点判断是否播报；\n"
                               + "· 关掉则只看今天星期几，不管调休。";
        }

        void PaintGain()
        {
            _gainText.Text = _gain ? "响度归一：开" : "响度归一：关";
            _gainBtn.Background = _gain ? Theme.Success : new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB1));
            _gainBtn.Foreground = Brushes.White;
            _gainBtn.Padding = new Thickness(14, 6, 14, 6);
            _gainBtn.ToolTip = "开启后用 ffmpeg 把预警语音的响度归一（目标 -14 LUFS、真峰 -1.5dB），跟音乐一个量级，不会听不见；"
                             + "关闭则播放 SAPI 原始音量（比音乐明显小声）";
        }

        protected override void Build()
        {
            // ---------------- 卡片 1：订阅设置 ----------------
            // 两行接口地址：一行天气接口（列表）、一行预警接口（详情），都填完整地址，换供应商只改这两行。
            var box1 = new StackPanel();

            // 第一行：地区 / 区划码 / 地名筛选 / 提前拉取 / 兜底轮询
            var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(Tag("地区", "你所在地区的地名（例如“××县”）。中央气象台按区划码筛；兜底源 apihz 按这个地名查。**程序不带任何默认值，留空则兜底源不启用**——避免把地点写进程序/配置"));
            row1.Children.Add(_place);
            row1.Children.Add(Tag("区划码", "你所在县/区的 6 位行政区划代码（搜索“行政区划代码 + 你的县名”即可查到）。中央气象台预警编号前 6 位就是这个码，用它精确筛本地预警；**留空则不拉中央气象台**（否则会把别处的预警当本地播）"));
            row1.Children.Add(_areaCode);
            row1.Children.Add(Tag("地名筛选", "本地乡镇/地名关键词，可以填多个：中英文逗号、顿号、分号、空格都能分隔（例如“本站所在镇,邻近镇”）。预警经常先点名邻近乡镇，命中任意一个就算影响本地；正文点名了别的乡镇、一个都没命中 → 跳过；正文没点名具体乡镇（全域预警）→ 照播。留空 = 不做这层过滤"));
            row1.Children.Add(_town);
            row1.Children.Add(Tag("提前拉取(分)", null));
            row1.Children.Add(_prefetch);
            row1.Children.Add(Tag("兜底轮询(分)", "0 = 关闭（默认）：只在排程前按需拉取"));
            row1.Children.Add(_interval);
            box1.Children.Add(row1);

            // 第二、三行：两行接口地址（换供应商只改这两行）
            box1.Children.Add(UrlRow("天气接口", _listUrl,
                "返回当地当前预警编号列表的接口：一行填完整地址（含 id / key / place 参数）。程序从这里拿到最新预警编号"));
            box1.Children.Add(UrlRow("预警接口", _detailUrl,
                "按预警编号取预警全文的接口：地址里用 " + WeatherWarn.NumberToken + " 代表预警编号（例如 ...&number=" + WeatherWarn.NumberToken + "）；没写占位符时会自动在末尾补 &number=编号"));

            // 第四行：已启用 / 响度归一 / 语速 / 音色 / 重复 / 保存 / 立即拉取
            var row4 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _enableBtn.Margin = new Thickness(0, 0, 8, 0);
            row4.Children.Add(_enableBtn);
            row4.Children.Add(_gainBtn);
            row4.Children.Add(Tag("语速", "0 为正常，负数更慢。可点开选择，也可以直接输入"));
            row4.Children.Add(_rate);
            _voice.ToolTip = "播报音色（Windows 内置离线语音，不联网）。默认自动优先中文女声";
            row4.Children.Add(Tag("音色", null));
            row4.Children.Add(_voice);
            row4.Children.Add(Tag("重复", "播报重复次数：一句话太短，学生可能没听到；默认念 2 遍"));
            row4.Children.Add(_repeat);

            var btns1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            btns1.Children.Add(Theme.Button("保存", Theme.Primary, Brushes.White, "\uf0c7", SaveConfig));
            btns1.Children.Add(Theme.Button("立即拉取", null, null, "\uf021", () => _ = FetchNowAsync()));
            row4.Children.Add(btns1);
            box1.Children.Add(row4);

            var card1 = Card(box1);

            // ---------------- 卡片 2：最近预警与判断结果 ----------------
            var box2 = new StackPanel();
            var t2 = Theme.Title("当前预警（按等级排序，红→橙→黄→蓝）");
            t2.ToolTip = "接口每次只取最新一条（多条预警时按编号里的发布时间取最新的那条）";
            box2.Children.Add(t2);

            var rowButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            rowButtons.Children.Add(Theme.Button("试听播报", Theme.Success, Brushes.White, "\uf028", () => _ = PreviewAsync()));
            rowButtons.Children.Add(Theme.Button("重新合成并试听", null, null, "\uf01e", () => _ = PreviewAsync(force: true)));
            var openDir = Theme.Button("打开语音目录", null, null, "\uf07b", () =>
            {
                try { Directory.CreateDirectory(WeatherWarn.WarnDir); Process.Start("explorer.exe", WeatherWarn.WarnDir); } catch { }
            });
            openDir.ToolTip = "合成出来的预警语音缓存在这里，可以直接拷去别处用";
            rowButtons.Children.Add(openDir);
            box2.Children.Add(rowButtons);

            box2.Children.Add(_cacheText);
            box2.Children.Add(_judgeText);
            var sp = Theme.Label("播报文案", 12.5, Theme.TextGray);
            sp.Margin = new Thickness(0, 8, 0, 2);
            box2.Children.Add(sp);
            box2.Children.Add(_speechText);
            var bp = Theme.Label("预警全文", 12.5, Theme.TextGray);
            bp.Margin = new Thickness(0, 8, 0, 2);
            box2.Children.Add(bp);
            box2.Children.Add(_bodyText);
            var card2 = Card(box2);

            // ---------------- 卡片 3：预报节点 ----------------
            var box3 = new StackPanel();
            var t3 = Theme.Title("预报节点（允许播报的时段：排程到点的时刻落在这些时段内，且预警还没过有效期，才会播报）");
            t3.ToolTip = "例：周一~周五 07:30-12:00、13:30-18:00。节点 = 允许播报的时段：排程到点那一刻在时段内、且预警还有效（未过期、未解除、地名命中、调休通过）就播，**不看预警是几点发布的**（早上 6 点发布、有效期到中午的预警，09:30 到点照样播）。排程里没设引用编号 1000 的任务则完全不会播。开始时间晚于结束时间表示跨零点（如 22:00-06:00）";
            box3.Children.Add(t3);

            var followRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            followRow.Children.Add(_followBtn);
            var followTip = Theme.Label("开：调休放假日不播报，调休补班日按「周一~周五」的节点播报（判定口径与排程任务一致）；关：只看星期几。",
                12, Theme.TextGray);
            followTip.VerticalAlignment = VerticalAlignment.Center;
            followTip.Margin = new Thickness(8, 0, 0, 0);
            followRow.Children.Add(followTip);
            box3.Children.Add(followRow);

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
            head.Children.Add(new TextBlock { Text = "星期（点一下选中/取消）", FontSize = 12, Foreground = Theme.TextGray, Width = 300 });
            head.Children.Add(new TextBlock { Text = "开始", FontSize = 12, Foreground = Theme.TextGray, Width = 92 });
            head.Children.Add(new TextBlock { Text = "结束", FontSize = 12, Foreground = Theme.TextGray, Width = 92 });
            box3.Children.Add(head);
            box3.Children.Add(_windowList);

            var btns3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            btns3.Children.Add(Theme.Button("添加节点", null, null, "\uf0fe", () => AddWindowRow(new JsonObject
            {
                ["days"] = new JsonArray { 1, 2, 3, 4, 5 },
                ["start"] = "07:30",
                ["end"] = "12:00"
            })));
            btns3.Children.Add(Theme.Button("恢复默认节点", null, null, "\uf01e", () =>
            {
                _rows.Clear(); _windowList.Children.Clear();
                foreach (var n in WeatherWarn.DefaultWindows()) AddWindowRow(n as JsonObject);
            }));
            btns3.Children.Add(Theme.Button("保存节点", Theme.Primary, Brushes.White, "\uf0c7", SaveWindows));
            box3.Children.Add(btns3);
            var card3 = Card(box3);

            Root.Children.Add(card1);
            Root.Children.Add(card2);
            Root.Children.Add(card3);

            var tip = Theme.Label("提示：编号 1000 是保留编号，音乐库里看不到它；到「任务排程」新建/编辑任务时，"
                                + "在内容列表里选中「气象预警播报（自动生成）」即可定时播报。", 12, Theme.TextGray);
            tip.TextWrapping = TextWrapping.Wrap;
            tip.Margin = new Thickness(2, 0, 0, 8);
            Root.Children.Add(tip);

            LoadConfig();
        }

        /// <summary>一行接口地址：左边标签固定宽度，右边输入框拉满。</summary>
        static StackPanel UrlRow(string label, TextBox box, string tip)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var lb = Theme.Label(label, 12.5, Theme.TextGray);
            lb.Width = 78;
            lb.VerticalAlignment = VerticalAlignment.Center;
            lb.ToolTip = tip;
            box.ToolTip = tip;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(lb);
            row.Children.Add(box);
            return row;
        }

        static TextBlock Tag(string text, string tip)
        {
            var t = Theme.Label(text, 12.5, Theme.TextGray);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(14, 0, 6, 0);
            if (!string.IsNullOrEmpty(tip)) t.ToolTip = tip;
            return t;
        }

        // ---------------- 配置读写 ----------------

        void LoadConfig()
        {
            _enabled = WeatherWarn.GetEnabled();
            PaintEnable();
            _listUrl.Text = WeatherWarn.GetListUrl();
            _detailUrl.Text = WeatherWarn.GetDetailUrl();
            _place.Text = WeatherWarn.GetPlace();
            _areaCode.Text = WeatherWarn.GetAreaCode();
            _town.Text = WeatherWarn.GetTown();
            _interval.Text = WeatherWarn.GetIntervalMin().ToString();
            _prefetch.Text = WeatherWarn.GetPrefetchMin().ToString();
            _rate.Text = WeatherWarn.GetRate().ToString();
            _repeat.Text = WeatherWarn.GetRepeat().ToString();
            _gain = WeatherWarn.GetGain();
            PaintGain();
            _follow = WeatherWarn.GetFollowTiaoxiu();
            PaintFollow();

            // 音色下拉：第一项是"自动"，其余是系统里真实装了的语音
            _voice.Items.Clear();
            _voice.Items.Add(VoiceAuto);
            foreach (var v in WeatherWarn.InstalledVoices()) _voice.Items.Add(v);
            _voice.SelectedIndex = 0;
            string want = WeatherWarn.GetVoice();
            if (want.Length > 0)
            {
                for (int i = 0; i < _voice.Items.Count; i++)
                    if (string.Equals(_voice.Items[i]?.ToString() ?? "", want, StringComparison.OrdinalIgnoreCase)) { _voice.SelectedIndex = i; break; }
            }

            _rows.Clear(); _windowList.Children.Clear();
            foreach (var n in WeatherWarn.GetWindows()) AddWindowRow(n as JsonObject);

            RefreshCacheView();
            SetStatus(_enabled ? "预警订阅已启用" : "预警订阅已停用（不会拉取，编号 1000 也不会播报）",
                _enabled ? Theme.Success : Theme.Warn);
        }

        void PaintEnable()
        {
            _enableText.Text = _enabled ? "已启用" : "已停用";
            _enableBtn.Background = _enabled ? Theme.Success : new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB1));
            _enableBtn.Foreground = Brushes.White;
            _enableBtn.Padding = new Thickness(14, 6, 14, 6);
            _enableBtn.ToolTip = _enabled ? "点击停用气象预警订阅" : "点击启用气象预警订阅";
        }

        void SaveConfig()
        {
            try
            {
                WeatherWarn.SetEnabled(_enabled);
                WeatherWarn.SaveSetting("list_url", JsonValue.Create((_listUrl.Text ?? "").Trim()));
                WeatherWarn.SaveSetting("detail_url", JsonValue.Create((_detailUrl.Text ?? "").Trim()));
                WeatherWarn.SaveSetting("place", JsonValue.Create((_place.Text ?? "").Trim()));
                WeatherWarn.SaveSetting("area_code", JsonValue.Create((_areaCode.Text ?? "").Trim()));
                WeatherWarn.SaveSetting("town", JsonValue.Create((_town.Text ?? "").Trim()));
                int iv = int.TryParse((_interval.Text ?? "").Trim(), out int ivv) ? ivv : WeatherWarn.DefaultIntervalMin;
                WeatherWarn.SaveSetting("interval_min", JsonValue.Create(iv < 0 ? 0 : iv));
                int pf = int.TryParse((_prefetch.Text ?? "").Trim(), out int pff) ? pff : WeatherWarn.DefaultPrefetchMin;
                WeatherWarn.SaveSetting("prefetch_min", JsonValue.Create(Math.Max(0, Math.Min(120, pf))));
                int rate = int.TryParse((_rate.Text ?? "").Trim(), out int rr) ? rr : 0;
                WeatherWarn.SaveSetting("rate", JsonValue.Create(Math.Max(-10, Math.Min(10, rate))));
                int rep = int.TryParse((_repeat.Text ?? "").Trim(), out int rp) ? rp : WeatherWarn.DefaultRepeat;
                WeatherWarn.SaveSetting("repeat", JsonValue.Create(Math.Max(1, Math.Min(5, rep))));
                WeatherWarn.SaveSetting("gain", JsonValue.Create(_gain));
                string voice = _voice.SelectedIndex <= 0 ? "" : (_voice.SelectedItem?.ToString() ?? "");
                WeatherWarn.SaveSetting("voice", JsonValue.Create(voice));
                PlayLog.Write("预警", "配置保存", "info",
                    detail: $"地区 {_place.Text}（区划码 {_areaCode.Text}，地名筛选 {_town.Text}）｜天气接口 {_listUrl.Text}｜预警接口 {_detailUrl.Text}"
                            + $"｜提前拉取 {_prefetch.Text} 分钟｜兜底轮询 {_interval.Text} 分钟｜音色 {(_voice.SelectedIndex <= 0 ? "自动" : _voice.SelectedItem?.ToString())}｜语速 {_rate.Text}"
                            + $"｜重复 {_repeat.Text} 遍｜响度归一 {(_gain ? "开" : "关")}｜订阅{(_enabled ? "启用" : "停用")}");
                RefreshCacheView();
                SetStatus("已保存。改完周期/开关会立刻生效，不用重启程序。", Theme.Success);
            }
            catch (Exception ex) { SetStatus("保存失败：" + ex.Message, Theme.Danger); }
        }

        void SaveWindows()
        {
            try
            {
                var arr = new JsonArray();
                foreach (var r in _rows)
                {
                    if (r.Days.Count == 0) { SetStatus("有节点没有选星期，已取消保存", Theme.Danger); return; }
                    if (!IsHhMm(r.Start.Text) || !IsHhMm(r.End.Text))
                    {
                        SetStatus("时间格式不对：" + r.Start.Text + " / " + r.End.Text + "，请写成 HH:mm（例如 07:30）", Theme.Danger);
                        return;
                    }
                    var days = new JsonArray();
                    foreach (var d in r.Days.Distinct().OrderBy(x => x)) days.Add(d);
                    arr.Add(new JsonObject { ["days"] = days, ["start"] = r.Start.Text.Trim(), ["end"] = r.End.Text.Trim() });
                }
                WeatherWarn.SaveWindows(arr);
                WeatherWarn.SaveSetting("follow_tiaoxiu", JsonValue.Create(_follow));
                PlayLog.Write("预警", "预报节点保存", "info",
                    detail: "共 " + arr.Count + " 个节点（跟随调休规则：" + (_follow ? "开" : "关") + "）："
                            + string.Join("；", arr.Select(n => WeatherWarn.DayText(n["days"] as JsonArray) + " " + n["start"] + "-" + n["end"])));
                RefreshCacheView();
                SetStatus("预报节点已保存（共 " + arr.Count + " 个）", Theme.Success);
            }
            catch (Exception ex) { SetStatus("保存节点失败：" + ex.Message, Theme.Danger); }
        }

        static bool IsHhMm(string s) => Regex.IsMatch((s ?? "").Trim(), @"^([01]?\d|2[0-3]):[0-5]\d$");

        /// <summary>
        /// 把本页在无界面状态下渲染成一张 PNG（走真实布局代码），供人工/自动核对版式。
        /// 由 `BroadcastSystemNet.exe --warn-page-preview [输出路径] [宽度]` 调用；不进屏、不联网、不改数据。
        /// </summary>
        public static string RenderPreview(string outPath, int width = 1000, int height = 1150)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warn-page.png");
            if (width < 600) width = 600;
            if (height < 600) height = 600;

            var page = new WarnPage(null).RenderPage();
            var hostGrid = new Grid { Width = width, Height = height, Background = Brushes.White };
            hostGrid.Children.Add(page);
            hostGrid.Measure(new System.Windows.Size(width, height));
            hostGrid.Arrange(new Rect(0, 0, width, height));
            hostGrid.UpdateLayout();

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(hostGrid);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
            return System.IO.Path.GetFullPath(outPath);
        }

        // ---------------- 预报节点行 ----------------

        void AddWindowRow(JsonObject w)
        {
            var r = new WindowRow();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            string[] names = { "一", "二", "三", "四", "五", "六", "日" };
            var dayPanel = new StackPanel { Orientation = Orientation.Horizontal, Width = 300 };
            if (w?["days"] is JsonArray da)
                foreach (var d in da) { try { r.Days.Add(d.GetValue<int>()); } catch { } }
            for (int i = 1; i <= 7; i++)
            {
                int day = i;
                var b = Theme.Button("周" + names[i - 1], null, null, null);
                b.Padding = new Thickness(7, 4, 7, 4);
                b.Margin = new Thickness(0, 0, 3, 0);
                b.FontSize = 12;
                b.ToolTip = "点一下选中/取消 周" + names[i - 1];
                Action paint = () =>
                {
                    bool on = r.Days.Contains(day);
                    b.Background = on ? Theme.Primary : new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xF6));
                    b.Foreground = on ? Brushes.White : Theme.TextDark;
                };
                b.Click += (s, e) => { if (r.Days.Contains(day)) r.Days.Remove(day); else r.Days.Add(day); paint(); };
                paint();
                dayPanel.Children.Add(b);
            }
            panel.Children.Add(dayPanel);

            r.Start = Theme.TextBox(w?["start"]?.ToString() ?? "07:30", 80);
            r.Start.ToolTip = "开始时间，HH:mm，例如 07:30";
            r.Start.Margin = new Thickness(6, 0, 10, 0);
            panel.Children.Add(r.Start);
            r.End = Theme.TextBox(w?["end"]?.ToString() ?? "12:00", 80);
            r.End.ToolTip = "结束时间，HH:mm，例如 12:00；早于开始时间表示跨零点";
            r.End.Margin = new Thickness(0, 0, 10, 0);
            panel.Children.Add(r.End);

            var del = Theme.Button("", Theme.Danger, Brushes.White, "\uf1f8", () =>
            {
                _windowList.Children.Remove(panel);
                _rows.Remove(r);
            });
            del.ToolTip = "删除这个节点";
            del.Padding = new Thickness(9, 5, 9, 5);
            panel.Children.Add(del);

            r.Panel = panel;
            _rows.Add(r);
            _windowList.Children.Add(panel);
        }

        // ---------------- 抓取与试听 ----------------

        async System.Threading.Tasks.Task FetchNowAsync()
        {
            SetStatus("正在拉取…", Theme.TextGray);
            try
            {
                string msg = await WeatherWarn.FetchAndCacheAsync();
                Program.MarkWarnFetched();
                RefreshCacheView();
                SetStatus(msg, msg.StartsWith("拉取失败") ? Theme.Danger : (msg.StartsWith("未配置") ? Theme.Warn : Theme.Success));
            }
            catch (Exception ex) { SetStatus("拉取失败：" + ex.Message, Theme.Danger); }
        }

        async System.Threading.Tasks.Task PreviewAsync(bool force = false)
        {
            try
            {
                var caches = WeatherWarn.GetCaches();
                if (caches.Count == 0)
                {
                    SetStatus("还没有预警数据，先点「立即拉取」", Theme.Warn);
                    return;
                }

                // 与实际播报完全一致：逐条判断（等级从高到低），通过的才合成并依次播
                var playable = new List<JsonObject>();
                var labels = new List<string>();
                var skipped = new List<string>();
                foreach (var c in caches.OrderByDescending(c => WeatherWarn.LevelRank(c["signallevel"]?.ToString())))
                {
                    string label = (c["signaltype"]?.ToString() ?? "") + (c["signallevel"]?.ToString() ?? "");
                    if (WeatherWarn.Evaluate(c, WeatherWarn.GetWindows(), DateTime.Now, out string why, out _)) { playable.Add(c); labels.Add(label); }
                    else skipped.Add(label + "：" + why);
                }
                if (playable.Count == 0)
                {
                    SetStatus("没有可播报的预警 —— " + string.Join("；", skipped), Theme.Warn);
                    return;
                }

                SetStatus("正在离线合成 " + playable.Count + " 条（" + string.Join("、", labels) + "）…", Theme.TextGray);
                var files = new List<string>();
                foreach (var c in playable)
                {
                    var one = await System.Threading.Tasks.Task.Run(() => { string e; return WeatherWarn.PrepareOne(c, force, out e); });
                    files.AddRange(one);
                }
                if (files.Count == 0) { SetStatus("语音合成失败，检查系统是否装了中文语音（SAPI）", Theme.Danger); return; }

                string suffix = skipped.Count > 0 ? "（跳过 " + string.Join("；", skipped) + "）" : "";
                SetStatus("试听 " + playable.Count + " 条：" + string.Join("、", labels)
                          + "（响度归一" + (WeatherWarn.GetGain() ? "开" : "关") + "，重复 " + WeatherWarn.GetRepeat() + " 遍，满音量）" + suffix, Theme.Success);

                // 依次播完，不叠加
                await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var f in files)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = Program.FfplayPath,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            psi.ArgumentList.Add("-nodisp");
                            psi.ArgumentList.Add("-autoexit");
                            psi.ArgumentList.Add("-loglevel");
                            psi.ArgumentList.Add("quiet");
                            psi.ArgumentList.Add(f);
                            using (var p = Process.Start(psi))
                            {
                                // 【关键修复】同定时/手动播放：起播后立刻把该应用的会话音量拉回 100%。
                                // 否则若之前"停用广播"的渐弱把 ffplay 的按应用音量写成了 0%，
                                // 这里试听会同样没声音（进程在跑但听不到）。
                                try { if (p != null) Program.ForceProcessVolumeSoon(p, "预警试听"); } catch { }   // 带重试
                                p?.WaitForExit();
                            }
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex) { SetStatus("试听失败：" + ex.Message, Theme.Danger); }
        }

        // ---------------- 显示 ----------------

        void RefreshCacheView()
        {
            try
            {
                var caches = WeatherWarn.GetCaches();
                if (caches.Count == 0)
                {
                    bool noArea = string.IsNullOrWhiteSpace(WeatherWarn.GetAreaCode());
                    bool noPlace = string.IsNullOrWhiteSpace(WeatherWarn.GetPlace());
                    string hint = "";
                    if (noArea || noPlace)
                        hint = "还没填" + (noPlace ? "地区名" : "") + (noPlace && noArea ? " 和 " : "") + (noArea ? "区划码" : "")
                             + "：中央气象台接口要按区划码筛本地预警，不填就拉不到本地数据；填好后点上面的「保存」。\n"
                             + "（程序不带任何地区默认值——这两个值只存在你自己的配置文件里）\n\n";
                    _cacheText.Text = hint + "还没有拉到预警数据。拉取时间：" + (string.IsNullOrWhiteSpace(WeatherWarn.GetLastOk()) ? "（从未成功）" : WeatherWarn.GetLastOk())
                                    + (string.IsNullOrWhiteSpace(WeatherWarn.GetLastError()) ? "" : "\n上次失败：" + WeatherWarn.GetLastError());
                    _cacheText.Foreground = hint.Length > 0 ? Theme.Warn : Theme.TextGray;
                    _judgeText.Text = "";
                    _speechText.Text = "";
                    _bodyText.Text = "";
                    return;
                }

                var ordered = caches.OrderByDescending(c => WeatherWarn.LevelRank(c["signallevel"]?.ToString())).ToList();
                var cacheLines = new List<string>();
                var judgeLines = new List<string>();
                var speechLines = new List<string>();
                var bodyLines = new List<string>();
                bool anyPlay = false;

                foreach (var c in ordered)
                {
                    bool resolved = c["resolved"]?.GetValue<bool>() ?? false;
                    DateTime expect = WeatherWarn.GetExpect(c, out bool estimated);
                    string head = $"{c["signaltype"]}{c["signallevel"]}预警｜编号 {c["id"]}｜生效 {c["effective"]}｜预计 {expect:MM-dd HH:mm}"
                                + (estimated ? "（估算）" : "") + (resolved ? "｜已解除（" + c["resolved_at"] + "）" : "");
                    cacheLines.Add(head);

                    bool ok = WeatherWarn.Evaluate(c, WeatherWarn.GetWindows(), DateTime.Now, out string reason, out _);
                    if (ok) anyPlay = true;
                    judgeLines.Add((ok ? "可以播报 → " : "不播报 → ") + reason);

                    speechLines.Add(WeatherWarn.BuildSpeech(c));
                    bodyLines.Add(c["text"]?.ToString() ?? "");
                }

                _cacheText.Foreground = Theme.TextDark;
                _cacheText.Text = string.Join("\n", cacheLines)
                                + "\n拉取于 " + (ordered.FirstOrDefault()?["fetched"]?.ToString() ?? WeatherWarn.GetLastOk());

                _judgeText.Foreground = anyPlay ? Theme.Success : Theme.Warn;
                _judgeText.Text = "判断结果（按等级排序，红→橙→黄→蓝）：\n· " + string.Join("\n· ", judgeLines);

                _speechText.Text = string.Join("\n", speechLines);
                _speechText.Foreground = Theme.TextDark;
                _bodyText.Text = string.Join("\n\n", bodyLines);
            }
            catch (Exception ex) { _cacheText.Text = "读取预警缓存失败：" + ex.Message; }
        }
    }
}
