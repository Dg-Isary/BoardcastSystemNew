using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>
    /// 给 WPF 窗口加**毛玻璃虚化背景**（Windows 10/11 的 ACCENT_ENABLE_ACRYLICBLURBEHIND）。
    ///
    /// 用途（用户确认的语义）：**子窗口（vnt 设置 / 话筒 / 管理员）弹出时，把主窗口虚化**，
    /// 让子窗口与主窗口在视觉上区分开；子窗口关闭后调用 <see cref="Remove"/> 恢复原样。
    /// （不是让子窗口自己糊 —— 这一点我一开始理解错了，已按用户要求改回来。）
    ///
    /// 直接调 user32 的 SetWindowCompositionAttribute（与 Win11 终端/开始菜单同一条系统能力），
    /// 失败时静默降级（窗口照样能用，只是没有虚化）。
    /// </summary>
    public static class WindowBlur
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_DISABLED = 0;

        /// <summary>应用毛玻璃（tintArgb 是叠在虚化层上的底色，默认半透明深蓝）。</summary>
        public static void Apply(Window w, uint tintArgb = 0x99101828)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    // 句柄还没建：等窗口初始化完成再套用一次
                    w.SourceInitialized += (s, e) => Apply(w, tintArgb);
                    return;
                }
                if (SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, tintArgb) == 0)
                    SetAccent(hwnd, ACCENT_ENABLE_BLURBEHIND, tintArgb);   // 老系统不支持亚克力 → 退回普通模糊
            }
            catch { }
        }

        /// <summary>关闭虚化（子窗口关掉后调用，主窗口恢复原样）。</summary>
        public static void Remove(Window w)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetAccent(hwnd, ACCENT_DISABLED, 0);
            }
            catch { }
        }

        static int SetAccent(IntPtr hwnd, int state, uint tintArgb)
        {
            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = unchecked((int)tintArgb)
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
