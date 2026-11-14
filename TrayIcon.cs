using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryDisplay;

internal static class TrayIcon
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    internal static Icon CreateCombined(IReadOnlyList<Peripheral> devices, bool error = false, int size = 32)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = new Bitmap(size * 4, size * 4);
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(size * 4 / 32f, size * 4 / 32f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1;
        var foreground = light ? Color.FromArgb(62, 66, 71) : Color.FromArgb(213, 216, 219);
        var low = Color.FromArgb(246, 87, 94);
        var layout = SegmentedRing.Layout(devices);
        using var track = new Pen(Color.FromArgb(52, foreground), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var segment in layout)
        {
            Color color = error ? Color.FromArgb(130, foreground) : segment.Low ? low : foreground;
            if (segment.IsDot)
            {
                double radians = segment.StartAngle * Math.PI / 180;
                float x = 16 + 13 * (float)Math.Cos(radians);
                float y = 16 + 13 * (float)Math.Sin(radians);
                float radius = devices.Count > 12 ? 1.15f : 1.45f;
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, x - radius, y - radius, 2 * radius, 2 * radius);
            }
            else
            {
                g.DrawArc(track, 3, 3, 26, 26, segment.StartAngle, segment.SweepAngle);
                using var stroke = new Pen(color, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (segment.Battery.HasValue && segment.ProgressAngle > 0 && !error)
                    g.DrawArc(stroke, 3, 3, 26, 26, segment.StartAngle, segment.ProgressAngle);
            }
        }
        if (layout.Count == 0) g.DrawEllipse(track, 3, 3, 26, 26);
        using var diamond = new Pen(Color.FromArgb(185, foreground), 1.7f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(diamond, new PointF[] { new(16, 9.5f), new(22.5f, 16), new(16, 22.5f), new(9.5f, 16) });
        if (error)
        {
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(foreground);
            g.DrawString("!", font, brush, 13, 8);
        }
        return ToIcon(bitmap, size);
    }

    public static Icon Create(int? level, bool connected, bool error = false, bool ring = true, int size = 32)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = new Bitmap(size * 4, size * 4);
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(size * 4 / 32f, size * 4 / 32f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (ring)
        {
            bool light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1;
            Color foreground = light ? Color.FromArgb(65, 69, 73) : Color.FromArgb(200, 203, 207);
            Color active = !connected || error ? Color.FromArgb(125, 128, 133)
                : level <= 10 ? Color.FromArgb(240, 103, 103)
                : level <= 20 ? Color.FromArgb(235, 180, 84) : foreground;
            using var track = new Pen(Color.FromArgb(85, foreground), 2.7f);
            using var progress = new Pen(active, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(track, 3, 3, 26, 26);
            if (level > 0 && !error) g.DrawArc(progress, 3, 3, 26, 26, -135, Math.Max(1, 360 * level.Value / 100f - 5));
            using var center = new SolidBrush(Color.FromArgb(155, active));
            if (error || level is null)
            {
                using var smallFont = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
                using var centerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(error ? "!" : "?", smallFont, center, new RectangleF(4, 3, 24, 25), centerFormat);
            }
            else g.FillPolygon(center, [new(16, 10), new(22, 16), new(16, 22), new(10, 16)]);
            return ToIcon(bitmap, size);
        }
        Color color = !connected || error ? Color.FromArgb(155, 163, 175)
            : level <= 20 ? Color.FromArgb(255, 108, 108)
            : level <= 40 ? Color.FromArgb(255, 199, 91) : Color.FromArgb(107, 216, 191);
        using var brush = new SolidBrush(color);
        using var background = new SolidBrush(Color.FromArgb(75, 90, 100));
        string text = error ? "!" : level?.ToString() ?? "?";
        using var font = new Font("Segoe UI", text.Length == 3 ? 15 : 19, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(-2, -3, 36, 31), format);
        g.FillRectangle(background, 3, 28, 26, 3);
        if (level.HasValue && !error) g.FillRectangle(brush, 3, 28, 26 * level.Value / 100f, 3);
        return ToIcon(bitmap, size);
    }

    private static Icon ToIcon(Bitmap source, int size)
    {
        using var target = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        }
        var handle = target.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
