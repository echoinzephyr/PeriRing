using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow : Window
{
    private readonly TrayController controller;
    private readonly Grid root = new();
    private readonly StackPanel header = new() { Spacing = 12 };
    private readonly StackPanel body = new() { Spacing = 8 };
    private readonly SolidColorBrush cardBackground = new();
    private readonly SolidColorBrush cardBorder = new();
    private readonly SolidColorBrush separator = new();
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly Button refresh;
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly IntPtr hwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer deactivateTimer;
    private string page = "devices";
    private readonly Stack<string> navigation = new();
    private readonly Dictionary<string, double> pageOffsets = new();
    private int layoutRevision;
    private bool pinned;
    private bool visible;
    private bool updating;
    private bool disconnectedExpanded;
    private string renderedPage = "";
    private string renderedDeviceState = "";
    private object? renderedProviders;
    private string? renderedError;
    private string renderedManagement = "";
    private UpdateResult? renderedUpdate;
    private bool renderedCheckingUpdates;
    private string renderedUpdateStatus = "";
    private bool renderedRefreshing;
    private Storyboard? windowMotion;
    private Storyboard? pageMotion;
    private int motionRevision;
    private bool positioning;
    private Windows.Graphics.SizeInt32 plannedSize;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer resizeSaveTimer;
    public bool AllowClose { get; set; }
    public bool IsFlyoutVisible => visible;
    public void KeepOpenForCapture() => pinned = true;
    private bool Dark => root.ActualTheme == ElementTheme.Dark;
    private Color CardColor => Dark ? Color.FromArgb(185, 47, 47, 47) : Color.FromArgb(205, 255, 255, 255);
    private Brush Accent => new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));

    public FlyoutWindow(TrayController controller)
    {
        this.controller = controller;
        Title = AppBrand.DisplayName;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        root.Padding = new Thickness(16);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowSpacing = 14;
        root.Children.Add(header);
        scroll.Content = body;
        // WinUI's expanding overlay scrollbar needs its own gutter.
        scroll.Padding = new Thickness(0, 0, 12, 0);
        scroll.Margin = new Thickness(0, 0, -8, 0);
        scroll.HorizontalScrollMode = ScrollMode.Disabled;
        scroll.IsVerticalScrollChainingEnabled = false;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        refresh = IconButton("\uE72C", "刷新电量", controller.Refresh);
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(refresh);
        var exit = IconButton("\uE7E8", "退出", controller.Quit);
        Grid.SetColumn(exit, 1);
        footer.Children.Add(exit);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = new Border { CornerRadius = new CornerRadius(10), Child = root };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        string icon = Path.Combine(AppContext.BaseDirectory, AppBrand.IconFileName);
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        NativeWindow.Configure(hwnd);
        NativeWindow.SetMinimumSize(hwnd, 340, 240);
        AppWindow.Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; HideFlyout(); } };
        deactivateTimer = DispatcherQueue.CreateTimer();
        deactivateTimer.Interval = TimeSpan.FromMilliseconds(140);
        deactivateTimer.IsRepeating = false;
        deactivateTimer.Tick += (_, _) => { if (!pinned && !NativeWindow.OwnsForeground()) HideFlyout(); };
        resizeSaveTimer = DispatcherQueue.CreateTimer();
        resizeSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        resizeSaveTimer.IsRepeating = false;
        resizeSaveTimer.Tick += (_, _) =>
        {
            try { controller.Preferences.Save(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange || positioning || !visible || (AppWindow.Size.Width == plannedSize.Width && AppWindow.Size.Height == plannedSize.Height)) return;
            double scale = NativeWindow.Scale(hwnd);
            controller.Preferences.PopupWidth = AppWindow.Size.Width / scale;
            controller.Preferences.PopupHeight = AppWindow.Size.Height / scale;
            resizeSaveTimer.Stop(); resizeSaveTimer.Start();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !pinned) deactivateTimer.Start();
        };
        root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { HideFlyout(); e.Handled = true; } };
        root.ActualThemeChanged += (_, _) => { if (!updating) UpdateView(); };
        ApplyTheme();
        UpdateView();
    }

    private void ApplyTheme()
    {
        root.RequestedTheme = controller.Preferences.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        root.Background = new SolidColorBrush(Dark ? Color.FromArgb(195, 26, 26, 26) : Color.FromArgb(195, 247, 247, 247));
        cardBackground.Color = CardColor;
        cardBorder.Color = Dark ? Color.FromArgb(35, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
        separator.Color = Dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
        NativeWindow.SetDark(hwnd, Dark);
    }
    public void ToggleFlyout() { if (visible) HideFlyout(); else ShowFlyout(); }
    public void ShowFlyout()
    {
        bool opening = !visible;
        if (opening)
        {
            motionRevision++;
            windowMotion?.Stop();
            pageMotion?.Stop();
            pageMotion = null;
        }
        UpdateView(opening);
        if (opening)
        {
            double height = page == "devices" ? Math.Clamp(210 + controller.Devices.Count(d => d.IsPresent) * 80 + (controller.Devices.All(d => !d.IsPresent) ? 130 : 0), 280, 600)
                : page == "manage" ? Math.Clamp(170 + controller.ManagedDevices.Count * 80, 300, 620) : 620;
            PositionFlyout(controller.Preferences.PopupWidth, controller.Preferences.PopupHeight > 0 ? controller.Preferences.PopupHeight : height);
        }
        AppWindow.Show();
        Activate();
        NativeWindow.Show(hwnd);
        visible = true;
        if (opening) windowMotion = Fade(true, 210, null, root);
        if (opening) controller.Refresh();
    }
    private void HideFlyout()
    {
        if (!visible) return;
        visible = false;
        deactivateTimer.Stop();
        windowMotion?.Stop();
        int revision = ++motionRevision;
        windowMotion = Fade(false, 150, () =>
        {
            if (revision != motionRevision || visible || AllowClose) return;
            AppWindow.Hide();
            windowMotion?.Stop();
        }, root);
    }
    private void Navigate(string destination)
    {
        if (destination != page) { navigation.Push(page); page = destination; }
        UpdateView();
    }
    private void GoBack()
    {
        page = navigation.TryPop(out var previous) ? previous : "devices";
        UpdateView();
    }
    public void ShowSettings() => Navigate("settings");
    private void ShowBatteryAlerts() => Navigate("battery-alerts");
    public void ShowDevices() { navigation.Clear(); page = "devices"; UpdateView(); }
    public void ShowSupport() => Navigate("support");
    public void ShowDeviceManagement() => Navigate("manage");
    public void ShowUpdates() => Navigate("updates");

    private void PositionFlyout(double width, double height)
    {
        positioning = true;
        try
        {
            NativeWindow.Position(AppWindow, hwnd, width, height);
            plannedSize = AppWindow.Size;
        }
        finally { positioning = false; }
    }

    internal async Task CaptureViewsAsync(bool preview = false)
    {
        pinned = true;
        ShowFlyout();
        foreach (string view in new[] { "devices", "manage", "settings", "diagnostics", "support", "updates" })
        {
            page = view == "diagnostics" ? "settings" : view;
            if (preview)
            {
                controller.Preferences.PopupHeight = view switch { "devices" => 530, "settings" => 760, "diagnostics" => 560, "manage" => 430, "support" => 620, _ => 520 };
                PositionFlyout(controller.Preferences.PopupWidth, controller.Preferences.PopupHeight);
            }
            UpdateView();
            await Task.Delay(300);
            if (view == "diagnostics") { scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Task.Delay(150); }
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            using var outputReader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await outputReader.LoadAsync((uint)stream.Size);
            var png = new byte[(int)stream.Size];
            outputReader.ReadBytes(png);
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"winui-{view}.png"), png);
        }
    }

    public void ApplySettings() => UpdateView();

    private Storyboard? Fade(bool entering, int milliseconds, Action? completed, params FrameworkElement[] surfaces)
    {
        if (!uiSettings.AnimationsEnabled) { completed?.Invoke(); return null; }
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var animation = new Storyboard();
        var easing = new QuadraticEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn };
        foreach (var surface in surfaces)
        {
            var fade = new DoubleAnimation { From = entering ? 0 : 1, To = entering ? 1 : 0, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(fade, surface);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            animation.Children.Add(fade);
        }
        if (completed is not null) animation.Completed += (_, _) => completed();
        animation.Begin();
        return animation;
    }

    public void UpdateView(bool force = false)
    {
        if (updating) return;
        updating = true;
        bool changingPage = renderedPage != page;
        if (changingPage && renderedPage.Length > 0) pageOffsets[renderedPage] = scroll.VerticalOffset;
        double offset = changingPage ? pageOffsets.GetValueOrDefault(page) : scroll.VerticalOffset;
        try
        {
            ApplyTheme();
            refresh.IsEnabled = !controller.Refreshing;
            string refreshHint = controller.Refreshing ? "正在刷新电量…" : "刷新电量";
            ToolTipService.SetToolTip(refresh, refreshHint);
            AutomationProperties.SetName(refresh, refreshHint);
            bool pageChanged = renderedPage != page;
            if (!pageChanged && IsDeviceDetail)
            {
                UpdateDeviceDetail();
                return;
            }
            string deviceState = page == "devices" ? controller.Preferences.LowBatteryThreshold + "\n" + string.Join("\n", controller.PresentedDevices.Select(d => $"{d.Id}|{d.DisplayName}|{d.Transport}|{d.Connected}|{d.Level}|{d.GaugeValue}|{d.Charging}|{d.IsCached}|{d.Note}")) : "";
            string management = page == "manage" ? string.Join("\n", controller.ManagedDevices.Select(d => $"{d.Id}|{d.DisplayName}|{d.Level}|{d.IsCached}|{d.Connected}")
                .Concat(HistoricalDevices().Select(d => $"{d.Id}|{JournalDisplayName(d)}"))) : "";
            bool changed = page switch
            {
                "settings" or "battery-alerts" => false,
                "updates" => renderedUpdate != controller.Update || renderedCheckingUpdates != controller.CheckingUpdates || renderedUpdateStatus != controller.UpdateStatus,
                "support" => !ReferenceEquals(renderedProviders, controller.Providers),
                "manage" => renderedManagement != management,
                _ => renderedDeviceState != deviceState || renderedError != controller.Error
                    || controller.Devices.Count == 0 && renderedRefreshing != controller.Refreshing
            };
            if (!force && !pageChanged && !changed) return;
            if (pageChanged) pageMotion?.Stop();
            if (pageChanged || force) BuildHeader();
            body.Children.Clear();
            if (page == "settings") BuildSettings();
            else if (page == "battery-alerts") BuildBatteryAlerts();
            else if (page == "manage") BuildManagement();
            else if (page == "support") BuildSupport();
            else if (page == "updates") BuildUpdates();
            else if (IsDeviceDetail) BuildDeviceDetail();
            else BuildDevices();
            renderedPage = page;
            renderedDeviceState = deviceState;
            renderedProviders = controller.Providers;
            renderedError = controller.Error;
            renderedManagement = management;
            renderedUpdate = controller.Update;
            renderedCheckingUpdates = controller.CheckingUpdates;
            renderedUpdateStatus = controller.UpdateStatus;
            renderedRefreshing = controller.Refreshing;
            // The header and viewport survive content refreshes while a page transition is running.
            if (pageChanged && visible) pageMotion = Fade(true, 200, null, header, scroll);
            int revision = ++layoutRevision;
            string destination = page;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (revision != layoutRevision || page != destination) return;
                scroll.UpdateLayout();
                scroll.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true);
            });
        }
        finally { updating = false; }
    }

    private void BuildHeader()
    {
        header.Children.Clear();
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement leading = page == "devices" ? new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "periring-64.png"))), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center }
            : IconButton("\uE72B", "返回上一页", GoBack);
        row.Children.Add(leading);
        title.Text = page switch
        {
            "settings" => "设置", "support" => "支持设备", "manage" => "设备开关与排序", "updates" => "检查更新",
            "battery-alerts" => "低电量提醒",
            _ => IsDeviceDetail ? "设备详情" : AppBrand.Name
        };
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var pin = new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 15 }, IsChecked = pinned, Width = 34, Height = 34, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(pin, "保持浮层展开");
        AutomationProperties.SetName(pin, "保持浮层展开");
        pin.Click += (_, _) => pinned = pin.IsChecked == true;
        Grid.SetColumn(pin, 2); row.Children.Add(pin);
        if (page != "settings")
        {
            var settings = IconButton("\uE713", "设置", ShowSettings);
            Grid.SetColumn(settings, 3); row.Children.Add(settings);
        }
        header.Children.Add(row);
    }

    private void BuildDevices()
    {
        var devices = controller.PresentedDevices;
        if (controller.Error is not null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = controller.Error });
        var connected = devices.Where(d => d.IsPresent).ToList();
        int cached = connected.Count(d => d.IsCached);
        body.Children.Add(SectionLabel(cached == 0 ? $"已连接 · {connected.Count}" : $"已连接 · {connected.Count - cached}   上次读数 · {cached}"));
        foreach (var d in connected) body.Children.Add(DeviceCard(d));
        if (connected.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12, Padding = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "\uE702", FontSize = 36, Opacity = .5 });
            empty.Children.Add(new TextBlock { Text = controller.Refreshing ? "正在寻找你的外设" : "连接外设后，电量会显示在这里", FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "支持蓝牙及已适配的无线接收器", FontSize = 12, Opacity = .6, TextAlignment = TextAlignment.Center });
            body.Children.Add(Card(empty));
        }
        var other = devices.Where(d => !d.IsPresent).ToList();
        if (other.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var d in other) list.Children.Add(DeviceCard(d));
            var expander = new Expander { Header = $"未连接或暂未取得电量 · {other.Count}", Content = list, IsExpanded = disconnectedExpanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
            expander.Expanding += (_, _) => disconnectedExpanded = true;
            expander.Collapsed += (_, _) => disconnectedExpanded = false;
            body.Children.Add(expander);
        }
        var settings = new HyperlinkButton { Content = "蓝牙设置", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8, 0, 0) };
        settings.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
        body.Children.Add(settings);
    }

    private Border DeviceCard(Peripheral d)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ring = new Grid { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new ProgressRing { IsIndeterminate = false, Value = d.GaugeValue ?? 0, Minimum = 0, Maximum = 100, Width = 42, Height = 42,
            Foreground = d.IsPresent && controller.Preferences.IsLow(d.GaugeValue) ? new SolidColorBrush(Color.FromArgb(255, 231, 133, 75)) : Accent, Opacity = d.IsPresent ? 1 : .45 });
        ring.Children.Add(new FontIcon { Glyph = DeviceGlyph(d), FontSize = 16, Opacity = d.IsPresent ? .9 : .45 });
        row.Children.Add(ring);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(DeviceDetailLink(d.Id, d.DisplayName, 14));
        string detail = d.IsCached ? d.Transport + " · 上次读数" : !d.Connected ? d.State : d.Transport + (d.Charging ? " · 充电中" : !d.CanShow ? " · 暂未取得电量" : "");
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var level = new TextBlock { Text = d.Level, FontSize = d.Level.Length > 6 ? 13 : 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = d.IsPresent ? 1 : .5 };
        string levelDetails = d.VendorDisplay.HasValue ? $"接收器原始读数 {d.Battery}%\n官方驱动显示 {d.VendorDisplay}%（分档）" : d.Source;
        ToolTipService.SetToolTip(level, d.IsCached ? d.State + "\n" + levelDetails : levelDetails);
        Grid.SetColumn(level, 2); row.Children.Add(level);
        var toggle = new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 13 }, IsChecked = !controller.Preferences.HiddenDevices.Contains(d.Id), Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(toggle, "在托盘显示这个设备");
        AutomationProperties.SetName(toggle, $"在托盘显示 {d.DisplayName}");
        toggle.Click += (_, _) => controller.ToggleDevice(d.Id, toggle.IsChecked == true);
        Grid.SetColumn(toggle, 3); row.Children.Add(toggle);
        var card = Card(row);
        ToolTipService.SetToolTip(card, $"{d.DisplayName}\n{d.Source}\n{d.State}");
        return card;
    }

    private void BuildSettings()
    {
        body.Children.Add(SectionLabel("设备与显示"));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE8CB", "设备开关与排序", "选择显示的设备和优先级", ShowDeviceManagement),
            ToggleSetting("\uEA3A", "分段环形图标", null, controller.Preferences.RingIcons,
                value => { controller.Preferences.RingIcons = value; controller.Save(); }, "关闭后显示最低电量数字"),
            ToggleSetting("\uE9F9", "与官方驱动一致", "采用已核实的厂商电量分档", controller.Preferences.PreferVendorDisplay,
                value => { controller.Preferences.PreferVendorDisplay = value; controller.Save(); })));

        var theme = new ComboBox { Width = 108, MinWidth = 0, FontSize = 12, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = controller.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        AutomationProperties.SetName(theme, "外观");
        theme.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            controller.Preferences.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            controller.Save();
        };
        var interval = new ComboBox { Width = 108, MinWidth = 0, FontSize = 12, ItemsSource = new[] { "15 秒", "30 秒", "1 分钟", "5 分钟" }, SelectedIndex = Array.IndexOf(new[] { 15, 30, 60, 300 }, controller.Preferences.RefreshSeconds) };
        AutomationProperties.SetName(interval, "后台刷新间隔");
        if (interval.SelectedIndex < 0) interval.SelectedIndex = 2;
        interval.SelectionChanged += (_, _) => { if (!updating && interval.SelectedIndex >= 0) { controller.Preferences.RefreshSeconds = new[] { 15, 30, 60, 300 }[interval.SelectedIndex]; controller.Save(); } };
        body.Children.Add(SectionLabel("常规"));
        string alertState = controller.Preferences.LowBatteryAlerts ? "已开启" : "已关闭";
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE7E7", "低电量提醒", $"{alertState} · 阈值 {controller.Preferences.LowBatteryThreshold}%", ShowBatteryAlerts),
            ToggleSetting("\uE7E8", "开机启动", null, TrayController.StartupEnabled(), controller.SetStartup),
            SettingsRow("\uE790", "外观", null, theme),
            SettingsRow("\uE72C", "后台刷新", null, interval)));
        body.Children.Add(SectionLabel("关于"));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE946", "支持设备", null, ShowSupport),
            SettingsLink("\uE895", "检查更新", $"当前版本 v{UpdateService.CurrentLabel}", controller.CheckForUpdates)));
        body.Children.Add(SectionLabel("诊断"));
        body.Children.Add(SettingsGroup(
            ToggleSetting("\uEBE8", "Debug 模式", "记录扫描过程与错误，仅保存在本机", controller.Preferences.DebugMode,
                value => { controller.Preferences.DebugMode = value; controller.Save(); }),
            SettingsLink("\uE9D9", "导出诊断包", "设备接口、适配状态和日志，不会自动上传", controller.ExportDiagnostics),
            SettingsLink("\uE74D", "清理诊断数据", "删除本地日志及导出包，保留设备设置", controller.ClearDiagnostics)));
    }

    private void BuildBatteryAlerts()
    {
        var thresholds = Preferences.LowBatteryThresholds;
        var threshold = new ComboBox { Width = 108, MinWidth = 0, FontSize = 12, ItemsSource = thresholds.Select(value => $"{value}%").ToArray(),
            SelectedIndex = Array.IndexOf(thresholds, controller.Preferences.LowBatteryThreshold) };
        AutomationProperties.SetName(threshold, "低电量阈值");
        threshold.SelectionChanged += (_, _) =>
        {
            if (!updating && threshold.SelectedIndex >= 0) controller.SetLowBatteryThreshold(thresholds[threshold.SelectedIndex]);
        };
        var notificationStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        body.Children.Add(SettingsGroup(
            ToggleSetting("\uE7E7", "启用提醒", "使用 Windows 通知", controller.Preferences.LowBatteryAlerts,
                controller.SetLowBatteryAlerts),
            SettingsRow("\uEBA7", "低电量阈值", null, threshold),
            SettingsLink("\uE91C", "测试通知", null, () =>
            {
                notificationStatus.Text = controller.TestBatteryNotification();
                notificationStatus.Visibility = Visibility.Visible;
            })));
        body.Children.Add(notificationStatus);
    }

    private void BuildUpdates()
    {
        body.Children.Add(new TextBlock { Text = AppBrand.DisplayName, FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"当前版本 v{UpdateService.CurrentLabel}", FontSize = 13, Opacity = .7 });
        body.Children.Add(SettingsGroup(ToggleSetting("\uE895", "自动更新", "自动下载、备份替换并重新启动", controller.Preferences.AutomaticUpdates,
            value => { controller.Preferences.AutomaticUpdates = value; controller.Save(); })));
        body.Children.Add(new TextBlock { Text = "安装前备份被替换的文件；新版启动失败时恢复旧版。保留设备设置和电量缓存。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        var result = controller.Update;
        if (controller.CheckingUpdates)
        {
            body.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) });
            body.Children.Add(new TextBlock { Text = controller.UpdateStatus, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        }
        else if (result is not null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = !result.Success ? InfoBarSeverity.Warning
                : result.Available ? InfoBarSeverity.Informational : InfoBarSeverity.Success, Message = result.Message });
        var check = new Button { Content = controller.CheckingUpdates ? "正在检查…" : "重新检查", IsEnabled = !controller.CheckingUpdates, HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Click += (_, _) => controller.CheckForUpdates();
        body.Children.Add(check);
        if (result?.Available == true)
        {
            body.Children.Add(new TextBlock { Text = "更新会自动下载、安装并重新启动，保留现有设置。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        }
        body.Children.Add(new HyperlinkButton { Content = "GitHub 发布页面", NavigateUri = new Uri(UpdateService.Repository + "/releases"), HorizontalAlignment = HorizontalAlignment.Left });
    }

    private void BuildManagement()
    {
        body.Children.Add(new TextBlock { Text = "打开开关选择要显示的设备，拖动条目调整优先级。\n超过 5 个设备时，前三个显示为弧线，其余显示为点。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        var devices = controller.ManagedDevices;
        if (devices.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "读取到可显示电量的设备后，会出现在这里。", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
        }
        var list = new ListView { CanDragItems = true, CanReorderItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var d in devices)
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 8, 0, 8), MinHeight = 58 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "\uE700", FontSize = 14, Opacity = .5, VerticalAlignment = VerticalAlignment.Center });
            var name = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(DeviceDetailLink(d.Id, d.DisplayName, 13));
            name.Children.Add(new TextBlock { Text = $"{d.Level} · {(d.IsCached ? "上次读数" : d.Connected ? d.Transport : "当前未连接")}", FontSize = 11, Opacity = .6 });
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var enabled = new ToggleSwitch { IsOn = !controller.Preferences.HiddenDevices.Contains(d.Id), OnContent = "", OffContent = "", MinWidth = 0, Width = 45, VerticalAlignment = VerticalAlignment.Center };
            enabled.Toggled += (_, _) => { if (!updating) controller.ToggleDevice(d.Id, enabled.IsOn); };
            AutomationProperties.SetName(enabled, $"显示 {d.DisplayName}");
            Grid.SetColumn(enabled, 2); row.Children.Add(enabled);
            var up = IconButton("\uE70E", $"上移 {d.DisplayName}", () => controller.MoveDevice(d.Id, -1));
            up.Width = 28; up.Height = 28; up.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(up, 3); row.Children.Add(up);
            var down = IconButton("\uE70D", $"下移 {d.DisplayName}", () => controller.MoveDevice(d.Id, 1));
            down.Width = 28; down.Height = 28; down.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(down, 4); row.Children.Add(down);
            list.Items.Add(new ListViewItem { Content = row, Tag = d.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        list.DragItemsCompleted += (_, e) =>
        {
            if (e.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                controller.SetOrder(list.Items.Cast<ListViewItem>().Select(item => (string)item.Tag));
        };
        if (devices.Count > 0) body.Children.Add(list);
        var historical = HistoricalDevices();
        if (historical.Count == 0) return;
        body.Children.Add(SectionLabel("有历史记录的设备"));
        foreach (var device in historical)
            body.Children.Add(Card(DeviceDetailLink(device.Id, JournalDisplayName(device), 13)));
    }

    private void BuildSupport()
    {
        if (DebugService.Enabled)
        {
            var unknown = DebugService.Inventory.Where(d => d.Adapters.Length == 0).ToArray();
            body.Children.Add(SectionLabel($"待适配接口 · {unknown.Length}"));
            foreach (var d in unknown)
                body.Children.Add(SupportCard(d.Name, $"VID {d.Vendor} · PID {d.Product} · Usage {d.UsagePage:X4}/{d.Usage:X4}",
                    "未匹配私有协议；可导出诊断包用于适配分析"));
        }
        body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Message = "迈从 AX5 V2、A7 Ultra(RE) 已实测，V9 Turbo+ 和 VK75 的 2.4G 电量已由用户验证。" });
        foreach (var provider in BatteryService.Providers.Where(p => p is not UsbProvider))
        {
            var state = controller.Providers.FirstOrDefault(p => p.Name == provider.Name);
            body.Children.Add(SupportCard(provider.Name, provider.Coverage, state?.Warning ?? (state is null ? "尚未读取" : $"识别到 {state.DeviceCount} 个设备")));
        }
        foreach (var adapter in UsbProvider.Adapters)
            body.Children.Add(SupportCard(adapter.Name, adapter.Models, adapter switch
            {
                MchoseAdapter => "AX5 V2、A7 Ultra(RE) 已实测",
                MchoseHeadsetAdapter => "V9 Turbo+ 2.4G 电量已由用户实测",
                SonixKeyboardAdapter => "VK75 2.4G 电量已由用户实测；其他型号待验证",
                _ => "已接入 · 硬件待实测"
            }));
    }
    private Border SupportCard(string name, string coverage, string state)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = coverage, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = Accent, TextWrapping = TextWrapping.Wrap });
        return Card(content);
    }
    private Grid ToggleSetting(string glyph, string title, string? description, bool enabled, Action<bool> changed, string? hint = null)
    {
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 44, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => { if (!updating) changed(toggle.IsOn); };
        var row = SettingsRow(glyph, title, description, toggle);
        if (hint is not null) ToolTipService.SetToolTip(row, hint);
        return row;
    }
    private Grid SettingsRow(string glyph, string title, string? description, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 8, 12, 8), MinHeight = 50 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Opacity = .8, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (description is not null) text.Children.Add(new TextBlock { Text = description, FontSize = 11, Opacity = .6, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(control, 2); row.Children.Add(control);
        return row;
    }
    private Button SettingsLink(string glyph, string title, string? description, Action clicked)
    {
        var button = new Button { Content = SettingsRow(glyph, title, description, new FontIcon { Glyph = "\uE76C", FontSize = 12, Opacity = .6 }),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), CornerRadius = new CornerRadius(6) };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => clicked();
        return button;
    }
    private Border SettingsGroup(params UIElement[] rows)
    {
        var content = new StackPanel();
        foreach (var row in rows)
        {
            if (content.Children.Count > 0) content.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 0, 12, 0), Background = separator });
            content.Children.Add(row);
        }
        var group = Card(content);
        group.Padding = new Thickness(0);
        return group;
    }
    private Border Card(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = content,
        Background = cardBackground, BorderThickness = new Thickness(1),
        BorderBrush = cardBorder
    };
    private static TextBlock SectionLabel(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 3), Opacity = .8 };
    private static Button IconButton(string glyph, string name, Action clicked)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 34, Height = 34, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => clicked();
        return button;
    }
    private static string DeviceGlyph(Peripheral d)
    {
        string name = (d.Name + " " + d.Source).ToLowerInvariant();
        if (new[] { "head", "ear", "arctis", "cloud", "airpod", "wf-", "edge", "耳机" }.Any(name.Contains)) return "\uE7F6";
        if (new[] { "keyboard", "rk61", "widow", "deathstalker", "键盘", "k65" }.Any(name.Contains)) return "\uE765";
        if (new[] { "手柄", "xbox", "vader", "apex", "dual", "飞智" }.Any(name.Contains)) return "\uE967";
        return "\uE962";
    }

    internal async Task RunInteractionChecksAsync()
    {
        int count = 0;
        var failures = new List<string>();
        void Check(bool value, string name) { count++; if (!value) failures.Add(name); }
        IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element) yield return element;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        pinned = true;
        ShowSettings(); ShowFlyout();
        var openingFade = windowMotion;
        await Task.Delay(45);
        Check(uiSettings.AnimationsEnabled ? openingFade?.GetCurrentState() == ClockState.Active : openingFade is null,
            "Opening fade plays when enabled in Windows");
        await Task.Delay(300);
        var minimumSize = NativeWindow.MinimumTrackingSize(hwnd);
        Check(minimumSize.Width >= Math.Ceiling(340 * NativeWindow.Scale(hwnd))
            && minimumSize.Height >= Math.Ceiling(240 * NativeWindow.Scale(hwnd)),
            "Native border dragging respects minimum width and height before resizing");
        var toggle = Descendants(root).OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "分段环形图标");
        bool original = toggle.IsOn;
        try
        {
            toggle.IsOn = !original;
            await Task.Delay(200);
            Check(Descendants(root).Contains(toggle) && toggle.IsOn == !original, "Setting control survives its own toggle animation");
            UpdateView();
            Check(Descendants(root).Contains(toggle), "Background refresh does not replace settings controls");
            var knobs = Descendants(toggle).Where(e => e.Name.Contains("Knob", StringComparison.OrdinalIgnoreCase) && e.ActualWidth > 0 && e.ActualHeight > 0).ToArray();
            Check(knobs.Length > 0 && knobs.All(e =>
            {
                var bounds = e.TransformToVisual(toggle).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
                return bounds.Top >= -.5 && bounds.Bottom <= toggle.ActualHeight + .5;
            }), "Native toggle knob fits inside its full control height");
            var theme = Descendants(root).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "外观");
            theme.IsDropDownOpen = true;
            await Task.Delay(100);
            UpdateView();
            Check(Descendants(root).Contains(theme) && theme.IsDropDownOpen, "Refresh preserves an open settings dropdown");
            theme.IsDropDownOpen = false;
            HideFlyout();
            await Task.Delay(25);
            ShowFlyout();
            await Task.Delay(300);
            Check(visible && NativeWindow.IsVisible(hwnd) && root.Opacity > .99, "Rapid reopen is not hidden by an old close animation");
            ShowSupport(); ShowSettings();
            await Task.Delay(300);
            Check(Descendants(root).OfType<ToggleSwitch>().Count() == 4 && body.Opacity > .99, "Rapid page navigation settles on visible settings controls");
            AppWindow.Move(new Windows.Graphics.PointInt32(AppWindow.Position.X - 80, AppWindow.Position.Y));
            var expectedPosition = AppWindow.Position;
            var expectedSize = AppWindow.Size;
            bool BoundsUnchanged() => AppWindow.Position.X == expectedPosition.X && AppWindow.Position.Y == expectedPosition.Y
                && AppWindow.Size.Width == expectedSize.Width && AppWindow.Size.Height == expectedSize.Height;
            ShowUpdates(); ShowFlyout();
            await Task.Delay(250);
            Check(BoundsUnchanged(), "Opening update page preserves the visible window position and size");
            controller.ShowSettings(); controller.ShowDeviceManagement();
            await Task.Delay(250);
            Check(BoundsUnchanged(), "Navigation actions never reposition an already visible flyout");
            bool NoTranslation(FrameworkElement element)
            {
                var origin = element.RenderTransform?.TransformPoint(new Windows.Foundation.Point(0, 0)) ?? new Windows.Foundation.Point(0, 0);
                return Math.Abs(origin.X) < .01 && Math.Abs(origin.Y) < .01;
            }
            HideFlyout(); await Task.Delay(25); ShowFlyout(); await Task.Delay(40);
            ShowSettings(); await Task.Delay(40);
            Check(NoTranslation(root) && NoTranslation(body), "Open and page animations do not translate content");
            navigation.Clear();
            page = "devices"; UpdateView();
            ShowSettings(); await Task.Delay(200);
            scroll.ChangeView(null, Math.Min(300, scroll.ScrollableHeight), null, true);
            await Task.Delay(100);
            double settingsOffset = scroll.VerticalOffset;
            Check(settingsOffset > 0, "Settings scroll fixture reaches lower settings");
            ShowSupport(); await Task.Delay(150); GoBack(); await Task.Delay(200);
            Check(page == "settings" && Math.Abs(scroll.VerticalOffset - settingsOffset) < 1,
                "Back from support restores settings and its scroll position");
            ShowUpdates(); await Task.Delay(150);
            var automatic = Descendants(root).OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "自动更新");
            bool originalAutomatic = automatic.IsOn;
            automatic.IsOn = !originalAutomatic;
            Check(Descendants(root).Contains(automatic) && controller.Preferences.AutomaticUpdates == !originalAutomatic,
                "Automatic update switch lives in update page and survives saving");
            automatic.IsOn = originalAutomatic;
            GoBack(); await Task.Delay(200);
            Check(page == "settings" && Math.Abs(scroll.VerticalOffset - settingsOffset) < 1
                && !Descendants(root).OfType<ToggleSwitch>().Any(t => AutomationProperties.GetName(t) == "自动更新"),
                "Back from updates restores settings without duplicate update switch");
            GoBack(); await Task.Delay(150);
            Check(page == "devices", "Back from root settings returns to devices");
            ShowSettings(); await Task.Delay(150);
            var contentBounds = body.TransformToVisual(scroll).TransformBounds(new Windows.Foundation.Rect(0, 0, body.ActualWidth, body.ActualHeight));
            Check(scroll.ActualWidth - contentBounds.Right >= 10 && scroll.ActualWidth - contentBounds.Right <= 13,
                "Scrollbar gutter protects cards without excessive spacing");
            var scrollBounds = scroll.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
            Check(root.ActualWidth - scrollBounds.Right >= 6 && root.ActualWidth - scrollBounds.Right <= 10,
                "Scrollbar stays near the outer edge instead of consuming card width");
            Button NamedButton(string title) => Descendants(root).OfType<Button>().Single(b => AutomationProperties.GetName(b) == title);
            void Invoke(Button button) => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
            double alertsReturnOffset = scroll.VerticalOffset;
            Check(!Descendants(root).Any(c => AutomationProperties.GetName(c) is "低电量阈值" or "测试通知"),
                "General settings only shows the battery alert entry");
            Invoke(NamedButton("低电量提醒"));
            await Task.Delay(45);
            var alertsFade = pageMotion;
            Check(uiSettings.AnimationsEnabled ? alertsFade?.GetCurrentState() == ClockState.Active : alertsFade is null,
                "Page fade plays when enabled in Windows");
            UpdateView(true);
            Check(ReferenceEquals(pageMotion, alertsFade) && (!uiSettings.AnimationsEnabled || alertsFade?.GetCurrentState() == ClockState.Active),
                "Rebuilding the same page leaves its transition running");
            await Task.Delay(160);
            var threshold = Descendants(root).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "低电量阈值");
            var enableAlerts = Descendants(root).OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "启用提醒");
            bool originalAlerts = enableAlerts.IsOn;
            enableAlerts.IsOn = !originalAlerts;
            Check(page == "battery-alerts" && controller.Preferences.LowBatteryAlerts == !originalAlerts && Descendants(root).Contains(enableAlerts),
                "Alerts page groups the controls and retains the toggle when saving");
            enableAlerts.IsOn = originalAlerts;
            int initialThreshold = threshold.SelectedIndex;
            threshold.SelectedIndex = 6;
            Check(controller.Preferences.LowBatteryThreshold == 40 && Preferences.Load().LowBatteryThreshold == 40,
                "The settings threshold is applied and saved");
            Invoke(NamedButton("测试通知"));
            await Task.Delay(150);
            Check(Descendants(root).OfType<TextBlock>().Any(t => t.Visibility == Visibility.Visible && t.Text == "预览模式不发送系统通知。"),
                "Test notification reports its outcome in the alerts page");
            Invoke(NamedButton("返回上一页"));
            await Task.Delay(200);
            Check(page == "settings" && Math.Abs(scroll.VerticalOffset - alertsReturnOffset) < 1
                && Descendants(NamedButton("低电量提醒")).OfType<TextBlock>().Any(t => t.Text.EndsWith("阈值 40%", StringComparison.Ordinal)),
                "Back from alerts restores settings and shows the saved threshold");
            ShowBatteryAlerts();
            await Task.Delay(150);
            Descendants(root).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "低电量阈值").SelectedIndex = initialThreshold;
            GoBack();
            ShowDeviceManagement();
            var originalWidth = AppWindow.Size.Width;
            var originalHeight = AppWindow.Size.Height;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(340 * NativeWindow.Scale(hwnd)), originalHeight));
            const string testDevice = "preview-mouse";
            Navigate(DevicePagePrefix + testDevice);
            await Task.Delay(200);
            var aliasEditor = Descendants(root).OfType<TextBox>().Single();
            aliasEditor.Text = "正在编辑";
            UpdateView();
            Check(Descendants(root).Contains(aliasEditor) && aliasEditor.Text == "正在编辑", "Background refresh preserves the alias draft");
            Check(Descendants(root).OfType<Grid>().Any(g => AutomationProperties.GetName(g) == "最近 7 天电量趋势图")
                && deviceDetailStatus!.Text.Contains("原始名称"), "Device details include the trend and original model at minimum width");
            string? previousAlias = controller.Preferences.DeviceAliases.GetValueOrDefault(testDevice);
            Check(controller.SetDeviceAlias(testDevice, "测试鼠标") && deviceDetailName!.Text == "测试鼠标"
                && Preferences.Load().DeviceAliases.GetValueOrDefault(testDevice) == "测试鼠标", "Device alias persists and updates the selected device");
            Check(controller.SetDeviceAlias(testDevice, previousAlias) && deviceDetailName!.Text != "测试鼠标", "Reset restores the original display name");
            GoBack();
            Check(page == "manage", "Back from device details returns to device management");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(originalWidth, originalHeight));
        }
        finally { controller.Preferences.RingIcons = original; controller.Save(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-self-test.json"), System.Text.Json.JsonSerializer.Serialize(new
            { Passed = failures.Count == 0, Checks = count, WindowsAnimationsEnabled = uiSettings.AnimationsEnabled, Failures = failures }));
        if (failures.Count > 0) Environment.ExitCode = 1;
    }
}
