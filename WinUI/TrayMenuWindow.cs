using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BatteryDisplay;

internal sealed class TrayMenuWindow : Window
{
    private readonly TrayController controller;
    private readonly Grid root = new() { Width = 1, Height = 1 };
    private readonly IntPtr hwnd;
    private MenuFlyout? menu;
    public bool IsOpen { get; private set; }
    public TrayMenuWindow(TrayController controller)
    {
        this.controller = controller;
        Title = AppBrand.Name + "菜单";
        Content = root;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }
    public async void ShowMenu()
    {
        if (IsOpen) { menu?.Hide(); return; }
        IsOpen = true;
        root.RequestedTheme = controller.Preferences.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        var point = NativeWindow.CursorPosition();
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(point.X, point.Y, 1, 1));
        AppWindow.Show();
        Activate();
        NativeWindow.Show(hwnd);
        menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        menu.Items.Add(Item("刷新电量", "\uE72C", controller.Refresh));
        var devices = new MenuFlyoutSubItem { Text = "显示的设备", Icon = new FontIcon { Glyph = "\uE772" } };
        foreach (var d in controller.ManagedDevices)
        {
            var toggle = new ToggleMenuFlyoutItem { Text = $"{d.Name} — {d.TooltipLevel}", IsChecked = !controller.Preferences.HiddenDevices.Contains(d.Id) };
            toggle.Click += (_, _) => controller.ToggleDevice(d.Id, toggle.IsChecked);
            devices.Items.Add(toggle);
        }
        if (devices.Items.Count == 0) devices.Items.Add(new MenuFlyoutItem { Text = "暂无在线设备", IsEnabled = false });
        menu.Items.Add(devices);
        menu.Items.Add(Item("设备开关与排序…", "\uE8CB", controller.ShowDeviceManagement));
        menu.Items.Add(new MenuFlyoutSeparator());
        var startup = new ToggleMenuFlyoutItem { Text = "开机启动", IsChecked = TrayController.StartupEnabled() };
        startup.Click += (_, _) => controller.SetStartup(startup.IsChecked);
        menu.Items.Add(startup);
        var ring = new ToggleMenuFlyoutItem { Text = "分段环形图标", IsChecked = controller.Preferences.RingIcons };
        ring.Click += (_, _) => { controller.Preferences.RingIcons = ring.IsChecked; controller.Save(); };
        menu.Items.Add(ring);
        var official = new ToggleMenuFlyoutItem { Text = "与官方驱动电量一致", IsChecked = controller.Preferences.PreferVendorDisplay };
        official.Click += (_, _) => { controller.Preferences.PreferVendorDisplay = official.IsChecked; controller.Save(); };
        menu.Items.Add(official);
        var alerts = new ToggleMenuFlyoutItem { Text = "低电量提醒", IsChecked = controller.Preferences.LowBatteryAlerts };
        alerts.Click += (_, _) => { controller.Preferences.LowBatteryAlerts = alerts.IsChecked; controller.Save(); };
        menu.Items.Add(alerts);
        var appearance = new MenuFlyoutSubItem { Text = "外观", Icon = new FontIcon { Glyph = "\uE790" } };
        foreach (var (label, value) in new[] { ("跟随系统", "System"), ("浅色", "Light"), ("深色", "Dark") })
        {
            var theme = new ToggleMenuFlyoutItem { Text = label, IsChecked = controller.Preferences.Theme == value };
            theme.Click += (_, _) => { controller.Preferences.Theme = value; controller.Save(); };
            appearance.Items.Add(theme);
        }
        menu.Items.Add(appearance);
        menu.Items.Add(Item("更多设置", "\uE713", controller.ShowSettings));
        menu.Items.Add(Item("支持设备", "\uE946", controller.ShowSupport));
        menu.Items.Add(Item("检查更新…", "\uE895", controller.CheckForUpdates));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("退出", "\uE7E8", controller.Quit));
        menu.Closed += (_, _) => { IsOpen = false; AppWindow.Hide(); };
        try
        {
            await Task.Delay(50);
            menu.ShowAt(root, new FlyoutShowOptions { Position = new Windows.Foundation.Point(0, 0), Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
        }
        catch (Exception ex)
        {
            IsOpen = false;
            AppWindow.Hide();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "menu-error.txt"), ex.ToString());
        }
    }
    public void HideMenu() => menu?.Hide();
    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }
}
