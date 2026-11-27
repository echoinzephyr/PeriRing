using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    private void BuildSettings()
    {
        var concentricArcs = new List<Microsoft.UI.Xaml.Shapes.Path>();
        void UpdateConcentricPreviewColors(bool colorful)
        {
            Brush[] brushes = colorful
                ? [previewRing1Brush, previewRing2Brush, previewRing3Brush]
                : [previewForegroundBrush, previewForegroundBrush, previewForegroundBrush];
            for (int i = 0; i < concentricArcs.Count && i < brushes.Length; i++)
                concentricArcs[i].Stroke = brushes[i];
        }

        var concentricColorRow = ToggleSetting("\uE790", "同心圆环分色", "按颜色区分各圈，低电量仍显示红色", controller.Preferences.ConcentricColorRings,
            value =>
            {
                controller.Preferences.ConcentricColorRings = value;
                UpdateConcentricPreviewColors(value);
                controller.Save();
            });
        var concentricColorDivider = new Border { Height = 1, Margin = new Thickness(56, 0, 14, 0), Background = separator };
        void UpdateConcentricRowVisibility(string style)
        {
            var vis = style == "Concentric" ? Visibility.Visible : Visibility.Collapsed;
            concentricColorRow.Visibility = vis;
            concentricColorDivider.Visibility = vis;
        }

        var styleCards = new Dictionary<string, (Button Button, TextBlock Title)>(StringComparer.Ordinal);
        void ApplyStyleSelection(string active)
        {
            foreach (var (key, card) in styleCards)
            {
                bool selected = key == active;
                card.Button.Background = selected ? pinnedTintBrush : iconBadgeBackground;
                card.Button.BorderBrush = selected ? pinnedBorderBrush : cardBorder;
                card.Button.BorderThickness = new Thickness(selected ? 1.5 : 1);
                card.Title.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium;
            }
            UpdateConcentricRowVisibility(active);
        }

        var stylePickerHeader = new Grid { ColumnSpacing = 8 };
        stylePickerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stylePickerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stylePickerHeader.Children.Add(new FontIcon { Glyph = "\uEA3A", FontSize = 13, Opacity = .75, VerticalAlignment = VerticalAlignment.Center });
        var styleHeading = new TextBlock { Text = "托盘图标样式", FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(styleHeading, 1);
        stylePickerHeader.Children.Add(styleHeading);

        var styleGrid = new Grid { ColumnSpacing = 8 };
        var styleSpecs = new (string Key, string Label, string Hint, Canvas Preview)[]
        {
            ("Segmented", "分段圆环", "多设备弧", BuildSegmentedPreview()),
            ("Concentric", "同心圆环", "一圈一台", BuildConcentricPreview(concentricArcs)),
            ("DotRing", "环形点阵", "最低电量", BuildDotRingPreview())
        };
        for (int i = 0; i < styleSpecs.Length; i++)
        {
            styleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var spec = styleSpecs[i];
            var stack = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(spec.Preview);
            var titleText = new TextBlock { Text = spec.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            var hintText = new TextBlock { Text = spec.Hint, FontSize = 10.5, Opacity = .58, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(titleText);
            stack.Children.Add(hintText);
            var button = new Button
            {
                Content = stack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(6, 10, 6, 8),
                CornerRadius = new CornerRadius(8),
                MinWidth = 0
            };
            AutomationProperties.SetName(button, $"托盘样式：{spec.Label}");
            string styleKey = spec.Key;
            button.Click += (_, _) =>
            {
                if (updating || controller.Preferences.EffectiveTrayIconStyle == styleKey) return;
                controller.Preferences.SetTrayIconStyle(styleKey);
                ApplyStyleSelection(styleKey);
                controller.Save();
            };
            styleCards[spec.Key] = (button, titleText);
            Grid.SetColumn(button, i);
            styleGrid.Children.Add(button);
        }
        UpdateConcentricPreviewColors(controller.Preferences.ConcentricColorRings);
        ApplyStyleSelection(controller.Preferences.EffectiveTrayIconStyle);

        var stylePickerBlock = new StackPanel { Spacing = 10, Padding = new Thickness(14, 12, 14, 12) };
        stylePickerBlock.Children.Add(stylePickerHeader);
        stylePickerBlock.Children.Add(styleGrid);

        var managed = controller.ManagedDevices;
        int visibleManaged = managed.Count(d => !controller.Preferences.HiddenDevices.Contains(d.Id));
        string deviceSummary = managed.Count > 0 ? $"已显示 {visibleManaged}/{managed.Count}" : "暂无设备";

        var displayGroupContent = new StackPanel { Padding = new Thickness(0, 2, 0, 2) };
        displayGroupContent.Children.Add(stylePickerBlock);
        displayGroupContent.Children.Add(concentricColorDivider);
        displayGroupContent.Children.Add(concentricColorRow);
        displayGroupContent.Children.Add(new Border { Height = 1, Margin = new Thickness(56, 0, 14, 0), Background = separator });
        displayGroupContent.Children.Add(SettingsLink("\uE8CB", "设备开关与排序", "选择显示项与优先级", ShowDeviceManagement, deviceSummary));
        displayGroupContent.Children.Add(new Border { Height = 1, Margin = new Thickness(56, 0, 14, 0), Background = separator });
        displayGroupContent.Children.Add(ToggleSetting("\uE9F9", "与官方驱动一致", "采用已核实的厂商分档", controller.Preferences.PreferVendorDisplay,
            value => { controller.Preferences.PreferVendorDisplay = value; controller.Save(); }));
        var displayGroup = Card(displayGroupContent);
        displayGroup.Padding = new Thickness(0);

        body.Children.Add(SectionLabel("托盘与显示", first: true));
        body.Children.Add(displayGroup);

        var themeSwitcher = BuildThemeSwitcher();
        var interval = new ComboBox { Width = 96, MinWidth = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { "15 秒", "30 秒", "1 分钟", "5 分钟" }, SelectedIndex = Array.IndexOf(new[] { 15, 30, 60, 300 }, controller.Preferences.RefreshSeconds) };
        AutomationProperties.SetName(interval, "后台刷新间隔");
        if (interval.SelectedIndex < 0) interval.SelectedIndex = 2;
        interval.SelectionChanged += (_, _) => { if (!updating && interval.SelectedIndex >= 0) { controller.Preferences.RefreshSeconds = new[] { 15, 30, 60, 300 }[interval.SelectedIndex]; controller.Save(); } };

        body.Children.Add(SectionLabel("常规"));
        string alertState = controller.Preferences.LowBatteryAlerts ? "已开启" : "已关闭";
        body.Children.Add(SettingsGroup(
            SettingsRow("\uE790", "外观", "浮层与菜单配色", themeSwitcher),
            SettingsLink("\uE7E7", "低电量提醒", "过低时发送系统通知", ShowBatteryAlerts, $"{alertState} · 阈值 {controller.Preferences.LowBatteryThreshold}%"),
            ToggleSetting("\uE7E8", "开机启动", "登录后驻留系统托盘", TrayController.StartupEnabled(), controller.SetStartup),
            SettingsRow("\uE72C", "后台刷新", "自动轮询电量间隔", interval)));

        body.Children.Add(SectionLabel("支持与诊断"));
        body.Children.Add(SettingsGroup(
            SettingsLink("\uE946", "支持设备", "已接入品牌与读取渠道", ShowSupport),
            SettingsLink("\uEBE8", "诊断与日志", "Debug 日志与诊断包", ShowDiagnostics, controller.Preferences.DebugMode ? "Debug 已开" : null),
            SettingsLink("\uE895", "检查更新", "新版本与自动更新", controller.CheckForUpdates, $"v{UpdateService.CurrentLabel}")));
    }

    private Border BuildThemeSwitcher()
    {
        var grid = new Grid { ColumnSpacing = 2 };
        var items = new (string Key, string Label, string AutoName)[]
        {
            ("System", "系统", "外观：跟随系统"),
            ("Light", "浅色", "外观：浅色"),
            ("Dark", "深色", "外观：深色")
        };
        var buttons = new Dictionary<string, Button>(StringComparer.Ordinal);
        void RefreshState(string active)
        {
            foreach (var (key, btn) in buttons)
            {
                bool selected = key == active;
                btn.Background = selected ? pinnedTintBrush : new SolidColorBrush(Colors.Transparent);
                btn.BorderBrush = selected ? pinnedBorderBrush : new SolidColorBrush(Colors.Transparent);
                btn.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                btn.Opacity = selected ? 1 : .75;
            }
        }
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var item = items[i];
            var btn = new Button
            {
                Content = item.Label,
                FontSize = 12,
                Padding = new Thickness(9, 4, 9, 4),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1)
            };
            AutomationProperties.SetName(btn, item.AutoName);
            string themeKey = item.Key;
            btn.Click += (_, _) =>
            {
                if (updating || controller.Preferences.Theme == themeKey) return;
                controller.Preferences.Theme = themeKey;
                RefreshState(themeKey);
                controller.Save();
            };
            buttons[item.Key] = btn;
            Grid.SetColumn(btn, i);
            grid.Children.Add(btn);
        }
        RefreshState(controller.Preferences.Theme);
        var container = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2),
            Background = iconBadgeBackground,
            BorderBrush = cardBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = grid
        };
        AutomationProperties.SetName(container, "外观");
        return container;
    }

    private static Microsoft.UI.Xaml.Shapes.Path ArcPath(double cx, double cy, double radius, double startDeg, double sweepDeg, Brush stroke, double thickness)
    {
        double clamped = Math.Clamp(sweepDeg, 0.5, 359.5);
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = (startDeg + clamped) * Math.PI / 180.0;
        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad)),
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(cx + radius * Math.Cos(endRad), cy + radius * Math.Sin(endRad)),
            Size = new Windows.Foundation.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = clamped > 180
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    private Canvas BuildSegmentedPreview()
    {
        var canvas = new Canvas { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
        const double cx = 16, cy = 16, r = 11.4, stroke = 3.2;
        foreach (var (start, sweep, progress) in new[] { (-72d, 144d, 118d), (108d, 144d, 90d) })
        {
            canvas.Children.Add(ArcPath(cx, cy, r, start, sweep, previewTrackBrush, stroke));
            canvas.Children.Add(ArcPath(cx, cy, r, start, progress, previewForegroundBrush, stroke));
        }
        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection { new(16, 11), new(21, 16), new(16, 21), new(11, 16) },
            Fill = previewForegroundBrush
        });
        return canvas;
    }

    private Canvas BuildConcentricPreview(List<Microsoft.UI.Xaml.Shapes.Path> arcs)
    {
        var canvas = new Canvas { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
        const double cx = 16, cy = 16;
        foreach (var (r, thickness, sweep) in new[] { (12.0d, 2.5d, 275d), (7.8d, 2.5d, 205d), (3.8d, 2.4d, 290d) })
        {
            var track = new Ellipse { Width = r * 2, Height = r * 2, Stroke = previewTrackBrush, StrokeThickness = thickness };
            Canvas.SetLeft(track, cx - r);
            Canvas.SetTop(track, cy - r);
            canvas.Children.Add(track);
            var progress = ArcPath(cx, cy, r, -90, sweep, previewForegroundBrush, thickness);
            arcs.Add(progress);
            canvas.Children.Add(progress);
        }
        return canvas;
    }

    private Canvas BuildDotRingPreview()
    {
        var canvas = new Canvas { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
        const double cx = 16, cy = 16, ringR = 10.8;
        const double exact = 5.75;
        for (int i = 0; i < 8; i++)
        {
            double angle = (-90 + i * 45) * Math.PI / 180.0;
            double frac = Math.Clamp(exact - i, 0, 1);
            double r = 1.7 + frac * 0.95;
            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = frac > 0.02 ? previewForegroundBrush : previewTrackBrush,
                Opacity = frac > 0.02 ? 0.30 + frac * 0.70 : 1
            };
            Canvas.SetLeft(dot, cx + ringR * Math.Cos(angle) - r);
            Canvas.SetTop(dot, cy + ringR * Math.Sin(angle) - r);
            canvas.Children.Add(dot);
        }
        const double coreR = 2.8;
        var core = new Ellipse { Width = coreR * 2, Height = coreR * 2, Fill = previewForegroundBrush };
        Canvas.SetLeft(core, cx - coreR);
        Canvas.SetTop(core, cy - coreR);
        canvas.Children.Add(core);
        return canvas;
    }

    private void BuildDiagnostics()
    {
        body.Children.Add(SectionLabel("日志记录", first: true));
        body.Children.Add(SettingsGroup(
            ToggleSetting("\uEBE8", "Debug 模式", "记录扫描过程与错误，仅保存在本机", controller.Preferences.DebugMode,
                value => { controller.Preferences.DebugMode = value; controller.Save(); })));
        body.Children.Add(SectionLabel("诊断数据"));
        body.Children.Add(SettingsGroup(
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
