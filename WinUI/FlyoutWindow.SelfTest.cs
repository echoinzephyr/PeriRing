using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    internal async Task CaptureViewsAsync(bool preview = false)
    {
        pinned = true;
        ShowFlyout();
        foreach (string view in new[] { "devices", "manage", "settings", "battery-alerts", "device-detail", "diagnostics", "support", "updates" })
        {
            page = view switch
            {
                "diagnostics" => "settings",
                "device-detail" => DevicePagePrefix + "preview-mouse",
                _ => view
            };
            if (preview)
            {
                controller.Preferences.PopupHeight = view switch
                {
                    "devices" => 520, "settings" => 680, "battery-alerts" => 440, "device-detail" => 780,
                    "diagnostics" => 560, "manage" => 480, "support" => 620, _ => 520
                };
                PositionFlyout(controller.Preferences.PopupWidth, controller.Preferences.PopupHeight);
            }
            UpdateView();
            await Task.Delay(450);
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
            await Task.Delay(120);
            var managedIds = controller.ManagedDevices;
            if (managedIds.Count >= 2)
            {
                Check(!NamedButton($"上移 {managedIds[0].DisplayName}").IsEnabled
                    && !NamedButton($"下移 {managedIds[^1].DisplayName}").IsEnabled,
                    "Management page disables boundary move buttons");
            }
            var originalWidth = AppWindow.Size.Width;
            var originalHeight = AppWindow.Size.Height;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(340 * NativeWindow.Scale(hwnd)), originalHeight));
            const string testDevice = "preview-mouse";
            Navigate(DevicePagePrefix + testDevice);
            await Task.Delay(200);
            var aliasEditor = Descendants(root).OfType<TextBox>().Single(t => t.Header as string == "设备别名");
            aliasEditor.Text = "正在编辑";
            UpdateView();
            Check(Descendants(root).Contains(aliasEditor) && aliasEditor.Text == "正在编辑", "Background refresh preserves the alias draft");
            Check(Descendants(root).OfType<Grid>().Any(g => AutomationProperties.GetName(g) == "最近 7 天电量趋势图")
                && deviceDetailStatus!.Text.Contains("原始名称"), "Device details include the trend and original model at minimum width");
            Check(Descendants(root).OfType<TextBlock>().Any(t => t.Text == "上次充电")
                && Descendants(root).OfType<TextBlock>().Any(t => t.Text == "平均耗电"),
                "Device details display battery discharge statistics when continuous samples exist");
            Check(Descendants(root).OfType<TextBlock>().Any(t => t.Text == "多模连接关联"),
                "Device details expose multi-mode device linking controls");
            Invoke(NamedButton("切换趋势图到24 小时"));
            await Task.Delay(100);
            Check(Descendants(root).OfType<TextBlock>().Any(t => t.Text == "最近 24 小时")
                && Descendants(root).OfType<Grid>().Any(g => AutomationProperties.GetName(g) == "最近 7 天电量趋势图"),
                "Device details trend chart supports switching to 24-hour window");
            Invoke(NamedButton("切换趋势图到7 天"));
            await Task.Delay(100);
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
