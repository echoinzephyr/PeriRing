namespace BatteryDisplay;

internal static class V9ReaderTests
{
    internal static async Task<int> RunAsync(List<string> failures)
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) failures.Add(message); }
        var valid = new HidDescriptor("test&mi_00&col02#receiver", 0x3837, 0x600A, "MCHOSE V9 Turbo+",
            new() { UsagePage = 0xFF00, InputLength = 64, OutputLength = 64 });
        var other = valid with { Path = "test&mi_00&col01#receiver", Caps = new() { UsagePage = 0xFF00, InputLength = 16, OutputLength = 64 } };
        HidDescriptor? opened = null;
        var immediate = new Session((_, _) => Task.FromResult<byte[]?>([0x55, 0x65, 76, 2]));
        var adapter = new MchoseHeadsetAdapter(d => { opened = d; return immediate; });
        var reading = await adapter.ReadAsync(other, [other, valid], default);
        Check(opened == valid && reading.Single().Battery == 76, "V9 unsupported first collection does not block its compatible sibling");
        Check(immediate.Disposed && immediate.Writes == 1, "V9 selected session is queried once and disposed");
        reading = await adapter.ReadAsync(valid, [valid, other], default);
        Check(reading.Single().Battery == 76, "V9 compatible first collection keeps the same reading");
        opened = null;
        reading = await adapter.ReadAsync(other, [other, valid with { Path = "different&mi_00&col02#receiver" }], default);
        Check(opened is null && reading.Single().Battery is null, "V9 never borrows an interface from a different receiver");
        var delayed = new Session(async (_, token) => { await Task.Delay(600, token); return [0x55, 0x65, 64, 2]; });
        reading = await new MchoseHeadsetAdapter(_ => delayed).ReadAsync(valid, [valid], default);
        Check(reading.Single().Battery == 64 && delayed.Writes == 1, "V9 accepts a valid 600 ms reply within its request deadline");
        var unrelated = new Session(async (index, token) =>
        { await Task.Delay(index == 1 ? 10 : 600, token); return index == 1 ? [0x55, 0x11, 0, 0] : [0x55, 0x65, 63, 3]; });
        reading = await new MchoseHeadsetAdapter(_ => unrelated).ReadAsync(valid, [valid], default);
        Check(reading.Single() is { Battery: 63, Charging: true } && unrelated.Writes == 1, "V9 ignores unrelated reports while retaining the same deadline");
        var silence = new Session(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; });
        reading = await new MchoseHeadsetAdapter(_ => silence).ReadAsync(valid, [valid], default);
        Check(reading.Count == 0 && silence.Writes == 3 && silence.Disposed, "V9 no-response retries remain bounded and never invent zero");
        var cancelled = new Session(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; });
        using var cancel = new CancellationTokenSource(30);
        try { await new MchoseHeadsetAdapter(_ => cancelled).ReadAsync(valid, [valid], cancel.Token); Check(false, "V9 parent cancellation propagates"); }
        catch (OperationCanceledException) { Check(cancelled.Disposed && cancelled.Writes == 1, "V9 parent cancellation propagates without retry"); }
        var asleep = new Session((_, _) => Task.FromResult<byte[]?>([0x55, 0x65, 60, 0x1A]));
        reading = await new MchoseHeadsetAdapter(_ => asleep).ReadAsync(valid, [valid], default);
        Check(reading.Count == 0 && asleep.Writes == 1 && asleep.Disposed, "V9 sleep ends probing without a live battery");
        return checks;
    }
    private sealed class Session(Func<int, CancellationToken, Task<byte[]?>> read) : IHidBatterySession
    {
        public int Writes { get; private set; }
        private int reads;
        public bool Disposed { get; private set; }
        public Task WriteAsync(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!report.SequenceEqual(new byte[] { 0x55, 0x65, 1 })) throw new InvalidDataException("Unexpected write");
            Writes++; return Task.CompletedTask;
        }
        public Task<byte[]?> ReadAsync(int timeoutMs, CancellationToken token) => read(++reads, token);
        public void Dispose() => Disposed = true;
    }
}
