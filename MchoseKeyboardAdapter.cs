namespace BatteryDisplay;

// MCHOSE wireless keyboards expose the pack level in a read-only vendor feature report.
// Layout captured on a G87 receiver: report 0x06, 8 bytes, payload[2] = level, 0..100.
// No configuration or lighting command is ever sent to this collection.
internal sealed class MchoseKeyboardAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public MchoseKeyboardAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "迈从键盘";
    public string Models => "MCHOSE 2.4G Wireless 接收器（0x41E4:0x2001）的 Feature 0x06 电量报告";

    // The vendor ID is shared with other Compx keyboards and the receiver ID is generic,
    // so the feature-only collection and its exact report length are part of the match.
    public bool Matches(HidDescriptor d) => d is { Vendor: 0x41E4, Product: 0x2001 }
        && d.Caps is { UsagePage: 0xFF04, Usage: 2, FeatureLength: ReportLength };

    internal const byte ReportId = 0x06;
    internal const int ReportLength = 8;

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = connect(d);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(30, token);
            var reading = Parse(connection.GetFeature(ReportId, ReportLength), d);
            if (reading is not null) return [reading];
        }
        return [];
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d)
    {
        if (response is null || response.Length < 4 || response[0] != ReportId || response[3] > 100) return null;
        return new(d.Id, "迈从 · MCHOSE 2.4G 键盘", true, response[3], "迈从键盘电量", "2.4G");
    }
}
