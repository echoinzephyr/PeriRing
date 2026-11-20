using System.Text;

namespace BatteryDisplay;

internal sealed class LogitechAdapter : IHidBatteryAdapter
{
    public string Name => "罗技";
    public string Models => "G PRO Wireless、PRO X SUPERLIGHT / 2 / 2 DEX、G502 LIGHTSPEED 系列；Unifying、Bolt、Lightspeed、Nano 及部分 G 系列耳机";
    public bool Matches(HidDescriptor d) => d.Vendor == 0x046D && d.Caps.OutputLength >= 20 && d.Caps.InputLength >= 20 &&
        ((d.Caps.UsagePage == 0xFF00 && d.Caps.Usage == 2) || (d.Caps.UsagePage == 0xFF43 && d.Caps.Usage == 0x0202));
    private static readonly HashSet<ushort> Receivers = [0xC548, 0xC52B, 0xC532, 0xC52F, 0xC518, 0xC51A, 0xC51B, 0xC521,
        0xC525, 0xC526, 0xC52E, 0xC531, 0xC534, 0xC535, 0xC537, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D];

    public async Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token)
    {
        using var connection = new HidConnection(d);
        HidConnection? shortConnection = null;
        try
        {
            var shortDescriptor = all.FirstOrDefault(a => a.Group == d.Group && a.Caps.UsagePage == 0xFF00 && a.Caps.Usage == 1);
            if (shortDescriptor is not null) { try { shortConnection = new(shortDescriptor); } catch (IOException) { } }
            var channel = new Channel(connection, shortConnection);
            var slots = PollSlots(d);
            var readings = new List<Peripheral>();
            foreach (byte slot in slots)
            {
                Peripheral? reading = null;
                foreach (ushort feature in new ushort[] { 0x1004, 0x1000, 0x1001, 0x1F20 })
                {
                    var root = await channel.Request(slot, 0, 0, [(byte)(feature >> 8), (byte)feature, 0], token);
                    if (root.Data is null)
                    {
                        if (root.Error == 1 && shortConnection is not null)
                        {
                            var old = await channel.Register(slot, 0x0D, token);
                            if (old is { Length: >= 3 } && old[0] <= 100 && (old[2] & 0xF0) is 0x30 or 0x50 or 0x90)
                                reading = new(d.Id + ":" + slot, $"罗技设备 {slot}", true, old[0], "罗技 HID++ 1.0", "2.4G", (old[2] & 0xF0) is 0x50 or 0x90);
                            if (reading is null)
                            {
                                var coarse = await channel.Register(slot, 7, token);
                                if (coarse is { Length: >= 2 })
                                {
                                    int? gauge = coarse[0] switch { 7 => 90, 5 => 50, 3 => 20, 1 => 5, _ => null };
                                    if (gauge.HasValue) reading = new(d.Id + ":" + slot, $"罗技设备 {slot}", true, null, "罗技 HID++ 1.0", "2.4G",
                                        (coarse[1] & 0x21) == 0x21 || (coarse[1] & 0x22) == 0x22, gauge, GaugeText(gauge.Value), "已连接 · 设备仅上报档位");
                                }
                            }
                        }
                        break;
                    }
                    if (root.Data.Length == 0 || root.Data[0] == 0) continue;
                    var status = await channel.Request(slot, root.Data[0], feature == 0x1004 ? (byte)1 : (byte)0, [], token);
                    if (status.Data is not null) reading = Parse(feature, status.Data, d, slot);
                    if (reading is not null) break;
                }
                if (reading is null) continue;
                if (slot == 255 && DeviceCatalog.Find(d, "logitech") is not null)
                {
                    readings.Add(reading);
                    continue;
                }
                var nameRoot = await channel.Request(slot, 0, 0, [0, 5, 0], token);
                if (nameRoot.Data is { Length: > 0 } && nameRoot.Data[0] != 0)
                {
                    var lengthReply = await channel.Request(slot, nameRoot.Data[0], 0, [], token);
                    if (lengthReply.Data is { Length: > 0 })
                    {
                        int length = Math.Min(64, (int)lengthReply.Data[0]);
                        var nameBytes = new List<byte>();
                        while (nameBytes.Count < length)
                        {
                            var fragment = await channel.Request(slot, nameRoot.Data[0], 1, [(byte)nameBytes.Count, 0, 0], token);
                            if (fragment.Data is not { Length: > 0 }) break;
                            nameBytes.AddRange(fragment.Data.Take(Math.Min(length - nameBytes.Count, fragment.Data.Length)));
                        }
                        if (nameBytes.Count == length && length > 0)
                        {
                            var reportedName = Encoding.UTF8.GetString(nameBytes.ToArray()).TrimEnd('\0').Trim();
                            if (reportedName.Length > 0) reading = reading with { Name = DisplayName(reportedName) };
                        }
                    }
                }
                readings.Add(reading);
            }
            return readings;
        }
        finally { shortConnection?.Dispose(); }
    }

    internal static byte[] PollSlots(HidDescriptor d) =>
        Receivers.Contains(d.Product) || d.Name.Contains("receiver", StringComparison.OrdinalIgnoreCase) ? [1, 2, 3, 4, 5, 6] : [255];

    internal static string DisplayName(string reportedName) => reportedName.ToUpperInvariant() switch
    {
        "G PRO WIRELESS GAMING MOUSE" => "Logitech G PRO Wireless",
        "PRO X WIRELESS" => "Logitech G PRO X SUPERLIGHT",
        "PRO X 2" => "Logitech G PRO X SUPERLIGHT 2",
        "PRO X 2 DEX" => "Logitech G PRO X SUPERLIGHT 2 DEX",
        "G502 GAMING MOUSE" => "Logitech G502 LIGHTSPEED",
        "G502 X LS" => "Logitech G502 X LIGHTSPEED",
        _ => reportedName
    };

    private static string GaugeText(int gauge) => gauge <= 5 ? "极低（档位）" : gauge <= 20 ? "低（档位）" : gauge <= 50 ? "中（档位）" : "高（档位）";
    internal static Peripheral? Parse(ushort feature, byte[] p, HidDescriptor d, byte slot)
    {
        if (p.Length < 3) return null;
        string id = d.Id + ":" + slot;
        var model = slot == 255 ? DeviceCatalog.Find(d, "logitech") : null;
        string name = model?.Name ?? d.Name;
        string transport = slot == 255 ? model?.Transport ?? "USB / 2.4G" : "2.4G";
        bool charging = p[2] is >= 1 and <= 4;
        if (feature is 0x1004 or 0x1000)
        {
            if (p[0] is > 0 and <= 100) return new(id, name, true, p[0], "罗技 HID++", transport, charging);
            if (feature == 0x1004 && p[0] == 0)
            {
                int? gauge = p[1] switch { 8 => 90, 4 => 50, 2 => 20, 1 => 5, _ => null };
                if (gauge.HasValue) return new(id, name, true, null, "罗技 HID++", transport, charging, gauge, GaugeText(gauge.Value), "已连接 · 设备仅上报档位");
            }
            return null;
        }
        if (feature is not (0x1001 or 0x1F20)) return null;
        int mv = p[0] << 8 | p[1];
        if (mv is < 2500 or > 5000 || (feature == 0x1F20 && (p[2] & 1) == 0)) return null;
        // Voltage-only devices are explicitly marked as estimates.
        int gaugeValue = VoltageEstimate(mv);
        return new(id, name, true, null, "罗技 HID++ 电压", transport, feature == 0x1001 ? (p[2] & 0x80) != 0 : (p[2] & 2) != 0,
            gaugeValue, $"约 {gaugeValue}%", "已连接 · 按电压估算");
    }
    private static int VoltageEstimate(int mv)
    {
        (int Mv, int Percent)[] curve = [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];
        if (mv >= curve[0].Mv) return 100;
        for (int i = 1; i < curve.Length; i++)
            if (mv >= curve[i].Mv) return curve[i].Percent + (mv - curve[i].Mv) * (curve[i - 1].Percent - curve[i].Percent) / (curve[i - 1].Mv - curve[i].Mv);
        return 0;
    }

    private sealed class Channel(HidConnection main, HidConnection? shortConnection)
    {
        private byte sequence;
        public async Task<(byte[]? Data, int Error)> Request(byte slot, byte feature, byte function, byte[] parameters, CancellationToken token)
        {
            byte fn = (byte)(function << 4 | (0xA + sequence++ % 6));
            var request = new byte[20];
            request[0] = 0x11; request[1] = slot; request[2] = feature; request[3] = fn;
            Array.Copy(parameters, 0, request, 4, Math.Min(16, parameters.Length));
            await main.WriteAsync(request, token);
            var deadline = Environment.TickCount64 + 450;
            while (Environment.TickCount64 < deadline)
            foreach (var connection in shortConnection is null ? new[] { main } : new[] { main, shortConnection })
            {
                var r = await connection.ReadAsync(35, token);
                if (r is not { Length: >= 4 } || r[1] != slot) continue;
                if (r[0] is 0x10 or 0x11 && r[2] == feature && r[3] == fn) return (r[4..], 0);
                if (r.Length >= 6 && r[2] is 0x8F or 0xFF && r[3] == feature && r[4] == fn) return (null, r[5]);
            }
            return (null, 0);
        }
        public async Task<byte[]?> Register(byte slot, byte register, CancellationToken token)
        {
            if (shortConnection is null) return null;
            await shortConnection.WriteAsync([0x10, slot, 0x81, register, 0, 0, 0], token);
            for (int i = 0; i < 5; i++)
            {
                var r = await shortConnection.ReadAsync(70, token);
                if (r is { Length: >= 7 } && r[0] == 0x10 && r[1] == slot && r[2] == 0x81 && r[3] == register) return r[4..];
            }
            return null;
        }
    }
}
