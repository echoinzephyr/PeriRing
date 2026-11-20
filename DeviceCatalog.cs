using System.Text.Json;

namespace BatteryDisplay;

internal sealed record DeviceModel(ushort Vendor, ushort Product, string Name, string Protocol, int Parameter = 0,
    int Interface = -1, int Report = 0, int ReportSize = 65, int Offset = 4, int Scale = 1, int ChargeOffset = 9, string Marker = "", string? Transport = null);
internal static class DeviceCatalog
{
    public static readonly List<DeviceModel> Models = Load();
    private static List<DeviceModel> Load()
    {
        using var stream = typeof(DeviceCatalog).Assembly.GetManifestResourceStream("BatteryDisplay.DeviceCatalog.json")
            ?? throw new InvalidOperationException("设备支持列表缺失");
        return JsonSerializer.Deserialize<List<DeviceModel>>(stream) ?? [];
    }
    public static DeviceModel? Find(HidDescriptor d, string protocol) => Models.FirstOrDefault(m => m.Vendor == d.Vendor && m.Product == d.Product && m.Protocol == protocol);
}
