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

    public bool IsLow(int? level) => level is int value && value <= LowBatteryThreshold;

    public List<Peripheral> Present(IEnumerable<Peripheral> devices) => devices.Select(d => d with { UseVendorDisplay = PreferVendorDisplay, Alias = DeviceAliases.GetValueOrDefault(d.Id) })
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
