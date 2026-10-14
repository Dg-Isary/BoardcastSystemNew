#nullable disable

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using ListBox = System.Windows.Controls.ListBox;
using Slider = System.Windows.Controls.Slider;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FontWeights = System.Windows.FontWeights;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>播控队列里的一项。</summary>
    public class QueueItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Display => (Index + 1) + ". " + (string.IsNullOrEmpty(Name) ? "(未命名)" : Name);
    }

    /// <summary>
    /// 播控台：手动播放（与网页端共用同一套播控状态 /api/player/cmd）。
    /// 定时打铃会抢占手动播放，这里的状态完全实时反映后端。
    /// </summary>
    public class PlayerPage : PageBase
    {
        private readonly ObservableCollection<QueueItem> _queue = new ObservableCollection<QueueItem>();
        private ListBox _queueList;
        private ListBox _libraryList;
        private TextBlock _nowPlaying, _progressText, _modeText, _volumeText, _queueHint;

        // ---- 可拖动进度条用到的状态 ----
        bool _dragging;                       // 正在拖动
        DateTime _lastSeekAt = DateTime.MinValue;
        double _lastDuration;                 // 最近一次已知时长（拖动时换算用）

        /// <summary>
        /// 把鼠标 X 换算成秒并跳转。[force]=true 表示按下/松手（立即发），false 表示拖动中（150ms 节流）。
        /// 没有时长（停止/无当前曲目）时不响应。
        /// </summary>
        void SeekByX(double x, double width, bool force)
        {
            if (width <= 1 || _lastDuration <= 0) return;
            double frac = Math.Max(0, Math.Min(1, x / width));
            try
            {
                _progress.Value = frac * 100;                                        // 界面立刻跟手（不等回执）
                _progressText.Text = Fmt(frac * _lastDuration) + " / " + Fmt(_lastDuration);
            }
            catch { }
            if (!force && (DateTime.UtcNow - _lastSeekAt).TotalMilliseconds < 150) return;
            _lastSeekAt = DateTime.UtcNow;
            try { _ = DesktopApi.PlayerSeek(frac * _lastDuration); } catch { }
        }
        private ProgressBar _progress;
        private Slider _volume;
        private Button _playPause;
        private JsonArray _music = new JsonArray();
        private bool _draggingVolume;
        private bool _playing;
        private int _lastVolume = -1;

        private static readonly Brush AccentBg = new SolidColorBrush(Color.FromRgb(0xEA, 0xF4, 0xFB));

        public PlayerPage(MainWindow host) : base(host, "播控台",
            "手动播放") { }

        protected override void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 播放器
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 队列 + 音乐库

            root.Children.Add(At(BuildPlayerCard(), 0));
            root.Children.Add(At(BuildQueueAndLibrary(), 1));
            FillHost.Children.Add(root);

            // 主窗口每 900ms 已经轮询过一次 /api/sys，这里直接复用，不再额外打接口
            Host.SysUpdated += OnSys;
            Root.Unloaded += (s, e) => Host.SysUpdated -= OnSys;

            SetStatus("正在读取播控状态…");
            _ = LoadLibraryAsync();
        }

        private static UIElement At(UIElement el, int row) { Grid.SetRow(el, row); return el; }

        private static Button Btn(string text, string icon, Action onClick, Brush bg = null, Brush fg = null, double fontSize = 13)
        {
            var b = Theme.Button(text, bg, fg, icon, onClick);
            b.FontSize = fontSize;
            b.Padding = new Thickness(14, 9, 14, 9);
            b.Margin = new Thickness(0, 0, 8, 0);
            return b;
        }

        // ================= 播放器卡片 =================

        private UIElement BuildPlayerCard()
        {
            var panel = new StackPanel();

            // 第一行：当前播放
            var nowRow = new StackPanel { Orientation = Orientation.Horizontal };
            nowRow.Children.Add(Theme.Icon("\uf028", 18, Theme.Primary));
            _nowPlaying = new TextBlock
            {
                Text = "当前没有手动播放",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nowRow.Children.Add(_nowPlaying);
            panel.Children.Add(nowRow);

            // 第二行：进度条
            _progress = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = Theme.Primary,
                Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xF2, 0xF7)),
                BorderThickness = new Thickness(0)
            };
            // 【可拖动进度条】外面套一层透明命中层接管鼠标：
            //   · 按下/移动 → 按位置换算秒数**实时跳转**（用户要求"跟手"），150ms 节流防止刷爆命令；
            //   · 松手 → 再补一次精确跳转；
            //   · 只在"播放中/暂停"（有时长）时可拖；停止状态没有当前曲目，拖了没意义。
            var progressHost = new Grid { Margin = new Thickness(0, 12, 0, 0), Height = 18, Background = Brushes.Transparent };
            _progress.Margin = new Thickness(0);
            _progress.VerticalAlignment = VerticalAlignment.Center;
            progressHost.Children.Add(_progress);
            var progressHit = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "按住拖动可跳转（播放中/暂停时可用）" };
            progressHost.Children.Add(progressHit);
            progressHit.MouseLeftButtonDown += (s, e) => { _dragging = true; progressHit.CaptureMouse(); SeekByX(e.GetPosition(progressHost).X, progressHost.ActualWidth, true); };
            progressHit.MouseMove += (s, e) => { if (_dragging) SeekByX(e.GetPosition(progressHost).X, progressHost.ActualWidth, false); };
            progressHit.MouseLeftButtonUp += (s, e) => { if (_dragging) { _dragging = false; try { progressHit.ReleaseMouseCapture(); } catch { } SeekByX(e.GetPosition(progressHost).X, progressHost.ActualWidth, true); } };
            panel.Children.Add(progressHost);
            _progressText = new TextBlock
            {
                Text = "00:00 / 00:00",
                FontSize = 11.5,
                Foreground = Theme.TextGray,
                Margin = new Thickness(0, 5, 0, 0),
                FontFamily = new FontFamily("Consolas")
            };
            panel.Children.Add(_progressText);

            // 第三行：传输控制
            var controls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            controls.Children.Add(Btn("上一首", "\uf048", () => Cmd(DesktopApi.PlayerPrev())));
            _playPause = Btn("播放", "\uf04b", TogglePlayPause, Theme.Primary, Brushes.White);
            controls.Children.Add(_playPause);
            controls.Children.Add(Btn("停止", "\uf04d", () => Cmd(DesktopApi.PlayerStop()), Theme.Danger, Brushes.White));
            controls.Children.Add(Btn("下一首", "\uf051", () => Cmd(DesktopApi.PlayerNext())));

            var spacer = new Border { Width = 18 };
            controls.Children.Add(spacer);

            _modeText = new TextBlock
            {
                Text = "模式：顺序播放",
                FontSize = 12.5,
                Foreground = Theme.TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            controls.Children.Add(_modeText);
            var modeBtn = Btn("切换模式", "\uf074", () => Cmd(DesktopApi.PlayerCycleMode()));
            modeBtn.Padding = new Thickness(12, 9, 12, 9);
            controls.Children.Add(modeBtn);

            controls.Children.Add(new Border { Width = 18 });

            // 音量整组放在一个横向 StackPanel 里，换行时整体换行，避免"100%"掉到下一行最左边
            var volumeGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            volumeGroup.Children.Add(new TextBlock
            {
                Text = "音量",
                FontSize = 12.5,
                Foreground = Theme.TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _volume = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                Width = 140,
                VerticalAlignment = VerticalAlignment.Center,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 5
            };
            volumeGroup.Children.Add(_volume);
            _volume.PreviewMouseLeftButtonDown += (s, e) => _draggingVolume = true;
            _volume.PreviewMouseLeftButtonUp += (s, e) =>
            {
                _draggingVolume = false;
                Cmd(DesktopApi.PlayerVolume((int)_volume.Value));
            };
            _volumeText = new TextBlock
            {
                Text = "100%",
                FontSize = 12,
                Foreground = Theme.TextGray,
                Width = 46,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                FontFamily = new FontFamily("Consolas")
            };
            volumeGroup.Children.Add(_volumeText);
            _volume.ValueChanged += (s, e) =>
            {
                _volumeText.Text = (int)_volume.Value + "%";
                _lastVolume = (int)_volume.Value;
            };
            controls.Children.Add(volumeGroup);
            panel.Children.Add(controls);

            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 16),
                Margin = new Thickness(0, 0, 0, 10),
                Child = panel
            };
        }

        // ================= 队列 + 音乐库 =================

        private UIElement BuildQueueAndLibrary()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左：播放队列（用 Grid 而不是 DockPanel，避免底栏按钮被列表挤掉）
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var leftHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            leftHead.Children.Add(new TextBlock { Text = "播放队列", FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, VerticalAlignment = VerticalAlignment.Center });
            _queueHint = new TextBlock { Text = "", FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            leftHead.Children.Add(_queueHint);
            Grid.SetRow(leftHead, 0);
            left.Children.Add(leftHead);

            var queueButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            queueButtons.Children.Add(Btn("播放选中", "\uf04b", PlaySelectedInQueue, Theme.Primary, Brushes.White));
            queueButtons.Children.Add(Btn("移出队列", "\uf2ed", RemoveFromQueue));
            queueButtons.Children.Add(Btn("清空队列", "\uf12d", ClearQueue));
            Grid.SetRow(queueButtons, 2);
            left.Children.Add(queueButtons);

            _queueList = new ListBox
            {
                ItemsSource = _queue,
                FontSize = 12.5,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                DisplayMemberPath = "Display"
            };
            // 队列通常很短：关掉虚拟化，保证"当前播放"高亮一定生效
            _queueList.SetValue(VirtualizingPanel.IsVirtualizingProperty, false);
            _queueList.SetValue(ScrollViewer.CanContentScrollProperty, false);
            _queueList.MouseDoubleClick += (s, e) => PlaySelectedInQueue();
            Grid.SetRow(_queueList, 1);
            left.Children.Add(_queueList);

            var leftCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 10, 0),
                Child = left
            };
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            // 右：音乐库
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var rightHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            rightHead.Children.Add(new TextBlock { Text = "音乐库", FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark, VerticalAlignment = VerticalAlignment.Center, ToolTip = "双击条目或点「加入队列」即可加入播放队列；音频目录会自动展开成多首" });
            Grid.SetRow(rightHead, 0);
            right.Children.Add(rightHead);

            var libButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            libButtons.Children.Add(Btn("加入队列", "\uf0fe", AddSelectedToQueue, Theme.Success, Brushes.White));
            libButtons.Children.Add(Btn("重新载入", "\uf021", () => { _ = LoadLibraryAsync(); }));
            Grid.SetRow(libButtons, 2);
            right.Children.Add(libButtons);

            _libraryList = new ListBox
            {
                FontSize = 12.5,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Background = Brushes.White
            };
            _libraryList.MouseDoubleClick += (s, e) => AddSelectedToQueue();
            Grid.SetRow(_libraryList, 1);
            right.Children.Add(_libraryList);

            var rightCard = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Child = right
            };
            Grid.SetColumn(rightCard, 1);
            grid.Children.Add(rightCard);

            return grid;
        }

        // ================= 数据同步 =================

        private async System.Threading.Tasks.Task LoadLibraryAsync()
        {
            try
            {
                _music = await DesktopApi.GetMusic();
                _libraryList.Items.Clear();
                foreach (var n in _music)
                {
                    if (n is not JsonObject o) continue;
                    string id = o["id"]?.ToString() ?? "";
                    string name = o["name"]?.ToString() ?? "";
                    string type = o["type"]?.ToString() ?? "file";
                    string path = o["path"]?.ToString() ?? "";
                    _libraryList.Items.Add(new ListBoxItem
                    {
                        Content = $"{name}　（编号 {id} · {MusicTypes.ToDisplay(type)}）",
                        Tag = new[] { name, path, type },
                        FontSize = 12.5,
                        Padding = new Thickness(6, 4, 6, 4)
                    });
                }
                SetStatus("音乐库 " + _libraryList.Items.Count + " 条　·　双击或点「加入队列」即可手动播放");
            }
            catch (Exception ex) { SetStatus("读取音乐库失败：" + ex.Message, Theme.Danger); }
        }

        /// <summary>主窗口状态刷新时回调（已经在 UI 线程上）。</summary>
        private void OnSys(JsonObject sys)
        {
            try
            {
                var mp = sys?["manual_player"] as JsonObject;
                if (mp == null) return;

                string status = mp["status"]?.ToString() ?? "stopped";
                string mode = mp["mode"]?.ToString() ?? "sequential";
                int index = mp["current_index"]?.GetValue<int>() ?? 0;
                double elapsed = mp["elapsed"]?.GetValue<double>() ?? 0;
                double duration = mp["current_duration"]?.GetValue<double>() ?? 0;
                var queue = mp["queue"] as JsonArray ?? new JsonArray();

                // 队列
                RebuildQueue(queue);

                // 当前播放
                string current = "";
                if (status != "stopped" && index >= 0 && index < queue.Count)
                    current = queue[index]?["name"]?.ToString() ?? "";
                _nowPlaying.Text = string.IsNullOrEmpty(current)
                    ? (status == "paused" ? "已暂停（未在播放）" : "当前没有手动播放")
                    : current;
                _nowPlaying.Foreground = status == "playing" ? Theme.Success : Theme.TextDark;

                // 进度

                _lastDuration = duration;      // 拖动换算秒数要用
                if (!_dragging)                // 拖动中不要被定时器拉回去（否则手感"跳"）
                {
                    double pct = duration > 0 ? Math.Min(100, elapsed / duration * 100) : 0;
                    _progress.Value = pct;
                    _progressText.Text = Fmt(elapsed) + " / " + (duration > 0 ? Fmt(duration) : "--:--");
                }

                // 播放/暂停按钮
                bool playing = status == "playing";
                _playing = playing;
                var content = _playPause.Content as StackPanel;
                if (content != null)
                {
                    if (content.Children.Count > 0 && content.Children[0] is TextBlock icon)
                        icon.Text = Theme.HasIconFont ? (playing ? "\uf04c" : "\uf04b") : (playing ? "II" : ">");
                    if (content.Children.Count > 1 && content.Children[1] is TextBlock txt)
                        txt.Text = playing ? "暂停" : "播放";
                }

                // 模式
                _modeText.Text = "模式：" + (mode == "loop" ? "列表循环" : mode == "single" ? "单曲循环" : "顺序播放");

                // 音量（用户正在拖动时不要抢）
                int vol = mp["volume"]?.GetValue<int>() ?? 100;
                if (!_draggingVolume && vol != _lastVolume)
                {
                    _lastVolume = vol;
                    _volume.Value = vol;
                    _volumeText.Text = vol + "%";
                }

                // 高亮当前项
                HighlightCurrent(status == "playing" || status == "paused" ? index : -1);

                _queueHint.Text = _queue.Count == 0 ? "队列为空：从右侧音乐库加入即可手动播放" : "共 " + _queue.Count + " 首";
            }
            catch { }
        }

        private void RebuildQueue(JsonArray queue)
        {
            bool same = queue.Count == _queue.Count;
            if (same)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    if ((_queue[i].Name ?? "") != (queue[i]?["name"]?.ToString() ?? "")) { same = false; break; }
                }
            }
            if (same) return;

            int selected = _queueList.SelectedIndex;
            _queue.Clear();
            for (int i = 0; i < queue.Count; i++)
            {
                _queue.Add(new QueueItem
                {
                    Index = i,
                    Name = queue[i]?["name"]?.ToString() ?? "",
                    Path = queue[i]?["path"]?.ToString() ?? ""
                });
            }
            if (selected >= 0 && selected < _queue.Count) _queueList.SelectedIndex = selected;
        }

        private void HighlightCurrent(int index)
        {
            for (int i = 0; i < _queueList.Items.Count; i++)
            {
                var container = _queueList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (container == null) continue;
                container.Background = i == index ? AccentBg : Brushes.Transparent;
                container.FontWeight = i == index ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private static string Fmt(double sec)
        {
            if (sec < 0) sec = 0;
            int total = (int)Math.Round(sec);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        // ================= 操作 =================

        private async void Cmd(System.Threading.Tasks.Task<JsonNode> task)
        {
            try
            {
                await task;
                await System.Threading.Tasks.Task.Delay(180);
                await Host.RefreshNowAsync();
            }
            catch { }
        }

        private void TogglePlayPause()
        {
            bool playing = _playing;
            Cmd(playing ? DesktopApi.PlayerPause() : DesktopApi.PlayerPlay());
        }

        private void PlaySelectedInQueue()
        {
            int i = _queueList.SelectedIndex;
            if (i < 0) { SetStatus("请先在队列里选择一首。", Theme.Danger); return; }
            Cmd(DesktopApi.PlayerPlayIndex(i));
            SetStatus("已切到队列第 " + (i + 1) + " 首。", Theme.Warn);
        }

        private async void RemoveFromQueue()
        {
            int i = _queueList.SelectedIndex;
            if (i < 0) { SetStatus("请先选择要移出的条目。", Theme.Danger); return; }
            var item = _queue[i];
            await DesktopApi.PlayerQueueRemove(i);
            SetStatus("已把《" + item.Name + "》移出播放队列。", Theme.Warn);
            await System.Threading.Tasks.Task.Delay(150);
            await Host.RefreshNowAsync();
        }

        private async void ClearQueue()
        {
            if (_queue.Count == 0) return;
            if (!AppDialog.Confirm(Host, "清空播放队列",
                "确定把队列里的 " + _queue.Count + " 首全部移出吗？（正在播放的会同时停止）",
                "清空队列", "取消", AppDialogKind.Warning)) return;
            for (int i = _queue.Count - 1; i >= 0; i--) await DesktopApi.PlayerQueueRemove(0);
            await DesktopApi.PlayerStop();
            SetStatus("播放队列已清空。", Theme.Success);
            await System.Threading.Tasks.Task.Delay(150);
            await Host.RefreshNowAsync();
        }

        private async void AddSelectedToQueue()
        {
            var item = _libraryList.SelectedItem as ListBoxItem;
            var info = item?.Tag as string[];
            if (info == null) { SetStatus("请先在右侧音乐库里选择一条。", Theme.Danger); return; }
            await DesktopApi.PlayerQueueAdd(info[0], info[1], info[2]);
            SetStatus("已把《" + info[0] + "》加入播放队列" + (info[2] == "folder" ? "（目录会自动展开）" : "") + "。", Theme.Success);
            await System.Threading.Tasks.Task.Delay(200);
            await Host.RefreshNowAsync();
        }
    }
}
