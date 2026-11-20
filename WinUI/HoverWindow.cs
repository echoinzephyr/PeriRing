using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BatteryDisplay;

internal sealed class HoverWindow : Window
{
    private readonly StackPanel list = new() { Spacing = 3, Padding = new Thickness(8, 5, 8, 5) };
    private readonly IntPtr hwnd;
    public bool IsVisible { get; private set; }
    public HoverWindow()
    {
        Title = AppBrand.DisplayName;
        Content = new Border { Child = list, CornerRadius = new CornerRadius(4) };
        SystemBackdrop = new DesktopAcrylicBackdrop();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
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
        list.RequestedTheme = preferences.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        bool dark = list.ActualTheme == ElementTheme.Dark;
        NativeWindow.SetDark(hwnd, dark);
        list.Background = new SolidColorBrush(dark ? Color.FromArgb(230, 30, 30, 30) : Color.FromArgb(230, 249, 249, 249));
        list.Children.Clear();
        if (devices.Count == 0) list.Children.Add(new TextBlock { Text = "暂未取得在线设备的电量", FontSize = 12, Opacity = .7 });
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var line = new TextBlock { Text = $"{d.DisplayName}: {d.TooltipLevel}", FontSize = 12, MaxWidth = 390, TextWrapping = TextWrapping.Wrap };
            if (preferences.IsLow(d.GaugeValue)) line.Foreground = new SolidColorBrush(Color.FromArgb(255, 246, 87, 94));
            list.Children.Add(line);
        }
        list.Measure(new Windows.Foundation.Size(406, double.PositiveInfinity));
        NativeWindow.Position(AppWindow, hwnd, Math.Clamp(Math.Ceiling(list.DesiredSize.Width) + 2, 80, 410), Math.Min(700, Math.Ceiling(list.DesiredSize.Height) + 2));
        AppWindow.Show(false);
        NativeWindow.ShowInactive(hwnd);
        IsVisible = true;
    }
    public void HideValues() { AppWindow.Hide(); IsVisible = false; }
}
