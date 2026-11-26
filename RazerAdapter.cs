namespace BatteryDisplay;

internal sealed class RazerAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public RazerAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "雷蛇";
    public string Models => "已登记的 Viper / DeathAdder / Basilisk / Naga / Cobra / Pro Click，以及 BlackWidow / DeathStalker 无线键盘";
    public bool Matches(HidDescriptor d) => DeviceCatalog.Find(d, "razer") is not null && d.Caps.FeatureLength >= 91;

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        var model = DeviceCatalog.Find(d, "razer")!;
        using var connection = connect(d);
        async Task<int?> Query(byte command)
        {
            byte[] request = BuildRequest((byte)model.Parameter, command);
            if (!connection.SetFeature(request)) return null;
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(d.Product is 0x0062 or 0x0094 ? 400 : 65, token);
                var response = connection.GetFeature(0, 91);
                if (response is null) continue;
                int? value = ParseReply(response, command);
                if (value.HasValue) return value;
                if (response.Length >= 2 && response[1] is 4 or 5) return null;
            }
            return null;
        }
        int? raw = await Query(0x80);
        if (!raw.HasValue) return [];
        int? charge = await Query(0x84);
        return [new(d.Id, model.Name, true, (raw.Value * 100 + 127) / 255, "雷蛇电量服务", model.Transport ?? "USB / 2.4G", charge is > 0)];
    }
    internal static byte[] BuildRequest(byte transaction, byte command)
    {
        var report = new byte[91];
        report[2] = transaction;
        report[6] = 2; report[7] = 7; report[8] = command;
        for (int i = 3; i <= 88; i++) report[89] ^= report[i];
        return report;
    }
    internal static int? ParseReply(byte[] r, byte command)
    {
        if (r.Length < 91 || r[0] != 0 || r[1] != 2 || r[7] != 7 || r[8] != command || r[6] < 2) return null;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= r[i];
        return crc == r[89] ? r[10] : null;
    }
}
