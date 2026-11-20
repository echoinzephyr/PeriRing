namespace BatteryDisplay;

internal sealed class LowBatteryAlerts
{
    private readonly HashSet<string> notified = new(StringComparer.Ordinal);
    private int lastThreshold = Preferences.DefaultLowBatteryThreshold;

    public void Observe(IEnumerable<Peripheral> devices, bool enabled, Func<Peripheral, bool> notify, int threshold = Preferences.DefaultLowBatteryThreshold)
    {
        if (lastThreshold != threshold)
        {
            notified.Clear();
            lastThreshold = threshold;
        }
        foreach (var device in devices)
        {
            if (!device.Connected || device.IsCached || device.GaugeValue is not int level) continue;
            if (level > threshold + 5) notified.Remove(device.Id);
            if (enabled && !device.Charging && level <= threshold && !notified.Contains(device.Id) && notify(device))
                notified.Add(device.Id);
        }
    }
}
