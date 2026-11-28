namespace BatteryDisplay;

// ATK / VXE wireless mice on a Nordic-based dongle answer a read-only battery query on
// output report 0x08. Reply: 08 72 .. .. .. 07 01 <level> <mV low> <mV high>.
// The first query after the mouse sleeps is usually dropped, so the session retries.
internal sealed class AtkNordicAdapter(Func<HidDescriptor, IHidBatterySession> connect) : IHidBatteryAdapter
{
    public AtkNordicAdapter() : this(d => new HidConnection(d)) { }
    public string Name => "ATK Nordic 鼠标";
    public string Models => "ATK / VXE 2.4G 接收器（0x373B:0x1216）的 Output 0x08 电量查询";

    public bool Matches(HidDescriptor d) => d is { Vendor: 0x373B, Product: 0x1216 }
        && d.Caps is { UsagePage: 0xFF05, Usage: 1, InputLength: >= ReportLength, OutputLength: >= ReportLength };

    internal const int ReportLength = 64;
    internal const int Attempts = 5;
    private static readonly byte[] Command = [0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01];

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = connect(d);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(150, token);
            // Drop reports queued by an earlier query so a stale frame cannot be read as this answer.
            for (int i = 0; i < 20; i++)
                if (await connection.ReadAsync(1, token) is null) break;
            try { await connection.WriteAsync(BatteryRequest(), token); }
            catch (IOException) { continue; }
            var reading = await connection.ReadUntilAsync(6, 120, response => Parse(response, d), token);
            if (reading is not null) return [reading];
        }
        return [];
    }

    internal static byte[] BatteryRequest()
    {
        var request = new byte[ReportLength];
        request[0] = 0x08;
        Command.CopyTo(request, 1);
        return request;
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d)
    {
        if (response is null || response.Length < 10 || response[0] != 0x08 || response[1] != 0x72
            || response[5] != 0x07 || response[7] > 100) return null;
        return new(d.Id, "ATK 2.4G 鼠标", true, response[7], "ATK 2.4G 电量", "2.4G");
    }
}
