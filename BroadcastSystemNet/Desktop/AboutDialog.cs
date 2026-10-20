using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 关于窗：只保留 版本号 / 引用信息 / 自检命令 / 项目地址（GitHub 可点击打开）。
    /// 数据目录、日志目录、看板地址、图标字体状态这些都放在「运行总览」页，这里不再重复，
    /// 免得把使用者的本机路径暴露在截图里（与"配置不留地区信息"同一个考虑）。
    /// </summary>
    public sealed class AboutDialog : Window
    {
        /// <summary>项目地址（点击用默认浏览器打开）。</summary>
        public const string GitHubUrl = "https://github.com/Dg-Isary/BoardcastSystemNew";

        /// <summary>离线自检命令：不需要界面、不改配置，跑完打印逐项结果。</summary>
        public const string SelfTestCommand = "BroadcastSystemNet.exe --self-test";

        /// <summary>
        /// 版本号的唯一来源：csproj 的 &lt;Version&gt;（编译进程序集信息）。
        /// 安装脚本由 tools\build-release.ps1 用同一个值传入，不再手工同步两处。
        /// </summary>
        public static string AppVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');            // 带 git 提交号时截断（1.0.0+abc123）
                    return plus > 0 ? info.Substring(0, plus) : info;
                }
                return asm.GetName().Version?.ToString(3) ?? "未知";
            }
            catch { return "未知"; }
        }

        /// <summary>
        /// 编译时间（由 csproj 的 GenerateBuildInfo 目标在每次编译时写入 BuildInfo.g.cs）。
        /// 用它区分"同一个版本号的不同构建"：现场排查时先看这个时间，能立刻判断
        /// 用户装的是不是我们刚发的那一版，省掉一轮来回问。
        /// </summary>
        public static string BuiltAt()
        {
            try
            {
                // 由 csproj 的 <AssemblyMetadata Include="Built" .../> 生成，SDK 会产出
                // [assembly: AssemblyMetadata("Built", "yyyy-MM-dd HH:mm")]。
                var hit = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(x => x != null && x.Key == "Built");
                return hit == null || string.IsNullOrWhiteSpace(hit.Value) ? "未知" : hit.Value;
            }
            catch { return "未知"; }
        }

        /// <summary>引用的第三方组件（界面/网页/加密/音频）。</summary>
        static readonly (string Name, string Note)[] Refs =
        {
            ("Font Awesome Free 6.4.0", "界面与网页图标（字体 SIL OFL 1.1 / 代码 MIT）"),
            ("Chart.js 4.4.1", "网页实时波形图（MIT）"),
            ("flatpickr 4.6.13", "网页日期选择（MIT）"),
            ("BouncyCastle.Cryptography 2.6.2", "加密算法（MIT）"),
            ("FFmpeg / FFplay / FFprobe", "音频播放与响度归一（随程序打包，LGPL 构建，独立可执行文件分发）"),
            (".NET 8 (WPF + ASP.NET Core)", "运行时与内置 Web 服务（MIT）"),
            ("本程序图标", "自制矢量图，无版权限制（source: third_party/Opensource.svg）"),
            ("铃声资源库", "初始铃声（可选组件，由构建脚本从独立仓库获取）"),
            // 开发过程中用过的 AI 工具（列在这里，取代原先底部那行署名）
            ("Gemini Pro", "早期版本的开发与设计协助"),
            ("DeepSeek Harness", "当前版本的开发协助"),
        };

        public AboutDialog()
        {
            Title = "关于";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;   // 高缩放下可拉大，避免按钮被挤出可见区域
            ShowInTaskbar = false;
            // 【高缩放修复 · 175% 下看不到"关闭"】
            // 以前是 SizeToContent.Height + MaxHeight = Math.Max(200, WorkArea-240)：
            // 175% 笔记本工作区只有约 420 DIP → 420-240=180 被那个 200 的**保底值**抬成 200，
            // 而本窗内容远高于 200 → 窗口被压到 200 高，底部"关闭"与署名整块被裁掉（用户实测反馈）。
            // 现在改成"固定初始高度 + Grid 三行(Auto/星号/Auto)"：正文滚动，底部按钮永远可见。
            SizeToContent = SizeToContent.Manual;
            double waW = SystemParameters.WorkArea.Width, waH = SystemParameters.WorkArea.Height;
            Width = Math.Max(420, Math.Min(560, waW - 40));    // 175% 下屏幕 DIP 宽度也小，一起夹住
            Height = Math.Max(300, Math.Min(620, waH - 40));
            MinWidth = 400;
            MinHeight = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var card = new Border
            {
                Margin = new Thickness(18),
                Background = Theme.Card,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(22, 18, 22, 16),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.18,
                    Color = Color.FromRgb(0x2C, 0x3E, 0x50)
                }
            };

            // 三行布局：标题(Auto) / 正文(星号，内部滚动) / 底部按钮(Auto)。
            // 这样"关闭"永远在可视区内，不再靠 MaxHeight 的算术去猜（见构造函数注释）。
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new StackPanel();
            var body = new StackPanel();

            // ---------- 标题 + 版本 ----------
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Theme.Icon("\uf028", 18, Theme.Primary));
            head.Children.Add(new TextBlock
            {
                Text = "智能校园广播系统",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            top.Children.Add(head);

            top.Children.Add(new TextBlock
            {
                // 版本号 + 编译时间：同一个版本号可能被重新构建多次（改图标、改文案都会重编），
                // 光有 4.1.2 分不清是哪个构建产物，加上"编译于"就一目了然。
                Text = "版本 " + AppVersion() + "　·　编译于 " + BuiltAt(),
                FontSize = 13,
                Foreground = Theme.TextGray,
                Margin = new Thickness(27, 4, 0, 0)
            });

            // ---------- 引用信息 ----------
            body.Children.Add(SectionTitle("引用信息"));
            foreach (var r in Refs)
            {
                // 用 Grid 固定"名称列"宽度：说明文字永远从同一条竖线开始（原来用 MinWidth，
                // 名称一长就会把说明推右，整列看着参差不齐）；行内三样都垂直居中，避免高低不齐。
                var row = new Grid { Margin = new Thickness(2, 3, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // 圆点
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });      // 名称（固定）
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 说明
                var dot = Theme.Icon("\uf111", 6, Theme.Primary);
                dot.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);
                var name = new TextBlock
                {
                    Text = r.Name,
                    FontSize = 12.5,
                    Foreground = Theme.TextDark,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var note = new TextBlock
                {
                    Text = r.Note,
                    FontSize = 11.5,
                    Foreground = Theme.TextGray,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap      // 说明长就换行，不能被裁掉右边
                };
                Grid.SetColumn(note, 2);
                row.Children.Add(note);
                body.Children.Add(row);
            }

            // ---------- 自检命令 ----------
            body.Children.Add(SectionTitle("离线自检命令"));
            var cmdRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 0) };
            var cmdBox = new Border
            {
                Background = Theme.Neutral,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = SelfTestCommand,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    Foreground = Theme.TextDark,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            cmdRow.Children.Add(cmdBox);
            var cmdCopy = Theme.Button("复制", null, null, "\uf0c5", () =>
            {
                try { Clipboard.SetText(SelfTestCommand); } catch { }
            });
            cmdCopy.Margin = new Thickness(10, 0, 0, 0);            // 原来紧贴命令框，看着像挤在一起
            cmdCopy.VerticalAlignment = VerticalAlignment.Center;   // 与命令框垂直居中，避免一高一低
            cmdRow.Children.Add(cmdCopy);
            body.Children.Add(cmdRow);

            // ---------- 项目地址 ----------
            body.Children.Add(SectionTitle("项目地址"));
            var linkRow = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };   // 竖排：第一行地址、第二行按钮
            var hint = new TextBlock { FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(2, 6, 0, 0) };
            // 地址单独占一行、按钮另起一行：地址+两个按钮挤在一行时总宽超出卡片，
            // 右边的「复制地址」会被裁掉（渲染核对时发现的）。
            var linkUrl = new TextBlock
            {
                Text = GitHubUrl,
                FontSize = 13,
                Foreground = Theme.Primary,
                Margin = new Thickness(2, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            linkRow.Children.Add(linkUrl);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
            // 用按钮而不是超链接：部分环境下超链接的导航事件不触发（用户反馈"地址打不开"），
            // 按钮点击一定进得来；再配一个「复制地址」兜底，浏览器打不开也能手动粘。
            var openBtn = Theme.Button("打开项目主页", Theme.Primary, Brushes.White, "\uf08e", () =>
            {
                bool ok = OpenUrl(GitHubUrl);
                hint.Text = ok ? "" : "没能自动打开浏览器：请点右边「复制地址」，再粘到浏览器地址栏。";
            });
            openBtn.VerticalAlignment = VerticalAlignment.Center;
            btnRow.Children.Add(openBtn);
            var copyBtn = Theme.Button("复制地址", null, null, "\uf0c5", () =>
            {
                try { Clipboard.SetText(GitHubUrl); hint.Text = "地址已复制：" + GitHubUrl; }
                catch { hint.Text = "复制失败，请手动输入：" + GitHubUrl; }
            });
            copyBtn.Margin = new Thickness(8, 0, 0, 0);
            copyBtn.VerticalAlignment = VerticalAlignment.Center;
            btnRow.Children.Add(copyBtn);
            linkRow.Children.Add(btnRow);
            body.Children.Add(linkRow);
            body.Children.Add(hint);

            // ---------- 底部（关闭按钮 + 署名，整组放进 Auto 行，保证永远可见）----------
            var bottom = new StackPanel();
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            footer.Children.Add(Theme.Button("关闭", Theme.Primary, Brushes.White, "\uf00c", Close));
            bottom.Children.Add(footer);

            bottom.Children.Add(new TextBlock
            {
                Text = "Designed by Isary",
                FontSize = 11,
                Foreground = Theme.TextGray,
                Margin = new Thickness(0, 12, 0, 0)
            });

            var bodyScroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(top, 0); root.Children.Add(top);
            Grid.SetRow(bodyScroll, 1); root.Children.Add(bodyScroll);
            Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            card.Child = root;
            Content = card;
        }

        static TextBlock SectionTitle(string text) => new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextDark,
            Margin = new Thickness(0, 16, 0, 6)
        };

        /// <summary>
        /// 用默认浏览器打开网址。三重兜底，避免"点了没反应"：
        /// ① .NET 的 ShellExecute；② Win32 ShellExecuteW；③ 交给 cmd start。任一成功即返回 true。
        /// </summary>
        static bool OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return true;
            }
            catch { }
            try
            {
                if (ShellExecuteW(IntPtr.Zero, "open", url, null, null, 5 /* SW_SHOWNORMAL */) != IntPtr.Zero) return true;
            }
            catch { }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start \"\" \"" + url + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }
            catch { }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        /// <summary>
        /// 自检用：在给定的**受限尺寸**下离屏布局一次，返回底部按钮行的底边 Y 与可用高度。
        /// 用来断言"关闭"不会被挤出窗口 —— 175% 缩放下 MaxHeight 里 Math.Max(200, …) 的保底值
        /// 会把窗口压到 200 高，底部"关闭"与署名整块被裁掉（用户实测反馈）。
        /// 纯布局计算：不进屏、不联网、不改数据。
        /// </summary>
        public static string CheckFooterVisibleForTest(double width, double height)
        {
            try
            {
                var win = new AboutDialog();
                var root = win.Content as FrameworkElement;
                if (root == null) return "无内容";
                root.Measure(new System.Windows.Size(width, height));
                root.Arrange(new System.Windows.Rect(0, 0, width, height));
                root.UpdateLayout();
                var card = root as Border;
                var grid = card?.Child as Grid;
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

        /// <summary>离屏渲染关于窗（不显示、不改数据），用于人工核对对齐。</summary>
        public static string RenderPreview(string outPath, int width = 520)
        {
            try
            {
                var win = new AboutDialog();
                if (width <= 0) width = 520;
                win.Width = width;
                var root = win.Content as FrameworkElement;
                if (root == null) return "关于窗没有内容，无法渲染";
                root.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                root.Arrange(new System.Windows.Rect(0, 0, width, root.DesiredSize.Height));
                root.UpdateLayout();
                int h = (int)Math.Ceiling(root.DesiredSize.Height);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                if (string.IsNullOrWhiteSpace(outPath)) outPath = System.IO.Path.Combine(Program.AppDataDir, "about-preview.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath) ?? Program.AppDataDir);
                using (var fs = System.IO.File.Create(outPath)) enc.Save(fs);
                return outPath + $"（{width}×{h}）";
            }
            catch (Exception ex) { return "渲染失败：" + ex.Message; }
        }
    }
}
