using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace BatteryDisplay;

internal sealed partial class FlyoutWindow
{
    private const string DevicePagePrefix = "device:";
    private bool IsDeviceDetail => page.StartsWith(DevicePagePrefix, StringComparison.Ordinal);
    private string DeviceDetailId => page[DevicePagePrefix.Length..];
    private TextBlock? deviceDetailName;
    private TextBlock? deviceDetailStatus;
    private StackPanel? deviceDetailHistory;
    private (int Count, BatterySample? First, BatterySample? Last, bool PreferVendor)? renderedHistory;

    private List<BatteryJournalDevice> HistoricalDevices()
    {
        var managed = controller.ManagedDevices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        return controller.JournalDevices.Where(device => !managed.Contains(device.Id)).ToList();
    }

    private string JournalDisplayName(BatteryJournalDevice device) => controller.Preferences.DeviceAliases.GetValueOrDefault(device.Id) ?? device.Name;

    private HyperlinkButton DeviceDetailLink(string id, string name, int fontSize)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = name, FontSize = fontSize, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis }, Padding = new Thickness(0), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(link, $"查看 {name} 的设备详情");
        link.Click += (_, _) => Navigate(DevicePagePrefix + id);
        return link;
    }

    private void BuildDeviceDetail()
    {
        string id = DeviceDetailId;
        deviceDetailName = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        deviceDetailStatus = new TextBlock { FontSize = 12, Opacity = .72, TextWrapping = TextWrapping.Wrap };
        var overview = new StackPanel { Spacing = 5 };
        overview.Children.Add(deviceDetailName);
        overview.Children.Add(deviceDetailStatus);
        body.Children.Add(Card(overview));

        if (Peripheral.SupportsAlias(id)) body.Children.Add(Card(BuildAliasEditor(id)));
        else body.Children.Add(Card(new TextBlock { Text = "此手柄接口只提供临时编号，无法为具体手柄保存别名。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap }));

        deviceDetailHistory = new StackPanel { Spacing = 8 };
        body.Children.Add(deviceDetailHistory);
        renderedHistory = null;
        UpdateDeviceDetail();
    }

    private StackPanel BuildAliasEditor(string id)
    {
        var editor = new StackPanel { Spacing = 8 };
        var alias = new TextBox { Header = "设备别名", PlaceholderText = "使用原始名称", MaxLength = Preferences.MaxAliasLength,
            Text = controller.Preferences.DeviceAliases.GetValueOrDefault(id, "") };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "保存" };
        var reset = new Button { Content = "恢复原名" };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void SaveAlias(string? value)
        {
            status.Visibility = Visibility.Visible;
            string? normalized = Preferences.NormalizeAlias(value);
            if (!string.IsNullOrWhiteSpace(value) && normalized is null)
            {
                status.Text = "别名包含不支持的字符。";
                return;
            }
            if (!controller.SetDeviceAlias(id, normalized))
            {
                status.Text = "无法保存设备别名，请检查程序目录的写入权限。";
                return;
            }
            alias.Text = normalized ?? "";
            status.Text = normalized is null ? "已恢复原名。" : "已保存别名。";
        }
        save.Click += (_, _) => SaveAlias(alias.Text);
        reset.Click += (_, _) => SaveAlias(null);
        commands.Children.Add(save);
        commands.Children.Add(reset);
        editor.Children.Add(alias);
        editor.Children.Add(commands);
        editor.Children.Add(status);
        return editor;
    }

    private void UpdateDeviceDetail()
    {
        if (deviceDetailName is null || deviceDetailStatus is null || deviceDetailHistory is null) return;
        string id = DeviceDetailId;
        var current = controller.PresentedDevices.Find(device => device.Id == id);
        var history = controller.JournalDevices.Find(device => device.Id == id);
        string original = current?.Name ?? history?.Name ?? deviceDetailName.Tag as string ?? "设备";
        deviceDetailName.Tag = original;
        deviceDetailName.Text = current?.DisplayName ?? controller.Preferences.DeviceAliases.GetValueOrDefault(id) ?? original;
        string transport = current?.Transport ?? history?.Transport ?? "";
        string state = current is null ? "当前未连接" : $"{current.Level} · {current.State}";
        var samples = history?.Samples.ToArray() ?? [];
        var latest = samples.LastOrDefault();
        var readTime = current?.BatteryUpdatedAt ?? latest?.Time;
        string reading = readTime is DateTimeOffset time ? $"\n{(current?.BatteryUpdatedAt is null ? "最后记录" : "最后读数")} {time.ToLocalTime():MM-dd HH:mm:ss}" : "";
        deviceDetailStatus.Text = $"原始名称 · {original}\n{(transport.Length > 0 ? transport + " · " : "")}{state}{reading}";

        bool preferVendor = controller.Preferences.PreferVendorDisplay;
        var revision = (samples.Length, samples.FirstOrDefault(), latest, preferVendor);
        if (renderedHistory == revision) return;
        renderedHistory = revision;
        deviceDetailHistory.Children.Clear();
        deviceDetailHistory.Children.Add(SectionLabel("最近 7 天"));
        if (samples.Length == 0)
        {
            deviceDetailHistory.Children.Add(Card(new TextBlock { Text = "取得有效的电量百分比后会开始记录。", FontSize = 12, TextWrapping = TextWrapping.Wrap }));
            return;
        }
        deviceDetailHistory.Children.Add(Card(BuildHistoryChart(samples, preferVendor)));
        deviceDetailHistory.Children.Add(SectionLabel("最近记录"));
        var recent = new StackPanel { Spacing = 9 };
        foreach (var sample in samples.Reverse().Take(10))
        {
            string value = $"{sample.Level(preferVendor)}% · {(sample.Charging ? "充电中" : "未充电")}";
            recent.Children.Add(new TextBlock { Text = $"{sample.Time.ToLocalTime():MM-dd HH:mm}    {value}", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        deviceDetailHistory.Children.Add(Card(recent));
    }

    private Grid BuildHistoryChart(BatterySample[] samples, bool preferVendor)
    {
        DateTimeOffset end = DateTimeOffset.Now;
        DateTimeOffset start = end - BatteryJournal.Retention;
        var chart = new Grid { RowSpacing = 6, ColumnSpacing = 8 };
        AutomationProperties.SetName(chart, "最近 7 天电量趋势图");
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (int level in new[] { 100, 50, 0 })
            chart.Children.Add(new TextBlock { Text = $"{level}%", FontSize = 10, Opacity = .65,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = level switch
                { 100 => VerticalAlignment.Top, 50 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom } });

        var plot = new Canvas { Height = 108 };
        Grid.SetColumn(plot, 1);
        chart.Children.Add(plot);
        plot.SizeChanged += (_, _) =>
        {
            plot.Children.Clear();
            double width = Math.Max(0, plot.ActualWidth - 6);
            double height = plot.Height - 6;
            foreach (double y in new[] { 3, 3 + height / 2, 3 + height })
                plot.Children.Add(new Line { X1 = 3, X2 = width + 3, Y1 = y, Y2 = y, Stroke = separator, StrokeThickness = 1 });
            double lastX = 0, lastY = 0;
            var ink = Accent;
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                double x = 3 + width * Math.Clamp((sample.Time - start).TotalDays / BatteryJournal.Retention.TotalDays, 0, 1);
                double y = 3 + height * (1 - sample.Level(preferVendor) / 100d);
                if (i > 0 && !sample.StartsSegment)
                    plot.Children.Add(new Line { X1 = lastX, X2 = x, Y1 = lastY, Y2 = y, Stroke = ink, StrokeThickness = 2 });
                if (sample.StartsSegment || i == 0 || i == samples.Length - 1 || samples[i + 1].StartsSegment)
                {
                    var point = new Ellipse { Width = 5, Height = 5, Fill = ink };
                    Canvas.SetLeft(point, x - 2.5);
                    Canvas.SetTop(point, y - 2.5);
                    plot.Children.Add(point);
                }
                lastX = x;
                lastY = y;
            }
        };
        var from = new TextBlock { Text = $"{start:MM-dd}", FontSize = 10, Opacity = .65 };
        var to = new TextBlock { Text = $"{end:MM-dd}", FontSize = 10, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var date in new[] { from, to })
        {
            Grid.SetRow(date, 1);
            Grid.SetColumn(date, 1);
            chart.Children.Add(date);
        }
        return chart;
    }
}
