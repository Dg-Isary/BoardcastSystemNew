#nullable disable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace BroadcastSystemNet.Desktop
{
    /// <summary>托盘图标要表达的系统状态（QQ 那种右上角小圆点）。</summary>
    public enum TrayState
    {
        /// <summary>红色：系统已停用（总开关关闭），不会打铃。</summary>
        Disabled = 0,
        /// <summary>蓝色：系统正常，但今天没有排程。</summary>
        NoSchedule = 1,
        /// <summary>绿色：系统正常，且今天有排程。</summary>
        Scheduled = 2
    }

    /// <summary>
    /// 托盘图标相关的绘制：右上角状态圆点（GDI+）与状态文案/配色（WPF 菜单复用）。
    /// </summary>
    public static class TrayIcons
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// 用 Windows 自己的图标绘制（支持 PNG 压缩帧）把底图打进 Graphics。
        /// 不能用 Icon.ToBitmap()：res\favicon.ico 是单帧 256×256 的 PNG 压缩 ICO，
        /// GDI+ 的 ToBitmap/DrawIcon 会抛 "Requested range extends past the end of the array"，
        /// 而 DrawIconEx 走的是系统图标解码，PNG 帧也能正常缩放绘制。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy,
            int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        // ================= 配色（与 Theme 保持一致） =================

        public static readonly DColor Primary = DColor.FromArgb(0x34, 0x98, 0xDB);
        public static readonly DColor Danger = DColor.FromArgb(0xE7, 0x4C, 0x3C);
        public static readonly DColor Success = DColor.FromArgb(0x2E, 0xCC, 0x71);
        public static readonly DColor WarnColor = DColor.FromArgb(0xE6, 0x7E, 0x22);
        public static readonly DColor TextDark = DColor.FromArgb(0x2C, 0x3E, 0x50);
        public static readonly DColor TextGray = DColor.FromArgb(0x7F, 0x8C, 0x8D);

        public static DColor DotColorOf(TrayState s)
        {
            switch (s)
            {
                case TrayState.Disabled: return Danger;
                case TrayState.Scheduled: return Success;
                default: return Primary;
            }
        }

        /// <summary>同一个状态色的 WPF 版本（托盘菜单里的状态点用）。</summary>
        public static System.Windows.Media.Color DotColorWpf(TrayState s)
        {
            var c = DotColorOf(s);
            return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
        }

        /// <summary>状态一句话文案，托盘提示与右键菜单头部共用。</summary>
        public static string StatusText(TrayState s)
        {
            switch (s)
            {
                case TrayState.Disabled: return "已停用 · 不会打铃";
                case TrayState.NoSchedule: return "运行正常 · 今日无排程";
                default: return "运行正常 · 今日有排程";
            }
        }

        // ================= 右上角状态标记 =================

        /// <summary>
        /// 在程序图标右上角画状态标记，返回新的托盘图标。三种状态用不同形状区分：
        /// 红=方块、蓝=圆、绿=三角；三种颜色与主界面一致，不做淡化。
        /// 都带一圈白色描边，深浅背景下都看得清。调用方负责在替换后 Dispose 旧图标。
        /// </summary>
        public static Icon BuildStateIcon(TrayState state)
        {
            int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            var color = DotColorOf(state);

            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(DColor.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // 底图：程序自带图标（DrawIconEx 支持 PNG 压缩帧的 ICO）
                    try
                    {
                        if (Program.AppIcon != null)
                        {
                            IntPtr hdc = g.GetHdc();
                            try { DrawIconEx(hdc, 0, 0, Program.AppIcon.Handle, size, size, 0, IntPtr.Zero, 3 /*DI_NORMAL*/); }
                            finally { g.ReleaseHdc(hdc); }
                        }
                    }
                    catch { }

                    // 标记：边长约为图标的 42%，贴右上角（三种形状同尺寸，只靠形状与颜色区分）
                    float box = size * 0.42f;
                    float x = size - box - size * 0.03f;
                    float y = size * 0.03f;
                    float ring = Math.Max(1f, size * 0.085f);

                    using (var white = new SolidBrush(DColor.White))
                    using (var brush = new SolidBrush(color))
                    {
                        DrawMarker(g, state, white, x - ring, y - ring, box + ring * 2);   // 白色描边底
                        DrawMarker(g, state, brush, x, y, box);                            // 实心彩色
                    }
                }

                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        /// <summary>按状态画形状：红=方块、蓝=圆、绿=播放键那种朝右的三角。</summary>
        private static void DrawMarker(Graphics g, TrayState state, Brush brush, float x, float y, float box)
        {
            switch (state)
            {
                case TrayState.Disabled:
                    g.FillRectangle(brush, x, y, box, box);
                    break;
                case TrayState.Scheduled:
                    // 朝右的三角形（播放符号）：左边竖边 + 右边顶点
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(x + box * 0.08f, y),
                        new PointF(x + box * 0.96f, y + box / 2f),
                        new PointF(x + box * 0.08f, y + box)
                    });
                    break;
                default:
                    g.FillEllipse(brush, x, y, box, box);
                    break;
            }
        }

        /// <summary>右键菜单头部用的同形状小图标（与托盘上的形状保持一致）。</summary>
        public static System.Windows.Shapes.Shape BuildMarkerShape(TrayState state, double size)
        {
            var color = new System.Windows.Media.SolidColorBrush(DotColorWpf(state));
            double ring = 1.6;
            switch (state)
            {
                case TrayState.Disabled:
                    return new System.Windows.Shapes.Rectangle
                    {
                        Width = size, Height = size, Fill = color,
                        Stroke = System.Windows.Media.Brushes.White, StrokeThickness = ring,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
                case TrayState.Scheduled:
                    return new System.Windows.Shapes.Polygon
                    {
                        Points = new System.Windows.Media.PointCollection
                        {
                            new System.Windows.Point(0, 0),
                            new System.Windows.Point(size, size / 2),
                            new System.Windows.Point(0, size)
                        },
                        Fill = color,
                        Stroke = System.Windows.Media.Brushes.White, StrokeThickness = ring,
                        Width = size, Height = size,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
                default:
                    return new System.Windows.Shapes.Ellipse
                    {
                        Width = size, Height = size, Fill = color,
                        Stroke = System.Windows.Media.Brushes.White, StrokeThickness = ring,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
            }
        }

        /// <summary>
        /// 把三种状态的托盘图标画成一张预览图（含真实托盘尺寸与放大版），供人工核对。
        /// 由 `BroadcastSystemNet.exe --tray-preview [输出路径]` 调用；纯本地绘制，不联网、不改数据。
        /// </summary>
        public static string WriteStatePreview(string outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                outPath = Path.Combine(Path.GetTempPath(), "tray-states.png");

            var states = new[]
            {
                new { State = TrayState.Disabled,   Caption = "红：已停用（总开关关闭，不会打铃）" },
                new { State = TrayState.NoSchedule, Caption = "蓝：运行正常，今天没有排程" },
                new { State = TrayState.Scheduled,  Caption = "绿：运行正常，今天有排程" }
            };

            int tray = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            int rowH = 116, bigSize = 88;
            using (var canvas = new Bitmap(400, rowH * states.Length))
            using (var g = Graphics.FromImage(canvas))
            using (var font = new Font("Microsoft YaHei UI", 11f))
            using (var small = new Font("Microsoft YaHei UI", 9f))
            {
                g.Clear(DColor.FromArgb(0xF4, 0xF6, 0xF8));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;

                for (int i = 0; i < states.Length; i++)
                {
                    int y = i * rowH;
                    g.DrawString(states[i].Caption, font, Brushes.Black, 14, y + 8);
                    using (var icon = BuildStateIcon(states[i].State))
                    using (var real = icon.ToBitmap())
                    {
                        g.DrawImage(real, 14, y + 40, tray, tray);              // 真实托盘尺寸
                        g.DrawImage(real, 120, y + 14, bigSize, bigSize);       // 放大版
                    }
                    g.DrawString("↑ 实际 " + tray + "px", small, Brushes.DimGray, 14, y + 66);
                    g.DrawString("↑ 同图放大", small, Brushes.DimGray, 120, y + 104);
                }

                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                canvas.Save(outPath, ImageFormat.Png);
            }
            return Path.GetFullPath(outPath);
        }
    }
}
