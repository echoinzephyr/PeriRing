using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace BatteryDisplay;

// Wire-format reference: JKWTCN/BatteryMonitor, MIT (see THIRD-PARTY-NOTICES.md).
internal sealed class FlydigiProvider : IBatteryProvider
{
    public string Name => "飞智手柄";
    public string Coverage => "官方 SpaceStationService 识别的 Apex / Vader / Direwolf 等手柄；需安装官方驱动";
    public string Verification => "协议与边界检查通过；硬件待实测";
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("FDG_PROTOCOL\n");

    public async Task<List<Peripheral>> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", "fcs.sock", PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(150, timeout.Token); }
        catch (TimeoutException) { return []; }
        using var packet = new MemoryStream();
        packet.Write(Header);
        packet.Write([5, 0, 0, 0, 0x08, 0x81, 0x20, 0x1A, 0x00]); // GetConnectedDevices, no device writes.
        await pipe.WriteAsync(packet.ToArray(), timeout.Token);
        for (int i = 0; i < 12; i++)
        {
            var header = new byte[Header.Length];
            await pipe.ReadExactlyAsync(header, timeout.Token);
            if (!header.SequenceEqual(Header)) throw new InvalidDataException("飞智服务返回了未知格式");
            var lengthBytes = new byte[4];
            await pipe.ReadExactlyAsync(lengthBytes, timeout.Token);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (length > 1024 * 1024) throw new InvalidDataException("飞智服务消息过大");
            var body = new byte[(int)length];
            await pipe.ReadExactlyAsync(body, timeout.Token);
            var fields = Proto.Fields(body);
            if (fields.Any(f => f.Number == 1 && f.Integer == 4097)) return ParseDevices(body);
        }
        return [];
    }

    internal static List<Peripheral> ParseDevices(byte[] body)
    {
        var result = new List<Peripheral>();
        foreach (var container in Proto.Fields(body).Where(f => f.Number is 5 or 6 && f.Bytes is not null))
        foreach (var device in Proto.Fields(container.Bytes!).Where(f => f.Number == 1 && f.Bytes is not null))
        {
            var fields = Proto.Fields(device.Bytes!);
            ulong? GetInt(int n) => fields.FirstOrDefault(f => f.Number == n && f.Bytes is null)?.Integer;
            string GetText(int n) => fields.FirstOrDefault(f => f.Number == n && f.Bytes is not null)?.Bytes is byte[] b ? Encoding.UTF8.GetString(b) : "";
            if (GetInt(8) != 1) continue;
            string name = GetText(1), code = GetText(4), mac = GetText(6);
            if (string.IsNullOrWhiteSpace(name)) name = "飞智手柄";
            ulong? level = GetInt(9);
            bool charging = level == 6;
            int? gauge = level <= 5 ? (int)level.Value * 20 : null;
            string description = charging ? "充电中" : gauge.HasValue ? $"电量 {level}/5（档位）" : "—";
            result.Add(new("flydigi:" + (mac.Length > 0 ? mac : code + ":" + name), name, true, null, "飞智后台服务",
                GetInt(3) == 1 ? "USB" : "无线", charging, gauge, description,
                charging ? "正在充电" : gauge.HasValue ? "已连接 · 设备仅上报档位" : "已连接 · 未上报电量"));
        }
        return result;
    }
}

internal static class Proto
{
    internal sealed record Field(int Number, ulong Integer, byte[]? Bytes = null);
    public static List<Field> Fields(byte[] bytes)
    {
        int position = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (int i = 0; i < 10 && position < bytes.Length; i++)
            {
                byte b = bytes[position++];
                if (i == 9 && b > 1) throw new InvalidDataException("无效的服务数据整数");
                value |= (ulong)(b & 0x7F) << (i * 7);
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("服务数据不完整");
        }
        var fields = new List<Field>();
        while (position < bytes.Length)
        {
            if (fields.Count >= 10000) throw new InvalidDataException("服务字段过多");
            ulong tag = Varint();
            if (tag < 8 || tag >> 3 > 536870911) throw new InvalidDataException("无效的服务数据字段");
            int number = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0: fields.Add(new(number, Varint())); break;
                case 2:
                    ulong length = Varint();
                    if (length > (ulong)(bytes.Length - position)) throw new InvalidDataException("服务数据长度不完整");
                    fields.Add(new(number, 0, bytes[position..(position + (int)length)]));
                    position += (int)length;
                    break;
                case 1: if (bytes.Length - position < 8) throw new InvalidDataException(); position += 8; break;
                case 5: if (bytes.Length - position < 4) throw new InvalidDataException(); position += 4; break;
                default: throw new InvalidDataException("服务字段格式不支持");
            }
        }
        return fields;
    }
}
