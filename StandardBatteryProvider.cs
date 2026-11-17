using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.System.Power;

namespace BatteryDisplay;

// Windows battery-class interfaces only; no arbitrary feature reports to unknown HID devices.
internal sealed class StandardBatteryProvider : IBatteryProvider
{
    public string Name => "标准外设电池接口";
    public string Coverage => "提供 Windows 电池报告的 USB、HID 外设；不含电脑内置电池";
    public string Verification => "标准系统接口；实际覆盖取决于设备驱动";
    public async Task<List<Peripheral>> ReadAsync(CancellationToken token)
    {
        var devices = await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector()).AsTask(token);
        var result = new List<Peripheral>();
        foreach (var device in devices)
        {
            if (!device.Id.Contains("HID#", StringComparison.OrdinalIgnoreCase) && !device.Id.Contains("USB#", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var battery = await Battery.FromIdAsync(device.Id).AsTask(token);
                if (battery is null) continue;
                var report = battery.GetReport();
                int? level = Percentage(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);
                result.Add(new("battery:" + device.Id, device.Name, report.Status != BatteryStatus.NotPresent, level, Name,
                    "USB / 无线接收器", report.Status == BatteryStatus.Charging));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { DebugService.Log(Name, "read-failed", new { Device = DebugService.Key(device.Id), Type = ex.GetType().Name, ex.HResult }); }
        }
        return result;
    }
    internal static int? Percentage(int? remaining, int? full) => full is > 0 && remaining is >= 0 && remaining <= full
        ? (int)Math.Round(100d * remaining.Value / full.Value) : null;
}
