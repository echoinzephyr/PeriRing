using System.Text.Json;

namespace BatteryDisplay;

internal sealed record Peripheral(string Id, string Name, bool Connected, int? Battery, string Source, string Transport = "蓝牙", bool Charging = false,
    int? Gauge = null, string? LevelDescription = null, string? Note = null, int? VendorDisplay = null, bool UseVendorDisplay = false,
    string? ConnectionId = null, bool ReceiverPresent = false, bool IsCached = false, DateTimeOffset? BatteryUpdatedAt = null, string? Alias = null)
{
    public string DisplayName => Alias ?? (Name.StartsWith("迈从 · ", StringComparison.Ordinal) ? Name[5..] : Name);
    public static bool SupportsAlias(string id) => !id.StartsWith("xinput:", StringComparison.Ordinal);
    public bool IsPresent => Connected || IsCached && ReceiverPresent;
    public bool CanShow => IsPresent && (Battery.HasValue || Gauge.HasValue || Charging);
    public int? DisplayBattery => UseVendorDisplay ? VendorDisplay ?? Battery : Battery;
    public int? GaugeValue => DisplayBattery ?? Gauge;
    public string State => IsCached ? "设备休眠或暂未响应 · 显示上次电量" + (BatteryUpdatedAt is DateTimeOffset time ? $"（{time:MM-dd HH:mm}）" : "")
        : Note ?? (!Connected ? Battery.HasValue ? "已断开 · 电量为上次上报" : "已断开 · 暂无电量数据"
        : Battery.HasValue ? Charging ? "正在充电" : "已连接" : "已连接 · 未上报电量");
    public string Level => LevelDescription ?? (DisplayBattery.HasValue ? $"{DisplayBattery}%" : "—");
    public string TooltipLevel => Level + (IsCached ? "（上次）" : "");
}

internal sealed class Preferences
{
    public const int DefaultLowBatteryThreshold = 20;
    public const int MaxAliasLength = 40;
    internal static readonly int[] LowBatteryThresholds = [10, 15, 20, 25, 30, 35, 40, 45, 50];
    public HashSet<string> HiddenDevices { get; set; } = [];
    public bool LowBatteryAlerts { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = DefaultLowBatteryThreshold;
    public Dictionary<string, string> DeviceAliases { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DeviceLinks { get; set; } = new(StringComparer.Ordinal);
    public bool RingIcons { get; set; } = true;
    public bool PreferVendorDisplay { get; set; } = true;
    public string Theme { get; set; } = "System";
    public List<string> DeviceOrder { get; set; } = [];
    public double PopupWidth { get; set; } = 450;
    public double PopupHeight { get; set; }
    public int RefreshSeconds { get; set; } = 60;
    public bool AutomaticUpdates { get; set; } = true;
    public bool DebugMode { get; set; }
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "settings.json");

    public static Preferences Load()
    {
        try { return FromJson(File.ReadAllText(FilePath)); }
        catch { return new(); }
    }

    internal static Preferences FromJson(string json)
    {
        var result = JsonSerializer.Deserialize<Preferences>(json) ?? new();
        result.HiddenDevices ??= [];
        result.DeviceAliases = (result.DeviceAliases ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && Peripheral.SupportsAlias(entry.Key))
            .Select(entry => (entry.Key, Alias: NormalizeAlias(entry.Value)))
            .Where(entry => entry.Alias is not null).ToDictionary(entry => entry.Key, entry => entry.Alias!, StringComparer.Ordinal);
        result.DeviceLinks = NormalizeLinks(result.DeviceLinks);
        if (!LowBatteryThresholds.Contains(result.LowBatteryThreshold)) result.LowBatteryThreshold = DefaultLowBatteryThreshold;
        result.DeviceOrder = (result.DeviceOrder ?? []).Distinct(StringComparer.Ordinal).ToList();
        result.RefreshSeconds = Math.Clamp(result.RefreshSeconds, 15, 3600);
        result.PopupWidth = Math.Clamp(result.PopupWidth, 340, 1100);
        result.PopupHeight = result.PopupHeight <= 0 ? 0 : Math.Clamp(result.PopupHeight, 240, 1100);
        return result;
    }

    public static string? NormalizeAlias(string? value)
    {
        string? alias = value?.Trim();
        return !string.IsNullOrEmpty(alias) && alias.Length <= MaxAliasLength && !alias.Any(char.IsControl) ? alias : null;
    }

    internal static Dictionary<string, string> NormalizeLinks(Dictionary<string, string>? raw)
    {
        var valid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (secondary, primary) in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(secondary) || string.IsNullOrWhiteSpace(primary)) continue;
            if (secondary == primary || !Peripheral.SupportsAlias(secondary) || !Peripheral.SupportsAlias(primary)) continue;
            valid[secondary] = primary;
        }
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in valid.Keys)
        {
            string current = key;
            var visited = new HashSet<string>(StringComparer.Ordinal) { current };
            bool cycle = false;
            while (valid.TryGetValue(current, out var next))
            {
                if (!visited.Add(next)) { cycle = true; break; }
                current = next;
            }
            if (!cycle && current != key) flattened[key] = current;
        }
        return flattened;
    }

    public string ResolveCanonicalId(string id)
    {
        if (DeviceLinks.Count == 0) return id;
        string current = id;
        var visited = new HashSet<string>(StringComparer.Ordinal) { current };
        while (DeviceLinks.TryGetValue(current, out var next) && visited.Add(next))
            current = next;
        return current;
    }

    public List<string> LinkedIdsFor(string canonicalId)
    {
        string root = ResolveCanonicalId(canonicalId);
        var ids = new List<string> { root };
        foreach (var (secondary, _) in DeviceLinks)
            if (ResolveCanonicalId(secondary) == root && !ids.Contains(secondary, StringComparer.Ordinal))
                ids.Add(secondary);
        return ids;
    }

    public bool IsLow(int? level) => level is int value && value <= LowBatteryThreshold;

    public List<Peripheral> Present(IEnumerable<Peripheral> devices) => devices
        .GroupBy(d => ResolveCanonicalId(d.Id), StringComparer.Ordinal)
        .Select(group =>
        {
            var primary = group.FirstOrDefault(d => d.Id == group.Key);
            var best = group
                .OrderByDescending(d => d.Connected && d.CanShow)
                .ThenByDescending(d => d.Connected)
                .ThenByDescending(d => d.IsPresent && d.CanShow)
                .ThenByDescending(d => d.Battery.HasValue || d.Gauge.HasValue)
                .ThenByDescending(d => d.Id == group.Key)
                .First();
            return best with
            {
                Id = group.Key,
                Name = primary?.Name ?? best.Name,
                UseVendorDisplay = PreferVendorDisplay,
                Alias = DeviceAliases.GetValueOrDefault(group.Key)
            };
        })
        .OrderBy(d => { int index = DeviceOrder.IndexOf(d.Id); return index < 0 ? int.MaxValue : index; })
        .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
