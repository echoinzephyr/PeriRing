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
        var redDevices = Enumerable.Range(0, 6).Select(i => new Peripheral(i.ToString(), "test", true, i == 5 ? 10 : 80, "test")).ToArray();
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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, ProtocolChecks = checks, Failures = failures }));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
}
