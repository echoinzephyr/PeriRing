namespace BatteryDisplay;

internal sealed class AtkKeyboardAdapter : IHidBatteryAdapter
{
    private int sequence;

    public string Name => "ATK 键盘";
    public string Models => "V75X（USB 有线）";

    // This USB identifier is shared by other keyboards, so also require the model name.
    public bool Matches(HidDescriptor d) => d is { Vendor: 0x320F, Product: 0x5055 }
        && d.Name.Trim().Equals("ATK V75X", StringComparison.OrdinalIgnoreCase)
        && d.Caps is { UsagePage: 0xFF1C, Usage: 0x0092, InputLength: >= 10, OutputLength: >= 32 };

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        ushort requestId = (ushort)((Interlocked.Increment(ref sequence) & 0x7FFF) + 1);
        using var connection = new HidConnection(d);
        await connection.WriteAsync(BatteryRequest(requestId), token);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var response = await connection.ReadAsync(150, token);
            if (Parse(response, d, requestId) is { } reading) return [reading];
        }
        return [];
    }

    internal static byte[] BatteryRequest(ushort sequence)
    {
        var request = new byte[32];
        request[0] = 0x04;
        request[1] = (byte)sequence;
        request[2] = (byte)(sequence >> 8);
        request[3] = 0x1A;
        return request;
    }

    internal static Peripheral? Parse(byte[]? response, HidDescriptor d, ushort sequence)
    {
        if (response is null || response.Length < 10 || response[0] != 0x04 || response[3] != 0x1A
            || (response[1] | response[2] << 8) != sequence || response[5] != 0 || response[6] != 0
            || response[8] > 100 || response[9] > 2) return null;
        return new(d.Id, "ATK V75X", true, response[8], "ATK 键盘电量", "USB", response[9] != 0);
    }
}
