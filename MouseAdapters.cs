namespace BatteryDisplay;

internal sealed class MchoseAdapter : IHidBatteryAdapter
{
    public string Name => "迈从";
    public string Models => "AX5 V2、M7、L7、A7 / A7 V2 的已登记有线接口，以及 Realtek 接收器";
    public bool Matches(HidDescriptor d) => d.Caps.UsagePage == 0xFF01 && d.Caps.FeatureLength >= 21 &&
        (IsWired(d) || d is { Vendor: 0x5253, Product: 0x1020 } or { Vendor: 0x3837, Product: 0x100A or 0x100B });

    private static bool IsWired(HidDescriptor d) => d.Caps is { UsagePage: 0xFF01, Usage: 1 } && d is
        { Vendor: 0x5253, Product: 0x0010 or 0x0011 or 0x0020 or 0x0030 or 0x0031 or 0x0070 or 0x00A1 or 0x00B0 or 0x00B1 or 0x00C0 }
        or { Vendor: 0x3837, Product: 0x4010 or 0x4015 or 0x4016 or 0x4018 or 0x4019 or 0x4021 or 0x4023 or 0x4110 };

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        var reports = new List<(byte Id, int Length)> { (0x11, 21) };
        if (d.Caps.FeatureLength > 21) reports.Add((0x11, d.Caps.FeatureLength));
        if (IsWired(d) && d.Caps.FeatureLength >= 65) reports.Add((0x12, d.Caps.FeatureLength));
        foreach (var (id, length) in reports)
        {
            var request = Enumerable.Repeat((byte)0xFF, length).ToArray();
            request[0] = id;
            request[1] = 0xF9;
            if (!connection.SetFeature(request)) continue;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(40, token);
                var response = connection.GetFeature(id, length);
                if (response is null) continue;
                var reading = Parse(response, d, id);
                if (reading is not null) return [reading];
            }
        }
        return [];
    }

    internal static Peripheral? Parse(byte[] response, HidDescriptor d, byte reportId = 0x11) =>
        HidBattery.ParseStatus(response, d.Name, d.Path, IsWired(d) ? d.Product : null, reportId);
}

internal sealed class SinowealthAdapter : IHidBatteryAdapter
{
    public string Name => "WLmouse / LAMZU / Glorious / 迈从 A5";
    public string Models => "Beast X / Max / Mini Pro、Maya X、Model O Wireless、A5 Pro Max";
    public bool Matches(HidDescriptor d) => d.Caps.UsagePage >= 0xFF00 && d.Caps.FeatureLength >= 64 &&
        ((d.Vendor == 0x36A7 && d.Product is 0xA887 or 0xA868 or 0xA880) ||
        (d.Vendor == 0x373E && d.Product is 0x001E or 0x001C) ||
        (d.Vendor == 0x258A && d.Product == 0x2022) ||
        (d.Vendor == 0x2023 && d.Product is 0xF013 or 0xF015 or 0xF019));

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        var request = new byte[65];
        new byte[] { 0, 0, 0, 2, 2, 0, 0x83 }.CopyTo(request, 0);
        if (!connection.SetFeature(request)) return [];
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(50, token);
            var response = connection.GetFeature(0, 65);
            var reading = Parse(response, d);
            if (reading is not null) return [reading];
        }
        return [];
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d)
    {
        if (response is null) return null;
        for (int i = 5; i + 2 < response.Length; i++)
        {
            if (response[i] != 0x83 || response[i - 1] != 0 || response[i - 2] != 2 || response[i - 3] != 2
                || response[i - 4] != 0 || response[i - 5] != 0xA1 || response[i + 2] > 100) continue;
            return new(d.Id, d.Name, true, response[i + 2], "2.4G 状态查询", "USB / 2.4G", response[i + 1] != 0);
        }
        return null;
    }
}

internal sealed class CompxAdapter : IHidBatteryAdapter
{
    public string Name => "ATK / VGN / VXE / Pulsar";
    public string Models => "已登记的 R1 / X1 / A9、Dragonfly 4K、X2 系列；含部分 Hitscan / Scyrox / G-Wolves 接收器";
    public bool Matches(HidDescriptor d) => DeviceCatalog.Find(d, "compx") is not null
        && d.Caps.UsagePage == 0xFF02 && d.Caps.Usage == 2 && d.Caps is { InputLength: >= 17, OutputLength: >= 17 };
    internal static byte[] Request()
    {
        var request = new byte[17];
        request[0] = 8;
        request[1] = 4;
        request[16] = 0x49;
        return request;
    }
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        await connection.WriteAsync(Request(), token);
        for (int i = 0; i < 5; i++)
        {
            var response = await connection.ReadAsync(150, token);
            if (response is null) break;
            var reading = Parse(response, d);
            if (reading is not null) return [reading];
        }
        return [];
    }
    internal static Peripheral? Parse(byte[] r, HidDescriptor d)
    {
        if (r.Length < 17 || r[0] != 8 || r[1] != 4 || r[6] > 100 || (r.Take(17).Sum(b => b) & 255) != 0x55) return null;
        var model = DeviceCatalog.Find(d, "compx");
        return new(d.Id, model?.Name ?? d.Name, true, r[6], "CompX 电量服务", model?.Transport ?? "USB / 2.4G", r[7] != 0);
    }
}
