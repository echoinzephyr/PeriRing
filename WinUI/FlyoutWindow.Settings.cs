using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    private void BuildSettings()
    {
        var iconStyle = new ComboBox
        {
            Width = 112,
            MinWidth = 0,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "分段圆环", "同心圆环", "环形点阵" },
            SelectedIndex = controller.Preferences.EffectiveTrayIconStyle switch { "Concentric" => 1, "DotRing" => 2, _ => 0 }
        };
        AutomationProperties.SetName(iconStyle, "托盘图标样式");
        iconStyle.SelectionChanged += (_, _) =>
        {
            if (updating || iconStyle.SelectedIndex < 0) return;
            controller.Preferences.SetTrayIconStyle(iconStyle.SelectedIndex switch { 1 => "Concentric", 2 => "DotRing", _ => "Segmented" });
            controller.Save();
        };
        body.Children.Add(SectionLabel("设备与显示", first: true));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE8CB", "设备开关与排序", "选择托盘显示项与排列优先级", ShowDeviceManagement),
            SettingsRow("\uEA3A", "托盘图标样式", "分段圆环、同心圆环或环形点阵（最低电量）", iconStyle),
            ToggleSetting("\uE790", "同心圆环分色", "各圈以不同颜色区分设备，低电量仍显示红色", controller.Preferences.ConcentricColorRings,
                value => { controller.Preferences.ConcentricColorRings = value; controller.Save(); }),
            ToggleSetting("\uE9F9", "与官方驱动一致", "采用已核实的厂商电量分档", controller.Preferences.PreferVendorDisplay,
                value => { controller.Preferences.PreferVendorDisplay = value; controller.Save(); })));

        var theme = new ComboBox { Width = 112, MinWidth = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = controller.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        AutomationProperties.SetName(theme, "外观");
        theme.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            controller.Preferences.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            controller.Save();
        };
        var interval = new ComboBox { Width = 112, MinWidth = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { "15 秒", "30 秒", "1 分钟", "5 分钟" }, SelectedIndex = Array.IndexOf(new[] { 15, 30, 60, 300 }, controller.Preferences.RefreshSeconds) };
        AutomationProperties.SetName(interval, "后台刷新间隔");
        if (interval.SelectedIndex < 0) interval.SelectedIndex = 2;
        interval.SelectionChanged += (_, _) => { if (!updating && interval.SelectedIndex >= 0) { controller.Preferences.RefreshSeconds = new[] { 15, 30, 60, 300 }[interval.SelectedIndex]; controller.Save(); } };
        body.Children.Add(SectionLabel("常规"));
        string alertState = controller.Preferences.LowBatteryAlerts ? "已开启" : "已关闭";
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE7E7", "低电量提醒", "电量过低时发送系统通知", ShowBatteryAlerts, $"{alertState} · 阈值 {controller.Preferences.LowBatteryThreshold}%"),
            ToggleSetting("\uE7E8", "开机启动", "登录后自动驻留系统托盘", TrayController.StartupEnabled(), controller.SetStartup),
            SettingsRow("\uE790", "外观", "控制浮层与菜单深浅配色", theme),
            SettingsRow("\uE72C", "后台刷新", "自动轮询设备电量的间隔", interval)));
        body.Children.Add(SectionLabel("关于"));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE946", "支持设备", "查看已接入品牌与读取渠道", ShowSupport),
            SettingsLink("\uE895", "检查更新", "查看新版本与自动更新设置", controller.CheckForUpdates, $"v{UpdateService.CurrentLabel}")));
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
        var threshold = new ComboBox { Width = 112, MinWidth = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ItemsSource = thresholds.Select(value => $"{value}%").ToArray(),
            SelectedIndex = Array.IndexOf(thresholds, controller.Preferences.LowBatteryThreshold) };
        AutomationProperties.SetName(threshold, "低电量阈值");
        threshold.SelectionChanged += (_, _) =>
        {
            if (!updating && threshold.SelectedIndex >= 0) controller.SetLowBatteryThreshold(thresholds[threshold.SelectedIndex]);
        };
        var notificationStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var statusCard = Card(notificationStatus);
        statusCard.Visibility = Visibility.Collapsed;
        body.Children.Add(SectionLabel("提醒规则", first: true));
        body.Children.Add(SettingsGroup(
            ToggleSetting("\uE7E7", "启用提醒", "使用 Windows 应用通知推送低电量警告", controller.Preferences.LowBatteryAlerts,
                controller.SetLowBatteryAlerts),
            SettingsRow("\uEBA7", "低电量阈值", "托盘图标与浮层同步采用此阈值", threshold)));
        body.Children.Add(SectionLabel("通知检查"));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE91C", "测试通知", "立即发送一条模拟通知以验证系统权限", () =>
            {
                notificationStatus.Text = controller.TestBatteryNotification();
                notificationStatus.Visibility = Visibility.Visible;
                statusCard.Visibility = Visibility.Visible;
            })));
        body.Children.Add(statusCard);
    }

    private void BuildManagement()
    {
        body.Children.Add(new TextBlock { Text = "打开开关选择要显示的设备，拖动条目调整优先级。\n分段圆环超过 5 台时显示 3 弧 + 点；同心圆环由外向内对应优先级，超过 3 台汇总到圆心点。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        var devices = controller.ManagedDevices;
        if (devices.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "读取到可显示电量的设备后，会出现在这里。", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
        }
        var list = new ListView { CanDragItems = true, CanReorderItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 8, 10, 8), MinHeight = 56 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "\uE700", FontSize = 13, Opacity = .45, VerticalAlignment = VerticalAlignment.Center });
            var name = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(DeviceDetailLink(d.Id, d.DisplayName, 13));
            name.Children.Add(new TextBlock { Text = $"{d.Level} · {(d.IsCached ? "上次读数" : d.Connected ? d.Transport : "当前未连接")}", FontSize = 11, Opacity = .62 });
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var enabled = new ToggleSwitch { IsOn = !controller.Preferences.HiddenDevices.Contains(d.Id), OnContent = "", OffContent = "", MinWidth = 0, Width = 45, VerticalAlignment = VerticalAlignment.Center };
            enabled.Toggled += (_, _) => { if (!updating) controller.ToggleDevice(d.Id, enabled.IsOn); };
            AutomationProperties.SetName(enabled, $"显示 {d.DisplayName}");
            Grid.SetColumn(enabled, 2); row.Children.Add(enabled);
            var up = IconButton("\uE70E", $"上移 {d.DisplayName}", () => controller.MoveDevice(d.Id, -1));
            up.Width = 28; up.Height = 28; up.VerticalAlignment = VerticalAlignment.Center;
            up.IsEnabled = i > 0;
            Grid.SetColumn(up, 3); row.Children.Add(up);
            var down = IconButton("\uE70D", $"下移 {d.DisplayName}", () => controller.MoveDevice(d.Id, 1));
            down.Width = 28; down.Height = 28; down.VerticalAlignment = VerticalAlignment.Center;
            down.IsEnabled = i < devices.Count - 1;
            Grid.SetColumn(down, 4); row.Children.Add(down);
            var itemCard = Card(row);
            itemCard.Padding = new Thickness(0);
            itemCard.Margin = new Thickness(0, 3, 0, 3);
            list.Items.Add(new ListViewItem
            {
                Content = itemCard,
                Tag = d.Id,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
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
}
