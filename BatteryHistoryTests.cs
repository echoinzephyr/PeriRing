namespace BatteryDisplay;

internal static class BatteryHistoryTests
{
    internal static int Run(List<string> failures)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) failures.Add(message); }
        var now = DateTimeOffset.Parse("2026-11-15T12:00:00+08:00");
        var mouse = new Peripheral("mouse-a", "Mouse A", true, 74, "test", "2.4G", VendorDisplay: 70,
            ConnectionId: "receiver-a", ReceiverPresent: true);
        var history = new BatteryHistory();
        var awake = history.Apply([mouse], now).Single();
        Check(awake.CanShow && !awake.IsCached && awake.BatteryUpdatedAt == now, "Fresh sample is timestamped");
        var noResponse = new Peripheral("receiver-a-entry", "Receiver", false, null, "test", "USB / 2.4G",
            ConnectionId: "receiver-a", ReceiverPresent: true);
        var sleeping = history.Apply([noResponse], now.AddMinutes(1)).Single();
        Check(sleeping is { Battery: 74, VendorDisplay: 70, Connected: false, IsCached: true } && sleeping.CanShow,
            "Sleeping mouse retains its own battery in the tray");
        Check(sleeping.Id == mouse.Id && sleeping.Transport == "2.4G", "Sleeping sample keeps identity and connection type");
        Check(sleeping.BatteryUpdatedAt == now && sleeping.TooltipLevel.Contains("上次") && sleeping.State.Contains("11-15 12:00"),
            "Repeated scans never relabel an old sample as fresh");
        Check(history.Apply([], now.AddMinutes(2)).Count == 0, "Unplugged receiver disappears from tray");
        var unknown = history.Apply([noResponse with { ConnectionId = "receiver-b" }], now).Single();
        Check(!unknown.CanShow && unknown.Battery is null, "A different receiver never borrows the cached battery");
        var second = mouse with { Id = "mouse-b", Name = "Mouse B", ConnectionId = "receiver-b", Battery = 13, VendorDisplay = 10 };
        history.Apply([mouse, second], now);
        var both = history.Apply([noResponse, noResponse with { Id = "receiver-b-entry", ConnectionId = "receiver-b" }], now.AddMinutes(3));
        Check(both.Count == 2 && both.Single(d => d.Id == "mouse-a").Battery == 74 && both.Single(d => d.Id == "mouse-b").Battery == 13,
            "Two identical receivers retain independent readings");
        var resumed = history.Apply([mouse with { Battery = 61, VendorDisplay = 60 }], now.AddMinutes(4)).Single();
        Check(!resumed.IsCached && resumed.Battery == 61 && resumed.BatteryUpdatedAt == now.AddMinutes(4), "Wake replaces the cached sample");
        var restored = BatteryHistory.Import(history.Export());
        Check(restored.Apply([noResponse], now.AddMinutes(5)).Single() is { Battery: 61, IsCached: true }, "Cache survives an app restart");
        var emptyHistory = new BatteryHistory();
        Check(!emptyHistory.Apply([noResponse], now).Single().CanShow, "Never-read device does not get a fabricated level");
        emptyHistory.Apply([mouse with { Battery = 0, VendorDisplay = null }], now);
        Check(emptyHistory.Apply([noResponse], now).Single() is { Battery: 0, IsCached: true }, "Confirmed zero battery can be retained");
        var charging = new BatteryHistory(); charging.Apply([mouse with { Charging = true }], now);
        Check(!charging.Apply([noResponse], now).Single().Charging, "Cached reading does not assert charging is still active");
        Check(!history.Apply([new("bt", "Bluetooth mouse", false, 80, "Windows")], now).Single().CanShow,
            "Disconnected Bluetooth history stays excluded");
        Check(!history.Apply([noResponse with { ReceiverPresent = false }], now).Single().CanShow, "Absent USB link cannot display cached battery");
        return count;
    }
}
