using System.Text.Json;

namespace BatteryDisplay;

internal sealed record DeviceModel(ushort Vendor, ushort Product, string Name, string Protocol, int Parameter = 0,
    int Interface = -1, int Report = 0, int ReportSize = 65, int Offset = 4, int Scale = 1, int ChargeOffset = 9, string Marker = "", string? Transport = null);
internal static class DeviceCatalog
{
    public static readonly List<DeviceModel> Models = Load();
    private static readonly Dictionary<(string Protocol, ushort Vendor, ushort Product), DeviceModel> Index = BuildIndex(Models);
    private static List<DeviceModel> Load()
    {
        using var stream = typeof(DeviceCatalog).Assembly.GetManifestResourceStream("BatteryDisplay.DeviceCatalog.json")
            ?? throw new InvalidOperationException("设备支持列表缺失");
        return JsonSerializer.Deserialize<List<DeviceModel>>(stream) ?? [];
    }
    private static Dictionary<(string Protocol, ushort Vendor, ushort Product), DeviceModel> BuildIndex(List<DeviceModel> models)
    {
        var map = new Dictionary<(string Protocol, ushort Vendor, ushort Product), DeviceModel>(models.Count);
        foreach (var model in models) map.TryAdd((model.Protocol, model.Vendor, model.Product), model);
        return map;
    }
    public static DeviceModel? Find(HidDescriptor d, string protocol) =>
        Index.GetValueOrDefault((protocol, d.Vendor, d.Product));
}
