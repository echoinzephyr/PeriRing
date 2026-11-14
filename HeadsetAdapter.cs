namespace BatteryDisplay;

internal sealed class HeadsetAdapter : IHidBatteryAdapter
{
    public string Name => "赛睿耳机";
    public string Models => "已登记的 Arctis Nova 7 / 5 / 3 Wireless、Arctis 7+、GameBuds";
    public bool Matches(HidDescriptor d) => DeviceCatalog.Find(d, "steelseries") is not null && d.Caps.UsagePage == 0xFFC0 && d.Caps.OutputLength >= 2;
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        await connection.WriteAsync([0, 0xB0], token);
        for (int i = 0; i < 6; i++)
        {
            var response = await connection.ReadAsync(120, token);
            if (response is null) break;
            var reading = Parse(response, d);
            if (reading is not null) return [reading];
        }
        return [];
    }
    internal static Peripheral? Parse(byte[] response, HidDescriptor d)
    {
        var model = DeviceCatalog.Find(d, "steelseries");
        if (model is null) return null;
        var r = response.Length > 0 && response[0] == 0 ? response[1..] : response;
        if (r.Length < 4 || r[0] != 0xB0) return null;
        int level;
        bool charging;
        bool discrete = model.Parameter is 1 or 3;
        switch (model.Parameter)
        {
            case 0: case 1:
                if (r[1] != 3 || r[3] == 0) return null;
                level = r[2]; charging = r[3] is 1 or 2;
                break;
            case 2:
                if (r.Length < 5 || r[1] == 2) return null;
                level = r[3]; charging = r[4] == 1;
                break;
            case 3:
                if (r[1] == 1) return null;
                level = r[2]; charging = r[3] == 1;
                break;
            case 4:
                if (r.Length < 7) return null;
                var levels = new List<byte>();
                if (r[3] == 3) levels.Add(r[5]);
                if (r[4] == 3) levels.Add(r[6]);
                if (levels.Count == 0) return null;
                level = levels.Min(); charging = false;
                break;
            default: return null;
        }
        if (level > (discrete ? 4 : 100)) return null;
        return discrete ? new(d.Id, model.Name, true, null, "赛睿电量服务", "2.4G", charging, level * 25, $"约 {level * 25}%", "已连接 · 设备仅上报档位")
            : new(d.Id, model.Name, true, level, "赛睿电量服务", "2.4G", charging);
    }
}
