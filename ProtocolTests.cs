namespace BatteryDisplay;

internal static class ProtocolTests
{
    internal static int Run(List<string> failures)
    {
        int count = 0;
        void Check(bool condition, string name) { count++; if (!condition) failures.Add(name); }
        void Reject(Action action, string name)
        {
            try { action(); Check(false, name); }
            catch (InvalidDataException) { Check(true, name); }
        }
        HidDescriptor Descriptor(ushort vid, ushort pid, ushort usagePage = 0xFF02, ushort usage = 2) =>
            new("test", vid, pid, "test", new() { UsagePage = usagePage, Usage = usage, InputLength = 65, OutputLength = 65, FeatureLength = 91 });
        var ax5Wired = Descriptor(0x5253, 0x00A1, 0xFF01, 1) with { Name = "MCHOSE AX5 V2" };
        var mchose = new MchoseAdapter();
        Check(mchose.Matches(ax5Wired) && !mchose.Matches(Descriptor(0x5253, 0x00A1, 0xFF0B, 0x104))
            && !mchose.Matches(Descriptor(0x5253, 0x00A1, 0xFF01, 2)), "AX5 V2 wired option only queries its designated vendor collection");
        byte[] MchoseStatus(ushort vendor, ushort model, byte flags, byte level, byte charging)
        {
            byte[] data = [(byte)vendor, (byte)(vendor >> 8), (byte)model, (byte)(model >> 8), 0, 0, 0, 0, flags, level, charging];
            return [0x11, 0xF9, .. data.Select(b => (byte)(b ^ 0xFF))];
        }
        var ax5Status = MchoseStatus(0x5253, 0x00A1, 0, 82, 1);
        var mchoseReceiver = ax5Wired with { Product = 0x1020 };
        Check(MchoseAdapter.Parse(ax5Status, ax5Wired) is { Connected: true, Transport: "USB", Battery: 82, VendorDisplay: 80, Charging: true },
            "AX5 V2 wired reading is valid with the wireless link bit clear");
        Check(MchoseAdapter.Parse(ax5Status, mchoseReceiver) is null, "Offline MCHOSE receivers still require the active connection flag");
        Check(MchoseAdapter.Parse(MchoseStatus(0x5253, 0x00A1, 8, 82, 0), mchoseReceiver) is { Transport: "2.4G", Battery: 82, Charging: false },
            "AX5 V2 wireless reading is preserved");
        Check(MchoseAdapter.Parse(MchoseStatus(0x5253, 0x0031, 0, 82, 1), ax5Wired) is null
            && MchoseAdapter.Parse(MchoseStatus(0x5253, 0x00A1, 0, 101, 1), ax5Wired) is null,
            "AX5 V2 wired mode rejects a different model or invalid percentage");
        foreach (var (vendor, model) in new (ushort, ushort)[] { (0x5253, 0x0031), (0x5253, 0x00B0), (0x3837, 0x4018), (0x3837, 0x4021) })
        {
            var cable = Descriptor(vendor, model, 0xFF01, 1);
            Check(mchose.Matches(cable) && MchoseAdapter.Parse(MchoseStatus(vendor, model, 0, 91, 1), cable)
                is { Transport: "USB", Battery: 91, Charging: true }, $"MCHOSE {vendor:X4}:{model:X4} direct status");
        }
        Check(!mchose.Matches(Descriptor(0x3837, 0x418C, 0xFF01, 1)), "Unrelated MCHOSE models require their own protocol");
        ax5Status[0] = 0x12;
        Check(MchoseAdapter.Parse(ax5Status, ax5Wired, 0x12) is { Transport: "USB", Battery: 82 }
            && MchoseAdapter.Parse(ax5Status, ax5Wired) is null, "MCHOSE direct long report is only accepted when requested");
        var sonix = new HidDescriptor("test&mi_03", 0x05AC, 0x024F, "VK 75 Wireless Receiver",
            new() { UsagePage = 0xFF60, Usage = 0x61, InputLength = 33, OutputLength = 33 });
        var sonixAdapter = new SonixKeyboardAdapter();
        Check(sonixAdapter.Matches(sonix), "VK75 exact wireless command interface");
        Check(!sonixAdapter.Matches(sonix with { Name = "Apple Keyboard" }), "Shared Apple VID/PID cannot identify a VK75");
        Check(!sonixAdapter.Matches(sonix with { Path = "test&mi_00" }), "Keyboard input interface is never queried");
        var wiredVk75 = new HidDescriptor("test&mi_02", 0x05AC, 0x024F, "Valkyrie 75",
            new() { UsagePage = 0xFF13, FeatureLength = 65 });
        var wiredReading = sonixAdapter.ReadAsync(wiredVk75, [wiredVk75], CancellationToken.None).GetAwaiter().GetResult().Single();
        Check(wiredReading is { Connected: true, Battery: null, Transport: "USB" } && !wiredReading.CanShow,
            "Wired VK75 identification requires no battery command and never fabricates a level");
        var sonixRequest = SonixKeyboardAdapter.BatteryRequest();
        Check(sonixRequest.Length == 33 && sonixRequest[1] == 0x20 && sonixRequest[2] == 1
            && sonixRequest[^1] == sonixRequest.Take(32).Sum(b => b), "SONiX battery read command and byte-sum checksum");
        var sonixReply = new byte[33]; sonixReply[1] = 0x20; sonixReply[2] = 1; sonixReply[4] = 78;
        Check(SonixKeyboardAdapter.ParseBattery(sonixReply) == 78, "SONiX matching battery reply");
        sonixReply[4] = 0;
        Check(SonixKeyboardAdapter.ParseBattery(sonixReply) is null, "SONiX unavailable reply is not zero battery");
        sonixReply[4] = 101;
        Check(SonixKeyboardAdapter.ParseBattery(sonixReply) is null, "SONiX invalid percentage is rejected");
        sonixReply[4] = 78; sonixReply[2] = 2;
        Check(SonixKeyboardAdapter.ParseBattery(sonixReply) is null, "SONiX unrelated command reply is ignored");
        Check(SonixKeyboardAdapter.ParseBattery([0, 0x20, 1, 0, 78]) is null, "SONiX truncated reply is rejected");
        var compx = Descriptor(0x3554, 0xF58A);
        byte[] power = [8, 4, 0, 0, 0, 2, 80, 0, 0x0F, 0xAD, 0, 0, 0, 0, 0, 0, 0x3B];
        Check(CompxAdapter.Parse(power, compx)?.Battery == 80, "CompX recorded 80% frame");
        power[16] ^= 1;
        Check(CompxAdapter.Parse(power, compx) is null, "CompX rejects damaged checksum");
        power[16] ^= 1; power[1] = 0x0A;
        Check(CompxAdapter.Parse(power, compx) is null, "CompX ignores unsolicited events");
        Check(CompxAdapter.Request().SequenceEqual(new byte[] { 8, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49 }), "CompX read-only request frame");
        byte[] pulsarCable = [8, 4, 0, 0, 0, 2, 25, 1, 0x0E, 0x52, 0, 0, 0, 0, 0, 0, 0xCD];
        var compxAdapter = new CompxAdapter();
        var pulsar = Descriptor(0x3710, 0x3414);
        Check(compxAdapter.Matches(pulsar) && CompxAdapter.Parse(pulsarCable, pulsar) is { Transport: "USB", Battery: 25, Charging: true },
            "Pulsar X2 CrazyLight Mini captured cable reading shows USB");
        Check(CompxAdapter.Parse(pulsarCable, compx with { Product = 0xF58C }) is { Transport: "USB" }
            && CompxAdapter.Parse(pulsarCable, compx) is { Transport: "2.4G" }, "VXE R1 Pro Max distinguishes the cable and receiver");

        var atkWired = Descriptor(0x320F, 0x5055, 0xFF1C, 0x92) with { Name = "ATK V75X" };
        var atk = new AtkKeyboardAdapter();
        Check(atk.Matches(atkWired) && !atk.Matches(atkWired with { Name = "Other Evision keyboard" })
            && !atk.Matches(atkWired with { Product = 0x5088 }), "ATK polling is limited to the named V75X cable collection");
        var atkRequest = AtkKeyboardAdapter.BatteryRequest(0x010A);
        Check(atkRequest.Length == 32 && atkRequest.Take(4).SequenceEqual(new byte[] { 4, 10, 1, 0x1A }) && atkRequest.Skip(4).All(b => b == 0),
            "ATK request contains only the battery command and request ID");
        byte[] atkReply = [4, 10, 1, 0x1A, 0, 0, 0, 0, 91, 1];
        Check(AtkKeyboardAdapter.Parse(atkReply, atkWired, 0x010A) is { Transport: "USB", Battery: 91, Charging: true }, "ATK V75X cable battery and charging");
        Check(AtkKeyboardAdapter.Parse(atkReply, atkWired, 0x010B) is null, "ATK ignores a reply for an older request");
        atkReply[9] = 3;
        Check(AtkKeyboardAdapter.Parse(atkReply, atkWired, 0x010A) is null, "ATK rejects an unknown charging status");

        var nzxtWired = Descriptor(0x1E71, 0x2129, 0xFFCA, 1);
        var nzxtWireless = nzxtWired with { Product = 0x2101 };
        var nzxt = new NzxtAdapter();
        Check(nzxt.Matches(nzxtWired) && nzxt.Matches(nzxtWireless) && !nzxt.Matches(nzxtWired with { Product = 0x2131 }),
            "NZXT only queries the Lift Elite Wireless cable and receiver");
        var nzxtRequest = NzxtAdapter.BatteryRequest();
        Check(nzxtRequest.Length == 64 && nzxtRequest.Take(5).SequenceEqual(new byte[] { 0x4E, 2, 0x81, 0, 0xB0 }), "NZXT telemetry read request");
        var nzxtReply = new byte[64];
        nzxtReply[0] = 0x4E; nzxtReply[1] = 2; nzxtReply[2] = 0x97;
        nzxtReply[17] = 84; nzxtReply[22] = 1;
        Check(NzxtAdapter.Parse(nzxtReply, nzxtWired) is { Transport: "USB", Battery: 84, Charging: true }, "NZXT cable percentage and charging flag");
        nzxtReply[17] = 77; nzxtReply[22] = 0;
        Check(NzxtAdapter.Parse(nzxtReply, nzxtWireless) is { Transport: "2.4G", Battery: 77, Charging: false }, "NZXT receiver percentage and charging flag");
        nzxtReply[18] = 1;
        Check(NzxtAdapter.Parse(nzxtReply, nzxtWireless) is null && NzxtAdapter.Parse([0x4E, 0xE5, 0], nzxtWired) is null,
            "NZXT ignores acknowledgements and out-of-range 16-bit levels");

        byte[] sinowealth = [0, 0xA1, 0, 2, 2, 0, 0x83, 1, 75];
        Check(SinowealthAdapter.Parse(sinowealth, Descriptor(0x373E, 0x001E)) is { Battery: 75, Charging: true }, "Sinowealth matching response");
        sinowealth[1] = 0xA4;
        Check(SinowealthAdapter.Parse(sinowealth, compx) is null, "Sinowealth asleep status is not live battery");
        sinowealth[1] = 0xA1; sinowealth[8] = 101;
        Check(SinowealthAdapter.Parse(sinowealth, compx) is null, "Sinowealth rejects invalid battery");

        var razerRequest = RazerAdapter.BuildRequest(0x1F, 0x80);
        Check(razerRequest.Length == 91 && razerRequest[2] == 0x1F && razerRequest[89] == 0x85, "Razer battery request and CRC");
        var razerReply = new byte[91];
        razerReply[1] = 2; razerReply[2] = 0x1F; razerReply[6] = 2; razerReply[7] = 7; razerReply[8] = 0x80; razerReply[10] = 128; razerReply[89] = 5;
        Check(RazerAdapter.ParseReply(razerReply, 0x80) == 128, "Razer matching power reply");
        Check(DeviceCatalog.Find(Descriptor(0x1532, 0x00E5), "razer") is { Transport: "USB", Parameter: 0x1F }
            && DeviceCatalog.Find(Descriptor(0x1532, 0x00E6), "razer") is { Transport: "2.4G", Parameter: 0x1F },
            "Razer Viper V4 Pro cable and receiver keep their reported transport and transaction ID");
        Check(RazerAdapter.ParseReply(razerReply, 0x84) is null, "Razer ignores another command's reply");
        razerReply[89] ^= 1;
        Check(RazerAdapter.ParseReply(razerReply, 0x80) is null, "Razer rejects invalid CRC");

        var logi = Descriptor(0x046D, 0xC548);
        Check(LogitechAdapter.Parse(0x1000, [63, 0, 0], logi, 1)?.Battery == 63, "HID++ exact percentage");
        var coarse = LogitechAdapter.Parse(0x1004, [0, 4, 1], logi, 1);
        Check(coarse is { Battery: null, Gauge: 50, Charging: true } && coarse.Level.Contains("档位"), "HID++ coarse reading is labeled");
        Check(LogitechAdapter.Parse(0x1001, [255, 255, 0], logi, 1) is null, "HID++ filler voltage is rejected");
        Check(LogitechAdapter.Parse(0x1F20, [0x0F, 0x00, 0], logi, 1) is null, "HID++ disconnected headset is not live");
        var voltage = LogitechAdapter.Parse(0x1001, [0x0F, 0x00, 0], logi, 1);
        Check(voltage is { Battery: null, Gauge: not null } && voltage.State.Contains("估算"), "HID++ voltage estimate is explicit");
        var gpw = Descriptor(0x046D, 0xC088, 0xFF00, 2);
        var logitech = new LogitechAdapter();
        Check(logitech.Matches(gpw) && LogitechAdapter.PollSlots(gpw).SequenceEqual(new byte[] { 255 }),
            "G PRO Wireless uses the direct HID++ address on the cable");
        Check(LogitechAdapter.Parse(0x1001, [0x0F, 0x13, 0x80], gpw, 255)
            is { Name: "Logitech G PRO Wireless", Battery: null, Gauge: not null, Transport: "USB", Charging: true } gpwEstimate
            && gpwEstimate.Level.StartsWith("约 ", StringComparison.Ordinal),
            "Wired G PRO Wireless voltage is an estimate, with USB and charging labels");
        foreach (var (product, name) in new (ushort, string)[]
        {
            (0xC094, "Logitech G PRO X SUPERLIGHT"), (0xC09B, "Logitech G PRO X SUPERLIGHT 2"), (0xC0A0, "Logitech G PRO X SUPERLIGHT 2 DEX"),
            (0xC08D, "Logitech G502 LIGHTSPEED"), (0xC098, "Logitech G502 X LIGHTSPEED"), (0xC095, "Logitech G502 X PLUS")
        })
        {
            var cable = gpw with { Product = product };
            Check(logitech.Matches(cable) && LogitechAdapter.Parse(0x1004, [62, 4, 1], cable, 255) is { Transport: "USB", Battery: 62, Charging: true } wired
                && wired.Name == name, $"Logitech direct model and transport {product:X4}");
        }
        foreach (ushort product in new ushort[] { 0xC539, 0xC545, 0xC547, 0xC54D })
        {
            var receiver = gpw with { Product = product, Name = "罗技接收器" };
            Check(logitech.Matches(receiver) && LogitechAdapter.PollSlots(receiver).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 })
                && LogitechAdapter.Parse(0x1004, [0x61, 8, 0], receiver, 1) is { Name: "罗技接收器", Transport: "2.4G", Battery: 97 },
                $"Logitech {product:X4} queries paired slots without guessing a model from the receiver");
        }
        Check(LogitechAdapter.DisplayName("PRO X 2") == "Logitech G PRO X SUPERLIGHT 2"
            && LogitechAdapter.DisplayName("PRO X 2 LIGHTSPEED") == "PRO X 2 LIGHTSPEED", "GPX2 firmware name does not replace the similarly named headset");

        var sony = Descriptor(0x054C, 0x0CE6, 1, 5);
        var ds = new byte[64]; ds[0] = 1; ds[53] = 0x15;
        Check(SonyAdapter.Parse(ds, sony) is { Battery: null, Gauge: 50, Charging: true }, "DualSense ten-percent battery step");
        Check(SonyAdapter.Parse([1, 0, 0, 0, 0, 0, 0, 0], sony) is null, "Sony minimal Bluetooth report is not battery");
        var ds4 = new byte[64]; ds4[0] = 1; ds4[31] = 4;
        Check(SonyAdapter.Parse(ds4, Descriptor(0x054C, 0x0BA0)) is null, "DS4 receiver without controller is not empty battery");

        var steel = Descriptor(0x1038, 0x22A1);
        Check(HeadsetAdapter.Parse([0xB0, 3, 80, 3], steel) is { Battery: 80, Charging: false }, "Nova 7 live response");
        Check(HeadsetAdapter.Parse([0xB0, 2, 80, 0], steel) is null, "Nova 7 offline receiver cache is rejected");
        Check(HeadsetAdapter.Parse([1, 0, 99, 2], steel) is null, "SteelSeries unrelated report is ignored");
        var asus = Descriptor(0x0B05, 0x1A72);
        byte[] asusReply = [0, 0x12, 7, 0, 0, 67, 0, 0, 0, 0, 1];
        Check(AsusAdapter.Parse(asusReply, asus) is { Battery: 67, Charging: true }, "ASUS report-id normalization");
        asusReply[5] = 201;
        Check(AsusAdapter.Parse(asusReply, asus) is null, "ASUS invalid percentage is rejected");
        Check(HyperXAdapter.ParseBattery([0x66, 0x89, 0x0E, 0xD7, 48]) == 48, "HyperX recorded battery frame");
        Check(HyperXAdapter.ParseBattery([0x66, 0x89, 0, 0, 0]) is null, "HyperX uninitialized receiver is not zero battery");
        Check(CorsairAdapter.Parse([1, 2, 0, 0x26, 2]) == 550, "Corsair recorded 55% response");
        Check(CorsairAdapter.Parse([1, 2, 5, 0x26, 2]) is null, "Corsair failed command is rejected");

        Check(ControllerProvider.FromXInput(0, 2, 1) is { Battery: null, Gauge: 33 }, "XInput is a level, not an exact percentage");
        Check(ControllerProvider.FromXInput(0, 1, 3) is { Battery: null, Gauge: null }, "Wired XInput does not fabricate 100%");
        Check(ControllerProvider.FromXInput(0, 255, 3) is { Battery: null, Gauge: null }, "Unknown XInput battery type is not a reading");
        Check(ControllerProvider.FromXInput(0, 0, 0) is null, "Disconnected XInput is omitted");
        byte[] device = [0x0A, 5, 86, 97, 100, 101, 114, 0x18, 2, 0x22, 1, 49, 0x40, 1, 0x48, 3];
        byte[] Envelope(byte[] data) => [8, 0x81, 0x20, 0x2A, (byte)(data.Length + 2), 0x0A, (byte)data.Length, .. data];
        var fly = FlydigiProvider.ParseDevices(Envelope(device)).Single();
        Check(fly.Name == "Vader" && fly.Battery is null && fly.Gauge == 60 && fly.Level.Contains("档位"), "Flydigi service list and coarse level");
        device[^1] = 6;
        Check(FlydigiProvider.ParseDevices(Envelope(device)).Single() is { Charging: true, Battery: null }, "Flydigi charging is not invented percentage");
        device[^1] = 3; device[^3] = 0;
        Check(FlydigiProvider.ParseDevices(Envelope(device)).Count == 0, "Flydigi disconnected device is omitted");
        Reject(() => Proto.Fields([0x0A, 0x7F]), "Protobuf truncated length is bounded");
        Reject(() => Proto.Fields([0]), "Protobuf zero tag is invalid");
        Reject(() => Proto.Fields(Enumerable.Repeat((byte)255, 11).ToArray()), "Protobuf varint overflow is bounded");

        Check(DeviceCatalog.Models.GroupBy(m => (m.Protocol, m.Vendor, m.Product)).All(g => g.Count() == 1), "Device catalog has no conflicting identities");
        Check(UsbProvider.Adapters.All(a => !a.Matches(Descriptor(0x05AC, 0x024F))), "Unknown keyboard is never sent vendor queries");
        var path = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#7&1234abcd&0&0000#{guid}";
        var first = logi with { Path = path };
        var sibling = logi with { Path = path.Replace("col01", "col02").Replace("0000", "0001") };
        var other = logi with { Path = path.Replace("1234abcd", "abcdef12") };
        Check(first.Group == sibling.Group && first.Group != other.Group, "Collections merge while two identical receivers remain distinct");
        var report = Task.Run(() => BatteryService.ScanAsync(CancellationToken.None, [new TestProvider(false), new TestProvider(true)])).GetAwaiter().GetResult();
        Check(report.Devices.Count == 1 && report.Providers.Count(p => p.Warning is not null) == 1, "One provider failure does not remove another provider's readings");
        count += MchoseHeadsetTests.Run(failures);
        count += Task.Run(() => V9ReaderTests.RunAsync(failures)).GetAwaiter().GetResult();
        return count;
    }

    private sealed class TestProvider(bool fail) : IBatteryProvider
    {
        public string Name => fail ? "failed test" : "working test";
        public string Coverage => "test";
        public string Verification => "test";
        public Task<List<Peripheral>> ReadAsync(CancellationToken token) => fail
            ? Task.FromException<List<Peripheral>>(new IOException("expected test failure"))
            : Task.FromResult(new List<Peripheral> { new("test-device", "test", true, 80, "fixture") });
    }
}
