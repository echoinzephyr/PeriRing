using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryDisplay;

internal static class DeviceScanner
{
    private static readonly string[] Properties =
    [
        "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected",
        "System.Devices.ContainerId", "System.Devices.Aep.ContainerId",
        "System.Devices.BatteryLife", "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"
    ];

    public static async Task<List<Peripheral>> ScanAsync(CancellationToken token)
    {
        var nativeTask = Task.Run(NativeBattery.ReadAll, token);
        var classicTask = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var leTask = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            Properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        await Task.WhenAll(nativeTask, classicTask, leTask);
        var native = await nativeTask;
        var result = new List<Peripheral>();
        var bleIds = (await leTask).Select(d => d.Id).ToHashSet();
        foreach (var d in (await classicTask).Concat(await leTask))
        {
            token.ThrowIfCancellationRequested();
            string address = d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a)
                ? NativeBattery.NormalizeAddress(a?.ToString() ?? "") : "";
            Guid container = Guid.Empty;
            if (d.Properties.TryGetValue("System.Devices.ContainerId", out var c)) Guid.TryParse(c?.ToString(), out container);
            bool connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var state) && state is true;
            var match = native.FirstOrDefault(n => (address.Length == 12 && n.Address == address)
                || (container != Guid.Empty && n.Container == container));
            int? level = match?.Battery;
            string source = level.HasValue ? "Windows 上报" : "";
            foreach (string key in new[] { "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", "System.Devices.BatteryLife" })
            {
                if (!level.HasValue && d.Properties.TryGetValue(key, out var value) && value is not null
                    && int.TryParse(value.ToString(), out int number) && number is >= 0 and <= 100)
                { level = number; source = "Windows 上报"; }
            }
            if (connected && !level.HasValue && bleIds.Contains(d.Id))
            {
                level = await ReadGattAsync(d.Id, token);
                if (level.HasValue) source = "蓝牙电量服务";
            }
            string id = address.Length == 12 ? address : container != Guid.Empty ? container.ToString() : d.Id;
            result.Add(new(id, string.IsNullOrWhiteSpace(d.Name) ? "蓝牙设备" : d.Name, connected, level, source));
        }
        return result.GroupBy(d => d.Id).Select(g =>
        {
            var best = g.OrderByDescending(d => d.Connected).ThenByDescending(d => d.Battery.HasValue).First();
            return best with { Connected = g.Any(d => d.Connected) };
        }).OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    private static async Task<int?> ReadGattAsync(string id, CancellationToken outerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(timeout.Token);
            if (device is null) return null;
            var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(timeout.Token);
            try
            {
                if (services.Status != GattCommunicationStatus.Success) return null;
                foreach (var service in services.Services)
                {
                    var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel).AsTask(timeout.Token);
                    if (chars.Status != GattCommunicationStatus.Success) continue;
                    foreach (var characteristic in chars.Characteristics)
                    {
                        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
                        if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0) continue;
                        using var reader = DataReader.FromBuffer(read.Value);
                        byte battery = reader.ReadByte();
                        if (battery <= 100) return battery;
                    }
                }
            }
            finally { foreach (var service in services.Services) service.Dispose(); }
        }
        catch (OperationCanceledException) when (!outerToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Debug.WriteLine(ex); }
        return null;
    }
}
