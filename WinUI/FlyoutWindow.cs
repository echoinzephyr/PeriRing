using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace BatteryDisplay;

internal sealed class FlyoutWindow : Window
{
    private readonly TrayController controller;
    private readonly Grid root = new();
    private readonly StackPanel header = new() { Spacing = 12 };
    private readonly StackPanel body = new() { Spacing = 8 };
    private readonly TextBlock status = new() { FontSize = 12, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly IntPtr hwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer deactivateTimer;
    private string page = "devices";
    private bool pinned;
    private bool visible;
    private bool updating;
    private bool disconnectedExpanded;
    private string renderedPage = "";
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
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(status);
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
            int minWidth = (int)(340 * scale), minHeight = (int)(240 * scale);
            if (AppWindow.Size.Width < minWidth || AppWindow.Size.Height < minHeight)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(minWidth, AppWindow.Size.Width), Math.Max(minHeight, AppWindow.Size.Height)));
                return;
            }
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
        NativeWindow.SetDark(hwnd, Dark);
    }
    public void ToggleFlyout() { if (visible) HideFlyout(); else ShowFlyout(); }
    public void ShowFlyout()
    {
        UpdateView();
        double height = page == "devices" ? Math.Clamp(210 + controller.Devices.Count(d => d.Connected) * 80 + (controller.Devices.All(d => !d.Connected) ? 130 : 0), 280, 600)
            : page == "manage" ? Math.Clamp(170 + controller.ManagedDevices.Count * 80, 300, 620) : 620;
        positioning = true;
        NativeWindow.Position(AppWindow, hwnd, controller.Preferences.PopupWidth, controller.Preferences.PopupHeight > 0 ? controller.Preferences.PopupHeight : height);
        plannedSize = AppWindow.Size;
        positioning = false;
        AppWindow.Show();
        Activate();
        NativeWindow.Show(hwnd);
        visible = true;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)) };
        Storyboard.SetTarget(fade, root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
    private void HideFlyout()
    {
        visible = false;
        deactivateTimer.Stop();
        AppWindow.Hide();
    }
    public void ShowSettings() { page = "settings"; UpdateView(); }
    public void ShowSupport() { page = "support"; UpdateView(); }
    public void ShowDeviceManagement() { page = "manage"; UpdateView(); }
    public void ShowUpdates() { page = "updates"; UpdateView(); }

    internal async Task CaptureViewsAsync()
    {
        pinned = true;
        ShowFlyout();
        foreach (string view in new[] { "devices", "manage", "settings", "support", "updates" })
        {
            page = view;
            UpdateView();
            await Task.Delay(300);
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

    public void UpdateView()
    {
        if (updating) return;
        updating = true;
        double offset = renderedPage == page ? scroll.VerticalOffset : 0;
        try
        {
            ApplyTheme();
            BuildHeader();
            body.Children.Clear();
            if (page == "settings") BuildSettings();
            else if (page == "manage") BuildManagement();
            else if (page == "support") BuildSupport();
            else if (page == "updates") BuildUpdates();
            else BuildDevices();
            status.Text = controller.Refreshing ? "正在读取设备电量…" : controller.Error is not null ? "读取遇到问题，点击刷新重试"
                : controller.Updated is DateTimeOffset time ? $"更新于 {time:HH:mm:ss} · 每 {controller.Preferences.RefreshSeconds} 秒刷新" : "等待首次读取";
            renderedPage = page;
            DispatcherQueue.TryEnqueue(() => scroll.ChangeView(null, offset, null, true));
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
            : IconButton("\uE72B", "返回设备", () => { page = "devices"; UpdateView(); });
        row.Children.Add(leading);
        title.Text = page == "settings" ? "设置" : page == "support" ? "支持设备" : page == "manage" ? "设备开关与排序" : page == "updates" ? "检查更新" : AppBrand.Name;
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var pin = new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 15 }, IsChecked = pinned, Width = 34, Height = 34, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(pin, "保持浮层展开");
        AutomationProperties.SetName(pin, "保持浮层展开");
        pin.Click += (_, _) => pinned = pin.IsChecked == true;
        Grid.SetColumn(pin, 2); row.Children.Add(pin);
        var settings = IconButton("\uE713", "设置", ShowSettings);
        Grid.SetColumn(settings, 3); row.Children.Add(settings);
        header.Children.Add(row);
        if (page != "devices") return;
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var refresh = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE72C", FontSize = 15 }, new TextBlock { Text = controller.Refreshing ? "正在刷新" : "刷新电量" } } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !controller.Refreshing
        };
        refresh.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        refresh.Click += (_, _) => controller.Refresh();
        actions.Children.Add(refresh);
        var supported = IconButton("\uE946", "支持设备", () => { page = "support"; UpdateView(); });
        supported.Width = 40; supported.Height = 36;
        Grid.SetColumn(supported, 1); actions.Children.Add(supported);
        // Primary actions live in the tray context menu; keep the quick panel compact.
    }

    private void BuildDevices()
    {
        var devices = controller.PresentedDevices;
        if (controller.Error is not null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = controller.Error });
        var connected = devices.Where(d => d.Connected).ToList();
        body.Children.Add(SectionLabel($"已连接 · {connected.Count}"));
        foreach (var d in connected) body.Children.Add(DeviceCard(d));
        if (connected.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12, Padding = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "\uE702", FontSize = 36, Opacity = .5 });
            empty.Children.Add(new TextBlock { Text = controller.Refreshing ? "正在寻找你的外设" : "连接外设后，电量会显示在这里", FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "支持蓝牙及已适配的无线接收器", FontSize = 12, Opacity = .6, TextAlignment = TextAlignment.Center });
            body.Children.Add(Card(empty));
        }
        var other = devices.Where(d => !d.Connected).ToList();
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
            Foreground = d.Connected && d.GaugeValue <= 20 ? new SolidColorBrush(Color.FromArgb(255, 231, 133, 75)) : Accent, Opacity = d.Connected ? 1 : .45 });
        ring.Children.Add(new FontIcon { Glyph = DeviceGlyph(d), FontSize = 16, Opacity = d.Connected ? .9 : .45 });
        row.Children.Add(ring);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = d.Name.Replace("迈从 · ", ""), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string detail = !d.Connected ? d.State : d.Transport + (d.Charging ? " · 充电中" : !d.CanShow ? " · 暂未取得电量" : "");
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var level = new TextBlock { Text = d.Level, FontSize = d.Level.Length > 6 ? 13 : 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = d.Connected ? 1 : .5 };
        ToolTipService.SetToolTip(level, d.VendorDisplay.HasValue ? $"接收器原始读数 {d.Battery}%\n官方驱动显示 {d.VendorDisplay}%（分档）" : d.Source);
        Grid.SetColumn(level, 2); row.Children.Add(level);
        var toggle = new ToggleButton { Content = new FontIcon { Glyph = "\uE718", FontSize = 13 }, IsChecked = !controller.Preferences.HiddenDevices.Contains(d.Id), Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(toggle, "在托盘显示这个设备");
        AutomationProperties.SetName(toggle, $"在托盘显示 {d.Name}");
        toggle.Click += (_, _) => controller.ToggleDevice(d.Id, toggle.IsChecked == true);
        Grid.SetColumn(toggle, 3); row.Children.Add(toggle);
        var card = Card(row);
        ToolTipService.SetToolTip(card, $"{d.Name}\n{d.Source}\n{d.State}");
        return card;
    }

    private void BuildSettings()
    {
        body.Children.Add(SectionLabel("托盘"));
        var manage = new Button { Content = "设备开关与排序", HorizontalAlignment = HorizontalAlignment.Stretch };
        manage.Click += (_, _) => { page = "manage"; UpdateView(); };
        body.Children.Add(manage);
        body.Children.Add(ToggleSetting("环形汇总图标", "每台设备一段弧；超过 5 台时显示 3 段弧，其余用点表示。关闭后显示最低电量数字。", controller.Preferences.RingIcons,
            value => { controller.Preferences.RingIcons = value; controller.Save(); }));
        body.Children.Add(ToggleSetting("与官方驱动一致", "采用已核实的厂商分档；设备卡片仍保留原始值", controller.Preferences.PreferVendorDisplay,
            value => { controller.Preferences.PreferVendorDisplay = value; controller.Save(); }));
        body.Children.Add(ToggleSetting("低电量提醒", "电量不高于 20% 时提醒", controller.Preferences.LowBatteryAlerts,
            value => { controller.Preferences.LowBatteryAlerts = value; controller.Save(); }));
        body.Children.Add(SectionLabel("应用"));
        body.Children.Add(ToggleSetting("开机启动", "登录 Windows 后在托盘运行", TrayController.StartupEnabled(), controller.SetStartup));
        var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = controller.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        theme.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            controller.Preferences.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            controller.Save();
        };
        body.Children.Add(Setting("外观", theme));
        var interval = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "15 秒", "30 秒", "1 分钟", "5 分钟" }, SelectedIndex = Array.IndexOf(new[] { 15, 30, 60, 300 }, controller.Preferences.RefreshSeconds) };
        if (interval.SelectedIndex < 0) interval.SelectedIndex = 2;
        interval.SelectionChanged += (_, _) => { if (!updating && interval.SelectedIndex >= 0) { controller.Preferences.RefreshSeconds = new[] { 15, 30, 60, 300 }[interval.SelectedIndex]; controller.Save(); } };
        body.Children.Add(Setting("刷新间隔", interval));
        var support = new Button { Content = "支持设备与读取状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        support.Click += (_, _) => { page = "support"; UpdateView(); };
        body.Children.Add(support);
        var updates = new Button { Content = $"检查更新 · 当前 v{UpdateService.CurrentLabel}", HorizontalAlignment = HorizontalAlignment.Stretch };
        updates.Click += (_, _) => controller.CheckForUpdates();
        body.Children.Add(updates);
    }

    private void BuildUpdates()
    {
        body.Children.Add(new TextBlock { Text = AppBrand.DisplayName, FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"当前版本 v{UpdateService.CurrentLabel}", FontSize = 13, Opacity = .7 });
        var result = controller.Update;
        if (controller.CheckingUpdates)
        {
            body.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) });
            body.Children.Add(new TextBlock { Text = "正在检查新版本…", FontSize = 13 });
        }
        else if (result is not null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = !result.Success ? InfoBarSeverity.Warning
                : result.Available ? InfoBarSeverity.Informational : InfoBarSeverity.Success, Message = result.Message });
        var check = new Button { Content = controller.CheckingUpdates ? "正在检查…" : "重新检查", IsEnabled = !controller.CheckingUpdates, HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Click += (_, _) => controller.CheckForUpdates();
        body.Children.Add(check);
        if (result?.Available == true)
        {
            var download = new HyperlinkButton { Content = result.Download is not null ? "下载新版（Windows x64）" : "查看新版与下载", NavigateUri = result.Download ?? result.ReleasePage, HorizontalAlignment = HorizontalAlignment.Left };
            body.Children.Add(download);
            body.Children.Add(new TextBlock { Text = "下载后退出环电，解压并替换程序文件；保留 data 文件夹即可保留设置。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
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
            return;
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
            name.Children.Add(new TextBlock { Text = d.Name.Replace("迈从 · ", ""), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = $"{d.Level} · {(d.Connected ? d.Transport : "当前未连接")}", FontSize = 11, Opacity = .6 });
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var enabled = new ToggleSwitch { IsOn = !controller.Preferences.HiddenDevices.Contains(d.Id), OnContent = "", OffContent = "", MinWidth = 0, Width = 45, VerticalAlignment = VerticalAlignment.Center };
            enabled.Toggled += (_, _) => { if (!updating) controller.ToggleDevice(d.Id, enabled.IsOn); };
            AutomationProperties.SetName(enabled, $"显示 {d.Name}");
            Grid.SetColumn(enabled, 2); row.Children.Add(enabled);
            var up = IconButton("\uE70E", $"上移 {d.Name}", () => controller.MoveDevice(d.Id, -1));
            up.Width = 28; up.Height = 28; up.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(up, 3); row.Children.Add(up);
            var down = IconButton("\uE70D", $"下移 {d.Name}", () => controller.MoveDevice(d.Id, 1));
            down.Width = 28; down.Height = 28; down.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(down, 4); row.Children.Add(down);
            list.Items.Add(new ListViewItem { Content = row, Tag = d.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        list.DragItemsCompleted += (_, e) =>
        {
            if (e.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                controller.SetOrder(list.Items.Cast<ListViewItem>().Select(item => (string)item.Tag));
        };
        body.Children.Add(list);
    }

    private void BuildSupport()
    {
        body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Message = "迈从 AX5 V2 已实测。其余适配已接入并检查解析逻辑，仍需对应硬件实测。" });
        foreach (var provider in BatteryService.Providers.Where(p => p is not UsbProvider))
        {
            var state = controller.Providers.FirstOrDefault(p => p.Name == provider.Name);
            body.Children.Add(SupportCard(provider.Name, provider.Coverage, state?.Warning ?? (state is null ? "尚未读取" : $"识别到 {state.DeviceCount} 个设备")));
        }
        foreach (var adapter in UsbProvider.Adapters)
            body.Children.Add(SupportCard(adapter.Name, adapter.Models, adapter is MchoseAdapter ? "AX5 V2 已实测" : "已接入 · 硬件待实测"));
    }
    private Border SupportCard(string name, string coverage, string state)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = coverage, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = Accent, TextWrapping = TextWrapping.Wrap });
        return Card(content);
    }
    private Border ToggleSetting(string title, string description, bool enabled, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Header = title, IsOn = enabled, OnContent = "已开启", OffContent = "已关闭" };
        toggle.Toggled += (_, _) => { if (!updating) changed(toggle.IsOn); };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(toggle);
        content.Children.Add(new TextBlock { Text = description, FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });
        return Card(content);
    }
    private Border Setting(string label, UIElement control)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
        content.Children.Add(control);
        return Card(content);
    }
    private Border Card(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = content,
        Background = new SolidColorBrush(CardColor), BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Dark ? Color.FromArgb(35, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0))
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
}
