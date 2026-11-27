namespace BatteryDisplay;

internal sealed record ConcentricRingLayer(int DeviceIndex, bool IsCenterDot, float Radius, float StrokeWidth, int? Battery, int LowThreshold, bool LowOverride = false, bool Charging = false)
{
    public bool Low => LowOverride || (!Charging && Battery is int value && value <= LowThreshold);
    public float ProgressAngle => 360f * Math.Clamp(Battery ?? 0, 0, 100) / 100f;
}

internal static class ConcentricRing
{
    public static List<ConcentricRingLayer> Layout(IReadOnlyList<Peripheral> devices, int lowThreshold = Preferences.DefaultLowBatteryThreshold)
    {
        int count = devices.Count;
        if (count == 0) return [];
        if (count == 1)
            return [new(0, false, 12.6f, 4.4f, devices[0].GaugeValue, lowThreshold, Charging: devices[0].Connected && devices[0].Charging)];
        if (count == 2)
            return
            [
                new(0, false, 13.2f, 3.8f, devices[0].GaugeValue, lowThreshold, Charging: devices[0].Connected && devices[0].Charging),
                new(1, false, 5.8f, 3.8f, devices[1].GaugeValue, lowThreshold, Charging: devices[1].Connected && devices[1].Charging)
            ];
        if (count == 3)
            return
            [
                new(0, false, 13.8f, 2.9f, devices[0].GaugeValue, lowThreshold, Charging: devices[0].Connected && devices[0].Charging),
                new(1, false, 8.8f, 2.9f, devices[1].GaugeValue, lowThreshold, Charging: devices[1].Connected && devices[1].Charging),
                new(2, false, 3.8f, 2.9f, devices[2].GaugeValue, lowThreshold, Charging: devices[2].Connected && devices[2].Charging)
            ];

        // 4+ devices: top 3 priority devices get outer/middle/inner rings; overflow devices share the center dot.
        var overflow = devices.Skip(3).ToArray();
        int? minOverflow = overflow.Select(d => d.GaugeValue).Where(v => v.HasValue).Min();
        bool anyOverflowLow = overflow.Any(d => !(d.Connected && d.Charging) && d.GaugeValue is int value && value <= lowThreshold);
        bool anyOverflowCharging = !anyOverflowLow && overflow.Any(d => d.Connected && d.Charging);
        return
        [
            new(0, false, 13.8f, 2.6f, devices[0].GaugeValue, lowThreshold, Charging: devices[0].Connected && devices[0].Charging),
            new(1, false, 9.2f, 2.6f, devices[1].GaugeValue, lowThreshold, Charging: devices[1].Connected && devices[1].Charging),
            new(2, false, 4.8f, 2.6f, devices[2].GaugeValue, lowThreshold, Charging: devices[2].Connected && devices[2].Charging),
            new(3, true, 1.6f, 0f, minOverflow, lowThreshold, anyOverflowLow, anyOverflowCharging)
        ];
    }
}
