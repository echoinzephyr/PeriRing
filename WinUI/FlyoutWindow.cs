using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private readonly SolidColorBrush scrollDivider = new();
    private readonly SolidColorBrush iconBadgeBackground = new();
    private readonly SolidColorBrush accentBrush = new();
    private readonly SolidColorBrush lowBatteryBrush = new();
    private readonly SolidColorBrush chargingBrush = new();
    private readonly SolidColorBrush pinnedTintBrush = new();
    private readonly SolidColorBrush pinnedBorderBrush = new();
    private readonly SolidColorBrush pinnedIconBrush = new();
    private readonly SolidColorBrush mutedIconBrush = new();
    private readonly SolidColorBrush previewForegroundBrush = new();
    private readonly SolidColorBrush previewTrackBrush = new();
    private readonly SolidColorBrush previewRing1Brush = new();
    private readonly SolidColorBrush previewRing2Brush = new();
    private readonly SolidColorBrush previewRing3Brush = new();
    private readonly LinearGradientBrush brandBrush = new() { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
    private readonly Border topScrollDivider = new() { Height = 1, Opacity = 0, Margin = new Thickness(-16, 0, -16, 0) };
    private readonly Border bottomScrollDivider = new() { Height = 1, Opacity = 0, Margin = new Thickness(-16, 0, -16, 0) };
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly Button refresh;
    private readonly RotateTransform refreshSpinTransform = new();
    private Storyboard? refreshSpinMotion;
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
    private bool renderedInstallingUpdate;
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
    private Brush Accent => accentBrush;

    public FlyoutWindow(TrayController controller)
    {
        this.controller = controller;
        Title = AppBrand.DisplayName;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        root.Padding = new Thickness(16);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headerHost = new StackPanel();
        header.Padding = new Thickness(0, 0, 0, 10);
        topScrollDivider.Background = scrollDivider;
        topScrollDivider.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        headerHost.Children.Add(header);
        headerHost.Children.Add(topScrollDivider);
        root.Children.Add(headerHost);
        body.Margin = new Thickness(0, 4, 0, 4);
        scroll.Content = body;
        // WinUI's expanding overlay scrollbar needs its own gutter.
        scroll.Padding = new Thickness(0, 0, 12, 0);
        scroll.Margin = new Thickness(0, 0, -8, 0);
        scroll.HorizontalScrollMode = ScrollMode.Disabled;
        scroll.IsVerticalScrollChainingEnabled = false;
        scroll.ViewChanged += (_, _) => UpdateScrollDividers();
        scroll.SizeChanged += (_, _) => UpdateScrollDividers();
        body.SizeChanged += (_, _) => UpdateScrollDividers();
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footerHost = new StackPanel();
        bottomScrollDivider.Background = scrollDivider;
        bottomScrollDivider.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        footerHost.Children.Add(bottomScrollDivider);
        var footer = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        refresh = IconButton("\uE72C", "刷新电量", controller.Refresh);
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        if (refresh.Content is FontIcon refreshIcon)
        {
            refreshIcon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            refreshIcon.RenderTransform = refreshSpinTransform;
        }
        footer.Children.Add(refresh);
        var exit = IconButton("\uE7E8", "退出", controller.Quit);
        Grid.SetColumn(exit, 1);
        footer.Children.Add(exit);
        footerHost.Children.Add(footer);
        Grid.SetRow(footerHost, 2);
        root.Children.Add(footerHost);
        Content = new Border { CornerRadius = new CornerRadius(10), Child = root };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeWindow.HideFromSwitchers(AppWindow, hwnd);
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
        NativeWindow.SetDeviceChangeHandler(hwnd, controller.NotifyDeviceChanged);
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
        scrollDivider.Color = Dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        iconBadgeBackground.Color = Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);
        brandBrush.GradientStops.Clear();
        if (Dark)
        {
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 162, 232, 193), Offset = 0 });
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 105, 206, 165), Offset = 0.5 });
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 56, 169, 145), Offset = 1 });
        }
        else
        {
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 53, 168, 139), Offset = 0 });
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 36, 138, 113), Offset = 0.55 });
            brandBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 25, 111, 91), Offset = 1 });
        }
        var accentColor = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        accentBrush.Color = accentColor;
        lowBatteryBrush.Color = Dark ? Color.FromArgb(255, 246, 102, 108) : Color.FromArgb(255, 214, 65, 72);
        chargingBrush.Color = Dark ? Color.FromArgb(255, 105, 206, 165) : Color.FromArgb(255, 26, 136, 98);
        pinnedTintBrush.Color = Color.FromArgb(Dark ? (byte)54 : (byte)28, accentColor.R, accentColor.G, accentColor.B);
        pinnedBorderBrush.Color = Color.FromArgb(Dark ? (byte)110 : (byte)64, accentColor.R, accentColor.G, accentColor.B);
        pinnedIconBrush.Color = Dark ? Color.FromArgb(255, 134, 224, 186) : accentColor;
        mutedIconBrush.Color = Dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(145, 24, 24, 24);
        previewForegroundBrush.Color = Dark ? Color.FromArgb(255, 244, 246, 249) : Color.FromArgb(255, 28, 33, 38);
        previewTrackBrush.Color = Dark ? Color.FromArgb(72, 244, 246, 249) : Color.FromArgb(56, 28, 33, 38);
        previewRing1Brush.Color = Dark ? Color.FromArgb(255, 86, 217, 165) : Color.FromArgb(255, 22, 148, 106);
        previewRing2Brush.Color = Dark ? Color.FromArgb(255, 76, 185, 255) : Color.FromArgb(255, 26, 118, 210);
        previewRing3Brush.Color = Dark ? Color.FromArgb(255, 255, 198, 72) : Color.FromArgb(255, 208, 128, 16);
        NativeWindow.SetDark(hwnd, Dark);
    }

    private void UpdateScrollDividers()
    {
        topScrollDivider.Opacity = scroll.VerticalOffset > 1 ? 1 : 0;
        bottomScrollDivider.Opacity = scroll.ScrollableHeight - scroll.VerticalOffset > 1 ? 1 : 0;
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
    private void ShowDiagnostics() => Navigate("diagnostics");
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

    public void ApplySettings() => UpdateView();

    private Storyboard? Fade(bool entering, int milliseconds, Action? completed, params FrameworkElement[] surfaces)
    {
        if (!uiSettings.AnimationsEnabled) { completed?.Invoke(); return null; }
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var animation = new Storyboard();
        var easing = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn };
        foreach (var surface in surfaces)
        {
            double from = entering ? (surface.Opacity is > 0.01 and < 0.99 ? surface.Opacity : 0) : surface.Opacity;
            var fade = new DoubleAnimation { From = from, To = entering ? 1 : 0, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(fade, surface);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            animation.Children.Add(fade);
        }
        if (completed is not null) animation.Completed += (_, _) => completed();
        animation.Begin();
        return animation;
    }

    private void AnimatePageEntrance()
    {
        if (!uiSettings.AnimationsEnabled) { pageMotion = null; return; }
        var animation = new Storyboard();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var surface in new FrameworkElement[] { header, scroll })
        {
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = easing };
            Storyboard.SetTarget(fade, surface);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            animation.Children.Add(fade);
        }
        int itemIndex = 0;
        foreach (var child in body.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (itemIndex >= 6) break;
            child.Opacity = 0;
            var stagger = new DoubleAnimation
            {
                From = 0, To = 1,
                BeginTime = TimeSpan.FromMilliseconds(itemIndex * 20),
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(stagger, child);
            Storyboard.SetTargetProperty(stagger, nameof(UIElement.Opacity));
            animation.Children.Add(stagger);
            itemIndex++;
        }
        animation.Begin();
        pageMotion = animation;
    }

    private void UpdateRefreshMotion(bool active)
    {
        if (active && uiSettings.AnimationsEnabled)
        {
            if (refreshSpinMotion is not null) return;
            var spin = new DoubleAnimation
            {
                From = 0, To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(750)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(spin, refreshSpinTransform);
            Storyboard.SetTargetProperty(spin, nameof(RotateTransform.Angle));
            storyboard.Children.Add(spin);
            storyboard.Begin();
            refreshSpinMotion = storyboard;
            return;
        }
        if (refreshSpinMotion is null) return;
        refreshSpinMotion.Stop();
        refreshSpinMotion = null;
        refreshSpinTransform.Angle = 0;
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
            UpdateRefreshMotion(controller.Refreshing);
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
                "settings" or "battery-alerts" or "diagnostics" => false,
                "updates" => renderedUpdate != controller.Update || renderedCheckingUpdates != controller.CheckingUpdates
                    || renderedInstallingUpdate != controller.InstallingUpdate || renderedUpdateStatus != controller.UpdateStatus,
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
            else if (page == "diagnostics") BuildDiagnostics();
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
            renderedInstallingUpdate = controller.InstallingUpdate;
            renderedUpdateStatus = controller.UpdateStatus;
            renderedRefreshing = controller.Refreshing;
            // The header and viewport survive content refreshes while a page transition is running.
            if (pageChanged && visible) AnimatePageEntrance();
            int revision = ++layoutRevision;
            string destination = page;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (revision != layoutRevision || page != destination) return;
                scroll.UpdateLayout();
                scroll.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true);
                UpdateScrollDividers();
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
        UIElement leading = page == "devices" ? BuildBrandMark()
            : IconButton("\uE72B", "返回上一页", GoBack);
        row.Children.Add(leading);
        title.Text = page switch
        {
            "settings" => "设置", "support" => "支持设备", "manage" => "设备开关与排序", "updates" => "检查更新",
            "battery-alerts" => "低电量提醒", "diagnostics" => "诊断与日志",
            _ => IsDeviceDetail ? "设备详情" : AppBrand.Name
        };
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var pinIcon = new FontIcon { Glyph = "\uE718", FontSize = 15 };
        var pin = new Button { Content = pinIcon, Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        StylePinButton(pin, pinIcon, pinned);
        ToolTipService.SetToolTip(pin, pinned ? "已固定浮层" : "保持浮层展开");
        AutomationProperties.SetName(pin, "保持浮层展开");
        pin.Click += (_, _) =>
        {
            pinned = !pinned;
            StylePinButton(pin, pinIcon, pinned);
            ToolTipService.SetToolTip(pin, pinned ? "已固定浮层" : "保持浮层展开");
        };
        Grid.SetColumn(pin, 2); row.Children.Add(pin);
        if (page != "settings")
        {
            var settings = IconButton("\uE713", "设置", ShowSettings);
            Grid.SetColumn(settings, 3); row.Children.Add(settings);
        }
        header.Children.Add(row);
    }

    private Canvas BuildBrandMark()
    {
        var mark = new Canvas { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        const double cx = 12, cy = 12, radius = 8.75;
        foreach (double centerAngle in new[] { -90d, 30d, 150d })
        {
            double start = (centerAngle - 42) * Math.PI / 180;
            double end = (centerAngle + 42) * Math.PI / 180;
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(cx + radius * Math.Cos(start), cy + radius * Math.Sin(start)),
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Windows.Foundation.Point(cx + radius * Math.Cos(end), cy + radius * Math.Sin(end)),
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = brandBrush,
                StrokeThickness = 2.75,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
        mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Polygon
        {
            Points = new PointCollection { new(12, 8.2), new(15.8, 12), new(12, 15.8), new(8.2, 12) },
            Stroke = brandBrush,
            StrokeThickness = 1.9,
            StrokeLineJoin = PenLineJoin.Round
        });
        return mark;
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
    private Grid SettingsRow(string glyph, string title, string? description, FrameworkElement control, Thickness? padding = null)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = padding ?? new Thickness(14, 10, 14, 10), MinHeight = 52 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBadge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
            Background = iconBadgeBackground, VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 14, Opacity = .85, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        row.Children.Add(iconBadge);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
        if (description is not null) text.Children.Add(new TextBlock { Text = description, FontSize = 11.5, Opacity = .62, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(control, 2); row.Children.Add(control);
        return row;
    }
    private Button SettingsLink(string glyph, string title, string? description, Action clicked, string? valueText = null)
    {
        var chevron = new FontIcon
        {
            Glyph = "\uE76C", FontSize = 11, Opacity = .55, VerticalAlignment = VerticalAlignment.Center,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
            TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) }
        };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(valueText))
            trailing.Children.Add(new TextBlock { Text = valueText, FontSize = 12, Opacity = .68, VerticalAlignment = VerticalAlignment.Center });
        trailing.Children.Add(chevron);
        var button = new Button
        {
            Content = SettingsRow(glyph, title, description, trailing, new Thickness(10, 8, 10, 8)),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), CornerRadius = new CornerRadius(6)
        };
        button.PointerEntered += (_, _) =>
        {
            if (!uiSettings.AnimationsEnabled) return;
            chevron.Opacity = .92;
            chevron.Translation = new System.Numerics.Vector3(3, 0, 0);
        };
        button.PointerExited += (_, _) =>
        {
            chevron.Opacity = .55;
            chevron.Translation = System.Numerics.Vector3.Zero;
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => clicked();
        return button;
    }
    private Border SettingsGroup(params UIElement[] rows)
    {
        var content = new StackPanel { Padding = new Thickness(0, 2, 0, 2) };
        foreach (var row in rows)
        {
            if (content.Children.Count > 0) content.Children.Add(new Border { Height = 1, Margin = new Thickness(56, 0, 14, 0), Background = separator });
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
    private static TextBlock SectionLabel(string text, bool first = false) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, first ? 2 : 12, 0, 0), Opacity = .68
    };
    private static Button IconButton(string glyph, string name, Action clicked)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 34, Height = 34, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => clicked();
        return button;
    }
}
