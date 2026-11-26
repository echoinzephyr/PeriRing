namespace BatteryDisplay;

internal sealed class AsusAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public AsusAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "华硕 ROG / TUF";
    public string Models => "已登记的 Harpe、Keris、Gladius、Chakram、Spatha、Pugio、TUF M4 等无线鼠标";
    public bool Matches(HidDescriptor d)
    {
        var model = DeviceCatalog.Find(d, "asus");
        return model is not null && d.Path.Contains($"&mi_{model.Interface:X2}", StringComparison.OrdinalIgnoreCase)
            && d.Caps.OutputLength >= model.ReportSize && (model.Marker.Length == 0 || d.Path.Contains(model.Marker, StringComparison.OrdinalIgnoreCase));
    }
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        var model = DeviceCatalog.Find(d, "asus")!;
        using var connection = connect(d);
        await connection.WriteAsync([(byte)model.Report, 0x12, 7], token);
        var reading = await connection.ReadUntilAsync(6, 100, response => Parse(response, d), token, stopOnTimeout: true);
        return reading is not null ? [reading] : [];
    }
    internal static Peripheral? Parse(byte[] response, HidDescriptor d)
    {
        var model = DeviceCatalog.Find(d, "asus");
        if (model is null) return null;
        var r = response.Length >= 3 && response[0] == model.Report && response[1] == 0x12 && response[2] == 7 ? response[1..] : response;
        if (r.Length <= Math.Max(model.Offset, model.ChargeOffset) || r[0] != 0x12 || r[1] != 7) return null;
        int value = r[model.Offset] * model.Scale;
        if (value > 100) return null;
        return model.Scale == 1 ? new(d.Id, model.Name, true, value, "华硕电量服务", "USB / 2.4G", r[model.ChargeOffset] != 0)
            : new(d.Id, model.Name, true, null, "华硕电量服务", "USB / 2.4G", r[model.ChargeOffset] != 0, value, $"约 {value}%", "已连接 · 设备仅上报档位");
    }
}

internal sealed class HyperXAdapter : IHidBatteryAdapter
{
    public string Name => "HyperX 耳机";
    public string Models => "Cloud III Wireless、Cloud II Core Wireless";
    public bool Matches(HidDescriptor d) => d.Vendor == 0x03F0 && d.Product is 0x05B7 or 0x0C9D or 0x0795 or 0x0995
        && d.Caps.UsagePage == 0xFF13 && d.Caps.Usage == 1;
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        async Task<byte[]?> Query(byte command)
        {
            var request = new byte[62]; request[0] = 0x66; request[1] = command;
            bool sent = false;
            if (d.Caps.OutputLength >= 62) { try { await connection.WriteAsync(request, token); sent = true; } catch (IOException) { } }
            if (!sent && !connection.SetFeature(request)) return null;
            for (int i = 0; i < 6; i++)
            {
                var r = await connection.ReadAsync(150, token);
                if (r is { Length: >= 3 } && r[0] == 0x66 && (r[1] == command || r[1] == (command == 0x89 ? 0x0D : 0x0C))) return r;
            }
            return null;
        }
        int? level = ParseBattery(await Query(0x89));
        if (!level.HasValue) return [];
        var charge = await Query(0x8A);
        return [new(d.Id, d.Product is 0x05B7 or 0x0C9D ? "HyperX Cloud III Wireless" : "HyperX Cloud II Core Wireless", true,
            level, "HyperX 电量服务", "2.4G", charge is { Length: >= 3 } && charge[2] is 1 or 2)];
    }
    internal static int? ParseBattery(byte[]? r) => r is { Length: >= 5 } && r[0] == 0x66 && r[1] is 0x89 or 0x0D
        && (r[2] != 0 || r[3] != 0) && r[4] <= 100 ? r[4] : null;
}

internal sealed class KeychronAdapter : IHidBatteryAdapter
{
    public string Name => "Keychron";
    public string Models => "M5、Ultra-Link 8K、Nape Pro 接收器";
    public bool Matches(HidDescriptor d) => d.Vendor == 0x3434 && d.Product is 0xD028 or 0xD048 or 0xD026
        && d.Caps.UsagePage >= 0xFF00 && d.Caps.FeatureLength >= 64;
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        var request = new byte[64]; request[0] = 0xB3; request[1] = 6;
        if (!connection.SetFeature(request)) return [];
        for (int i = 0; i < 5; i++)
        {
            var r = await connection.ReadAsync(100, token);
            if (r is { Length: > 20 } && r[0] == 0xB4 && r[1] == 6 && r[20] <= 100)
                return [new(d.Id, d.Name, true, r[20], "Keychron 电量服务", "USB / 2.4G")];
        }
        return [];
    }
}

internal sealed class CorsairAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public CorsairAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "海盗船";
    public string Models => "Dark Core RGB Pro SE 接收器、K65 PLUS Wireless 接收器；Sabre v2 由 CompX 接入";
    public bool Matches(HidDescriptor d) => d.Vendor == 0x1B1C && d.Product is 0x1B7F or 0x2B07
        && d.Caps.UsagePage == 0xFF42 && d.Caps.OutputLength >= 65;
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = connect(d);
        int? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await connection.WriteAsync([0, 9, 2, 0x0F], token);
            for (int i = 0; i < 4; i++)
            {
                var response = await connection.ReadAsync(100, token);
                int? value = Parse(response);
                if (!value.HasValue) continue;
                if (last == value) return [new(d.Id, d.Product == 0x2B07 ? "Corsair K65 PLUS Wireless" : "Corsair Dark Core RGB Pro SE",
                    true, (int)Math.Round(value.Value / 10d), "Corsair 电量服务", "2.4G")];
                last = value;
                break;
            }
        }
        return [];
    }
    internal static int? Parse(byte[]? response)
    {
        if (response is null) return null;
        var r = response.Length > 0 && response[0] == 0 ? response[1..] : response;
        if (r.Length < 5 || r[0] != 1 || r[1] != 2 || r[2] != 0) return null;
        int value = r[3] | r[4] << 8;
        return value is > 0 and <= 1000 ? value : null;
    }
}
