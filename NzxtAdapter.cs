namespace BatteryDisplay;

internal sealed class NzxtAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public NzxtAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "NZXT";
    public string Models => "Lift Elite Wireless（USB 有线 / 2.4G）";

    public bool Matches(HidDescriptor d) => d is { Vendor: 0x1E71, Product: 0x2101 or 0x2129 }
        && d.Caps is { UsagePage: 0xFFCA, Usage: 1, InputLength: >= 27, OutputLength: >= 64 };

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = connect(d);
        // Drain queued telemetry before requesting a new sample.
        for (int i = 0; i < 3; i++)
            if (await connection.ReadAsync(1, token) is null) break;
        await connection.WriteAsync(BatteryRequest(), token);
        var reading = await connection.ReadUntilAsync(10, 150, response => Parse(response, d), token);
        return reading is not null ? [reading] : [];
    }

    internal static byte[] BatteryRequest()
    {
        var request = new byte[64];
        request[0] = 0x4E;
        request[1] = 0x02;
        request[2] = 0x81;
        request[4] = 0xB0;
        return request;
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d)
    {
        if (response is null || response.Length < 27 || response[0] != 0x4E || response[1] != 0x02 || response[2] != 0x97) return null;
        int percent = response[17] | response[18] << 8;
        if (percent > 100) return null;
        return new(d.Id, "NZXT Lift Elite Wireless", true, percent, "NZXT 电量服务", d.Product == 0x2129 ? "USB" : "2.4G",
            (response[22] & 1) != 0);
    }
}
