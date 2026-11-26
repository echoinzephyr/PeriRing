namespace BatteryDisplay;

internal static class AdapterSessionTests
{
    internal static async Task<int> RunAsync(List<string> failures)
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) failures.Add(name); }
        HidDescriptor Desc(ushort vid, ushort pid, ushort usagePage, ushort usage, string path = "test", string name = "test") =>
            new(path, vid, pid, name, new() { UsagePage = usagePage, Usage = usage, InputLength = 65, OutputLength = 65, FeatureLength = 91 });

        // CompX: skips unsolicited frame, parses valid frame, stops cleanly on timeout
        var compxDesc = Desc(0x3554, 0xF58A, 0xFF02, 2);
        byte[] compxValid = [8, 4, 0, 0, 0, 2, 80, 0, 0x0F, 0xAD, 0, 0, 0, 0, 0, 0, 0x3B];
        var compxSession = new MockSession([[8, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], compxValid]);
        var compxReading = await new CompxAdapter(_ => compxSession).ReadAsync(compxDesc, [compxDesc], CancellationToken.None);
        Check(compxReading.Single().Battery == 80 && compxSession.Writes.Count == 1 && compxSession.Disposed,
            "CompX session skips unsolicited frames and disposes cleanly");
        var compxTimeout = new MockSession([null]);
        Check((await new CompxAdapter(_ => compxTimeout).ReadAsync(compxDesc, [compxDesc], CancellationToken.None)).Count == 0 && compxTimeout.Disposed,
            "CompX session returns empty on timeout without fabricating battery");

        // SONiX: ignores unrelated report and zero-battery placeholder, retries and returns valid reading
        var sonixDesc = new HidDescriptor("test&mi_03", 0x05AC, 0x024F, "VK 75 Wireless Receiver",
            new() { UsagePage = 0xFF60, Usage = 0x61, InputLength = 33, OutputLength = 33 });
        var sonixZero = new byte[33]; sonixZero[1] = 0x20; sonixZero[2] = 1; sonixZero[4] = 0;
        var sonixValid = new byte[33]; sonixValid[1] = 0x20; sonixValid[2] = 1; sonixValid[4] = 74;
        var sonixSession = new MockSession([new byte[33], sonixZero, sonixValid]);
        var sonixReading = await new SonixKeyboardAdapter(_ => sonixSession).ReadAsync(sonixDesc, [sonixDesc], CancellationToken.None);
        Check(sonixReading.Single() is { Name: "Valkyrie 75", Battery: 74, Transport: "2.4G" } && sonixSession.Disposed,
            "SONiX session filters zero/unrelated frames and parses valid battery");

        // ATK V75X: rejects stale sequence reply and accepts matching request sequence
        var atkDesc = Desc(0x320F, 0x5055, 0xFF1C, 0x92, name: "ATK V75X");
        var atkSession = new MockSession(writeReply: request =>
        {
            byte lo = request[1], hi = request[2];
            byte[] stale = [4, (byte)(lo + 1), hi, 0x1A, 0, 0, 0, 0, 12, 0];
            byte[] valid = [4, lo, hi, 0x1A, 0, 0, 0, 0, 88, 1];
            return [stale, valid];
        });
        var atkReading = await new AtkKeyboardAdapter(_ => atkSession).ReadAsync(atkDesc, [atkDesc], CancellationToken.None);
        Check(atkReading.Single() is { Battery: 88, Charging: true, Transport: "USB" } && atkSession.Disposed,
            "ATK session ignores mismatched sequence IDs and reads matching response");

        // NZXT: drains queued telemetry before sending query, then parses 0x97 frame
        var nzxtDesc = Desc(0x1E71, 0x2129, 0xFFCA, 1);
        var nzxtReply = new byte[64];
        nzxtReply[0] = 0x4E; nzxtReply[1] = 2; nzxtReply[2] = 0x97; nzxtReply[17] = 66; nzxtReply[22] = 1;
        var nzxtSession = new MockSession([[0x4E, 0xE5, 0], null, [0x4E, 0xE5, 0], nzxtReply]);
        var nzxtReading = await new NzxtAdapter(_ => nzxtSession).ReadAsync(nzxtDesc, [nzxtDesc], CancellationToken.None);
        Check(nzxtReading.Single() is { Battery: 66, Charging: true, Transport: "USB" } && nzxtSession.Writes.Count == 1 && nzxtSession.Disposed,
            "NZXT session drains stale telemetry and parses battery frame");

        // Razer: feature report two-step battery + charging query, and early abort on error status
        var razerDesc = Desc(0x1532, 0x00E5, 0xFF00, 1);
        byte[] RazerReply(byte cmd, byte val, byte status = 2)
        {
            var r = new byte[91];
            r[1] = status; r[2] = 0x1F; r[6] = 2; r[7] = 7; r[8] = cmd; r[10] = val;
            for (int i = 3; i <= 88; i++) r[89] ^= r[i];
            return r;
        }
        var razerSession = new MockSession(featureReplies: [RazerReply(0x80, 128), RazerReply(0x84, 1)]);
        var razerReading = await new RazerAdapter(_ => razerSession).ReadAsync(razerDesc, [razerDesc], CancellationToken.None);
        Check(razerReading.Single() is { Battery: 50, Charging: true, Transport: "USB" } && razerSession.FeatureWrites.Count == 2 && razerSession.Disposed,
            "Razer session queries battery and charging feature reports");
        var razerError = new MockSession(featureReplies: [RazerReply(0x80, 0, status: 4)]);
        Check((await new RazerAdapter(_ => razerError).ReadAsync(razerDesc, [razerDesc], CancellationToken.None)).Count == 0
            && razerError.FeatureReads == 1 && razerError.Disposed,
            "Razer session aborts early on device error status");

        // SteelSeries & ASUS: clean ReadUntilAsync parsing
        var steelDesc = Desc(0x1038, 0x22A1, 0xFFC0, 1);
        var steelSession = new MockSession([[1, 0, 99, 2], [0xB0, 3, 73, 1]]);
        var steelReading = await new HeadsetAdapter(_ => steelSession).ReadAsync(steelDesc, [steelDesc], CancellationToken.None);
        Check(steelReading.Single() is { Battery: 73, Charging: true } && steelSession.Disposed,
            "SteelSeries session filters unrelated reports and reads live battery");

        var asusDesc = Desc(0x0B05, 0x1A72, 0xFF00, 1, path: "test&mi_00");
        var asusSession = new MockSession([[0, 0x12, 7, 0, 0, 61, 0, 0, 0, 0, 0]]);
        var asusReading = await new AsusAdapter(_ => asusSession).ReadAsync(asusDesc, [asusDesc], CancellationToken.None);
        Check(asusReading.Single() is { Battery: 61, Charging: false } && asusSession.Disposed,
            "ASUS session parses report-id normalized response");

        // Corsair: requires two matching readings before accepting
        var corsairDesc = Desc(0x1B1C, 0x2B07, 0xFF42, 1);
        var corsairSession = new MockSession([[1, 2, 0, 0x26, 2], [1, 2, 0, 0x26, 2]]);
        var corsairReading = await new CorsairAdapter(_ => corsairSession).ReadAsync(corsairDesc, [corsairDesc], CancellationToken.None);
        Check(corsairReading.Single() is { Battery: 55, Transport: "2.4G" } && corsairSession.Writes.Count == 2 && corsairSession.Disposed,
            "Corsair session confirms two consistent samples before reporting");

        // MCHOSE: feature report retry and wired 0x12 fallback
        var mchoseDesc = Desc(0x5253, 0x00A1, 0xFF01, 1, name: "MCHOSE AX5 V2");
        byte[] mchosePayload = [0x53, 0x52, 0xA1, 0x00, 0, 0, 0, 0, 0, 82, 1];
        byte[] mchoseReply = [0x11, 0xF9, .. mchosePayload.Select(b => (byte)(b ^ 0xFF))];
        var mchoseSession = new MockSession(featureReplies: [mchoseReply]);
        var mchoseReading = await new MchoseAdapter(_ => mchoseSession).ReadAsync(mchoseDesc, [mchoseDesc], CancellationToken.None);
        Check(mchoseReading.Single() is { Battery: 82, VendorDisplay: 80, Charging: true, Transport: "USB" } && mchoseSession.Disposed,
            "MCHOSE session reads wired feature report and computes vendor display");

        return checks;
    }

    private sealed class MockSession : IHidBatterySession
    {
        private readonly Queue<byte[]?> reads = new();
        private readonly Queue<byte[]?> features = new();
        private readonly Func<byte[], IEnumerable<byte[]?>>? writeReply;
        public List<byte[]> Writes { get; } = [];
        public List<byte[]> FeatureWrites { get; } = [];
        public int FeatureReads { get; private set; }
        public bool Disposed { get; private set; }

        public MockSession(IEnumerable<byte[]?>? readReplies = null, IEnumerable<byte[]?>? featureReplies = null,
            Func<byte[], IEnumerable<byte[]?>>? writeReply = null)
        {
            this.writeReply = writeReply;
            foreach (var r in readReplies ?? []) reads.Enqueue(r);
            foreach (var f in featureReplies ?? []) features.Enqueue(f);
        }

        public Task WriteAsync(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add(report);
            if (writeReply is not null)
                foreach (var reply in writeReply(report)) reads.Enqueue(reply);
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReadAsync(int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(reads.Count > 0 ? reads.Dequeue() : null);
        }

        public bool SetFeature(byte[] report)
        {
            FeatureWrites.Add(report);
            return true;
        }

        public byte[]? GetFeature(byte id, int length)
        {
            FeatureReads++;
            return features.Count > 0 ? features.Dequeue() : null;
        }

        public void Dispose() => Disposed = true;
    }
}
