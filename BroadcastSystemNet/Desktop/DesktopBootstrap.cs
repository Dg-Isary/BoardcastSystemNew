#nullable disable

using System;
using System.Collections.Generic;
using System.Windows;
using Application = System.Windows.Application;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>桌面端启动入口（与后端同进程，共用同一份配置与状态）。</summary>
    public static class DesktopBootstrap
    {
        public static void Run()
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    LogUiException(e.Exception);
                    e.Handled = true;
                };
                var win = new MainWindow();
                app.MainWindow = win;
                // 自愈：老版本的启动项没带 --silent，导致"静默启动"设置形同虚设；
                // 这里按当前设置把已存在的启动项修正一次（没有启动项就什么都不做）。
                SettingsPage.SyncAutostartSilentFlag();
                // 【文字清晰度】WPF 默认 Ideal 排版，小字号在高 DPI 下发虚；改用 Display（按像素对齐）+ 布局取整，
                // 文字明显更实。注意：本项目各窗口都用了 AllowsTransparency=true，WPF 在透明窗口上会禁用 ClearType，
                // 因此文字仍是灰度抗锯齿；若要完全清晰，需要去掉透明（代价是失去圆角与柔和阴影）。
                try
                {
                    System.Windows.Media.TextOptions.SetTextFormattingMode(win, System.Windows.Media.TextFormattingMode.Display);
                    win.UseLayoutRounding = true;
                }
                catch { }
                if (Program.SilentStart)
                {
                    // 静默启动（开机自启带 --silent）：**只驻留托盘与悬浮球，主界面完全不出现**。
                    // 历史坑（用户反馈）：以前是"最小化 + Loaded 里 Hide"，但 `app.Run(win)` 本身会**强制 Show()**
                    // 一次窗口 —— 于是开机后屏幕左下角必然缩着一个小窗口，而且它不进任务栏（后来调出来也看不到图标）。
                    // 正确做法：静默时**不把窗口交给 app.Run**，用无参 Run()，窗口从头到尾不显示。
                    try { win.ShowInTaskbar = false; win.ShowActivated = false; win.Visibility = Visibility.Hidden; } catch { }
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 没有可见窗口，别自动退出
                    app.Run();                                           // 关键：不传窗口 → 不会 Show()
                    return;
                }
                app.Run(win);
            }
            catch (Exception ex)
            {
                LogHelper.Log("Desktop Bootstrap Failed", ex);
                System.Windows.Forms.MessageBox.Show("桌面界面启动失败：" + ex.Message, "错误",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        // ================= 界面异常限流 =================
        // 历史教训：某个样式 TargetType 不匹配时，WPF 会在每次布局时抛同一个异常，
        // 一秒几十条，日志文件几分钟就涨到好几 MB，真正的错误反而被埋掉。
        // 因此同一条消息在窗口期内只记一次，其余只计数，下次落盘时附带折叠条数。

        private static readonly object ErrLock = new object();
        private static readonly Dictionary<string, DateTime> ErrLast = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, int> ErrFolded = new Dictionary<string, int>();
        private const int ErrWindowSeconds = 30;

        private static void LogUiException(Exception ex)
        {
            string key = ex == null ? "未知界面异常" : (ex.Message ?? ex.GetType().Name);
            int folded;
            lock (ErrLock)
            {
                DateTime last;
                bool seen = ErrLast.TryGetValue(key, out last);
                int n;
                ErrFolded.TryGetValue(key, out n);
                if (seen && (DateTime.Now - last).TotalSeconds < ErrWindowSeconds)
                {
                    ErrFolded[key] = n + 1;
                    return;   // 窗口期内重复异常：只累加，不写日志
                }
                folded = n;
                ErrLast[key] = DateTime.Now;
                ErrFolded[key] = 0;
            }
            string message = "Desktop UI Exception";
            if (folded > 0) message += "（同一错误已折叠 " + folded + " 次）";
            LogHelper.Log(message, ex);
        }
    }
}
