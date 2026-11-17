namespace BatteryDisplay;

// C-Media receiver protocol, separate from MCHOSE mice. Reference (MIT):
// https://github.com/brayanspagnol/OpenMHub/blob/f77c000334ad9e34499d7acab315a0d9ee8789b7/app/renderer/drivers/headset.js
// Attribution is retained in THIRD-PARTY-NOTICES.md. Windows hardware validation is pending.
internal sealed class MchoseHeadsetAdapter : IHidBatteryAdapter
{
    internal const int ReportLength = 64;
    private const byte ReportId = 0x55;
    private const byte BatteryCommand = 0x65;
    private const string DeviceName = "迈从 MCHOSE V9 Turbo+";
    public string Name => "迈从 V9 Turbo+ 耳机";
    public string Models => "V9 Turbo+ 2.4G 接收器（3837:600A）；待 Windows 硬件实测";

    // Exclude audio/input collections, the 6008 RGB controller and other models.
    public bool Matches(HidDescriptor d) => MatchesReceiver(d);
    private static bool MatchesReceiver(HidDescriptor d) => d.Vendor == 0x3837 && d.Product == 0x600A
        && d.Path.Contains("&mi_00", StringComparison.OrdinalIgnoreCase) && d.Caps.UsagePage >= 0xFF00;
    internal static bool HasExpectedReports(HidDescriptor d) => d.Caps.InputLength == ReportLength
        && d.Caps.OutputLength == ReportLength;
    internal static byte[] BatteryRequest() => [ReportId, BatteryCommand, 0x01];

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MatchesReceiver(d)) return [];
        if (!HasExpectedReports(d))
            return [new(d.Id, DeviceName, false, null, Name, "2.4G",
                Note: $"已识别 · HID 报告格式待适配（输入 {d.Caps.InputLength} / 输出 {d.Caps.OutputLength}）")];

        using var connection = new HidConnection(d);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Pads to OutputLength; reads and writes share the interrupt stream.
            // This adapter sends no configuration or firmware commands.
            await connection.WriteAsync(BatteryRequest(), token);
            for (int read = 0; read < 6; read++)
            {
                var response = await connection.ReadAsync(150, token);
                if (response is null) break;
                var reading = Parse(response, d);
                if (reading is not null) return [reading];
                if (response.Length >= 4 && response[0] == ReportId
                    && response[1] == BatteryCommand && response[3] == 0x1A) return [];
            }
        }
        return [];
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d)
    {
        // 2 = discharging, 3 = charging, 4 = full; 0x1A = asleep/offline.
        // Unknown states and zero-filled replies cannot become live readings.
        if (!MatchesReceiver(d) || !HasExpectedReports(d)
            || response is not { Length: >= 4 and <= ReportLength }
            || response[0] != ReportId || response[1] != BatteryCommand || response[2] > 100
            || response[3] is not (2 or 3 or 4)) return null;
        return new(d.Id, DeviceName, true, response[2], "迈从耳机电量服务", "2.4G", response[3] == 3);
    }
}
