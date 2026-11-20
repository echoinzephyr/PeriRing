namespace BatteryDisplay;

internal static class LowBatteryAlertTests
{
    internal static int Run(List<string> failures)
    {
        int checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) failures.Add(name); }
        var alerts = new LowBatteryAlerts();
        var mouse = new Peripheral("mouse", "AX5 V2", true, 22, "test", VendorDisplay: 20, UseVendorDisplay: true);
        int attempts = 0;
        bool Send(Peripheral _) { attempts++; return true; }
        alerts.Observe([mouse with { UseVendorDisplay = false }, mouse with { IsCached = true }, mouse with { Charging = true },
            mouse with { Connected = false }, mouse with { Battery = null, VendorDisplay = null }], true, Send);
        Check(attempts == 0, "Non-low, cached, charging, disconnected and unknown readings do not notify");
        alerts.Observe([mouse], false, Send);
        Check(attempts == 0, "Disabled alerts do not notify");
        alerts.Observe([mouse], true, _ => { attempts++; return false; });
        Check(attempts == 1, "Displayed 20 percent qualifies even when raw battery is 22 percent");
        alerts.Observe([mouse], true, Send);
        alerts.Observe([mouse], true, Send);
        Check(attempts == 2, "A rejected notification can retry, while an accepted one is sent once");
        alerts.Observe([mouse with { VendorDisplay = 25 }], true, Send);
        alerts.Observe([mouse], true, Send);
        Check(attempts == 2, "Exactly 25 percent does not reset the alert");
        alerts.Observe([mouse with { VendorDisplay = 26 }], false, Send);
        alerts.Observe([mouse, mouse with { Id = "other" }], true, Send);
        Check(attempts == 4, "A fresh recovery rearms the alert and device identities are independent");
        alerts.Observe([mouse with { VendorDisplay = 35 }], true, Send, 35);
        alerts.Observe([mouse with { VendorDisplay = 40 }], true, Send, 35);
        alerts.Observe([mouse with { VendorDisplay = 35 }], true, Send, 35);
        Check(attempts == 5, "Changed threshold applies to the next scan and includes its exact boundary");
        alerts.Observe([mouse with { VendorDisplay = 41 }], true, Send, 35);
        alerts.Observe([mouse with { VendorDisplay = 35 }], true, Send, 35);
        Check(attempts == 6, "A configured threshold rearms only after five additional points");
        Check(SegmentedRing.Layout([mouse with { VendorDisplay = 35 }], 35)[0].Low
            && !SegmentedRing.Layout([mouse with { VendorDisplay = 35 }], 30)[0].Low,
            "Ring follows the configured threshold and displayed value");
        return checks;
    }
}
