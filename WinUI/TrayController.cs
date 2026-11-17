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
    public UpdateResult? Update { get; private set; }
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueueTimer trayDpiTimer;
    private readonly DispatcherQueueTimer updateTimer;
    public string UpdateStatus { get; private set; } = "";
    private DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset lastCleanup = DateTimeOffset.MinValue;
    private int trayIconSize;
    private readonly Forms.NotifyIcon manager = new();
    private readonly HashSet<string> alerted = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly BatteryHistory batteryHistory = BatteryHistory.Load();
    private readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, "Local\\BatteryDisplay.ShowWindow");
    private readonly RegisteredWaitHandle showWait;
    private readonly FlyoutWindow window;
    private readonly TrayMenuWindow menuWindow;
    private readonly HoverWindow hoverWindow;
    private readonly DispatcherQueueTimer hoverDelay;
    private readonly DispatcherQueueTimer hoverTracking;
    private (int X, int Y) hoverOrigin;
    private bool quitting;
    private readonly bool preview;

    public TrayController(bool show, bool preview)
    {
        this.preview = preview;
        DebugService.Enabled = Preferences.DebugMode;
        window = new FlyoutWindow(this);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Device flyout ready\n");
        menuWindow = new TrayMenuWindow(this);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Tray menu ready\n");
        hoverWindow = new HoverWindow();
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Hover window ready\n");
        hoverDelay = dispatcher.CreateTimer();
        hoverDelay.Interval = TimeSpan.FromMilliseconds(650);
        hoverDelay.IsRepeating = false;
        hoverDelay.Tick += (_, _) =>
        {
            if (manager.Text.Length == 0 && !menuWindow.IsOpen && !window.IsFlyoutVisible) hoverWindow.ShowValues(TrayDevices, Preferences);
        };
        hoverTracking = dispatcher.CreateTimer();
        hoverTracking.Interval = TimeSpan.FromMilliseconds(100);
        hoverTracking.Tick += (_, _) =>
        {
            var cursor = NativeWindow.CursorPosition();
            if (Math.Abs(cursor.X - hoverOrigin.X) > 22 || Math.Abs(cursor.Y - hoverOrigin.Y) > 22 || menuWindow.IsOpen || window.IsFlyoutVisible) HideHover();
        };
        trayIconSize = NativeWindow.TrayIconSize();
        manager.Icon = TrayIcon.CreateCombined([], size: trayIconSize);
        manager.Text = AppBrand.Name + " · 正在读取";
        manager.Visible = true;
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Tray icon ready\n");
        manager.MouseClick += (_, e) => dispatcher.TryEnqueue(() =>
        {
            HideHover();
            if (e.Button == Forms.MouseButtons.Right) menuWindow.ShowMenu();
            else window.ToggleFlyout();
        });
        manager.MouseMove += (_, _) => dispatcher.TryEnqueue(ArmHover);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => dispatcher.TryEnqueue(window.ShowFlyout), null, Timeout.Infinite, false);
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(Preferences.RefreshSeconds);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        trayDpiTimer = dispatcher.CreateTimer();
        trayDpiTimer.Interval = TimeSpan.FromSeconds(3);
        trayDpiTimer.Tick += (_, _) => { if (NativeWindow.TrayIconSize() != trayIconSize) UpdateIcons(); };
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
        if (preview)
        {
            Devices = [new("preview-mouse", "MCHOSE AX5 V2", true, 75, "迈从接收器", "2.4G", VendorDisplay: 80),
                new("preview-pad", "Xbox 无线手柄", true, 62, "Windows 手柄接口", "无线"),
                new("preview-headset", "蓝牙耳机", true, 38, "Windows 上报", "蓝牙"),
                new("preview-off", "RK61 键盘", false, 80, "Windows 上报")];
            Updated = DateTimeOffset.Now;
        }
        if (show) window.ShowFlyout();
        else if (!preview) dispatcher.TryEnqueue(Refresh);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume && !quitting) dispatcher.TryEnqueue(Refresh); }

    public List<Peripheral> PresentedDevices => Preferences.Present(Devices);
    public List<Peripheral> ManagedDevices => PresentedDevices.Where(d => d.Battery.HasValue || d.Gauge.HasValue || d.Charging).ToList();
    public List<Peripheral> TrayDevices => PresentedDevices.Where(d => d.CanShow && !Preferences.HiddenDevices.Contains(d.Id)).ToList();
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
    public async void Refresh()
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
            Devices = batteryHistory.Apply(report.Devices, DateTimeOffset.Now);
            batteryHistory.Save();
            Providers = report.Providers;
            bool added = false;
            foreach (var d in ManagedDevices)
                if (!Preferences.DeviceOrder.Contains(d.Id)) { Preferences.DeviceOrder.Add(d.Id); added = true; }
            if (added) Preferences.Save();
            Updated = DateTimeOffset.Now;
            Error = null;
        }
        catch (OperationCanceledException) { if (!quitting) Error = "读取超时，稍后可重试"; }
        catch (Exception ex) { if (!quitting) Error = ex.Message; }
        finally
        {
            Refreshing = false;
            if (!quitting) { UpdateIcons(); window.UpdateView(); }
        }
    }
    private void UpdateIcons()
    {
        var devices = TrayDevices;
        var old = manager.Icon;
        trayIconSize = NativeWindow.TrayIconSize();
        manager.Icon = Preferences.RingIcons ? TrayIcon.CreateCombined(devices, Error is not null, trayIconSize)
            : TrayIcon.Create(devices.Select(d => d.DisplayBattery).Where(v => v.HasValue).Min(), devices.Count > 0, Error is not null, false, trayIconSize);
        old?.Dispose();
        // Use the compact system tooltip whenever all lines fit; larger lists use the same compact layout in WinUI.
        string tooltip = string.Join("\n", devices.Select(d => $"{d.Name.Replace("迈从 · ", "")}: {d.TooltipLevel}"));
        if (devices.Count == 0) tooltip = AppBrand.Name + "：暂无可显示电量的在线设备";
        manager.Text = tooltip.Length <= 127 ? tooltip : "";
        manager.Visible = true;
        if (hoverWindow.IsVisible) hoverWindow.ShowValues(devices, Preferences);
        foreach (var d in devices)
        {
            if (Error is null && Preferences.LowBatteryAlerts && !d.IsCached && !d.Charging && d.GaugeValue <= 20
                && alerted.Add(d.Id))
                manager.ShowBalloonTip(5000, AppBrand.Name + " · 电量偏低", $"{d.Name}：{d.Level}", Forms.ToolTipIcon.Warning);
            if (!d.IsCached && d.GaugeValue > 25) alerted.Remove(d.Id);
        }
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
    private async void RunUpdate(bool manual)
    {
        if (CheckingUpdates) return;
        if (!manual)
        {
            if (AutoUpdate.RecentFailure()) { lastUpdateCheck = DateTimeOffset.Now; return; }
        }
        lastUpdateCheck = DateTimeOffset.Now;
        CheckingUpdates = true;
        Update = null;
        UpdateStatus = "正在检查新版本…";
        window.UpdateView();
        try
        {
            Update = await UpdateService.CheckAsync(lifetime.Token);
            UpdateStatus = Update.Message;
            DebugService.Log("update", "checked", new { Update.Success, Update.Available, Update.LatestVersion });
            if (Update.Success && Update.Available && (manual || Preferences.AutomaticUpdates) && !preview)
            {
                var progress = new Progress<string>(message => { if (!quitting) { UpdateStatus = message; window.UpdateView(); } });
                var plan = await Task.Run(() => AutoUpdate.PrepareAsync(Update, progress, lifetime.Token));
                if (quitting) return;
                UpdateStatus = "正在安装更新，将自动重新启动…";
                window.UpdateView();
                AutoUpdate.Launch(plan);
                Quit();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatus = "自动更新未完成：" + ex.Message + "。可稍后重试。";
            DebugService.Log("update", "failed", new { Type = ex.GetType().Name, ex.HResult });
            Update = new(false, false, UpdateStatus, ReleasePage: new(UpdateService.Repository + "/releases"));
        }
        finally { CheckingUpdates = false; if (!quitting) window.UpdateView(); }
    }
    public async void ExportDiagnostics()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var report = await Task.Run(() => BatteryService.ScanAsync(timeout.Token));
            // Explicit export collects interface metadata even if continuous logging is off.
            bool logging = DebugService.Enabled;
            try { DebugService.Enabled = true; DebugService.Observe(await Task.Run(() => HidBattery.Enumerate(timeout.Token))); }
            finally { DebugService.Enabled = logging; }
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
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("BatteryDisplay") is string;
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
        for (int i = 0; i < 180 && (!Updated.HasValue || Refreshing); i++) await Task.Delay(250);
        await window.CaptureViewsAsync(preview);
    }
    public Task RunInteractionChecksAsync() => window.RunInteractionChecksAsync();
    public void Dispose()
    {
        if (quitting) return;
        quitting = true;
        lifetime.Cancel();
        SystemEvents.PowerModeChanged -= PowerChanged;
        timer.Stop();
        trayDpiTimer.Stop();
        updateTimer.Stop();
        hoverDelay.Stop(); hoverTracking.Stop();
        showWait.Unregister(null);
        showEvent.Dispose();
        var managerImage = manager.Icon; manager.Dispose(); managerImage?.Dispose();
        window.AllowClose = true;
        window.Close();
        menuWindow.HideMenu(); menuWindow.Close();
        hoverWindow.Close();
        lifetime.Dispose();
    }
}
