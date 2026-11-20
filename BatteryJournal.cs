using System.Text.Json;

namespace BatteryDisplay;

internal sealed record BatterySample(DateTimeOffset Time, int Percent, int? VendorPercent, bool Charging, bool StartsSegment)
{
    public int Level(bool preferVendor) => preferVendor ? VendorPercent ?? Percent : Percent;
}

internal sealed record BatteryJournalDevice(string Id, string Name, string Transport, List<BatterySample> Samples);

internal sealed class BatteryJournal
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(30);
    internal const int MaxDevices = 32;
    internal const int MaxSamples = 1024;
    private readonly string? path;
    private readonly Dictionary<string, BatteryJournalDevice> devices = new(StringComparer.Ordinal);
    private HashSet<string> continuous = new(StringComparer.Ordinal);
    private DateTimeOffset? lastScan;
    private bool dirty;

    internal BatteryJournal(string? path = null) => this.path = path;

    public static BatteryJournal Load(string path, DateTimeOffset now)
    {
        var journal = new BatteryJournal(path);
        if (!File.Exists(path)) return journal;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<BatteryJournalDevice>>(File.ReadAllText(path));
            foreach (var device in loaded ?? [])
            {
                if (device is null || string.IsNullOrWhiteSpace(device.Id) || device.Samples is null) continue;
                if (device.Samples.RemoveAll(sample => sample is null || sample.Percent is < 0 or > 100 || sample.VendorPercent is < 0 or > 100 || sample.Time > now) > 0)
                    journal.dirty = true;
                device.Samples.Sort((left, right) => left.Time.CompareTo(right.Time));
                journal.devices[device.Id] = device;
            }
            journal.Prune(now);
            journal.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { DebugService.Log("battery-journal", "load-failed", new { exception = ex.GetType().Name }); }
        return journal;
    }

    public void Observe(IEnumerable<Peripheral> readings, DateTimeOffset now)
    {
        Prune(now);
        if (lastScan is DateTimeOffset previous && (now < previous || now - previous > MaximumGap)) BreakContinuity();
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            if (!reading.Connected || reading.IsCached || reading.Battery is not (>= 0 and <= 100) || !current.Add(reading.Id)) continue;
            var device = devices.GetValueOrDefault(reading.Id) ?? new(reading.Id, reading.Name, reading.Transport, []);
            var last = device.Samples.LastOrDefault();
            int? vendor = reading.VendorDisplay is >= 0 and <= 100 ? reading.VendorDisplay : null;
            bool startsSegment = last is null || !continuous.Contains(reading.Id);
            if (startsSegment || last is not null && (last.Percent != reading.Battery || last.VendorPercent != vendor
                || last.Charging != reading.Charging || now - last.Time >= SampleInterval))
            {
                device.Samples.Add(new(now, reading.Battery.Value, vendor, reading.Charging, startsSegment));
                dirty = true;
            }
            if (device.Name != reading.Name || device.Transport != reading.Transport)
            {
                device = device with { Name = reading.Name, Transport = reading.Transport };
                dirty = true;
            }
            devices[reading.Id] = device;
        }
        continuous = current;
        lastScan = now;
        Prune(now);
        Save();
    }

    public void BreakContinuity() => continuous.Clear();

    public List<BatteryJournalDevice> Read(DateTimeOffset now)
    {
        Prune(now);
        Save();
        return devices.Values.OrderByDescending(device => device.Samples[^1].Time).ToList();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var device in devices.Values)
        {
            if (device.Samples.RemoveAll(sample => sample.Time < now - Retention || sample.Time > now) > 0) dirty = true;
            if (device.Samples.Count <= MaxSamples) continue;
            device.Samples.RemoveRange(0, device.Samples.Count - MaxSamples);
            dirty = true;
        }
        var remove = devices.Values.Where(device => device.Samples.Count == 0).Select(device => device.Id)
            .Concat(devices.Values.Where(device => device.Samples.Count > 0)
                .OrderByDescending(device => device.Samples[^1].Time).ThenBy(device => device.Id, StringComparer.Ordinal)
                .Skip(MaxDevices).Select(device => device.Id)).ToArray();
        foreach (string id in remove) dirty |= devices.Remove(id);
    }

    private void Save()
    {
        if (!dirty || path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(devices.Values));
            File.Move(temp, path, true);
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DebugService.Log("battery-journal", "save-failed", new { exception = ex.GetType().Name }); }
    }
}
