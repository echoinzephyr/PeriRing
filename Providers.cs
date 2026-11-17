namespace BatteryDisplay;

internal sealed record ProviderState(string Name, string Coverage, string Verification, int DeviceCount = 0, string? Warning = null);
internal sealed record ScanReport(List<Peripheral> Devices, List<ProviderState> Providers);

internal interface IBatteryProvider
{
    string Name { get; }
    string Coverage { get; }
    string Verification { get; }
    Task<List<Peripheral>> ReadAsync(CancellationToken token);
}

internal sealed class BluetoothProvider : IBatteryProvider
{
    public string Name => "标准蓝牙";
    public string Coverage => "Windows 上报的蓝牙外设、标准 BLE 电量服务";
    public string Verification => "本机已验证系统读数；BLE 直读待实测";
    public Task<List<Peripheral>> ReadAsync(CancellationToken token) => DeviceScanner.ScanAsync(token);
}

internal static class BatteryService
{
    internal static readonly IBatteryProvider[] Providers =
    [new BluetoothProvider(), new UsbProvider(), new ControllerProvider(), new FlydigiProvider(), new StandardBatteryProvider()];

    public static async Task<ScanReport> ScanAsync(CancellationToken token, IEnumerable<IBatteryProvider>? sources = null)
    {
        var snapshots = await Task.WhenAll((sources ?? Providers).Select(provider => Task.Run(async () =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var devices = await provider.ReadAsync(token);
                DebugService.Log(provider.Name, "scan-complete", new { Milliseconds = started.ElapsedMilliseconds, Count = devices.Count });
                return (Devices: devices, State: new ProviderState(provider.Name, provider.Coverage, provider.Verification, devices.Count));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DebugService.Log(provider.Name, "scan-failed", new { Milliseconds = started.ElapsedMilliseconds, Type = ex.GetType().Name, ex.HResult });
                return (Devices: new List<Peripheral>(), State: new ProviderState(provider.Name, provider.Coverage, provider.Verification, 0, ex.Message));
            }
        }, token)));
        var all = snapshots.SelectMany(s => s.Devices).ToList();
        // A service reading is preferred over an uninformative Windows entry for the same named pad.
        var flydigiNames = all.Where(d => d.Source == "飞智后台服务").Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        all.RemoveAll(d => d.Source == "Windows 手柄接口" && flydigiNames.Contains(d.Name));
        return new(all.GroupBy(d => d.Id).Select(g => g.OrderByDescending(d => d.Connected)
                .ThenByDescending(d => d.Battery.HasValue).First()).OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList(),
            snapshots.Select(s => s.State).ToList());
    }
}
