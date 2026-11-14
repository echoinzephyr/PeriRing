using System.Text.Json;

namespace BatteryDisplay;

internal sealed record Peripheral(string Id, string Name, bool Connected, int? Battery, string Source, string Transport = "蓝牙", bool Charging = false,
    int? Gauge = null, string? LevelDescription = null, string? Note = null, int? VendorDisplay = null, bool UseVendorDisplay = false)
{
    public bool CanShow => Connected && (Battery.HasValue || Gauge.HasValue || Charging);
    public int? DisplayBattery => UseVendorDisplay ? VendorDisplay ?? Battery : Battery;
    public int? GaugeValue => DisplayBattery ?? Gauge;
    public string State => Note ?? (!Connected ? Battery.HasValue ? "已断开 · 电量为上次上报" : "已断开 · 暂无电量数据"
        : Battery.HasValue ? Charging ? "正在充电" : "已连接" : "已连接 · 未上报电量");
    public string Level => LevelDescription ?? (DisplayBattery.HasValue ? $"{DisplayBattery}%" : "—");
}

internal sealed class Preferences
{
    public HashSet<string> HiddenDevices { get; set; } = [];
    public bool LowBatteryAlerts { get; set; } = true;
    public bool RingIcons { get; set; } = true;
    public bool PreferVendorDisplay { get; set; } = true;
    public string Theme { get; set; } = "System";
    public List<string> DeviceOrder { get; set; } = [];
    public double PopupWidth { get; set; } = 450;
    public double PopupHeight { get; set; }
    public int RefreshSeconds { get; set; } = 60;
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "settings.json");

    public static Preferences Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            result.HiddenDevices ??= [];
            result.DeviceOrder = (result.DeviceOrder ?? []).Distinct(StringComparer.Ordinal).ToList();
            result.RefreshSeconds = Math.Clamp(result.RefreshSeconds, 15, 3600);
            result.PopupWidth = Math.Clamp(result.PopupWidth, 340, 1100);
            result.PopupHeight = result.PopupHeight <= 0 ? 0 : Math.Clamp(result.PopupHeight, 240, 1100);
            return result;
        }
        catch { return new(); }
    }

    public List<Peripheral> Present(IEnumerable<Peripheral> devices) => devices.Select(d => d with { UseVendorDisplay = PreferVendorDisplay })
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
