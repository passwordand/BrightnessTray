using System.Drawing.Drawing2D;

namespace BrightnessTray.UI;

/// <summary>
/// 动态绘制托盘图标：一个随亮度变化的太阳图形。
/// 用代码画图而不是打包 .ico，避免额外资源文件，同时能让图标直观反映当前亮度。
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>
    /// 生成亮度图标。percent 越小，太阳越暗、光芒越短。
    /// </summary>
    public static Icon Create(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 亮度映射到颜色：0% 为暗灰，100% 为亮黄
            float t = percent / 100f;
            int r = (int)(110 + 145 * t);
            int green = (int)(110 + 125 * t);
            int blue = (int)(120 - 60 * t);
            Color core = Color.FromArgb(255, Math.Min(255, r), Math.Min(255, green), Math.Max(0, blue));

            // 光芒长度随亮度伸缩
            float inner = 6.5f;
            float outer = 9.5f + 5.0f * t;
            var center = new PointF(16f, 16f);

            using var pen = new Pen(core, 2.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            // 8 条光芒
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4.0;
                var p1 = new PointF(
                    center.X + (float)(Math.Cos(angle) * inner),
                    center.Y + (float)(Math.Sin(angle) * inner));
                var p2 = new PointF(
                    center.X + (float)(Math.Cos(angle) * outer),
                    center.Y + (float)(Math.Sin(angle) * outer));
                g.DrawLine(pen, p1, p2);
            }

            // 太阳本体
            float radius = 5.2f;
            using var brush = new SolidBrush(core);
            g.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            // 克隆一份，确保返回的 Icon 不依赖临时句柄
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
