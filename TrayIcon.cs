using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryDisplay;

internal static class TrayIcon
{
    private static Color ChargingColor(bool light) => light ? Color.FromArgb(22, 150, 106) : Color.FromArgb(74, 222, 155);

    internal static bool IsTaskbarLight()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static Bitmap CreateCanvas(int size, out Graphics g)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return bitmap;
    }

    internal static Icon CreateCombined(IReadOnlyList<Peripheral> devices, bool error = false, int size = 32, int lowThreshold = Preferences.DefaultLowBatteryThreshold)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = CreateCanvas(size, out var g);
        using (g)
        {
            bool light = IsTaskbarLight();
            var foreground = light ? Color.FromArgb(25, 30, 35) : Color.FromArgb(246, 248, 250);
            var low = Color.FromArgb(246, 87, 94);
            var charging = ChargingColor(light);
            var layout = SegmentedRing.Layout(devices, lowThreshold);
            using var track = new Pen(Color.FromArgb(light ? 72 : 76, foreground), 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var segment in layout)
            {
                Color color = error ? Color.FromArgb(130, foreground)
                    : segment.Charging ? charging
                    : segment.Low ? low : foreground;
                if (segment.IsDot)
                {
                    double radians = segment.StartAngle * Math.PI / 180;
                    float x = 16 + 13.75f * (float)Math.Cos(radians);
                    float y = 16 + 13.75f * (float)Math.Sin(radians);
                    float radius = devices.Count > 12 ? 1.4f : 2f;
                    using var dot = new SolidBrush(color);
                    g.FillEllipse(dot, x - radius, y - radius, 2 * radius, 2 * radius);
                }
                else
                {
                    g.DrawArc(track, 2.25f, 2.25f, 27.5f, 27.5f, segment.StartAngle, segment.SweepAngle);
                    using var stroke = new Pen(color, 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (segment.Battery.HasValue && segment.ProgressAngle > 0 && !error)
                        g.DrawArc(stroke, 2.25f, 2.25f, 27.5f, 27.5f, segment.StartAngle, segment.ProgressAngle);
                    else if (segment.Battery == 0 && !error)
                        g.DrawArc(stroke, 2.25f, 2.25f, 27.5f, 27.5f, segment.StartAngle, .1f);
                }
            }
            if (layout.Count == 0)
            {
                using var idle = new Pen(Color.FromArgb(145, foreground), 4.2f);
                g.DrawEllipse(idle, 2.25f, 2.25f, 27.5f, 27.5f);
            }
            if (error)
            {
                using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(low);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("!", font, brush, new RectangleF(6, 5, 20, 22), format);
            }
            else
            {
                using var diamond = new SolidBrush(foreground);
                g.FillPolygon(diamond, new PointF[] { new(16, 9), new(23, 16), new(16, 23), new(9, 16) });
            }
        }
        return ToIcon(bitmap);
    }

    internal static Icon CreateConcentric(IReadOnlyList<Peripheral> devices, bool error = false, int size = 32, int lowThreshold = Preferences.DefaultLowBatteryThreshold, bool colorRings = false)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = CreateCanvas(size, out var g);
        using (g)
        {
            bool light = IsTaskbarLight();
            var foreground = light ? Color.FromArgb(25, 30, 35) : Color.FromArgb(246, 248, 250);
            var low = Color.FromArgb(246, 87, 94);
            var charging = ChargingColor(light);
            Color[] palette = light
                ? [Color.FromArgb(255, 22, 148, 106), Color.FromArgb(255, 26, 118, 210), Color.FromArgb(255, 208, 128, 16), Color.FromArgb(255, 126, 87, 194)]
                : [Color.FromArgb(255, 86, 217, 165), Color.FromArgb(255, 76, 185, 255), Color.FromArgb(255, 255, 198, 72), Color.FromArgb(255, 192, 152, 255)];
            var layers = ConcentricRing.Layout(devices, lowThreshold);
            if (layers.Count == 0)
            {
                using var idle = new Pen(Color.FromArgb(error ? 110 : 145, foreground), 4.0f);
                g.DrawEllipse(idle, 16 - 12.6f, 16 - 12.6f, 25.2f, 25.2f);
            }
            else
            {
                foreach (var layer in layers)
                {
                    Color baseColor = colorRings ? palette[Math.Min(layer.DeviceIndex, palette.Length - 1)] : foreground;
                    Color color = error ? Color.FromArgb(110, foreground)
                        : layer.Low ? low
                        : layer.Charging ? charging : baseColor;
                    if (layer.IsCenterDot)
                    {
                        if (error) continue;
                        using var dot = new SolidBrush(color);
                        float d = layer.Radius * 2;
                        g.FillEllipse(dot, 16 - layer.Radius, 16 - layer.Radius, d, d);
                        continue;
                    }
                    float diameter = layer.Radius * 2;
                    float origin = 16 - layer.Radius;
                    Color trackBase = error ? foreground : colorRings ? baseColor : foreground;
                    using var track = new Pen(Color.FromArgb(error ? 55 : colorRings ? 76 : 74, trackBase), layer.StrokeWidth);
                    g.DrawEllipse(track, origin, origin, diameter, diameter);
                    if (error || !layer.Battery.HasValue) continue;
                    using var stroke = new Pen(color, layer.StrokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (layer.Battery.Value >= 100)
                        g.DrawEllipse(stroke, origin, origin, diameter, diameter);
                    else if (layer.ProgressAngle > 0)
                        g.DrawArc(stroke, origin, origin, diameter, diameter, -90, layer.ProgressAngle);
                    else
                        g.DrawArc(stroke, origin, origin, diameter, diameter, -90, .1f);
                }
            }
            if (error)
            {
                using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(low);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("!", font, brush, new RectangleF(6, 5, 20, 22), format);
            }
        }
        return ToIcon(bitmap);
    }

    internal static Icon CreateDotRing(IReadOnlyList<Peripheral> devices, bool error = false, int size = 32, int lowThreshold = Preferences.DefaultLowBatteryThreshold)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = CreateCanvas(size, out var g);
        using (g)
        {
            bool light = IsTaskbarLight();
            var foreground = light ? Color.FromArgb(27, 32, 37) : Color.FromArgb(246, 248, 250);
            var low = Color.FromArgb(246, 87, 94);
            var chargingColor = ChargingColor(light);
            var dim = Color.FromArgb(light ? 62 : 68, foreground);
            const float ringRadius = 11.4f;

            if (error)
            {
                using var muted = new SolidBrush(Color.FromArgb(76, foreground));
                for (int i = 0; i < 8; i++)
                {
                    double angle = (-90 + i * 45) * Math.PI / 180.0;
                    float x = 16f + ringRadius * (float)Math.Cos(angle);
                    float y = 16f + ringRadius * (float)Math.Sin(angle);
                    const float r = 2.0f;
                    g.FillEllipse(muted, x - r, y - r, r * 2f, r * 2f);
                }
                using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(low);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("!", font, brush, new RectangleF(6, 5, 20, 22), format);
            }
            else
            {
                int? minLevel = null;
                bool lowestCharging = false;
                foreach (var d in devices)
                {
                    if (d.GaugeValue is not int raw) continue;
                    int clamped = Math.Clamp(raw, 0, 100);
                    bool isCharging = d.Connected && d.Charging;
                    if (!minLevel.HasValue || clamped < minLevel.Value)
                    {
                        minLevel = clamped;
                        lowestCharging = isCharging;
                    }
                    else if (clamped == minLevel.Value && !isCharging)
                    {
                        lowestCharging = false;
                    }
                }

                if (!minLevel.HasValue)
                {
                    bool anyCharging = devices.Any(d => d.Connected && d.Charging);
                    using var idleDot = new SolidBrush(Color.FromArgb(anyCharging ? 68 : 96, foreground));
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = (-90 + i * 45) * Math.PI / 180.0;
                        float x = 16f + ringRadius * (float)Math.Cos(angle);
                        float y = 16f + ringRadius * (float)Math.Sin(angle);
                        const float r = 2.0f;
                        g.FillEllipse(idleDot, x - r, y - r, r * 2f, r * 2f);
                    }
                    float coreR = anyCharging ? 3.3f : 2.6f;
                    using var coreBrush = new SolidBrush(anyCharging ? chargingColor : Color.FromArgb(128, foreground));
                    g.FillEllipse(coreBrush, 16f - coreR, 16f - coreR, coreR * 2f, coreR * 2f);
                }
                else
                {
                    int level = minLevel.Value;
                    Color activeColor = lowestCharging ? chargingColor : level <= lowThreshold ? low : foreground;
                    float exact = level / 100f * 8f;
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = (-90 + i * 45) * Math.PI / 180.0;
                        float x = 16f + ringRadius * (float)Math.Cos(angle);
                        float y = 16f + ringRadius * (float)Math.Sin(angle);
                        float frac = Math.Clamp(exact - i, 0f, 1f);
                        float r = 1.9f + frac * 1.2f;
                        Color dotColor = frac > 0.02f
                            ? Color.FromArgb((int)Math.Round((0.26f + frac * 0.74f) * 255f), activeColor)
                            : dim;
                        using var brush = new SolidBrush(dotColor);
                        g.FillEllipse(brush, x - r, y - r, r * 2f, r * 2f);
                    }

                    const float centerR = 3.3f;
                    using var centerBrush = new SolidBrush(Color.FromArgb(level == 0 ? 64 : 255, activeColor));
                    g.FillEllipse(centerBrush, 16f - centerR, 16f - centerR, centerR * 2f, centerR * 2f);
                }
            }
        }
        return ToIcon(bitmap);
    }

    public static Icon Create(int? level, bool connected, bool error = false, bool ring = true, int size = 32, int lowThreshold = Preferences.DefaultLowBatteryThreshold)
    {
        size = Math.Clamp(size, 16, 256);
        using var bitmap = CreateCanvas(size, out var g);
        using (g)
        {
            if (ring)
            {
                bool light = IsTaskbarLight();
                Color foreground = light ? Color.FromArgb(65, 69, 73) : Color.FromArgb(200, 203, 207);
                Color active = !connected || error ? Color.FromArgb(125, 128, 133)
                    : level <= lowThreshold ? Color.FromArgb(240, 103, 103) : foreground;
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
            }
            else
            {
                Color color = !connected || error ? Color.FromArgb(155, 163, 175)
                    : level <= lowThreshold ? Color.FromArgb(255, 108, 108)
                    : level <= 40 ? Color.FromArgb(255, 199, 91) : Color.FromArgb(107, 216, 191);
                using var brush = new SolidBrush(color);
                using var background = new SolidBrush(Color.FromArgb(75, 90, 100));
                string text = error ? "!" : level?.ToString() ?? "?";
                using var font = new Font("Segoe UI", text.Length == 3 ? 15 : 19, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, brush, new RectangleF(-2, -3, 36, 31), format);
                g.FillRectangle(background, 3, 28, 26, 3);
                if (level.HasValue && !error) g.FillRectangle(brush, 3, 28, 26 * level.Value / 100f, 3);
            }
        }
        return ToIcon(bitmap);
    }

    // Encode a lossless 32-bit BI_RGB ICO stream directly instead of calling Bitmap.GetHicon().
    // GDI+ GetHicon() premultiplies RGB into a straight-alpha DIB, darkening every semi-transparent
    // track and anti-aliased edge into a muddy halo on Windows 11's taskbar.
    private static Icon ToIcon(Bitmap source)
    {
        int width = source.Width;
        int height = source.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        int stride;
        try
        {
            stride = data.Stride;
            pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally { source.UnlockBits(data); }

        int andStride = ((width + 31) / 32) * 4;
        int andSize = andStride * height;
        int xorSize = width * height * 4;
        int imageSize = 40 + xorSize + andSize;

        using var stream = new MemoryStream(22 + imageSize);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: ICO
        writer.Write((ushort)1); // count: 1
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);   // color count
        writer.Write((byte)0);   // reserved
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bits per pixel
        writer.Write((uint)imageSize);
        writer.Write((uint)22);  // image data offset

        writer.Write((uint)40);  // BITMAPINFOHEADER size
        writer.Write(width);
        writer.Write(height * 2); // XOR + AND mask height
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bit count
        writer.Write((uint)0);   // BI_RGB
        writer.Write((uint)(xorSize + andSize));
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        for (int y = height - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, width * 4);
        writer.Write(new byte[andSize]);
        writer.Flush();
        stream.Position = 0;
        return new Icon(stream);
    }
}
