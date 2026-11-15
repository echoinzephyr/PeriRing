using System.Text.Json;

namespace BatteryDisplay;

internal sealed class BatteryHistory
{
    private readonly Dictionary<string, Peripheral> readings = new(StringComparer.Ordinal);
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "battery-cache.json");

    public List<Peripheral> Apply(IReadOnlyList<Peripheral> snapshot, DateTimeOffset now)
    {
        var result = new List<Peripheral>();
        foreach (var device in snapshot)
        {
            if (device.Connected && (device.Battery.HasValue || device.Gauge.HasValue))
            {
                var fresh = device with { IsCached = false, BatteryUpdatedAt = now };
                if (fresh.ReceiverPresent && !string.IsNullOrEmpty(fresh.ConnectionId)) readings[fresh.Id] = fresh;
                result.Add(fresh);
            }
            else if (!device.Connected && device.ReceiverPresent && !string.IsNullOrEmpty(device.ConnectionId))
            {
                // Only retain readings while this exact physical USB link is enumerated.
                // Never borrow another receiver's percentage or treat a missing receiver as asleep.
                var previous = readings.Values.Where(p => p.ConnectionId == device.ConnectionId).ToArray();
                if (previous.Length == 0) { result.Add(device); continue; }
                result.AddRange(previous.Select(p => p with { Connected = false, IsCached = true, ReceiverPresent = true,
                    Charging = false, Note = null, LevelDescription = p.LevelDescription?.Contains("充电") == true ? null : p.LevelDescription }));
            }
            else result.Add(device);
        }
        return result.GroupBy(d => d.Id).Select(group => group.OrderBy(d => d.IsCached).First()).ToList();
    }

    internal string Export() => JsonSerializer.Serialize(readings.Values.OrderByDescending(d => d.BatteryUpdatedAt).Take(256));
    internal static BatteryHistory Import(string json)
    {
        var history = new BatteryHistory();
        foreach (var reading in (JsonSerializer.Deserialize<List<Peripheral>>(json) ?? []).Take(256))
            if (reading is not null && !string.IsNullOrEmpty(reading.Id) && !string.IsNullOrEmpty(reading.ConnectionId)
                && reading.BatteryUpdatedAt.HasValue && reading.Battery is not (< 0 or > 100) && reading.Gauge is not (< 0 or > 100)
                && (reading.Battery.HasValue || reading.Gauge.HasValue)) history.readings[reading.Id] = reading;
        return history;
    }
    public static BatteryHistory Load()
    {
        try { return Import(File.ReadAllText(FilePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", Export());
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }
}
