using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    private void BuildUpdates()
    {
        var hero = new Grid { ColumnSpacing = 12 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(BuildBrandMark());
        var brandInfo = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        brandInfo.Children.Add(new TextBlock { Text = AppBrand.DisplayName, FontSize = 16, FontWeight = FontWeights.SemiBold });
        brandInfo.Children.Add(new TextBlock { Text = $"当前版本 v{UpdateService.CurrentLabel}", FontSize = 12, Opacity = .68 });
        Grid.SetColumn(brandInfo, 1);
        hero.Children.Add(brandInfo);
        body.Children.Add(Card(hero));
        body.Children.Add(SectionLabel("更新方式"));
        body.Children.Add(SettingsGroup(ToggleSetting("\uE895", "自动更新", "安装前备份旧文件，新版启动失败时自动回退并保留个人数据", controller.Preferences.AutomaticUpdates,
            value => { controller.Preferences.AutomaticUpdates = value; controller.Save(); })));
        var result = controller.Update;
        bool busy = controller.CheckingUpdates || controller.InstallingUpdate;
        if (busy)
        {
            var statusGrid = new Grid { ColumnSpacing = 10 };
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusGrid.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center });
            var statusText = new TextBlock { Text = controller.UpdateStatus, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(statusText, 1);
            statusGrid.Children.Add(statusText);
            var cancel = new Button { Content = "取消", VerticalAlignment = VerticalAlignment.Center };
            cancel.Click += (_, _) => controller.CancelUpdate();
            Grid.SetColumn(cancel, 2);
            statusGrid.Children.Add(cancel);
            body.Children.Add(Card(statusGrid));
        }
        else if (result is not null)
        {
            body.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = !result.Success ? InfoBarSeverity.Warning
                    : result.Available ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
                Message = string.IsNullOrWhiteSpace(controller.UpdateStatus) ? result.Message : controller.UpdateStatus
            });
        }
        else if (!string.IsNullOrWhiteSpace(controller.UpdateStatus))
        {
            body.Children.Add(new TextBlock { Text = controller.UpdateStatus, FontSize = 12, Opacity = .72, TextWrapping = TextWrapping.Wrap });
        }
        if (!busy && result is { Success: true, Available: true })
        {
            var install = new Button
            {
                Content = $"立即更新到 v{result.LatestVersion}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = pinnedTintBrush,
                BorderBrush = pinnedBorderBrush
            };
            install.Click += (_, _) => controller.InstallAvailableUpdate();
            body.Children.Add(install);
        }
        var check = new Button { Content = controller.CheckingUpdates ? "正在检查…" : controller.InstallingUpdate ? "正在更新…" : "重新检查", IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Click += (_, _) => controller.CheckForUpdates();
        body.Children.Add(check);
        body.Children.Add(new HyperlinkButton { Content = "GitHub 发布页面", NavigateUri = new Uri(UpdateService.Repository + "/releases"), HorizontalAlignment = HorizontalAlignment.Left });
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
}
