using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
    /// </summary>
    public sealed class AdminWindow : Window
    {
        readonly StackPanel _onlineHost = new StackPanel();
        readonly StackPanel _adminHost = new StackPanel();

        public AdminWindow()
        {
            Title = "管理员（设备赋权）";
            Width = 640; Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(Theme.Title("设备赋权", 16));
            root.Children.Add(Theme.Label(
                "权限两档：管理员（可改排程/调休）与普通（默认，只读）。只要设备用过 App 就会出现在下面，"
                + "不需要话筒配对。设备凭证由程序自动下发（界面不显示、不可复制）；只有在这里赋权才会写进配置。", 12, Theme.TextGray));

            root.Children.Add(new TextBlock
            {
                Text = "在线设备（最近用过 App 的设备）", FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark, Margin = new Thickness(0, 16, 0, 4)
            });
            root.Children.Add(_onlineHost);

            root.Children.Add(new TextBlock
            {
                Text = "管理员设备（有改排程/调休权限 · 可移出）", FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = Theme.TextDark, Margin = new Thickness(0, 16, 0, 4)
            });
            root.Children.Add(_adminHost);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Rebuild();
        }

        static string Short(string id) => string.IsNullOrWhiteSpace(id) ? "(无ID)"
            : (id.Length > 12 ? id.Substring(0, 12) + "…" : id);

        void Rebuild()
        {
            _onlineHost.Children.Clear();
            _adminHost.Children.Clear();

            var seen = VntManager.SeenDeviceList();
            var sessions = new List<MicSecurity.Session>();
            try { sessions = MicServer.Registry.All.ToList(); } catch { }

            if (seen.Count == 0)
                _onlineHost.Children.Add(Theme.Label("（还没有设备用过 App：手机连上后会自动出现在这里，不需要话筒配对）", 12, Theme.TextGray));
            foreach (var d in seen)
            {
                string dev = d.Id;
                bool isAdmin = VntManager.IsAdminDevice(dev);
                var live = sessions.FirstOrDefault(s => string.Equals(s.DeviceId ?? "", dev, StringComparison.OrdinalIgnoreCase));
                string when = (DateTime.UtcNow - d.At).TotalSeconds < 20 ? "在线" : ("最后活动 " + d.At.ToLocalTime().ToString("HH:mm:ss"));
                var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                line.Children.Add(new TextBlock
                {
                    Text = Short(dev) + (string.IsNullOrWhiteSpace(d.Name) ? "" : "　" + d.Name) + "　" + when + "　" + (isAdmin ? "管理员" : "普通（默认）"),
                    Width = 400, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
                    Foreground = isAdmin ? Theme.Primary : Theme.TextDark,
                    ToolTip = "完整设备 ID：" + dev        // 悬停可见完整 ID（复制/核对用）
                });
                var toggle = Theme.Button(isAdmin ? "降为普通" : "设为管理员", isAdmin ? (System.Windows.Media.Brush)null : Theme.Primary,
                    isAdmin ? null : System.Windows.Media.Brushes.White, isAdmin ? "\uf068" : "\uf0c7", () =>
                    {
                        VntManager.SetDeviceRole(dev, isAdmin ? "view" : "admin");
                        PlayLog.Write("远程", "设备赋权", "info",
                            detail: "设备 " + dev + " 现在是" + (isAdmin ? "普通（只读）" : "管理员（可改排程/调休）") + "（已写配置）");
                        Rebuild();
                    });
                toggle.Margin = new Thickness(8, 0, 0, 0);
                line.Children.Add(toggle);
                var kick = Theme.Button("踢出", null, null, "\uf00d", () =>
                {
                    if (live != null) { try { MicServer.Registry.Remove(live.Sid); } catch { } try { MicDuck.ForceRestore(); } catch { } }
                    VntManager.RemoveTrustedDevice(dev);
                    VntManager.RemoveAdminDevice(dev);   // 踢出也**解除管理员身份**（用户反馈：只解除信任时它重连后还是管理员）
                    PlayLog.Write("远程", "踢出设备", "warn", detail: "设备 " + dev + " 已踢出并解除信任（下次连接必须重新输 6 位动态码）");
                    Rebuild();
                });
                kick.Margin = new Thickness(6, 0, 0, 0);
                if (!isAdmin) line.Children.Add(kick);   // 管理员设备不显示踢出（用户要求：取消管理员后才显示）
                _onlineHost.Children.Add(line);
            }

            var admins = VntManager.AdminDeviceList();
            if (admins.Count == 0)
                _adminHost.Children.Add(Theme.Label("（还没有管理员设备：默认都是普通，只读）", 12, Theme.TextGray));
            foreach (var id in admins)
            {
                var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                line.Children.Add(new TextBlock
                {
                    Text = Short(id) + "　管理员", Width = 400,
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Foreground = Theme.Primary
                });
                var demote = Theme.Button("降为普通", null, null, "\uf068", () =>
                {
                    VntManager.SetDeviceRole(id, "view");
                    PlayLog.Write("远程", "设备赋权", "info", detail: "设备 " + id + " 已降为普通（只读）");
                    Rebuild();
                });
                demote.Margin = new Thickness(8, 0, 0, 0);
                line.Children.Add(demote);
                _adminHost.Children.Add(line);
            }
        }
    }
}
