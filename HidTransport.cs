using System.Text.RegularExpressions;

namespace BatteryDisplay;

internal sealed record HidDescriptor(string Path, ushort Vendor, ushort Product, string Name, HidBattery.Caps Caps)
{
    public string Id => "hid:" + Path.ToUpperInvariant();
    // Collections of one interface share a physical link, but two receivers retain distinct instance ids.
    public string Group => Regex.Replace(Regex.Replace(Path, @"&col[0-9a-f]+", "", RegexOptions.IgnoreCase), @"&[0-9a-f]{4}(?=#\{)", "", RegexOptions.IgnoreCase).ToUpperInvariant();
}

internal sealed class HidConnection : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    private FileStream? stream;
    private readonly bool readOnly;
    public HidDescriptor Descriptor { get; }
    public HidConnection(HidDescriptor descriptor, bool readOnly = false)
    {
        Descriptor = descriptor;
        this.readOnly = readOnly;
        handle = HidBattery.CreateFileW(descriptor.Path, readOnly ? 0u : 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("设备接口正忙或不可访问"); }
    }

    public bool SetFeature(byte[] report) => HidBattery.HidD_SetFeature(handle, report, report.Length);
    public byte[]? GetFeature(byte id, int length)
    {
        if (length is < 1 or > 4096) return null;
        var buffer = new byte[length];
        buffer[0] = id;
        return HidBattery.HidD_GetFeature(handle, buffer, buffer.Length) ? buffer : null;
    }
    private FileStream Stream
    {
        get
        {
            if (stream is not null) return stream;
            var streamHandle = HidBattery.CreateFileW(Descriptor.Path, readOnly ? 0x80000000 : 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (streamHandle.IsInvalid) { streamHandle.Dispose(); throw new IOException("设备读写接口不可用"); }
            try { return stream = new FileStream(streamHandle, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 1, true); }
            catch { streamHandle.Dispose(); throw; }
        }
    }
    public async Task WriteAsync(byte[] report, CancellationToken token)
    {
        if (Descriptor.Caps.OutputLength < report.Length) throw new IOException("报告长度不受设备支持");
        var padded = new byte[Descriptor.Caps.OutputLength];
        report.CopyTo(padded, 0);
        await Stream.WriteAsync(padded, token);
    }
    public async Task<byte[]?> ReadAsync(int timeoutMs, CancellationToken token)
    {
        if (Descriptor.Caps.InputLength is < 1 or > 4096) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var buffer = new byte[Descriptor.Caps.InputLength];
            int count = await Stream.ReadAsync(buffer, timeout.Token);
            return count > 0 ? buffer[..count] : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
    public void Dispose() { stream?.Dispose(); handle.Dispose(); }
}

internal interface IHidBatteryAdapter
{
    string Name { get; }
    string Models { get; }
    bool Matches(HidDescriptor d);
    Task<List<Peripheral>> ReadAsync(HidDescriptor d, IReadOnlyList<HidDescriptor> all, CancellationToken token);
}

internal sealed class UsbProvider : IBatteryProvider
{
    internal static readonly IHidBatteryAdapter[] Adapters =
    [new MchoseAdapter(), new SinowealthAdapter(), new CompxAdapter(), new LogitechAdapter(), new RazerAdapter(), new SonyAdapter(), new HeadsetAdapter(),
        new AsusAdapter(), new HyperXAdapter(), new KeychronAdapter(), new CorsairAdapter(), new SonixKeyboardAdapter()];
    public string Name => "USB / 2.4G 外设";
    public string Coverage => string.Join("、", Adapters.Select(a => a.Name));
    public string Verification => "迈从 AX5 V2、A7 Ultra(RE) 已实测；其余按公开协议接入、待硬件实测";

    public async Task<List<Peripheral>> ReadAsync(CancellationToken token)
    {
        var all = HidBattery.Enumerate(token);
        var results = new List<Peripheral>();
        foreach (var adapter in Adapters)
        foreach (var group in all.Where(adapter.Matches).GroupBy(d => d.Group))
        {
            bool found = false;
            foreach (var d in group)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var readings = await adapter.ReadAsync(d, all, token);
                    if (readings.Count == 0) continue;
                    results.AddRange(readings.Select(reading => reading with { ConnectionId = group.Key, ReceiverPresent = true }));
                    found = true;
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"{adapter.Name}: {ex.Message}"); }
            }
            if (!found)
            {
                var d = group.First();
                results.Add(new(d.Id, d.Name, false, null, adapter.Name, "USB / 2.4G", Note: "未取得电量 · 设备可能休眠或接口忙",
                    ConnectionId: group.Key, ReceiverPresent: true));
            }
        }
        return results;
    }
}
