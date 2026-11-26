namespace BatteryDisplay;

// VK75 identities come from the official VK75 Driver 1.0.1.0 config.xml.
// The radio battery query is documented by sonix-keyboard-studio/SonixDongle.cs.
// Shared Apple VID/PID alone is insufficient: also check product name and command interface.
internal sealed class SonixKeyboardAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public SonixKeyboardAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "瓦尔基里 / SONiX 键盘";
    public string Models => "VK75 的 2.4G 电量已获用户实测确认；VK99 / VK99 Pro 已登记接口、待实测";

    private static bool IsValkyrieName(string name) => name is "Valkyrie 75" or "Valkyrie-99" or "Valkyrie-99pro" or "VK87";
    internal static bool IsWired(HidDescriptor d) => d.Vendor == 0x05AC && d.Product == 0x024F && IsValkyrieName(d.Name)
        && d.Path.Contains("&mi_02", StringComparison.OrdinalIgnoreCase) && d.Caps.UsagePage == 0xFF13 && d.Caps.FeatureLength >= 65;
    internal static bool IsReceiver(HidDescriptor d) => d.Path.Contains("&mi_03", StringComparison.OrdinalIgnoreCase)
        && d.Caps.UsagePage == 0xFF60 && d.Caps.Usage == 0x61 && d.Caps.InputLength == 33 && d.Caps.OutputLength == 33
        && (d.Vendor == 0x05AC && d.Product == 0x024F && d.Name is "VK 75 Wireless Receiver" or "VK99Pro Wireless Receiver" or "2.4G Wireless Receiver"
            || d.Vendor == 0x0C45 && d.Product == 0xFEFE && d.Name == "2.4G Dongle");
    public bool Matches(HidDescriptor d) => IsWired(d) || IsReceiver(d);

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        // The wired interface is identified without sending any command to it.
        if (IsWired(d)) return [new(d.Id, d.Name, true, null, Name, "USB",
            Note: "USB 有线连接 · 暂未取得电量，可尝试切换到 2.4G 接收器")];
        using var connection = connect(d);
        for (int retry = 0; retry < 2; retry++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(1500);
            try
            {
                await connection.WriteAsync(BatteryRequest(), timeout.Token);
                var reading = await connection.ReadUntilAsync(20, 50,
                    report => ParseBattery(report) is int battery ? new Peripheral(d.Id, ReceiverName(d.Name), true, battery, Name, "2.4G") : null,
                    timeout.Token);
                if (reading is not null) return [reading];
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        return [];
    }
    private static string ReceiverName(string name) => name switch
    {
        "VK 75 Wireless Receiver" => "Valkyrie 75",
        "VK99Pro Wireless Receiver" => "Valkyrie VK99 Pro",
        _ => name
    };
    internal static byte[] BatteryRequest()
    {
        var report = new byte[33];
        report[1] = 0x20; report[2] = 1; report[^1] = 0x21;
        return report;
    }
    // A zero status is unavailable in this protocol, not a confirmed empty battery.
    internal static int? ParseBattery(byte[]? report) => report is { Length: 33 } && report[0] == 0
        && report[1] == 0x20 && report[2] == 1 && report[3] == 0 && report[4] is > 0 and <= 100 ? report[4] : null;
}
