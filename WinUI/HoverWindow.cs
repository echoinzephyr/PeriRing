using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BatteryDisplay;

internal sealed class HoverWindow : Window
{
    private readonly StackPanel list = new() { Spacing = 7 };
    private readonly Border container;
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly IntPtr hwnd;
    public bool IsVisible { get; private set; }

    public HoverWindow()
    {
        Title = AppBrand.DisplayName;
        container = new Border
        {
            Child = list,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) }
        };
        Content = container;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeWindow.HideFromSwitchers(AppWindow, hwnd);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        NativeWindow.Configure(hwnd);
        NativeWindow.NoActivate(hwnd);
    }

    public void ShowValues(List<Peripheral> devices, Preferences preferences)
    {
        container.RequestedTheme = preferences.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        bool dark = container.ActualTheme == ElementTheme.Dark;
        NativeWindow.SetDark(hwnd, dark);
        container.Background = new SolidColorBrush(dark ? Color.FromArgb(228, 30, 30, 30) : Color.FromArgb(234, 249, 249, 249));
        container.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));

        var accent = new SolidColorBrush(uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
        var lowBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 246, 102, 108) : Color.FromArgb(255, 214, 65, 72));
        var chargingBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 105, 206, 165) : Color.FromArgb(255, 26, 136, 98));
        var trackBrush = new SolidColorBrush(dark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));

        list.Children.Clear();
        if (devices.Count == 0)
        {
            var empty = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(new FontIcon { Glyph = "\uE702", FontSize = 13, Opacity = .65 });
            empty.Children.Add(new TextBlock { Text = "暂未取得在线设备的电量", FontSize = 12, Opacity = .75 });
            list.Children.Add(empty);
        }
        else
        {
            foreach (var d in devices)
            {
                bool isLow = preferences.IsLow(d.GaugeValue);
                bool isCharging = d.Connected && d.Charging;
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(new FontIcon
                {
                    Glyph = FlyoutWindow.DeviceGlyph(d),
                    FontSize = 13,
                    Opacity = d.IsCached ? .55 : .85,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var name = new TextBlock
                {
                    Text = d.DisplayName,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    MaxWidth = 175,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Opacity = d.IsCached ? .78 : 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                if (d.GaugeValue is int gauge)
                {
                    const double barWidth = 44;
                    var barHost = new Grid { Width = barWidth, Height = 5, VerticalAlignment = VerticalAlignment.Center };
                    barHost.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = trackBrush });
                    barHost.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(2.5),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Width = Math.Max(4, barWidth * Math.Clamp(gauge, 0, 100) / 100d),
                        Background = isLow ? lowBrush : isCharging ? chargingBrush : accent,
                        Opacity = d.IsCached ? .55 : 1
                    });
                    Grid.SetColumn(barHost, 2);
                    row.Children.Add(barHost);
                }

                var valuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                if (isCharging)
                    valuePanel.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 11, Foreground = chargingBrush, VerticalAlignment = VerticalAlignment.Center });
                var valueText = new TextBlock
                {
                    Text = d.TooltipLevel,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = d.IsCached ? .72 : 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (isLow) valueText.Foreground = lowBrush;
                else if (isCharging) valueText.Foreground = chargingBrush;
                valuePanel.Children.Add(valueText);
                Grid.SetColumn(valuePanel, 3);
                row.Children.Add(valuePanel);

                list.Children.Add(row);
            }
        }

        container.Measure(new Windows.Foundation.Size(380, double.PositiveInfinity));
        NativeWindow.Position(AppWindow, hwnd,
            Math.Clamp(Math.Ceiling(container.DesiredSize.Width) + 2, 160, 386),
            Math.Min(680, Math.Ceiling(container.DesiredSize.Height) + 2));
        container.Opacity = 1;
        AppWindow.Show(false);
        NativeWindow.ShowInactive(hwnd);
        IsVisible = true;
    }

    public void HideValues()
    {
        AppWindow.Hide();
        IsVisible = false;
    }
}
