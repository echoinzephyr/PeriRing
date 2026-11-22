namespace BatteryDisplay;

// C-Media receiver protocol, separate from MCHOSE mice. Reference (MIT):
// https://github.com/brayanspagnol/OpenMHub/blob/f77c000334ad9e34499d7acab315a0d9ee8789b7/app/renderer/drivers/headset.js
// Attribution is retained in THIRD-PARTY-NOTICES.md.
internal sealed class MchoseHeadsetAdapter : IHidBatteryAdapter
{
    private readonly Func<HidDescriptor, IHidBatterySession> connect;
    public MchoseHeadsetAdapter() : this(d => new HidConnection(d)) { }
    internal MchoseHeadsetAdapter(Func<HidDescriptor, IHidBatterySession> connect) => this.connect = connect;
    internal const int ReportLength = 64;
    private const byte ReportId = 0x55;
    private const byte BatteryCommand = 0x65;
    private const string DeviceName = "迈从 MCHOSE V9 Turbo+";
    public string Name => "迈从 V9 Turbo+ 耳机";
    public string Models => "V9 Turbo+ 2.4G 接收器（3837:600A）";

    // Exclude audio/input collections, the 6008 RGB controller and other models.
    public bool Matches(HidDescriptor d) => MatchesReceiver(d);
    private static bool MatchesReceiver(HidDescriptor d) => d.Vendor == 0x3837 && d.Product == 0x600A
        && d.Path.Contains("&mi_00", StringComparison.OrdinalIgnoreCase) && d.Caps.UsagePage >= 0xFF00;
    internal static bool HasExpectedReports(HidDescriptor d) => d.Caps.InputLength == ReportLength
        && d.Caps.OutputLength == ReportLength;
    internal static byte[] BatteryRequest() => [ReportId, BatteryCommand, 0x01];
    internal static HidDescriptor? SelectReceiver(HidDescriptor d, IReadOnlyList<HidDescriptor> all) => !MatchesReceiver(d) ? null
        : HasExpectedReports(d) ? d : all.FirstOrDefault(candidate => candidate.Group == d.Group && MatchesReceiver(candidate) && HasExpectedReports(candidate));

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MatchesReceiver(d)) return [];
        var selected = SelectReceiver(d, all);
        if (selected is null)
            return [new(d.Id, DeviceName, false, null, Name, "2.4G",
                Note: $"已识别 · HID 报告格式待适配（输入 {d.Caps.InputLength} / 输出 {d.Caps.OutputLength}）")];

        d = selected;
        using var connection = connect(d);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptTimeout.CancelAfter(1000);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await connection.WriteAsync(BatteryRequest(), attemptTimeout.Token);
                for (int read = 0; read < 32 && elapsed.ElapsedMilliseconds < 1000; read++)
                {
                    int remaining = Math.Max(1, 1000 - (int)elapsed.ElapsedMilliseconds);
                    var response = await connection.ReadAsync(remaining, attemptTimeout.Token);
                    if (response is null) break;
                    var reading = Parse(response, d);
                    DebugService.Log(Name, "battery-response", new { Device = DebugService.Key(d.Id), Length = response.Length,
                        Valid = reading is not null, Battery = reading?.Battery, Charging = reading?.Charging,
                        Status = response.Length >= 4 && response[0] == ReportId && response[1] == BatteryCommand ? (int?)response[3] : null });
                    if (reading is not null) return [reading];
                    if (response.Length >= 4 && response[0] == ReportId
                        && response[1] == BatteryCommand && response[3] == 0x1A) return [];
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
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
