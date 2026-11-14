namespace BatteryDisplay;

internal sealed class SonyAdapter : IHidBatteryAdapter
{
    public string Name => "PlayStation";
    public string Models => "DualShock 4、DualSense、DualSense Edge；蓝牙仅监听已有电池报告";
    public bool Matches(HidDescriptor d) => d.Vendor == 0x054C && d.Product is 0x05C4 or 0x05C5 or 0x09CC or 0x0BA0 or 0x0CE6 or 0x0DF2
        && d.Caps.UsagePage == 1 && d.Caps.Usage is 4 or 5;
    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d, true);
        var deadline = Environment.TickCount64 + 650;
        while (Environment.TickCount64 < deadline)
        {
            var response = await connection.ReadAsync(100, token);
            if (response is null) continue;
            var reading = Parse(response, d);
            if (reading is not null) return [reading];
        }
        return [];
    }
    internal static Peripheral? Parse(byte[] r, HidDescriptor d)
    {
        if (r.Length == 0) return null;
        bool ds = d.Product is 0x0CE6 or 0x0DF2;
        bool bluetooth = r[0] is 0x31 or 0x11;
        int offset = ds ? r[0] switch { 1 => 53, 0x31 => 54, _ => -1 } : r[0] switch { 1 => 30, 0x11 => 32, _ => -1 };
        if (offset < 0 || r.Length <= offset) return null;
        if (d.Product == 0x0BA0 && r.Length > 31 && (r[31] & 4) != 0) return null;
        byte status = r[offset];
        int raw = status & 15;
        if (raw > 11) return null;
        bool charging = ds ? status >> 4 is 1 or 2 : (status & 0x10) != 0;
        int gauge = Math.Min(raw, 10) * 10;
        return new(d.Id, ds ? d.Product == 0x0DF2 ? "Sony DualSense Edge" : "Sony DualSense" : "Sony DualShock 4", true, null,
            "索尼手柄报告", bluetooth ? "蓝牙" : "USB", charging, gauge, $"约 {gauge}%", "已连接 · 10% 档位");
    }
}
