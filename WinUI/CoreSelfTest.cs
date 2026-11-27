using System.Drawing;
using System.Text.Json;

namespace BatteryDisplay;

internal static class CoreSelfTest
{
    public static void Run()
    {
        var failures = new List<string>();
        int checks = ProtocolTests.Run(failures);
        checks += UpdateServiceTests.Run(failures);
        checks += AutoUpdateTests.Run(failures);
        checks += CacheMaintenanceTests.Run(failures);
        checks += BatteryHistoryTests.Run(failures);
        checks += LowBatteryAlertTests.Run(failures);
        checks += BatteryJournalTests.Run(failures);
        if (NativeBattery.NormalizeAddress("aa:bb:cc:dd:ee:ff") != "AABBCCDDEEFF") failures.Add("Bluetooth address normalization");
        foreach (int level in new[] { 0, 9, 20, 40, 75, 80, 100 })
        {
            using var icon = TrayIcon.Create(level, true);
            using var bitmap = icon.ToBitmap();
            if (bitmap.Size != new Size(32, 32)) failures.Add("Tray icon dimensions");
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"icon-{level}.png"));
        }
        var historical = new Peripheral("test", "test", false, 75, "Windows");
        if (historical.CanShow || !historical.State.Contains("上次")) failures.Add("Stale readings are marked historical");
        foreach (var (raw, expected) in new[] { (14, 10), (15, 20), (74, 70), (75, 80), (84, 80), (85, 90), (95, 100), (100, 100) })
        {
            checks++;
            if (HidBattery.VendorBatteryDisplay(raw) != expected) failures.Add($"MCHOSE official display: {raw}");
        }
        var official = new Peripheral("test", "test", true, 75, "迈从", VendorDisplay: 80, UseVendorDisplay: true);
        if (official.DisplayBattery != 80 || official.Battery != 75 || official.GaugeValue != 80) failures.Add("Official display preserves raw reading");
        var rawReading = official with { UseVendorDisplay = false };
        if (rawReading.DisplayBattery != 75) failures.Add("Raw display can be selected");
        foreach (int count in Enumerable.Range(1, 10))
        {
            var devices = Enumerable.Range(0, count).Select(i => new Peripheral($"test-{i}", $"设备 {i + 1}", true, i == count - 1 ? 15 : 80, "fixture")).ToArray();
            var segments = SegmentedRing.Layout(devices);
            checks++;
            if (segments.Count != count || segments.Count(s => !s.IsDot) != (count <= 5 ? count : 3)
                || segments.Count(s => s.IsDot) != (count <= 5 ? 0 : count - 3)) failures.Add($"Segment count for {count} devices");
            checks++;
            var arcs = segments.Where(s => !s.IsDot).ToArray();
            if (arcs.Any(s => Math.Abs(s.SweepAngle - arcs[0].SweepAngle) > .001)) failures.Add($"Equal arc allocations: {count}");
            if (count > 5)
            {
                checks++;
                if (!segments.Single(s => s.DeviceIndex == count - 1).Low || segments.First(s => s.IsDot).Low && count > 6) failures.Add($"Independent low battery dot: {count}");
            }
            using var combined = TrayIcon.CreateCombined(devices);
            using var combinedBitmap = combined.ToBitmap();
            combinedBitmap.Save(Path.Combine(AppContext.BaseDirectory, $"combined-{count}.png"));
        }
        var half = SegmentedRing.Layout([new("a", "a", true, 50, "test"), new("b", "b", true, 100, "test")]);
        checks++;
        if (Math.Abs(half[0].ProgressAngle * 2 - half[1].ProgressAngle) > .001) failures.Add("Arc length is proportional to battery");
        using (var legible = TrayIcon.CreateCombined([new("a", "a", true, 80, "test")], size: 16))
        using (var pixels = legible.ToBitmap())
        {
            checks++;
            int ink = 0;
            for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) if (pixels.GetPixel(x, y).A > 160) ink++;
            if (ink < 65) failures.Add("Small tray mark has enough solid pixels to remain legible");
            checks++;
            if (pixels.GetPixel(8, 8).A < 220) failures.Add("Small tray center remains solid and visible");
        }
        var redDevices = Enumerable.Range(0, 6).Select(i => new Peripheral(i.ToString(), "test", true, i == 5 ? 10 : 80, "test")).ToArray();
        foreach (int count in new[] { 2, 5, 6 })
        {
            var fullDevices = Enumerable.Range(0, count).Select(i => new Peripheral(i.ToString(), "test", true, 100, "test")).ToArray();
            using var separated = TrayIcon.CreateCombined(fullDevices, size: 16);
            using var pixels = separated.ToBitmap();
            checks++;
            if (pixels.GetPixel(7, 1).A > 90 || pixels.GetPixel(8, 1).A > 90)
                failures.Add($"Full batteries retain a visible tray segment gap: {count}");
            pixels.Save(Path.Combine(AppContext.BaseDirectory, $"separated-{count}.png"));
        }
        foreach (int size in new[] { 16, 20, 24, 32, 48 })
        {
            using var dpiIcon = TrayIcon.CreateCombined(redDevices, size: size);
            using var dpiBitmap = dpiIcon.ToBitmap();
            checks++;
            if (dpiBitmap.Width != size || dpiBitmap.Height != size || dpiBitmap.GetPixel(0, 0).A != 0)
                failures.Add($"Native tray icon size and transparency: {size}");
            dpiBitmap.Save(Path.Combine(AppContext.BaseDirectory, $"combined-dpi-{size}.png"));
        }
        var priority = new Preferences { DeviceOrder = ["5", "2", "0"] };
        priority.HiddenDevices.Add("2");
        var ordered = priority.Present(redDevices);
        checks++;
        if (!ordered.Take(3).Select(d => d.Id).SequenceEqual(new[] { "5", "2", "0" })) failures.Add("User device priority controls order");
        checks++;
        if (ordered.Where(d => d.CanShow && !priority.HiddenDevices.Contains(d.Id)).Any(d => d.Id == "2")) failures.Add("Device display switch removes selected device");
        checks++;
        var restored = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(priority));
        if (restored is null || !restored.DeviceOrder.SequenceEqual(priority.DeviceOrder) || !restored.HiddenDevices.Contains("2")) failures.Add("Device order and display switches persist");
        using (var icon = TrayIcon.CreateCombined(redDevices))
        using (var image = icon.ToBitmap())
        {
            int redPixels = 0;
            for (int x = 0; x < image.Width; x++) for (int y = 0; y < image.Height; y++)
            { var c = image.GetPixel(x, y); if (c.A > 100 && c.R > 180 && c.G < 140 && c.B < 150) redPixels++; }
            checks++;
            if (redPixels == 0) failures.Add("Low battery dot renders red pixels");
        }
        using (var errorIcon = TrayIcon.CreateCombined([], error: true, size: 32))
        using (var errorImage = errorIcon.ToBitmap())
        {
            int alertPixels = 0;
            for (int x = 10; x < 22; x++) for (int y = 8; y < 24; y++)
            { var c = errorImage.GetPixel(x, y); if (c.A > 100 && c.R > 180 && c.G < 140 && c.B < 150) alertPixels++; }
            checks++;
            if (alertPixels == 0) failures.Add("Error state renders a distinct center alert mark");
        }
        foreach (int count in new[] { 1, 2, 3, 4, 5 })
        {
            var sampleDevices = Enumerable.Range(0, count).Select(i => new Peripheral($"ring-{i}", $"设备 {i + 1}", true, i == count - 1 && count >= 4 ? 12 : 55 + i * 12, "fixture")).ToArray();
            var layers = ConcentricRing.Layout(sampleDevices);
            checks++;
            int expectedLayers = count <= 3 ? count : 4;
            bool nonOverlapping = true;
            for (int i = 0; i < layers.Count - 1; i++)
            {
                float outerInnerEdge = layers[i].Radius - layers[i].StrokeWidth / 2f;
                float innerOuterEdge = layers[i + 1].IsCenterDot ? layers[i + 1].Radius : layers[i + 1].Radius + layers[i + 1].StrokeWidth / 2f;
                if (outerInnerEdge - innerOuterEdge < 1.2f) nonOverlapping = false;
            }
            if (layers.Count != expectedLayers || !nonOverlapping || (count >= 4 && (!layers[^1].IsCenterDot || !layers[^1].Low)))
                failures.Add($"Concentric ring geometry for {count} devices");
            if (count <= 4)
            {
                using var cIcon = TrayIcon.CreateConcentric(sampleDevices, size: 32);
                using var cBmp = cIcon.ToBitmap();
                cBmp.Save(Path.Combine(AppContext.BaseDirectory, $"concentric-{count}.png"));
                if (count == 2)
                {
                    checks++;
                    if (cBmp.GetPixel(16, 16).A != 0) failures.Add("Two concentric rings keep a clean open center");
                }
                if (count == 4)
                {
                    int overflowRed = 0;
                    for (int x = 13; x <= 19; x++) for (int y = 13; y <= 19; y++)
                    { var c = cBmp.GetPixel(x, y); if (c.A > 100 && c.R > 180 && c.G < 140 && c.B < 150) overflowRed++; }
                    checks++;
                    if (overflowRed == 0) failures.Add("Concentric overflow low-battery center dot renders red");
                }
            }
        }
        using (var colorIcon = TrayIcon.CreateConcentric(
            [new("c0", "A", true, 75, "t"), new("c1", "B", true, 60, "t"), new("c2", "C", true, 85, "t")], size: 32, colorRings: true))
        using (var colorBmp = colorIcon.ToBitmap())
        {
            colorBmp.Save(Path.Combine(AppContext.BaseDirectory, "concentric-color-3.png"));
            int chromatic = 0;
            for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++)
            {
                var c = colorBmp.GetPixel(x, y);
                if (c.A > 120 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 40) chromatic++;
            }
            checks++;
            if (chromatic < 40) failures.Add("Colorful concentric rings render distinct chromatic strokes");
        }
        using (var cErr = TrayIcon.CreateConcentric([], error: true, size: 32))
        using (var cErrBmp = cErr.ToBitmap())
        {
            int alertPixels = 0;
            for (int x = 10; x < 22; x++) for (int y = 8; y < 24; y++)
            { var c = cErrBmp.GetPixel(x, y); if (c.A > 100 && c.R > 180 && c.G < 140 && c.B < 150) alertPixels++; }
            checks++;
            if (alertPixels == 0) failures.Add("Concentric error state renders a distinct center alert mark");
        }
        using (var chgCombined = TrayIcon.CreateCombined([new("chg-1", "Mouse", true, 40, "t", Charging: true)], size: 20))
        using (var chgCombinedBmp = chgCombined.ToBitmap())
        {
            int green = 0, darkTrackHalos = 0;
            bool light = TrayIcon.IsTaskbarLight();
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++)
            {
                var c = chgCombinedBmp.GetPixel(x, y);
                if (c.A > 120 && c.G > c.R + 30) green++;
                if (!light && c.A >= 40 && c.A <= 100 && c.R < 180 && c.G < 180 && c.B < 180) darkTrackHalos++;
            }
            checks++;
            if (green < 15) failures.Add("Segmented ring turns green while charging");
            checks++;
            if (darkTrackHalos > 0) failures.Add($"Lossless 32-bit ICO preserves bright translucent track without dark premultiplication halo ({darkTrackHalos})");
        }
        using (var chgConcentric = TrayIcon.CreateConcentric([new("chg-1", "Mouse", true, 40, "t", Charging: true)], size: 20))
        using (var chgConcentricBmp = chgConcentric.ToBitmap())
        {
            int green = 0;
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++)
            { var c = chgConcentricBmp.GetPixel(x, y); if (c.A > 120 && c.G > c.R + 30) green++; }
            checks++;
            if (green < 15) failures.Add("Concentric ring turns green while charging");
        }
        using (var dNorm = TrayIcon.CreateDotRing([new("d0", "A", true, 72, "t"), new("d1", "B", true, 88, "t")], size: 32))
        using (var dNormBmp = dNorm.ToBitmap())
        {
            dNormBmp.Save(Path.Combine(AppContext.BaseDirectory, "dotring-72.png"));
            checks++;
            if (dNormBmp.GetPixel(16, 16).A < 220) failures.Add("DotRing renders solid center core dot");
        }
        using (var dLow = TrayIcon.CreateDotRing([new("d0", "A", true, 72, "t"), new("d1", "B", true, 15, "t")], size: 32))
        using (var dLowBmp = dLow.ToBitmap())
        {
            dLowBmp.Save(Path.Combine(AppContext.BaseDirectory, "dotring-low.png"));
            int red = 0;
            for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++)
            { var c = dLowBmp.GetPixel(x, y); if (c.A > 120 && c.R > 180 && c.G < 140 && c.B < 150) red++; }
            checks++;
            if (red < 20) failures.Add("DotRing low battery renders red dots");
        }
        using (var dChg = TrayIcon.CreateDotRing([new("d0", "A", true, 65, "t", Charging: true)], size: 32))
        using (var dChgBmp = dChg.ToBitmap())
        {
            dChgBmp.Save(Path.Combine(AppContext.BaseDirectory, "dotring-charging.png"));
            int green = 0;
            for (int x = 0; x < 32; x++) for (int y = 0; y < 32; y++)
            { var c = dChgBmp.GetPixel(x, y); if (c.A > 120 && c.G > c.R + 30) green++; }
            checks++;
            if (green < 20) failures.Add("DotRing charging state renders green dots");
        }
        using (var dIdle = TrayIcon.CreateDotRing([], size: 32))
        using (var dIdleBmp = dIdle.ToBitmap())
        {
            dIdleBmp.Save(Path.Combine(AppContext.BaseDirectory, "dotring-idle.png"));
            checks++;
            if (dIdleBmp.GetPixel(16, 16).A < 80) failures.Add("DotRing idle state keeps visible center mark");
        }
        using (var dErr = TrayIcon.CreateDotRing([], error: true, size: 32))
        using (var dErrBmp = dErr.ToBitmap())
        {
            int alertPixels = 0;
            for (int x = 10; x < 22; x++) for (int y = 8; y < 24; y++)
            { var c = dErrBmp.GetPixel(x, y); if (c.A > 100 && c.R > 180 && c.G < 140 && c.B < 150) alertPixels++; }
            checks++;
            if (alertPixels == 0) failures.Add("DotRing error state renders a distinct center alert mark");
        }
        var legacyNumber = Preferences.FromJson("""{"RingIcons":false}""");
        var legacyNumberStyle = Preferences.FromJson("""{"TrayIconStyle":"Number"}""");
        var legacySegmented = Preferences.FromJson("""{"RingIcons":true}""");
        var concentricPref = Preferences.FromJson("""{"TrayIconStyle":"Concentric","ConcentricColorRings":true}""");
        var dotRingPref = Preferences.FromJson("""{"TrayIconStyle":"DotRing"}""");
        checks++;
        if (legacyNumber.EffectiveTrayIconStyle != "DotRing" || legacyNumberStyle.EffectiveTrayIconStyle != "DotRing"
            || legacySegmented.EffectiveTrayIconStyle != "Segmented"
            || concentricPref.EffectiveTrayIconStyle != "Concentric" || !concentricPref.ConcentricColorRings || !concentricPref.RingIcons
            || dotRingPref.EffectiveTrayIconStyle != "DotRing" || dotRingPref.RingIcons)
            failures.Add("Tray icon style migrates legacy preferences and preserves Concentric/DotRing styles");
        checks++;
        if (FlyoutWindow.DeviceGlyph(new("ds-edge", "Sony DualSense Edge", true, null, "索尼手柄报告")) != "\uE967"
            || FlyoutWindow.DeviceGlyph(new("vk75", "Valkyrie 75", true, 80, "瓦尔基里 / SONiX 键盘")) != "\uE765"
            || FlyoutWindow.DeviceGlyph(new("v75x", "ATK V75X", true, 90, "ATK 键盘电量")) != "\uE765"
            || FlyoutWindow.DeviceGlyph(new("v9", "迈从 MCHOSE V9 Turbo+", true, 76, "迈从耳机电量服务")) != "\uE7F6"
            || FlyoutWindow.DeviceGlyph(new("ax5", "MCHOSE AX5 V2", true, 80, "迈从接收器")) != "\uE962")
            failures.Add("DeviceGlyph distinguishes controllers, keyboards, headsets and mice");
        checks++;
        if (DeviceCatalog.Models.Any(m => DeviceCatalog.Find(new("test", m.Vendor, m.Product, m.Name, default), m.Protocol) != m))
            failures.Add("DeviceCatalog O(1) index matches every registered model");
        bool wasDebugEnabled = DebugService.Enabled;
        try
        {
            DebugService.Enabled = false;
            var sampleDescriptor = new HidDescriptor("test-forced-observe", 0x3837, 0x600A, "Test Device", default);
            DebugService.Observe([sampleDescriptor], force: true);
            checks++;
            if (DebugService.Enabled || DebugService.Inventory.Count != 1 || DebugService.Inventory[0].Vendor != "3837")
                failures.Add("Forced inventory observation does not mutate DebugService.Enabled");
        }
        finally { DebugService.Enabled = wasDebugEnabled; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, ProtocolChecks = checks, Failures = failures }));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
}
