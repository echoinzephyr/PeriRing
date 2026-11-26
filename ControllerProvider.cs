using System.Runtime.InteropServices;
using Windows.Devices.Power;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryDisplay;

internal sealed class ControllerProvider : IBatteryProvider
{
    public string Name => "Windows 手柄";
    public string Coverage => "Xbox / XInput 兼容手柄，以及提供 Windows 电池报告的游戏控制器";
    public string Verification => "系统标准接口；硬件待实测";
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BatteryInfo { public byte Type, Level; }
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint user, byte device, out BatteryInfo battery);

    private static int wgiWarmedUp;

    public async Task<List<Peripheral>> ReadAsync(CancellationToken token)
    {
        var result = new List<Peripheral>();
        // WGI's initial device enumeration is asynchronous; wait once per process lifetime.
        _ = RawGameController.RawGameControllers.Count;
        if (Volatile.Read(ref wgiWarmedUp) == 0)
        {
            await Task.Delay(150, token);
            Volatile.Write(ref wgiWarmedUp, 1);
        }
        var controllers = RawGameController.RawGameControllers.ToArray();
        foreach (var controller in controllers)
        {
            token.ThrowIfCancellationRequested();
            BatteryReport? report = controller.TryGetBatteryReport();
            string name = string.IsNullOrWhiteSpace(controller.DisplayName) ? "游戏手柄" : controller.DisplayName;
            string id = "wgi:" + controller.NonRoamableId;
            int? value = null;
            // Many Xbox 360 emulators report a constant synthetic 1000/1000. Do not advertise that as a real percentage.
            bool emulator = controller.HardwareVendorId == 0x045E && controller.HardwareProductId == 0x028E;
            DebugService.Log(Name, "battery-report", new { Device = DebugService.Key(id), Vendor = controller.HardwareVendorId,
                Product = controller.HardwareProductId, SyntheticXbox360 = emulator, Remaining = report?.RemainingCapacityInMilliwattHours,
                Full = report?.FullChargeCapacityInMilliwattHours, Status = report?.Status.ToString() });
            if (!emulator && report is not null && report.Status is BatteryStatus.Charging or BatteryStatus.Discharging or BatteryStatus.Idle
                && report.FullChargeCapacityInMilliwattHours is > 0 && report.RemainingCapacityInMilliwattHours is >= 0)
            {
                double percent = 100d * report.RemainingCapacityInMilliwattHours.Value / report.FullChargeCapacityInMilliwattHours.Value;
                if (percent is >= 0 and <= 100) value = (int)Math.Round(percent);
            }
            result.Add(new(id, name, true, value, "Windows 手柄接口", "手柄接口", report?.Status == BatteryStatus.Charging));
        }
        // XInput is a fallback when WGI exposes no controllers. Do not merge unrelated slots by enumeration order.
        if (controllers.Length == 0)
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetBatteryInformation(slot, 0, out var battery) != 0 || battery.Type == 0) continue;
            var reading = FromXInput(slot, battery.Type, battery.Level);
            if (reading is not null) result.Add(reading);
        }
        return result;
    }

    internal static Peripheral? FromXInput(uint slot, byte type, byte level)
    {
        if (type == 0) return null;
        string name = $"XInput 手柄 {slot + 1}";
        if (type is not (2 or 3) || level > 3)
            return new($"xinput:{slot}", name, true, null, "Windows 手柄接口", "手柄接口", Note: type == 1 ? "接口未提供电池数据" : "电量未知");
        string description = new[] { "极低（档位）", "低（档位）", "中（档位）", "高（档位）" }[level];
        return new($"xinput:{slot}", name, true, null, "Windows 手柄接口", "无线", Gauge: level * 100 / 3,
            LevelDescription: description, Note: "已连接 · 设备仅上报档位");
    }
}
