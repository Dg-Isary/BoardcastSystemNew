using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// 本工程 UseWindowsForms=true，ImplicitUsings 会把 System.Drawing 也引进来，
// 于是 Brush/Color/Brushes 三个名字会与 System.Drawing 里的同名类型冲突（CS0104）。这里显式指定 WPF 版。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 「管理员」赋权窗口（一个窗口、两个列表：在线设备 / 管理员设备）。
    ///
    /// 【关键】在线设备的来源是**所有用过 App 的设备**（数据通道请求里带设备ID，内存记录，不写配置），
    /// 所以**不需要对方先做话筒配对** —— 用户指出"为什么要输配对码才能设管理员"是不合理的限制，已按此改正。
    ///
    /// 语义：权限两档（管理员/普通，默认普通）；凭证自动下发、界面不显示不可复制；不赋权不写配置；
    /// "踢出" = 断开话筒会话 + 解除信任（下次必须重新输 6 位动态码）。
    ///
    /// 【可读性】用户明确要求：**默认显示手机型号**，UUID 太长不适合人读。
    /// 所以主行只显示"机型 + 在线状态 + 角色"，设备 ID 只放在 ToolTip 里（核对/复制时悬停可见）。
    /// 机型取不到时退化成"未知机型"，绝不再把 UUID 顶到主行。
    /// </summary>
    public sealed class AdminWindow : Window
    {
        readonly StackPanel _onlineHost = new StackPanel();
        readonly StackPanel _adminHost = new StackPanel();
        readonly TextBlock _summary = new TextBlock
        {
            FontSize = 11.5, Foreground = Theme.TextGray, Margin = new Thickness(0, 2, 0, 0)
        };

        public AdminWindow()
        {
            Title = "管理员（设备赋权）";
            Width = 700; Height = 620;
            MinWidth = 560; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(20) };

            // ---------- 标题区 ----------
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Theme.Icon("\uf023", 17, Theme.Primary));
            head.Children.Add(new TextBlock
            {
                Text = "设备赋权",
                FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark,
                Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(head);
            root.Children.Add(new TextBlock
            {
                Text = "权限两档：管理员（可改排程/调休）与普通（默认，只读）。只要设备用过 App 就会出现在下面，"
                   + "不需要话筒配对。设备凭证由程序自动下发（界面不显示、不可复制）；只有在这里赋权才会写进配置。",
                FontSize = 11.5, Foreground = Theme.TextGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(26, 6, 0, 0)
            });
            root.Children.Add(_summary);

            // ---------- 在线设备 ----------
            root.Children.Add(SectionHead("在线设备", "\uf0c0", "最近用过 App 的设备（不需要话筒配对）"));
            root.Children.Add(_onlineHost);

            // ---------- 管理员设备 ----------
            root.Children.Add(SectionHead("管理员设备", "\uf132", "有改排程/调休权限 · 可移出"));
            root.Children.Add(_adminHost);

            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Rebuild();
        }

        /// <summary>小节标题：图标 + 主标题 + 灰色说明，统一间距，避免两块列表长得像一坨。</summary>
        static StackPanel SectionHead(string title, string icon, string hint)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 18, 0, 6) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Theme.Icon(icon, 13, Theme.Primary));
            row.Children.Add(new TextBlock
            {
                Text = title, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Theme.TextDark,
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(row);
            sp.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11, Foreground = Theme.TextGray,
                Margin = new Thickness(20, 1, 0, 0)
            });
            return sp;
        }

        /// <summary>
        /// 设备显示名：**优先机型**。
        /// 用户要求：UUID 太长不适合人类读 —— 所以主行只用机型，ID 一律塞 ToolTip。
        /// </summary>
        static string DisplayName(string deviceId, string knownName)
        {
            if (!string.IsNullOrWhiteSpace(knownName)) return knownName.Trim();
            try
            {
                string n = VntManager.TrustedDeviceName(deviceId);
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
            catch { }
            return "未知机型";
        }

        /// <summary>状态点：在线绿、离线灰。用 8px 圆点，比一行文字更快扫读。</summary>
        static Border Dot(Brush color) => new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        /// <summary>角色徽章：管理员=主色实心，普通=浅灰描边。比"管理员/普通（默认）"这几个字更醒目。</summary>
        static Border Badge(string text, Brush bg, Brush fg) => new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = fg }
        };

        /// <summary>一行设备：左（点 + 机型 + 状态行）右（徽章 + 按钮）。</summary>
        Border DeviceRow(UIElement left, IEnumerable<UIElement> right, bool highlight)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var b in right) btns.Children.Add(b);
            Grid.SetColumn(btns, 1);
            row.Children.Add(btns);

            return new Border
            {
                Background = highlight ? new SolidColorBrush(Color.FromArgb(0x14, 0x34, 0x98, 0xDB)) : Theme.Neutral,
                BorderBrush = highlight ? Theme.Primary : Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            };
        }

        /// <summary>左半：状态点 + 机型（主）+ 一行小字（在线/最后活动 · IP · 会话）。</summary>
        static StackPanel LeftCell(string name, bool online, string sub, bool isAdmin, string tooltip)
        {
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(Dot(online ? Theme.Success : new SolidColorBrush(Color.FromRgb(0xBD, 0xC3, 0xC7))));
            line1.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13.5,
                FontWeight = isAdmin ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isAdmin ? Theme.Primary : Theme.TextDark,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            col.Children.Add(line1);
            col.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 10.5, Foreground = Theme.TextGray,
                Margin = new Thickness(16, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                col.ToolTip = tooltip;
                line1.ToolTip = tooltip;
            }
            return col;
        }

        void Rebuild()
        {
            _onlineHost.Children.Clear();
            _adminHost.Children.Clear();

            var seen = VntManager.SeenDeviceList();
            var sessions = new List<MicSecurity.Session>();
            try { sessions = MicServer.Registry.All.ToList(); } catch { }

            int onlineCount = seen.Count(x => (DateTime.UtcNow - x.At).TotalSeconds < 20);
            int adminCount = 0;
            try { adminCount = VntManager.AdminDeviceList().Count; } catch { }
            _summary.Text = $"共 {seen.Count} 台用过 App · 当前在线 {onlineCount} 台 · 管理员 {adminCount} 台";

            // ================= 在线设备 =================
            if (seen.Count == 0)
            {
                _onlineHost.Children.Add(Theme.Label(
                    "还没有设备用过 App：手机连上后会自动出现在这里，不需要话筒配对。", 12, Theme.TextGray));
            }
            foreach (var d in seen)
            {
                string dev = d.Id;
                bool isAdmin = VntManager.IsAdminDevice(dev);
                var live = sessions.FirstOrDefault(s => string.Equals(s.DeviceId ?? "", dev, StringComparison.OrdinalIgnoreCase));
                bool online = (DateTime.UtcNow - d.At).TotalSeconds < 20;
                string when = online ? "在线" : ("最后活动 " + d.At.ToLocalTime().ToString("HH:mm:ss"));
                string name = DisplayName(dev, d.Name);

                var sub = when;
                if (live != null) sub += $"　会话 {live.Sid}　{live.SourceIp}";
                var left = LeftCell(name, online, sub, isAdmin, "完整设备 ID：" + dev);

                var right = new List<UIElement>();
                right.Add(Badge(isAdmin ? "管理员" : "普通",
                    isAdmin ? Theme.Primary : new SolidColorBrush(Color.FromArgb(0x1A, 0x2C, 0x3E, 0x50)),
                    isAdmin ? Brushes.White : Theme.TextGray));

                var toggle = Theme.Button(isAdmin ? "降为普通" : "设为管理员",
                    isAdmin ? (Brush)null : Theme.Primary,
                    isAdmin ? null : Brushes.White,
                    isAdmin ? "\uf068" : "\uf0c7", () =>
                    {
                        VntManager.SetDeviceRole(dev, isAdmin ? "view" : "admin");
                        PlayLog.Write("远程", "设备赋权", "info",
                            detail: "设备 " + dev + "（" + DisplayName(dev, d.Name) + "）现在是"
                                  + (isAdmin ? "普通（只读）" : "管理员（可改排程/调休）") + "（已写配置）");
                        Rebuild();
                    });
                toggle.Margin = new Thickness(8, 0, 0, 0);
                right.Add(toggle);

                // 管理员设备不显示踢出（用户要求：取消管理员后才显示）
                if (!isAdmin)
                {
                    var kick = Theme.Button("踢出", null, null, "\uf00d", () =>
                    {
                        if (live != null) { try { MicServer.Registry.Remove(live.Sid); } catch { } try { MicDuck.ForceRestore(); } catch { } }
                        VntManager.RemoveTrustedDevice(dev);
                        VntManager.RemoveAdminDevice(dev);   // 踢出也解除管理员身份（用户反馈：只解除信任时重连后还是管理员）
                        PlayLog.Write("远程", "踢出设备", "warn",
                            detail: "设备 " + dev + "（" + DisplayName(dev, d.Name) + "）已踢出并解除信任（下次连接必须重新输 6 位动态码）");
                        Rebuild();
                    });
                    kick.Margin = new Thickness(6, 0, 0, 0);
                    right.Add(kick);
                }
                _onlineHost.Children.Add(DeviceRow(left, right, isAdmin));
            }

            // ================= 管理员设备 =================
            var admins = VntManager.AdminDeviceList();
            if (admins.Count == 0)
            {
                _adminHost.Children.Add(Theme.Label(
                    "还没有管理员设备：默认都是普通（只读），需要改排程/调休时在上面的列表里点「设为管理员」。",
                    12, Theme.TextGray));
            }
            foreach (var id in admins)
            {
                var seenRec = seen.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                bool online = seenRec.Id != null && (DateTime.UtcNow - seenRec.At).TotalSeconds < 20;
                string name = DisplayName(id, seenRec.Name);

                var left = LeftCell(name, online, online ? "在线" : "离线（权限保留）", true, "完整设备 ID：" + id);
                var right = new List<UIElement>();
                right.Add(Badge("管理员", Theme.Primary, Brushes.White));
                var demote = Theme.Button("降为普通", null, null, "\uf068", () =>
                {
                    VntManager.SetDeviceRole(id, "view");
                    PlayLog.Write("远程", "设备赋权", "info", detail: "设备 " + id + "（" + name + "）已降为普通（只读）");
                    Rebuild();
                });
                demote.Margin = new Thickness(8, 0, 0, 0);
                right.Add(demote);
                _adminHost.Children.Add(DeviceRow(left, right, true));
            }
        }
    }
}
