using System.Text.RegularExpressions;

namespace BatteryDisplay;

internal sealed partial record HidDescriptor(string Path, ushort Vendor, ushort Product, string Name, HidBattery.Caps Caps)
{
    public string Id => "hid:" + Path.ToUpperInvariant();
    // Collections of one interface share a physical link, but two receivers retain distinct instance ids.
    public string Group => SuffixPattern().Replace(CollectionPattern().Replace(Path, ""), "").ToUpperInvariant();

    [GeneratedRegex(@"&col[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionPattern();
    [GeneratedRegex(@"&[0-9a-f]{4}(?=#\{)", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixPattern();
}

internal interface IHidBatterySession : IDisposable
{
    Task WriteAsync(byte[] report, CancellationToken token);
    Task<byte[]?> ReadAsync(int timeoutMs, CancellationToken token);
    bool SetFeature(byte[] report) => false;
    byte[]? GetFeature(byte id, int length) => null;
}

internal static class HidSessionExtensions
{
    public static async Task<T?> ReadUntilAsync<T>(this IHidBatterySession session, int maxReads, int timeoutMs,
        Func<byte[], T?> parse, CancellationToken token, bool stopOnTimeout = false) where T : class
    {
        for (int i = 0; i < maxReads; i++)
        {
            var response = await session.ReadAsync(timeoutMs, token);
            if (response is null)
            {
                if (stopOnTimeout) break;
                continue;
            }
            if (parse(response) is { } parsed) return parsed;
        }
        return null;
    }
}

internal sealed class HidConnection : IHidBatterySession
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

    public bool SetFeature(byte[] report)
    {
        bool success = HidBattery.HidD_SetFeature(handle, report, report.Length);
        DebugService.Log("HID", "feature-request", new { Device = DebugService.Key(Descriptor.Id), ReportId = report.FirstOrDefault(), Length = report.Length, Success = success });
        return success;
    }
    public byte[]? GetFeature(byte id, int length)
    {
        if (length is < 1 or > 4096) return null;
        var buffer = new byte[length];
        buffer[0] = id;
        bool success = HidBattery.HidD_GetFeature(handle, buffer, buffer.Length);
        DebugService.Log("HID", "feature-response", new { Device = DebugService.Key(Descriptor.Id), ReportId = id, Length = length, Success = success });
        return success ? buffer : null;
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
    [new MchoseAdapter(), new MchoseHeadsetAdapter(), new SinowealthAdapter(), new CompxAdapter(), new LogitechAdapter(), new RazerAdapter(), new SonyAdapter(), new HeadsetAdapter(),
        new AsusAdapter(), new HyperXAdapter(), new KeychronAdapter(), new CorsairAdapter(), new SonixKeyboardAdapter(),
        new AtkKeyboardAdapter(), new NzxtAdapter()];
    public string Name => "USB / 2.4G 外设";
    public string Coverage => string.Join("、", Adapters.Select(a => a.Name));
    public string Verification => "迈从 AX5 V2、A7 Ultra(RE) 已实测；V9 Turbo+ 和 VK75 的 2.4G 电量已获用户确认";

    public async Task<List<Peripheral>> ReadAsync(CancellationToken token)
    {
        var all = HidBattery.Enumerate(token);
        DebugService.Observe(all);
        // Probe distinct physical devices concurrently while keeping collections of the same link sequential.
        var work = Adapters
            .SelectMany(adapter => all.Where(adapter.Matches).GroupBy(d => d.Group).Select(group => (Adapter: adapter, GroupKey: group.Key, Descriptors: group.ToArray())))
            .GroupBy(item => item.GroupKey, StringComparer.Ordinal)
            .ToArray();
        var batches = await Task.WhenAll(work.Select(physical => Task.Run(async () =>
        {
            var groupResults = new List<Peripheral>();
            foreach (var (adapter, groupKey, descriptors) in physical)
            {
                bool found = false;
                foreach (var d in descriptors)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var readings = await adapter.ReadAsync(d, all, token);
                        DebugService.Log(adapter.Name, readings.Count > 0 ? "reading" : "no-reading", new { Device = DebugService.Key(d.Id), Count = readings.Count });
                        if (readings.Count == 0) continue;
                        groupResults.AddRange(readings.Select(reading => reading with { ConnectionId = groupKey, ReceiverPresent = true }));
                        found = true;
                        break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { DebugService.Log(adapter.Name, "read-failed", new { Device = DebugService.Key(d.Id), Type = ex.GetType().Name, ex.HResult }); }
                }
                if (!found)
                {
                    var d = descriptors[0];
                    groupResults.Add(new(d.Id, d.Name, false, null, adapter.Name, "USB / 2.4G", Note: "未取得电量 · 设备可能休眠或接口忙",
                        ConnectionId: groupKey, ReceiverPresent: true));
                }
            }
            return groupResults;
        }, token)));
        return batches.SelectMany(batch => batch).ToList();
    }
}
