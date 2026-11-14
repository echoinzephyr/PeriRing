namespace BatteryDisplay;

internal sealed record RingSegment(int DeviceIndex, bool IsDot, float StartAngle, float SweepAngle, int? Battery)
{
    public bool Low => Battery is <= 20;
    public float ProgressAngle => SweepAngle * Math.Clamp(Battery ?? 0, 0, 100) / 100f;
}

internal static class SegmentedRing
{
    public static List<RingSegment> Layout(IReadOnlyList<Peripheral> devices)
    {
        int count = devices.Count;
        if (count == 0) return [];
        if (count <= 5)
        {
            float sector = 360f / count;
            float gap = count == 1 ? 9 : 13;
            return devices.Select((d, index) => new RingSegment(index, false,
                -90 + index * sector + gap / 2, sector - gap, d.GaugeValue)).ToList();
        }
        // Three equally allocated arcs, then one compact dot for each remaining device.
        const float dotWeight = .35f;
        int dots = count - 3;
        float arcSector = 360f / (3 + dots * dotWeight);
        float dotSector = arcSector * dotWeight;
        var result = new List<RingSegment>();
        for (int i = 0; i < 3; i++)
            result.Add(new(i, false, -90 + i * arcSector + 6.5f, arcSector - 13, devices[i].GaugeValue));
        for (int i = 0; i < dots; i++)
            result.Add(new(i + 3, true, -90 + 3 * arcSector + (i + .5f) * dotSector, 0, devices[i + 3].GaugeValue));
        return result;
    }
    public static string Label(int index, int count) => count > 5 && index >= 3 ? $"点 {index - 2}" : $"弧 {index + 1}";
}
