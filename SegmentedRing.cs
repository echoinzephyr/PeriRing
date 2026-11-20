namespace BatteryDisplay;

internal sealed record RingSegment(int DeviceIndex, bool IsDot, float StartAngle, float SweepAngle, int? Battery, int LowThreshold)
{
    public bool Low => Battery <= LowThreshold;
    public float ProgressAngle => SweepAngle * Math.Clamp(Battery ?? 0, 0, 100) / 100f;
}

internal static class SegmentedRing
{
    public static List<RingSegment> Layout(IReadOnlyList<Peripheral> devices, int lowThreshold = Preferences.DefaultLowBatteryThreshold)
    {
        int count = devices.Count;
        if (count == 0) return [];
        if (count <= 5)
        {
            float sector = 360f / count;
            // Rounded ends consume about 18 degrees at this stroke width.
            // Leave a visible opening even after downsampling to a 16-pixel tray icon.
            float gap = count == 1 ? 26 : 34;
            return devices.Select((d, index) => new RingSegment(index, false,
                -90 + index * sector + gap / 2, sector - gap, d.GaugeValue, lowThreshold)).ToList();
        }
        // Three equally allocated arcs, then one compact dot for each remaining device.
        const float dotWeight = .35f;
        int dots = count - 3;
        float arcSector = 360f / (3 + dots * dotWeight);
        float dotSector = arcSector * dotWeight;
        float arcGap = Math.Min(34, arcSector * .6f);
        var result = new List<RingSegment>();
        for (int i = 0; i < 3; i++)
            result.Add(new(i, false, -90 + i * arcSector + arcGap / 2, arcSector - arcGap, devices[i].GaugeValue, lowThreshold));
        for (int i = 0; i < dots; i++)
            result.Add(new(i + 3, true, -90 + 3 * arcSector + (i + .5f) * dotSector, 0, devices[i + 3].GaugeValue, lowThreshold));
        return result;
    }
    public static string Label(int index, int count) => count > 5 && index >= 3 ? $"点 {index - 2}" : $"弧 {index + 1}";
}
