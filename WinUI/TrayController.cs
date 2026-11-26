using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BatteryDisplay;

internal sealed class TrayController : IDisposable
{
    public Preferences Preferences { get; } = Preferences.Load();
    public List<Peripheral> Devices { get; private set; } = [];
    public List<ProviderState> Providers { get; private set; } = [];
    public DateTimeOffset? Updated { get; private set; }
    public bool Refreshing { get; private set; }
    public string? Error { get; private set; }
    public bool CheckingUpdates { get; private set; }
    public bool InstallingUpdate { get; private set; }
    public UpdateResult? Update { get; private set; }
    private CancellationTokenSource? updateCts;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueueTimer trayDpiTimer;
    private readonly DispatcherQueueTimer updateTimer;
    private readonly DispatcherQueueTimer deviceChangeTimer;
    public string UpdateStatus { get; private set; } = "";
    private DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset lastCleanup = DateTimeOffset.MinValue;
    private int trayIconSize;
    private bool taskbarLight;
    private Task? activeRefresh;
    private readonly Forms.NotifyIcon manager = new();
    private readonly LowBatteryAlerts lowBatteryAlerts = new();
    private readonly BatteryNotifications notifications;
    private readonly CancellationTokenSource lifetime = new();
    private readonly BatteryHistory batteryHistory = BatteryHistory.Load();
    private readonly BatteryJournal journal;
    private readonly EventWaitHandle? showEvent;
    private readonly RegisteredWaitHandle? showWait;
    private readonly FlyoutWindow window;
    private readonly TrayMenuWindow menuWindow;
    private readonly HoverWindow hoverWindow;
    private readonly DispatcherQueueTimer hoverDelay;
    private readonly DispatcherQueueTimer hoverTracking;
    private (int X, int Y) hoverOrigin;
    private bool quitting;
    private bool sessionLocked;
    private readonly bool preview;

    public TrayController(bool show, bool preview)
    {
        this.preview = preview;
        journal = preview ? new() : BatteryJournal.Load(Path.Combine(AppContext.BaseDirectory, "data", "battery-history.json"), DateTimeOffset.Now);
        DebugService.Enabled = Preferences.DebugMode;
        window = new FlyoutWindow(this);
        DebugService.Log("startup", "flyout-ready");
        menuWindow = new TrayMenuWindow(this);
        DebugService.Log("startup", "menu-ready");
        hoverWindow = new HoverWindow();
        notifications = new(() => dispatcher.TryEnqueue(() =>
        {
            if (!quitting) { HideHover(); window.ShowDevices(); window.ShowFlyout(); }
        }));
        DebugService.Log("startup", "hover-ready");
        hoverDelay = dispatcher.CreateTimer();
        hoverDelay.Interval = TimeSpan.FromMilliseconds(420);
        hoverDelay.IsRepeating = false;
        hoverDelay.Tick += (_, _) =>
        {
            if (!menuWindow.IsOpen && !window.IsFlyoutVisible) hoverWindow.ShowValues(TrayDevices, Preferences);
        };
        hoverTracking = dispatcher.CreateTimer();
        hoverTracking.Interval = TimeSpan.FromMilliseconds(100);
        hoverTracking.Tick += (_, _) =>
        {
            var cursor = NativeWindow.CursorPosition();
            if (Math.Abs(cursor.X - hoverOrigin.X) > 22 || Math.Abs(cursor.Y - hoverOrigin.Y) > 22 || menuWindow.IsOpen || window.IsFlyoutVisible) HideHover();
        };
        trayIconSize = NativeWindow.TrayIconSize();
        taskbarLight = TrayIcon.IsTaskbarLight();
        manager.Icon = TrayIcon.CreateCombined([], size: trayIconSize);
        manager.Text = "";
        manager.Visible = true;
        DebugService.Log("startup", "tray-ready");
        manager.MouseClick += (_, e) => dispatcher.TryEnqueue(() =>
        {
            HideHover();
            if (e.Button == Forms.MouseButtons.Right) menuWindow.ShowMenu();
            else window.ToggleFlyout();
        });
        manager.MouseMove += (_, _) => dispatcher.TryEnqueue(ArmHover);
        if (!preview)
        {
            showEvent = new(false, EventResetMode.AutoReset, "Local\\BatteryDisplay.ShowWindow");
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => dispatcher.TryEnqueue(window.ShowFlyout), null, Timeout.Infinite, false);
        }
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(Preferences.RefreshSeconds);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        deviceChangeTimer = dispatcher.CreateTimer();
        deviceChangeTimer.Interval = TimeSpan.FromMilliseconds(1500);
        deviceChangeTimer.IsRepeating = false;
        deviceChangeTimer.Tick += (_, _) => Refresh();
        trayDpiTimer = dispatcher.CreateTimer();
        trayDpiTimer.Interval = TimeSpan.FromSeconds(3);
        trayDpiTimer.Tick += (_, _) =>
        {
            if (NativeWindow.TrayIconSize() != trayIconSize || TrayIcon.IsTaskbarLight() != taskbarLight) UpdateIcons();
        };
        trayDpiTimer.Start();
        updateTimer = dispatcher.CreateTimer();
        updateTimer.Interval = TimeSpan.FromSeconds(15);
        updateTimer.Tick += (_, _) =>
        {
            if (DateTimeOffset.Now - lastCleanup > TimeSpan.FromHours(1))
            {
                lastCleanup = DateTimeOffset.Now;
                _ = Task.Run(CacheMaintenance.Run);
            }
            if (!preview && Preferences.AutomaticUpdates && DateTimeOffset.Now - lastUpdateCheck > TimeSpan.FromHours(6))
                RunUpdate(false);
        };
        updateTimer.Start();
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        SystemEvents.SessionSwitch += SessionSwitched;
        if (preview)
        {
            Devices = [new("preview-mouse", "MCHOSE AX5 V2", true, 75, "迈从接收器", "2.4G", VendorDisplay: 80),
                new("preview-pad", "Xbox 无线手柄", true, 62, "Windows 手柄接口", "无线"),
                new("preview-headset", "蓝牙耳机", true, 38, "Windows 上报", "蓝牙"),
                new("preview-off", "RK61 键盘", false, 80, "Windows 上报")];
            Updated = DateTimeOffset.Now;
            var demo = Devices[0];
            journal.Observe([demo with { Battery = 100, VendorDisplay = 100, Charging = true }], Updated.Value.AddDays(-2));
            journal.Observe([demo with { Battery = 96, VendorDisplay = 100 }], Updated.Value.AddMinutes(-115));
            journal.Observe([demo with { Battery = 91, VendorDisplay = 90 }], Updated.Value.AddMinutes(-90));
            journal.Observe([demo with { Battery = 87, VendorDisplay = 90 }], Updated.Value.AddMinutes(-65));
            journal.Observe([demo with { Battery = 83, VendorDisplay = 80 }], Updated.Value.AddMinutes(-40));
            journal.Observe([demo with { Battery = 79, VendorDisplay = 80 }], Updated.Value.AddMinutes(-18));
            journal.Observe([demo], Updated.Value);
        }
        if (!preview) notifications.Register();
        if (show || !preview && notifications.WasActivated()) window.ShowFlyout();
        else if (!preview) dispatcher.TryEnqueue(Refresh);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume && !quitting && !sessionLocked) dispatcher.TryEnqueue(Refresh); }

    private void SessionSwitched(object sender, SessionSwitchEventArgs e)
    {
        if (quitting || preview) return;
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (quitting) return;
                sessionLocked = true;
                timer.Stop();
                deviceChangeTimer.Stop();
                DebugService.Log("power", "session-locked");
            });
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (quitting) return;
                sessionLocked = false;
                if (!timer.IsRunning) timer.Start();
                DebugService.Log("power", "session-unlocked");
                Refresh();
            });
        }
    }

    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (quitting || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        dispatcher.TryEnqueue(() =>
        {
            if (quitting) return;
            if (TrayIcon.IsTaskbarLight() != taskbarLight || NativeWindow.TrayIconSize() != trayIconSize) UpdateIcons();
        });
    }

    public void NotifyDeviceChanged()
    {
        if (quitting || preview || sessionLocked) return;
        dispatcher.TryEnqueue(() =>
        {
            if (quitting || preview || sessionLocked) return;
            deviceChangeTimer.Stop();
            deviceChangeTimer.Start();
        });
    }

    public List<Peripheral> PresentedDevices => Preferences.Present(Devices);
    public List<Peripheral> ManagedDevices => PresentedDevices.Where(d => d.Battery.HasValue || d.Gauge.HasValue || d.Charging).ToList();
    public List<Peripheral> TrayDevices => PresentedDevices.Where(d => d.CanShow && !Preferences.HiddenDevices.Contains(d.Id)).ToList();
    public List<BatteryJournalDevice> JournalDevices => journal.Read(DateTimeOffset.Now);
    private void ArmHover()
    {
        if (menuWindow.IsOpen || window.IsFlyoutVisible) return;
        if (!Updated.HasValue || (DateTimeOffset.Now - Updated.Value).TotalSeconds > 10) Refresh();
        if (manager.Text.Length > 0) return;
        var cursor = NativeWindow.CursorPosition();
        if (Math.Abs(cursor.X - hoverOrigin.X) > 4 || Math.Abs(cursor.Y - hoverOrigin.Y) > 4)
        { hoverOrigin = cursor; hoverDelay.Stop(); }
        if (!hoverWindow.IsVisible && !hoverDelay.IsRunning) hoverDelay.Start();
        if (!hoverTracking.IsRunning) hoverTracking.Start();
    }
    private void HideHover() { hoverDelay.Stop(); hoverTracking.Stop(); hoverWindow.HideValues(); }
    public void Refresh() => _ = RefreshAsync();
    public Task RefreshAsync()
    {
        if (quitting || preview) return Task.CompletedTask;
        if (activeRefresh is { IsCompleted: false } current) return current;
        var task = RunRefreshAsync();
        activeRefresh = task;
        return task;
    }

    private async Task RunRefreshAsync()
    {
        if (Refreshing || quitting || preview) return;
        Refreshing = true;
        window.UpdateView();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var report = await BatteryService.ScanAsync(timeout.Token);
            if (quitting) return;
            var now = DateTimeOffset.Now;
            journal.Observe(report.Devices, now);
            Devices = batteryHistory.Apply(report.Devices, now);
            batteryHistory.Save();
            Providers = report.Providers;
            bool added = false;
            foreach (var d in ManagedDevices)
                if (!Preferences.DeviceOrder.Contains(d.Id)) { Preferences.DeviceOrder.Add(d.Id); added = true; }
            if (added) Preferences.Save();
            Updated = now;
            Error = null;
        }
        catch (OperationCanceledException) { journal.BreakContinuity(); if (!quitting) Error = "读取超时，稍后可重试"; }
        catch (Exception ex) { journal.BreakContinuity(); if (!quitting) Error = ex.Message; }
        finally
        {
            Refreshing = false;
            if (!quitting)
            {
                UpdateIcons();
                window.UpdateView();
                if (Error is null) CheckLowBattery();
            }
        }
    }
    private void UpdateIcons()
    {
        var devices = TrayDevices;
        var old = manager.Icon;
        trayIconSize = NativeWindow.TrayIconSize();
        taskbarLight = TrayIcon.IsTaskbarLight();
        manager.Icon = Preferences.RingIcons ? TrayIcon.CreateCombined(devices, Error is not null, trayIconSize, Preferences.LowBatteryThreshold)
            : TrayIcon.Create(devices.Select(d => d.DisplayBattery).Min(), devices.Count > 0, Error is not null, false, trayIconSize, Preferences.LowBatteryThreshold);
        old?.Dispose();
        // Always use the Fluent WinUI hover card instead of the legacy system tooltip.
        manager.Text = "";
        manager.Visible = true;
        if (hoverWindow.IsVisible) hoverWindow.ShowValues(devices, Preferences);
    }
    private void CheckLowBattery() => lowBatteryAlerts.Observe(PresentedDevices, Preferences.LowBatteryAlerts, device =>
        !Preferences.HiddenDevices.Contains(device.Id) && notifications.Send("外设电量偏低", $"{device.DisplayName}：{device.Level}", DebugService.Key(device.Id)).Id != 0,
        Preferences.LowBatteryThreshold);

    public void SetLowBatteryAlerts(bool enabled)
    {
        Preferences.LowBatteryAlerts = enabled;
        Save();
        if (!enabled || preview) return;
        if (Updated.HasValue && Error is null) CheckLowBattery();
        else Refresh();
    }

    public void SetLowBatteryThreshold(int threshold)
    {
        Preferences.LowBatteryThreshold = threshold;
        Save();
        if (!Preferences.LowBatteryAlerts || preview) return;
        if (Updated.HasValue && Error is null) CheckLowBattery();
        else Refresh();
    }

    public bool SetDeviceAlias(string id, string? alias)
    {
        string? previous = Preferences.DeviceAliases.GetValueOrDefault(id);
        if (alias is null) Preferences.DeviceAliases.Remove(id);
        else Preferences.DeviceAliases[id] = alias;
        try { Preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (previous is null) Preferences.DeviceAliases.Remove(id);
            else Preferences.DeviceAliases[id] = previous;
            return false;
        }
        UpdateIcons();
        window.UpdateView();
        return true;
    }

    public List<BatterySample> MergedSamplesFor(string canonicalId) =>
        journal.ReadMergedSamples(Preferences.LinkedIdsFor(canonicalId), DateTimeOffset.Now);

    public List<(string Id, string Label)> LinkableDevicesFor(string canonicalId)
    {
        string root = Preferences.ResolveCanonicalId(canonicalId);
        var linked = Preferences.LinkedIdsFor(root).ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Devices)
        {
            if (!Peripheral.SupportsAlias(d.Id) || linked.Contains(d.Id) || Preferences.ResolveCanonicalId(d.Id) == root) continue;
            candidates[d.Id] = $"{d.Name}（{d.Transport}）";
        }
        foreach (var j in JournalDevices)
        {
            if (!Peripheral.SupportsAlias(j.Id) || linked.Contains(j.Id) || Preferences.ResolveCanonicalId(j.Id) == root) continue;
            candidates.TryAdd(j.Id, $"{j.Name}（{j.Transport}）");
        }
        return candidates.Select(kv => (kv.Key, kv.Value)).OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string DescribeDeviceId(string id)
    {
        var live = Devices.FirstOrDefault(d => d.Id == id);
        if (live is not null) return $"{live.Name}（{live.Transport}）";
        var history = JournalDevices.FirstOrDefault(d => d.Id == id);
        if (history is not null) return $"{history.Name}（{history.Transport}）";
        return id;
    }

    public bool LinkDevice(string secondaryId, string primaryId)
    {
        string root = Preferences.ResolveCanonicalId(primaryId);
        if (secondaryId == root || !Peripheral.SupportsAlias(secondaryId) || !Peripheral.SupportsAlias(root)) return false;
        var previous = new Dictionary<string, string>(Preferences.DeviceLinks, StringComparer.Ordinal);
        Preferences.DeviceLinks[secondaryId] = root;
        Preferences.DeviceLinks = Preferences.NormalizeLinks(Preferences.DeviceLinks);
        try { Preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Preferences.DeviceLinks = previous;
            return false;
        }
        UpdateIcons();
        window.UpdateView();
        return true;
    }

    public bool UnlinkDevice(string secondaryId)
    {
        if (!Preferences.DeviceLinks.ContainsKey(secondaryId)) return false;
        var previous = new Dictionary<string, string>(Preferences.DeviceLinks, StringComparer.Ordinal);
        Preferences.DeviceLinks.Remove(secondaryId);
        try { Preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Preferences.DeviceLinks = previous;
            return false;
        }
        UpdateIcons();
        window.UpdateView();
        return true;
    }

    public string TestBatteryNotification()
    {
        if (preview) return "预览模式不发送系统通知。";
        return notifications.Send("环电 · 通知测试", "这是一条测试通知，不代表任何设备的实际电量。", "test").Error
            ?? "Windows 已接收测试通知。若没看到横幅，请查看通知中心。";
    }
    public void ToggleDevice(string id, bool visible)
    {
        if (visible) Preferences.HiddenDevices.Remove(id); else Preferences.HiddenDevices.Add(id);
        Save();
    }
    public void SetOrder(IEnumerable<string> ids)
    {
        Preferences.DeviceOrder = ids.Concat(Preferences.DeviceOrder).Distinct(StringComparer.Ordinal).ToList();
        Save();
    }
    public void MoveDevice(string id, int direction)
    {
        var ids = ManagedDevices.Select(d => d.Id).ToList();
        int index = ids.IndexOf(id);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        SetOrder(ids);
    }
    public void ShowSettings() { HideHover(); window.ShowSettings(); window.ShowFlyout(); }
    public void ShowSupport() { HideHover(); window.ShowSupport(); window.ShowFlyout(); }
    public void ShowDeviceManagement() { HideHover(); window.ShowDeviceManagement(); window.ShowFlyout(); }
    public void CheckForUpdates()
    {
        HideHover();
        window.ShowUpdates(); window.ShowFlyout();
        RunUpdate(true);
    }
    private void RunUpdate(bool manual) => _ = RunUpdateAsync(manual);
    private async Task RunUpdateAsync(bool manual)
    {
        if (CheckingUpdates || InstallingUpdate) return;
        if (!manual)
        {
            if (AutoUpdate.RecentFailure()) { lastUpdateCheck = DateTimeOffset.Now; return; }
        }
        lastUpdateCheck = DateTimeOffset.Now;
        CheckingUpdates = true;
        Update = null;
        UpdateStatus = "正在检查新版本…";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateCts = cts;
        window.UpdateView();
        bool autoInstall = false;
        try
        {
            Update = await UpdateService.CheckAsync(cts.Token);
            UpdateStatus = Update.Message;
            DebugService.Log("update", "checked", new { Update.Success, Update.Available, Update.LatestVersion });
            if (Update.Success && Update.Available && !preview)
            {
                if (Preferences.AutomaticUpdates) autoInstall = true;
                else UpdateStatus = $"发现新版本 v{Update.LatestVersion}，可点击下方按钮立即更新。";
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            UpdateStatus = "已取消检查更新。";
            Update = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新未完成：" + ex.Message + "。可稍后重试。";
            DebugService.Log("update", "failed", new { Type = ex.GetType().Name, ex.HResult });
            Update = new(false, false, UpdateStatus, ReleasePage: new(UpdateService.Repository + "/releases"));
        }
        finally
        {
            if (ReferenceEquals(updateCts, cts)) updateCts = null;
            CheckingUpdates = false;
            if (!quitting) window.UpdateView();
        }
        if (autoInstall && Update is { Success: true, Available: true } release && !quitting)
            await InstallUpdateAsync(release);
    }

    public void InstallAvailableUpdate()
    {
        if (Update is { Success: true, Available: true } release && !preview)
            _ = InstallUpdateAsync(release);
    }

    public void CancelUpdate()
    {
        try { updateCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task InstallUpdateAsync(UpdateResult release)
    {
        if (CheckingUpdates || InstallingUpdate || quitting || preview) return;
        InstallingUpdate = true;
        UpdateStatus = "正在准备下载更新…";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateCts = cts;
        window.UpdateView();
        try
        {
            var progress = new Progress<string>(message => { if (!quitting) { UpdateStatus = message; window.UpdateView(); } });
            var plan = await Task.Run(() => AutoUpdate.PrepareAsync(release, progress, cts.Token), cts.Token);
            if (quitting) return;
            UpdateStatus = "正在安装更新，将自动重新启动…";
            window.UpdateView();
            AutoUpdate.Launch(plan);
            Quit();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            UpdateStatus = "已取消更新下载。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatus = "自动更新未完成：" + ex.Message + "。可稍后重试。";
            DebugService.Log("update", "failed", new { Type = ex.GetType().Name, ex.HResult });
            Update = new(false, false, UpdateStatus, ReleasePage: new(UpdateService.Repository + "/releases"));
        }
        finally
        {
            if (ReferenceEquals(updateCts, cts)) updateCts = null;
            InstallingUpdate = false;
            if (!quitting) window.UpdateView();
        }
    }
    public void ExportDiagnostics() => _ = ExportDiagnosticsAsync();
    public async Task ExportDiagnosticsAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var report = await Task.Run(() => BatteryService.ScanAsync(timeout.Token));
            // Explicit export collects interface metadata even if continuous logging is off.
            DebugService.Observe(await Task.Run(() => HidBattery.Enumerate(timeout.Token)), force: true);
            string zip = await Task.Run(() => DebugService.Export(report));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + zip + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { if (!quitting) { Error = "诊断导出失败：" + ex.Message; window.UpdateView(); } }
    }
    public void ClearDiagnostics()
    {
        try
        {
            DebugService.Cleanup(true);
            manager.ShowBalloonTip(3000, AppBrand.Name, "已清理可回收的本地日志和诊断包。Debug 开启时会继续生成新日志。", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { Error = "清理失败：" + ex.Message; window.UpdateView(); }
    }
    public void Save()
    {
        try { Preferences.Save(); Error = null; }
        catch (Exception ex) { Error = "设置保存失败：" + ex.Message; }
        timer.Interval = TimeSpan.FromSeconds(Preferences.RefreshSeconds);
        DebugService.Enabled = Preferences.DebugMode;
        UpdateIcons();
        window.ApplySettings();
    }
    public static bool StartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("BatteryDisplay") is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
    public void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("BatteryDisplay", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("BatteryDisplay", false);
        }
        catch (Exception ex) { Error = "开机启动设置失败：" + ex.Message; }
    }
    public void Quit() { Dispose(); Microsoft.UI.Xaml.Application.Current.Exit(); }
    public async Task CaptureAsync()
    {
        window.KeepOpenForCapture();
        if (!preview && (!Updated.HasValue || Refreshing)) await RefreshAsync();
        await window.CaptureViewsAsync(preview);
    }
    public Task RunInteractionChecksAsync() => window.RunInteractionChecksAsync();
    public void Dispose()
    {
        if (quitting) return;
        quitting = true;
        lifetime.Cancel();
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        SystemEvents.SessionSwitch -= SessionSwitched;
        timer.Stop();
        deviceChangeTimer.Stop();
        trayDpiTimer.Stop();
        updateTimer.Stop();
        hoverDelay.Stop(); hoverTracking.Stop();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        notifications.Dispose();
        var managerImage = manager.Icon; manager.Dispose(); managerImage?.Dispose();
        window.AllowClose = true;
        window.Close();
        menuWindow.HideMenu(); menuWindow.Close();
        hoverWindow.Close();
        lifetime.Dispose();
    }
}
