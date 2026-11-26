using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    private void BuildDevices()
    {
        var devices = controller.PresentedDevices;
        if (controller.Error is not null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = controller.Error });
        var connected = devices.Where(d => d.IsPresent).ToList();
        int cached = connected.Count(d => d.IsCached);
        body.Children.Add(SectionLabel(cached == 0 ? $"已连接 · {connected.Count}" : $"已连接 · {connected.Count - cached}   上次读数 · {cached}", first: true));
        foreach (var d in connected) body.Children.Add(DeviceCard(d));
        if (connected.Count == 0)
        {
            var empty = new StackPanel { Spacing = 10, Padding = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "\uE702", FontSize = 34, Opacity = .45 });
            empty.Children.Add(new TextBlock { Text = controller.Refreshing ? "正在寻找你的外设" : "连接外设后，电量会显示在这里", FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "支持蓝牙及已适配的无线接收器", FontSize = 12, Opacity = .6, TextAlignment = TextAlignment.Center });
            body.Children.Add(Card(empty));
        }
        var other = devices.Where(d => !d.IsPresent).ToList();
        if (other.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var d in other) list.Children.Add(DeviceCard(d));
            var expander = new Expander
            {
                Header = $"未连接或暂未取得电量 · {other.Count}",
                Content = list,
                IsExpanded = disconnectedExpanded,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 0)
            };
            expander.Expanding += (_, _) => disconnectedExpanded = true;
            expander.Collapsed += (_, _) => disconnectedExpanded = false;
            body.Children.Add(expander);
        }
        var btContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        btContent.Children.Add(new FontIcon { Glyph = "\uE702", FontSize = 13, Opacity = .75 });
        btContent.Children.Add(new TextBlock { Text = "打开系统蓝牙设置", FontSize = 12, Opacity = .8 });
        var settings = new Button
        {
            Content = btContent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 12, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };
        settings.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
        body.Children.Add(settings);
    }

    private Border DeviceCard(Peripheral d)
    {
        bool isLow = d.IsPresent && controller.Preferences.IsLow(d.GaugeValue);
        bool isCharging = d.Connected && d.Charging;
        var container = new Grid { ColumnSpacing = 4 };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = new Grid { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new ProgressRing
        {
            IsIndeterminate = false,
            Value = d.CanShow ? d.GaugeValue ?? 0 : 0,
            Minimum = 0,
            Maximum = 100,
            Width = 42,
            Height = 42,
            Foreground = isLow ? lowBatteryBrush : isCharging ? chargingBrush : Accent,
            Opacity = !d.IsPresent ? .35 : d.CanShow ? 1 : .24
        });
        ring.Children.Add(new FontIcon { Glyph = DeviceGlyph(d), FontSize = 16, Opacity = d.IsPresent ? .88 : .45 });
        row.Children.Add(ring);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = d.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        string detail = d.IsCached ? d.Transport + " · 上次读数" : !d.Connected ? d.State : d.Transport + (d.Charging ? " · 充电中" : !d.CanShow ? " · 暂未取得电量" : "");
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Opacity = .62, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var levelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (isCharging)
            levelPanel.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 12, Foreground = chargingBrush, VerticalAlignment = VerticalAlignment.Center });
        var level = new TextBlock
        {
            Text = d.Level,
            FontSize = d.Level.Length > 6 ? 13 : 21,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = d.IsPresent ? (d.CanShow ? 1 : .5) : .45
        };
        if (isLow) level.Foreground = lowBatteryBrush;
        else if (isCharging) level.Foreground = chargingBrush;
        levelPanel.Children.Add(level);
        string levelDetails = d.VendorDisplay.HasValue ? $"接收器原始读数 {d.Battery}%\n官方驱动显示 {d.VendorDisplay}%（分档）" : d.Source;
        ToolTipService.SetToolTip(levelPanel, d.IsCached ? d.State + "\n" + levelDetails : levelDetails);
        Grid.SetColumn(levelPanel, 2);
        row.Children.Add(levelPanel);

        var mainButton = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 8, 8, 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(mainButton, $"查看 {d.DisplayName} 的设备详情");
        mainButton.Click += (_, _) => Navigate(DevicePagePrefix + d.Id);
        container.Children.Add(mainButton);

        bool isPinned = !controller.Preferences.HiddenDevices.Contains(d.Id);
        var pinIcon = new FontIcon { Glyph = "\uE718", FontSize = 13 };
        var toggle = new Button
        {
            Content = pinIcon,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        StylePinButton(toggle, pinIcon, isPinned);
        ToolTipService.SetToolTip(toggle, isPinned ? "托盘显示中" : "未在托盘显示");
        AutomationProperties.SetName(toggle, $"在托盘显示 {d.DisplayName}");
        toggle.Click += (_, _) =>
        {
            isPinned = !isPinned;
            StylePinButton(toggle, pinIcon, isPinned);
            ToolTipService.SetToolTip(toggle, isPinned ? "托盘显示中" : "未在托盘显示");
            controller.ToggleDevice(d.Id, isPinned);
        };
        Grid.SetColumn(toggle, 1);
        container.Children.Add(toggle);

        var card = Card(container);
        card.Padding = new Thickness(4);
        ToolTipService.SetToolTip(card, $"{d.DisplayName}\n{d.Source}\n{d.State}");
        return card;
    }

    private void StylePinButton(Button button, FontIcon icon, bool isPinned)
    {
        if (isPinned)
        {
            button.Background = pinnedTintBrush;
            button.BorderBrush = pinnedBorderBrush;
            icon.Foreground = pinnedIconBrush;
            icon.Opacity = 1;
        }
        else
        {
            button.Background = new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = cardBorder;
            icon.Foreground = mutedIconBrush;
            icon.Opacity = 1;
        }
    }

    internal static string DeviceGlyph(Peripheral d)
    {
        string name = (d.Name + " " + d.Source).ToLowerInvariant();
        if (new[] { "手柄", "xbox", "vader", "apex", "direwolf", "dual", "飞智", "controller", "gamepad", "xinput" }.Any(name.Contains)) return "\uE967";
        if (new[] { "keyboard", "rk61", "widow", "deathstalker", "键盘", "k65", "vk75", "vk87", "vk99", "valkyrie", "v75x", "sonix" }.Any(name.Contains)) return "\uE765";
        if (new[] { "head", "ear", "arctis", "cloud", "airpod", "wf-", "gamebuds", "v9 turbo", "耳机" }.Any(name.Contains)) return "\uE7F6";
        return "\uE962";
    }
}
