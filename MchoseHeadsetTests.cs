namespace BatteryDisplay;

internal static class MchoseHeadsetTests
{
    internal static int Run(List<string> failures)
    {
        int count = 0;
        void Check(bool condition, string name) { count++; if (!condition) failures.Add(name); }
        var receiver = new HidDescriptor("test&mi_00", 0x3837, 0x600A, "MCHOSE V9 Turbo+",
            new() { UsagePage = 0xFF00, Usage = 1, InputLength = 64, OutputLength = 64, FeatureLength = 64 });
        var adapter = new MchoseHeadsetAdapter();
        Check(adapter.Matches(receiver), "V9 Turbo+ receiver is matched independently of mice");
        Check(adapter.Matches(receiver with { Path = "test&MI_00", Name = "USB Gaming Headset" }), "V9 path casing and generic product name");
        foreach (ushort pid in new ushort[] { 0x4018, 0x6008, 0x6009, 0x600D })
            Check(!adapter.Matches(receiver with { Product = pid }), $"V9 excludes unrelated or unverified model {pid:X4}");
        Check(!adapter.Matches(receiver with { Vendor = 0x1234 }), "V9 requires the exact vendor");
        Check(!adapter.Matches(receiver with { Path = "test&mi_01" }), "V9 audio interface is excluded");
        Check(!adapter.Matches(receiver with { Path = "test" }), "V9 interface must be explicitly identified");
        var input = receiver with { Caps = new() { UsagePage = 1, InputLength = 64, OutputLength = 64 } };
        Check(!adapter.Matches(input), "V9 generic input collection is excluded");
        Check(MchoseHeadsetAdapter.BatteryRequest().SequenceEqual(new byte[] { 0x55, 0x65, 0x01 }), "V9 documented battery query only");
        Check(MchoseHeadsetAdapter.HasExpectedReports(receiver), "V9 expected report sizes");
        var live = MchoseHeadsetAdapter.Parse([0x55, 0x65, 76, 2], receiver);
        Check(live is { Battery: 76, Connected: true, Charging: false, Transport: "2.4G" } && live.CanShow, "V9 live percentage");
        Check(live?.Id == receiver.Id && live?.VendorDisplay is null, "V9 preserves receiver identity without mouse display bins");
        Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, 76, 3], receiver) is { Battery: 76, Charging: true }, "V9 charging state");
        Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, 99, 4], receiver) is { Battery: 99, Charging: false }, "V9 full state does not invent 100 percent");
        foreach (byte level in new byte[] { 0, 100 })
            Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, level, 2], receiver)?.Battery == level, $"V9 valid battery boundary {level}");
        Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, 101, 2], receiver) is null, "V9 invalid percentage");
        foreach (byte state in new byte[] { 0, 1, 5, 0x1A, 255 })
            Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, 76, state], receiver) is null, $"V9 rejects offline or unknown state {state}");
        Check(MchoseHeadsetAdapter.Parse(new byte[64], receiver) is null, "V9 uninitialized reply is not zero battery");
        Check(MchoseHeadsetAdapter.Parse([0x55, 0x11, 76, 2], receiver) is null, "V9 unrelated EQ reply");
        Check(MchoseHeadsetAdapter.Parse([0xAA, 0x65, 76, 2], receiver) is null, "V9 wrong report ID");
        Check(MchoseHeadsetAdapter.Parse([0x55, 0x65, 76], receiver) is null, "V9 truncated reply");
        Check(MchoseHeadsetAdapter.Parse(null, receiver) is null, "V9 timeout is not a reading");
        var padded = new byte[64]; new byte[] { 0x55, 0x65, 76, 2 }.CopyTo(padded, 0);
        Check(MchoseHeadsetAdapter.Parse(padded, receiver)?.Battery == 76, "V9 Windows report padding");
        Check(MchoseHeadsetAdapter.Parse(new byte[65], receiver) is null, "V9 oversized reply");
        Check(MchoseHeadsetAdapter.Parse(padded, receiver with { Product = 0x6008 }) is null, "V9 parser excludes RGB controller");
        foreach (var caps in new HidBattery.Caps[]
        {
            new() { UsagePage = 0xFF00, InputLength = 3, OutputLength = 64 },
            new() { UsagePage = 0xFF00, InputLength = 64, OutputLength = 65 }
        })
        {
            var unexpected = receiver with { Caps = caps };
            Check(!MchoseHeadsetAdapter.HasExpectedReports(unexpected), "V9 incompatible report layout");
            var unknown = adapter.ReadAsync(unexpected, [unexpected], CancellationToken.None).GetAwaiter().GetResult().Single();
            Check(unknown is { Battery: null, Connected: false } && !unknown.CanShow && unknown.State.Contains("格式待适配"),
                "V9 incompatible layout is explained without device I/O");
            Check(MchoseHeadsetAdapter.Parse(padded, unexpected) is null, "V9 parser rejects incompatible layout");
        }
        Check(adapter.ReadAsync(input, [input], CancellationToken.None).GetAwaiter().GetResult().Count == 0, "V9 unmatched interface skips device I/O");
        try
        {
            adapter.ReadAsync(receiver, [receiver], new CancellationToken(true)).GetAwaiter().GetResult();
            Check(false, "V9 cancellation before device I/O");
        }
        catch (OperationCanceledException) { Check(true, "V9 cancellation before device I/O"); }
        return count;
    }
}
