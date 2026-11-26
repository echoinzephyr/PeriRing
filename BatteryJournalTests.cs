namespace BatteryDisplay;

internal static class BatteryJournalTests
{
    internal static int Run(List<string> failures)
    {
        int checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) failures.Add(name); }
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var device = new Peripheral("one", "Same model", true, 71, "test", "2.4G", VendorDisplay: 70);
        var second = device with { Id = "two" };
        var preferences = Preferences.FromJson("""{"DeviceOrder":["two","one"],"HiddenDevices":["two"],"RefreshSeconds":300}""");
        Check(preferences.LowBatteryThreshold == 20 && preferences.DeviceAliases.Count == 0 && preferences.RefreshSeconds == 300
            && preferences.HiddenDevices.Contains("two"), "Older settings retain their preferences and use the default threshold");
        preferences = Preferences.FromJson("""{"LowBatteryThreshold":35,"DeviceAliases":{"one":"  办公鼠标  ","two":"\r","xinput:0":"临时"}}""");
        var presented = preferences.Present([device, second]);
        Check(presented[0].DisplayName == "办公鼠标" && presented[0].Name == "Same model" && presented[1].DisplayName == "Same model"
            && preferences.LowBatteryThreshold == 35 && preferences.DeviceAliases.Count == 1, "Only the exact stable device ID receives the saved alias");
        Check(Preferences.FromJson("""{"LowBatteryThreshold":51,"DeviceAliases":null}""").LowBatteryThreshold == 20, "Invalid thresholds use the default");

        string file = Path.Combine(Path.GetTempPath(), "periring-journal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var journal = new BatteryJournal(file);
            void Observe(int minutes, params Peripheral[] readings) => journal.Observe(readings, now.AddMinutes(minutes));
            Observe(0, device, second with { Battery = null, Gauge = 66 }, second with { Battery = 101 });
            Observe(14, device);
            Check(journal.Read(now.AddMinutes(14)).Single().Samples.Count == 1, "Unchanged values are coalesced; gauge-only and invalid percentages are excluded");
            Observe(15, device);
            Observe(16, device with { Charging = true });
            Observe(17, device with { Charging = true, IsCached = true });
            Observe(18, device with { Charging = true });
            Observe(19, device with { Charging = true, Connected = false });
            Observe(20, device with { Charging = true });
            Observe(55, device);
            var entries = journal.Read(now.AddMinutes(55)).Single().Samples;
            Check(entries.Count == 6 && entries.Select(x => x.StartsSegment).SequenceEqual([true, false, false, true, true, true]),
                "History adds 15-minute and charging samples, and keeps sleep, disconnect and scan gaps separate");
            Check(entries[2].Charging && entries[0].Level(true) == 70 && entries[0].Level(false) == 71, "History preserves charging state and both percentage formats");
            journal = BatteryJournal.Load(file, now.AddMinutes(56));
            Observe(56, device);
            Check(journal.Read(now.AddMinutes(56)).Single().Samples is { Count: 7 } reloaded && reloaded[^1].StartsSegment,
                "History survives restart and starts a fresh segment");
            Check(journal.Read(now.AddDays(8)).Count == 0 && BatteryJournal.Load(file, now.AddDays(8)).Read(now.AddDays(8)).Count == 0,
                "Expired observations are removed from disk as well as the UI");
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }

        var bounded = new BatteryJournal();
        bounded.Observe(Enumerable.Range(0, 36).Select(i => device with { Id = $"mouse-{i:D2}" }), now);
        Check(bounded.Read(now).Count == BatteryJournal.MaxDevices, "History caps the number of devices");
        for (int i = 0; i < BatteryJournal.MaxSamples + 4; i++)
            bounded.Observe([device with { Battery = i % 100 }], now.AddSeconds(i + 1));
        Check(bounded.Read(now.AddHours(1)).Single(entry => entry.Id == device.Id).Samples.Count == BatteryJournal.MaxSamples,
            "Frequent changes are bounded per device");

        var statsEmpty = BatteryJournal.Summarize([new(now, 80, null, false, true)], false);
        Check(statsEmpty.DischargeRatePerHour is null && statsEmpty.EstimatedRemaining is null && statsEmpty.LastChargedAt is null,
            "Battery stats never fabricate discharge rates from a single sample");
        var statsDischarge = BatteryJournal.Summarize([
            new(now.AddHours(-5), 100, null, true, true),
            new(now.AddHours(-4), 100, null, false, false),
            new(now.AddHours(-2), 90, null, false, false),
            new(now, 80, null, false, false)
        ], false);
        Check(statsDischarge.LastChargedAt == now.AddHours(-4)
            && statsDischarge.DischargeRatePerHour is > 4.9 and < 5.1
            && statsDischarge.EstimatedRemaining is { TotalHours: > 15.9 and < 16.1 },
            "Battery stats calculate discharge rate, last full charge and conservative remaining time");

        var linkPrefs = Preferences.FromJson("""{"DeviceAliases":{"dongle":"主力鼠标"},"DeviceLinks":{"cable":"dongle","self":"self","a":"b","b":"a","xinput:0":"dongle"}}""");
        Check(linkPrefs.DeviceLinks.Count == 1 && linkPrefs.ResolveCanonicalId("cable") == "dongle"
            && linkPrefs.ResolveCanonicalId("a") == "a" && !linkPrefs.DeviceLinks.ContainsKey("xinput:0"),
            "Device links sanitize cycles, self-links and temporary XInput slots");
        var sleepingDongle = new Peripheral("dongle", "AX5 V2", false, 70, "迈从", "2.4G", IsCached: true, ReceiverPresent: true);
        var activeCable = new Peripheral("cable", "AX5 V2", true, 85, "迈从", "USB", Charging: true);
        var mergedDevices = linkPrefs.Present([sleepingDongle, activeCable]);
        Check(mergedDevices.Count == 1 && mergedDevices[0] is { Id: "dongle", DisplayName: "主力鼠标", Battery: 85, Charging: true, Transport: "USB" },
            "Linked multi-mode devices merge into one canonical entry preferring the active mode");

        var mergedJournal = new BatteryJournal();
        mergedJournal.Observe([sleepingDongle with { Connected = true, IsCached = false, Battery = 70 }], now.AddMinutes(-20));
        mergedJournal.Observe([activeCable], now);
        var mergedHistory = mergedJournal.ReadMergedSamples(linkPrefs.LinkedIdsFor("dongle"), now);
        Check(mergedHistory.Count == 2 && mergedHistory[0].StartsSegment && !mergedHistory[1].StartsSegment,
            "Merged history connects multi-mode transitions within the continuity window");
        return checks;
    }
}
