#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>悬浮球状态（由主窗口提供，1 秒刷新一次）。</summary>
    public class BallState
    {
        public bool Enabled;          // 广播系统是否启用
        public bool Scheduled;        // 今日是否还有排程（绿）
        public DateTime? NextRing;    // 下一个响铃时间点（今天之内；无则 null）
    }

    /// <summary>
    /// 桌面悬浮球：整球底色三色（红=停用/故障、蓝=待机、绿=今日有排程），中央是白色 Font Awesome 图标，
    /// 下方显示距下一个响铃的剩余时间。可拖动并记住位置；平时半透明、悬停变实心；右键弹与托盘相同菜单；左键配置自检。
    /// 另外：只要出现"任务引用的音乐失效 / 文件不存在 / 启动播放失败"这类会导致铃声不响的问题，球立刻变红并说明原因
    /// （数据取自应用自己的告警接口 /api/alerts，不依赖主窗口内部状态）。
    /// </summary>
    public class FloatingBall : Window
    {
        public const double BallSize = 64;

        /// <summary>是否正在被拖动。主窗口与本球的周期性刷新在拖动期间让路，避免抢 UI 线程导致"不跟手"。</summary>
        public static volatile bool Dragging;

        // 会导致"铃声不响"的关键词（与告警文案对应）
        private static readonly string[] FatalKeywords = { "文件不存在", "启动播放失败", "已失效" };

        private readonly Func<BallState> _state;
        private readonly Action _onSelfCheck;
        private readonly Action _onRightClick;
        private readonly Action _onOpenMain;
        private readonly Ellipse _disc = new Ellipse();
        private readonly TextBlock _icon = new TextBlock();

        // 倒计时扇形图：沿球的内边缘画一圈，一整圈 = 60 分钟，剩余越少扇形越短
        private const double RingRadius = 29;
        private const double RingThickness = 3.5;
        private const double RingFullMinutes = 60;
        private readonly Ellipse _ringTrack = new Ellipse();
        private readonly Path _ringArc = new Path();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private bool _dragging;
        private bool _pressed;
        private Point _pressScreen;
        private Point _pressWindow;

        // 单击/双击区分：按下后先等"系统双击间隔"，期间没有第二次点击才算单击（自检），
        // 否则算双击（打开主界面）。间隔取系统设置（默认 500ms），与资源管理器手感一致。
        private readonly DispatcherTimer _clickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, System.Windows.Forms.SystemInformation.DoubleClickTime))
        };
        private bool _singlePending;
        private string _fatalReason;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public FloatingBall(Func<BallState> stateProvider, Action onSelfCheck, Action onRightClickMenu, Action onOpenMain = null)
        {
            _state = stateProvider;
            _onSelfCheck = onSelfCheck;
            _onRightClick = onRightClickMenu;
            _onOpenMain = onOpenMain;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = BallSize;
            Height = BallSize;
            Opacity = 0.62;
            Cursor = Cursors.Hand;

            var pos = LoadPosition();
            Left = pos.Item1; Top = pos.Item2;

            _disc.Width = BallSize; _disc.Height = BallSize;
            _disc.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB));
            _disc.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 1.5,
                Opacity = 0.30,
                Color = Colors.Black
            };
            var root = new Grid();
            root.Children.Add(_disc);

            // 倒计时扇形图：底圈（淡淡的整圈）+ 剩余时间的扇形
            _ringTrack.Width = _ringTrack.Height = RingRadius * 2;
            _ringTrack.StrokeThickness = RingThickness;
            _ringTrack.Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            _ringTrack.HorizontalAlignment = HorizontalAlignment.Center;
            _ringTrack.VerticalAlignment = VerticalAlignment.Center;
            _ringTrack.IsHitTestVisible = false;
            root.Children.Add(_ringTrack);

            _ringArc.Width = _ringArc.Height = BallSize;
            _ringArc.Stretch = Stretch.None;          // 固定用 32,32 为圆心，扇形长短不影响位置
            _ringArc.StrokeThickness = RingThickness;
            _ringArc.Stroke = Brushes.White;
            _ringArc.StrokeStartLineCap = PenLineCap.Round;
            _ringArc.StrokeEndLineCap = PenLineCap.Round;
            _ringArc.HorizontalAlignment = HorizontalAlignment.Center;
            _ringArc.VerticalAlignment = VerticalAlignment.Center;
            _ringArc.IsHitTestVisible = false;
            root.Children.Add(_ringArc);

            // 中央白色图标（Font Awesome，绝不用 Emoji）
            _icon.FontFamily = Theme.IconFont ?? new FontFamily("Segoe UI Symbol");
            _icon.FontSize = 23;
            _icon.Foreground = Brushes.White;
            _icon.HorizontalAlignment = HorizontalAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _icon.IsHitTestVisible = false;
            root.Children.Add(_icon);

            Content = root;

            MouseEnter += (s, e) => Opacity = 1.0;
            MouseLeave += (s, e) => Opacity = 0.62;

            // 左键：拖动记位置 / 轻点自检。不用 DragMove()——它会阻塞到松手并把点击吞成拖动，
            // 结果"点球看自检"经常没反应（历史问题）。这里用 2 像素阈值自己判定。
            // 跟手要点：① 阈值小（超过 2px 立刻按整段位移移动）；② 显式捕获鼠标（拖到窗口外也不丢事件）；
            //           ③ 拖动期间卸掉投影（透明分层窗口每次移动都要重新合成，投影模糊最费）；
            //           ④ 拖动期间不让主窗口/本球做周期性刷新（详见 Dragging 标记）。
            MouseLeftButtonDown += (s, e) =>
            {
                _pressScreen = CursorPoint();
                _pressWindow = new Point(Left, Top);
                _pressed = true;
                _dragging = false;
                try { CaptureMouse(); } catch { }
            };
            MouseMove += (s, e) =>
            {
                if (!_pressed) return;
                Point p = CursorPoint();
                double dx = p.X - _pressScreen.X, dy = p.Y - _pressScreen.Y;
                if (!_dragging)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) < 2) return;      // 2 像素以内算点击
                    _dragging = true;
                    Dragging = true;
                    try { _disc.Effect = null; } catch { }           // 拖动期间去掉投影，移动更顺
                    Opacity = 1.0;
                }
                Left = _pressWindow.X + dx;
                Top = _pressWindow.Y + dy;
            };
            MouseLeftButtonUp += (s, e) =>
            {
                if (!_pressed) return;
                _pressed = false;
                try { ReleaseMouseCapture(); } catch { }
                if (_dragging)
                {
                    _dragging = false;
                    Dragging = false;
                    RestoreDiscShadow();
                    SavePosition();
                }
                else
                {
                    // 区分单击/双击：第二次点击到来就取消待发的单击动作
                    if (_singlePending)
                    {
                        _singlePending = false;
                        _clickTimer.Stop();
                        try { _onOpenMain?.Invoke(); } catch { }
                    }
                    else
                    {
                        _singlePending = true;
                        _clickTimer.Start();
                    }
                }
            };
            _clickTimer.Tick += (s, e) =>
            {
                _clickTimer.Stop();
                if (!_singlePending) return;
                _singlePending = false;
                try { _onSelfCheck?.Invoke(); } catch { }
            };
            MouseRightButtonUp += (s, e) => { try { _onRightClick?.Invoke(); } catch { } };

            _timer.Tick += (s, e) => Refresh();
            _alertTimer.Tick += async (s, e) => await PollFatalAsync();
            Loaded += async (s, e) => { Refresh(); _timer.Start(); _alertTimer.Start(); await PollFatalAsync(); };
            Closed += (s, e) => { _timer.Stop(); _alertTimer.Stop(); _clickTimer.Stop(); };
        }

        /// <summary>恢复球的投影（拖动结束后调用）。</summary>
        void RestoreDiscShadow()
        {
            try
            {
                _disc.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 1.5,
                    Opacity = 0.30,
                    Color = Colors.Black
                };
            }
            catch { }
        }

        /// <summary>按当前状态刷新底色、图标、倒计时与提示。</summary>
        public void Refresh()
        {
            if (Dragging) return;      // 拖动中不重算、不重绘，保证跟手
            BallState st;
            try { st = _state?.Invoke() ?? new BallState(); } catch { st = new BallState(); }

            bool fatal = !string.IsNullOrEmpty(_fatalReason);

            if (fatal)                       // 会导致铃声不响 → 红底 + 警告图标
            {
                _disc.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                _icon.Text = "\uf071";       // triangle-exclamation
            }
            else if (!st.Enabled)            // 已停用 → 红底 + 电源图标
            {
                _disc.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                _icon.Text = "\uf011";       // power-off
            }
            else if (st.Scheduled)           // 今日有排程 → 绿底 + 播放图标
            {
                _disc.Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
                _icon.Text = "\uf04b";       // play
            }
            else                             // 运行中但今日无排程 → 蓝底 + 铃铛
            {
                _disc.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB));
                _icon.Text = "\uf0f3";       // bell
            }

            // 扇形倒计时：一整圈 = 60 分钟；剩余时间越多扇形越长，超过 60 分钟就是整圈
            double? frac = null;
            if (!fatal && st.Enabled && st.NextRing.HasValue)
            {
                double mins = (st.NextRing.Value - DateTime.Now).TotalMinutes;
                if (mins < 0) mins = 0;
                frac = mins / RingFullMinutes;
                if (frac > 1) frac = 1;
            }
            _ringArc.Data = frac.HasValue && frac.Value > 0.004 ? BuildArc(frac.Value) : null;
            _ringArc.Opacity = (fatal || !st.Enabled) ? 0.0 : 1.0;
            _ringTrack.Opacity = (fatal || !st.Enabled) ? 0.35 : 1.0;

            if (fatal)
            {
                ToolTip = "【严重】" + _fatalReason + "\n这会导致铃声不响，请尽快到「音乐库」或「任务排程」修正。"
                          + "\n左键单击：配置自检　左键双击：打开主界面　右键：菜单";
            }
            else if (!st.Enabled)
            {
                ToolTip = "已停用（全局静态）\n左键单击：配置自检　左键双击：打开主界面　右键：菜单";
            }
            else
            {
                ToolTip = (st.Scheduled ? "运行中 · 今日有排程" : "运行中 · 今日无排程")
                          + (st.NextRing.HasValue
                                ? $"\n下一次排程 {st.NextRing.Value:HH:mm}（剩余 {RemainText(st.NextRing.Value)}）"
                                : "\n今天没有后续排程")
                          + "\n白色扇形 = 倒计时，一整圈 60 分钟"
                          + "\n左键单击：配置自检　左键双击：打开主界面　右键：菜单";
            }
        }

        /// <summary>剩余时间文字（气泡上不显示数字，只在提示里给准确值）。</summary>
        private static string RemainText(DateTime next)
        {
            TimeSpan span = next - DateTime.Now;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{span.Minutes} 分 {span.Seconds:00} 秒";
        }

        /// <summary>把剩余时间比例画成扇形（从 12 点方向顺时针）。</summary>
        private static PathGeometry BuildArc(double fraction)
        {
            double f = fraction <= 0 ? 0 : (fraction > 1 ? 1 : fraction);
            if (f <= 0) return null;
            if (f > 0.9995) f = 0.9995;              // 整圈用一段弧画不出来，留一线缺口
            double sweep = 360.0 * f;
            double cx = BallSize / 2.0;
            double cy = BallSize / 2.0;
            var fig = new PathFigure { StartPoint = RingPoint(cx, cy, -90.0), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = RingPoint(cx, cy, -90.0 + sweep),
                Size = new System.Windows.Size(RingRadius, RingRadius),
                IsLargeArc = sweep > 180.0,
                SweepDirection = SweepDirection.Clockwise
            });
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            return pg;
        }

        private static Point RingPoint(double cx, double cy, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(cx + RingRadius * Math.Cos(rad), cy + RingRadius * Math.Sin(rad));
        }


        /// <summary>
        /// 从应用自己的告警接口里找"会导致铃声不响"的问题（音乐引用失效 / 文件不存在 / 启动播放失败）。
        /// 命中就置红；接口里没有了就恢复常规三色。
        /// </summary>
        private async System.Threading.Tasks.Task PollFatalAsync()
        {
            try
            {
                string url = "http://127.0.0.1:" + Program.ListenPort + "/api/alerts";
                string body = await Http.GetStringAsync(url);
                string hit = null;
                foreach (var kw in FatalKeywords)
                {
                    if (body.IndexOf(kw, StringComparison.Ordinal) >= 0) { hit = kw; break; }
                }
                _fatalReason = hit == null ? null : "有任务找不到音乐或播放失败（" + hit + "）";
                Refresh();
            }
            catch { /* 接口不可达时保持现状，避免误报 */ }
        }

        /// <summary>
        /// 把悬浮球的扇形倒计时画成一张预览图（6 个不同剩余时间并排），供人工核对扇形长度。
        /// 由 `BroadcastSystemNet.exe --ball-preview [输出路径]` 调用；纯本地绘制，不联网、不改数据。
        /// </summary>
        public static string WriteStatePreview(string outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ball-states.png");

            var samples = new[] { 90.0, 45.0, 30.0, 15.0, 5.0, 1.0 };
            const double gap = 14, capH = 20;
            int n = samples.Length;
            double w = gap + n * (BallSize + gap);
            double h = gap + BallSize + 4 + capH + gap;

            var host = new Grid
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x36))
            };

            for (int i = 0; i < n; i++)
            {
                double minutes = samples[i];
                var ball = new FloatingBall(
                    () => new BallState { Enabled = true, Scheduled = true, NextRing = DateTime.Now.AddMinutes(minutes) },
                    null, null);
                ball.Refresh();                       // 构造时不刷新，这里按假状态刷一次
                var visual = ball.Content as FrameworkElement;
                ball.Content = null;                  // 取出来单独排版，避免窗口尺寸影响
                if (visual != null)
                {
                    visual.Width = BallSize;
                    visual.Height = BallSize;
                    visual.HorizontalAlignment = HorizontalAlignment.Left;
                    visual.VerticalAlignment = VerticalAlignment.Top;
                    visual.Margin = new Thickness(gap + i * (BallSize + gap), gap, 0, 0);
                    host.Children.Add(visual);
                }
                host.Children.Add(new TextBlock
                {
                    Text = minutes >= 60 ? "≥60 分（整圈）" : minutes.ToString("0") + " 分钟",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Width = BallSize,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(gap + i * (BallSize + gap), gap + BallSize + 4, 0, 0)
                });
            }

            host.Measure(new System.Windows.Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();

            const int scale = 3;                       // 放大 3 倍渲染，方便人工看清扇形长度
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
            return System.IO.Path.GetFullPath(outPath);
        }

        // ---------- 位置记忆（存到 setting.jsonl 的 global 记录里） ----------
        static Tuple<double, double> LoadPosition()
        {
            double x = SystemParameters.WorkArea.Right - BallSize - 24;
            double y = SystemParameters.WorkArea.Bottom - BallSize - 24;
            try
            {
                foreach (var n in Program.ReadSettingMerged())
                {
                    if (n is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                    double bx, by;
                    if (double.TryParse(o["ball_x"]?.ToString(), out bx) && double.TryParse(o["ball_y"]?.ToString(), out by))
                    {
                        if (bx > SystemParameters.VirtualScreenLeft - BallSize && bx < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                            by > SystemParameters.VirtualScreenTop - BallSize && by < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                            return Tuple.Create(bx, by);
                    }
                    break;
                }
            }
            catch { }
            return Tuple.Create(x, y);
        }

        /// <summary>光标位置换算成 WPF 逻辑坐标（WinForms 给的是物理像素，高 DPI 下必须除缩放比）。</summary>
        Point CursorPoint()
        {
            try
            {
                var scr = System.Windows.Forms.Control.MousePosition;
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Point(scr.X / dpi.DpiScaleX, scr.Y / dpi.DpiScaleY);
            }
            catch { return new Point(Left, Top); }
        }

        void SavePosition()
        {
            try
            {
                var all = Program.ReadSettingMerged();
                foreach (var n in all)
                {
                    if (n is not JsonObject o || o["_type"]?.ToString() != "global") continue;
                    o["ball_x"] = Math.Round(Left);
                    o["ball_y"] = Math.Round(Top);
                    Program.WriteSettingMerged( all);
                    break;
                }
            }
            catch { }
        }
    }

    /// <summary>配置自检：只读本地配置，逐项检查并返回可读结论（不改任何配置）。</summary>
    public static class ConfigSelfCheck
    {
        public class Item
        {
            public bool Ok;
            public string Level;   // 通过 / 警告 / 错误
            public string Text;
        }

        public static List<Item> Run()
        {
            var list = new List<Item>();
            void Add(bool ok, string level, string text) => list.Add(new Item { Ok = ok, Level = level, Text = text });

            foreach (var f in new[] { "setting.jsonl", "tasks.jsonl", "music.jsonl" })
            {
                try
                {
                    var arr = Program.ReadJsonl(f);
                    Add(true, "通过", f + "：可解析，" + arr.Count + " 条");
                }
                catch (Exception ex)
                {
                    Add(false, "错误", f + "：解析失败 — " + ex.Message);
                }
            }

            JsonArray tasks, music;
            try { tasks = Program.ReadJsonl("tasks.jsonl"); } catch { tasks = new JsonArray(); }
            try { music = Program.ReadJsonl("music.jsonl"); } catch { music = new JsonArray(); }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dupIds = 0, emptyPath = 0, missing = 0;
            var musicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in music)
            {
                if (n is not JsonObject m) continue;
                string id = m["id"]?.ToString() ?? "";
                if (id.Length > 0) { if (!ids.Add(id)) dupIds++; musicIds.Add(id); }
                string path = m["path"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(path)) { emptyPath++; continue; }
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                string full = System.IO.Path.IsPathRooted(path)
                    ? path
                    : System.IO.Path.Combine(Program.AppDataDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(full) && !Directory.Exists(full)) missing++;
            }
            Add(dupIds == 0, dupIds == 0 ? "通过" : "错误", dupIds == 0 ? "音乐编号无重复" : "有 " + dupIds + " 个重复编号");
            Add(emptyPath == 0, emptyPath == 0 ? "通过" : "警告", emptyPath == 0 ? "音乐路径都已填写" : "有 " + emptyPath + " 条音乐没填路径");
            Add(missing == 0, missing == 0 ? "通过" : "错误", missing == 0 ? "音乐文件都在" : "有 " + missing + " 条音乐的文件找不到（到点不会响）");

            int badTime = 0, badVol = 0, noCycle = 0, badRef = 0, taskCount = 0;
            foreach (var n in tasks)
            {
                if (n is not JsonObject t) continue;
                taskCount++;
                TimeSpan ts;
                if (!TimeSpan.TryParse(t["start_time"]?.ToString() ?? "", out ts)) badTime++;
                int vol; if (!int.TryParse(t["volume"]?.ToString(), out vol) || vol < 0 || vol > 100) badVol++;
                var days = t["days"] as JsonArray;
                var dates = t["specific_dates"] as JsonArray;
                if ((days == null || days.Count == 0) && (dates == null || dates.Count == 0)) noCycle++;
                var mids = t["music_ids"] as JsonArray;
                if (mids != null)
                {
                    foreach (var x in mids)
                    {
                        string id = x?.ToString() ?? "";
                        if (id.Length == 0) continue;
                        // 保留编号 1000（气象预警播报）是虚拟条目，不在 music.jsonl 里，但它是合法的，
                        // 不能算"引用了已删除的音乐"（否则一加预警任务就报误报）。
                        if (string.Equals(id, WeatherWarn.MusicId.ToString(), StringComparison.Ordinal)) continue;
                        if (!musicIds.Contains(id)) badRef++;
                    }
                }
            }
            Add(badTime == 0, badTime == 0 ? "通过" : "错误", badTime == 0 ? "任务共 " + taskCount + " 条，开始时间都合法" : "有 " + badTime + " 条任务的开始时间不合法");
            Add(badVol == 0, badVol == 0 ? "通过" : "警告", badVol == 0 ? "任务音量都在 0-100" : "有 " + badVol + " 条任务音量越界");
            Add(noCycle == 0, noCycle == 0 ? "通过" : "警告", noCycle == 0 ? "任务都有播放周期或特定日期" : "有 " + noCycle + " 条任务既没勾星期也没填特定日期（永远不会触发）");
            Add(badRef == 0, badRef == 0 ? "通过" : "错误", badRef == 0 ? "任务引用的音乐编号都存在" : "有 " + badRef + " 处任务引用了已删除的音乐编号（到点不会响）");

            // 气象预警播报（保留编号 1000）单独给一条状态：它不是"已删除的音乐"，也不会因为误报把自检弄红
            try
            {
                bool warnUsed = false;
                foreach (var n in tasks)
                {
                    if (n is not JsonObject t) continue;
                    var mw = t["music_ids"] as JsonArray;
                    if (mw == null) continue;
                    foreach (var x in mw)
                        if (string.Equals(x?.ToString() ?? "", WeatherWarn.MusicId.ToString(), StringComparison.Ordinal)) { warnUsed = true; break; }
                    if (warnUsed) break;
                }
                if (warnUsed)
                {
                    var wc = WeatherWarn.GetCache();
                    if (!WeatherWarn.GetEnabled())
                        Add(false, "警告", "有任务引用了气象预警播报（编号 1000），但预警订阅处于「已停用」，到点不会播报");
                    else if (wc == null || string.IsNullOrWhiteSpace(wc["id"]?.ToString()))
                        Add(false, "警告", "有任务引用了气象预警播报（编号 1000），但还没拉到预警数据"
                            + (string.IsNullOrWhiteSpace(WeatherWarn.GetLastError()) ? "" : "（上次失败：" + WeatherWarn.GetLastError() + "）"));
                    else
                    {
                        bool canPlay = WeatherWarn.Evaluate(wc, WeatherWarn.GetWindows(), DateTime.Now, out string why, out _);
                        Add(true, "通过", "气象预警播报（编号 1000）：最新 " + wc["signaltype"] + wc["signallevel"]
                            + "，当前判断" + (canPlay ? "可以播报" : "不播报（" + why + "）"));
                    }
                }
            }
            catch { }

            int subInvalid = 0, manualCnt = 0, subCnt = 0, clash = 0;
            var manualDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subDates = new List<string>();
            try
            {
                foreach (var n in Program.ReadSettingMerged())
                {
                    if (n is not JsonObject o) continue;
                    string tp = o["_type"]?.ToString();
                    if (tp == "sub_invalid") { subInvalid++; continue; }
                    if (tp != "tiaoxiu") continue;
                    string src = o["source"]?.ToString();
                    string sd = o["start_date"]?.ToString() ?? o["date"]?.ToString() ?? "";
                    if (src == "manual") { manualCnt++; if (sd.Length > 0) manualDates.Add(sd); }
                    else if (src == "sub") { subCnt++; if (sd.Length > 0) subDates.Add(sd); }
                }
                clash = subDates.Count(d => manualDates.Contains(d));
            }
            catch { }
            Add(subInvalid == 0, subInvalid == 0 ? "通过" : "警告", subInvalid == 0 ? "没有无效的云端调休记录" : "有 " + subInvalid + " 条无效云端记录（调休管理页可清除）");
            Add(true, "通过", "调休记录：手动 " + manualCnt + " 条、云端 " + subCnt + " 条" + (clash > 0 ? "，其中 " + clash + " 天手动与云端同日（已按手动优先处理）" : ""));

            try
            {
                var urls = Program.GetSubscriptionUrls();
                Add(urls.Count > 0, urls.Count > 0 ? "通过" : "警告",
                    urls.Count > 0
                        ? "订阅组 " + urls.Count + " 条" + (Program.GetSubscriptionLastOk().Length > 0 ? "，上次成功的是：" + Program.GetSubscriptionLastOk() : "")
                        : "订阅组为空（云端订阅已关闭）");
            }
            catch { }

            return list;
        }

        /// <summary>把结果拼成可复制的报告文本。</summary>
        public static string ToText(List<Item> items)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("配置自检报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('-', 52));
            foreach (var it in items)
                sb.AppendLine("[" + it.Level + "] " + it.Text);
            int warn = items.Count(i => i.Level == "警告"), err = items.Count(i => i.Level == "错误");
            sb.AppendLine(new string('-', 52));
            sb.AppendLine("共 " + items.Count + " 项：错误 " + err + "、警告 " + warn + "、通过 " + (items.Count - err - warn) + "（本次只读，未修改任何配置）");
            return sb.ToString();
        }
    }
}
