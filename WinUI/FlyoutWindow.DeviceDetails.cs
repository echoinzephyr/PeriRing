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
    private const string DevicePagePrefix = "device:";
    private bool IsDeviceDetail => page.StartsWith(DevicePagePrefix, StringComparison.Ordinal);
    private string DeviceDetailId => page[DevicePagePrefix.Length..];
    private TimeSpan historyWindow = BatteryJournal.Retention;
    private TextBlock? deviceDetailName;
    private TextBlock? deviceDetailStatus;
    private StackPanel? deviceDetailLinks;
    private StackPanel? deviceDetailHistory;
    private (int Count, BatterySample? First, BatterySample? Last, bool PreferVendor, TimeSpan Window, int Threshold, int LinkCount)? renderedHistory;

    private List<BatteryJournalDevice> HistoricalDevices()
    {
        var managed = controller.ManagedDevices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        return controller.JournalDevices
            .Where(device => !controller.Preferences.DeviceLinks.ContainsKey(device.Id) && !managed.Contains(controller.Preferences.ResolveCanonicalId(device.Id)))
            .ToList();
    }

    private string JournalDisplayName(BatteryJournalDevice device) => controller.Preferences.DeviceAliases.GetValueOrDefault(device.Id) ?? device.Name;

    private Button DeviceDetailLink(string id, string name, int fontSize)
    {
        var link = new Button
        {
            Content = new TextBlock { Text = name, FontSize = fontSize, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(-4, -2, 0, -2),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
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

        if (Peripheral.SupportsAlias(id))
        {
            body.Children.Add(Card(BuildAliasEditor(id)));
            deviceDetailLinks = new StackPanel { Spacing = 8 };
            body.Children.Add(deviceDetailLinks);
        }
        else
        {
            deviceDetailLinks = null;
            body.Children.Add(Card(new TextBlock
            {
                Text = "此手柄接口只提供临时编号，无法为具体手柄保存别名或关联多模连接。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            }));
        }

        deviceDetailHistory = new StackPanel { Spacing = 8 };
        body.Children.Add(deviceDetailHistory);
        renderedHistory = null;
        UpdateDeviceDetail();
    }

    private StackPanel BuildAliasEditor(string id)
    {
        string savedAlias = controller.Preferences.DeviceAliases.GetValueOrDefault(id, "");
        var editor = new StackPanel { Spacing = 8 };
        var alias = new TextBox
        {
            Header = "设备别名",
            PlaceholderText = "使用原始名称（按 Enter 保存）",
            MaxLength = Preferences.MaxAliasLength,
            Text = savedAlias
        };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "保存" };
        var reset = new Button { Content = "恢复原名" };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void UpdateCommandStates()
        {
            string draft = alias.Text.Trim();
            save.IsEnabled = !string.Equals(draft, savedAlias, StringComparison.Ordinal);
            reset.IsEnabled = savedAlias.Length > 0;
        }
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
            savedAlias = normalized ?? "";
            alias.Text = savedAlias;
            UpdateCommandStates();
            status.Text = normalized is null ? "已恢复原名。" : "已保存别名。";
        }
        alias.TextChanged += (_, _) => UpdateCommandStates();
        alias.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && save.IsEnabled)
            {
                SaveAlias(alias.Text);
                e.Handled = true;
            }
        };
        save.Click += (_, _) => SaveAlias(alias.Text);
        reset.Click += (_, _) => SaveAlias(null);
        UpdateCommandStates();
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
        string id = controller.Preferences.ResolveCanonicalId(DeviceDetailId);
        var current = controller.PresentedDevices.Find(device => device.Id == id);
        var history = controller.JournalDevices.Find(device => device.Id == id);
        string original = current?.Name ?? history?.Name ?? deviceDetailName.Tag as string ?? "设备";
        deviceDetailName.Tag = original;
        deviceDetailName.Text = current?.DisplayName ?? controller.Preferences.DeviceAliases.GetValueOrDefault(id) ?? original;
        string transport = current?.Transport ?? history?.Transport ?? "";
        string state = current is null ? "当前未连接" : $"{current.Level} · {current.State}";
        var samples = controller.MergedSamplesFor(id).ToArray();
        var latest = samples.LastOrDefault();
        var readTime = current?.BatteryUpdatedAt ?? latest?.Time;
        string reading = readTime is DateTimeOffset time ? $"\n{(current?.BatteryUpdatedAt is null ? "最后记录" : "最后读数")} {time.ToLocalTime():MM-dd HH:mm:ss}" : "";
        var linkedSecondaries = controller.Preferences.LinkedIdsFor(id).Where(linkedId => linkedId != id).ToArray();
        string linkedNote = linkedSecondaries.Length > 0 ? $"\n已关联 {linkedSecondaries.Length} 个同设备连接" : "";
        deviceDetailStatus.Text = $"原始名称 · {original}\n{(transport.Length > 0 ? transport + " · " : "")}{state}{reading}{linkedNote}";

        bool preferVendor = controller.Preferences.PreferVendorDisplay;
        int threshold = controller.Preferences.LowBatteryThreshold;
        var revision = (samples.Length, samples.FirstOrDefault(), latest, preferVendor, historyWindow, threshold, linkedSecondaries.Length);
        if (renderedHistory == revision) return;
        renderedHistory = revision;
        RebuildDeviceLinkSection(id, linkedSecondaries);
        deviceDetailHistory.Children.Clear();

        var headerRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(SectionLabel(historyWindow <= TimeSpan.FromHours(24) ? "最近 24 小时" : "最近 7 天", first: true));
        if (samples.Length > 0)
        {
            var rangeSwitch = BuildHistoryRangeSwitch();
            Grid.SetColumn(rangeSwitch, 1);
            headerRow.Children.Add(rangeSwitch);
        }
        deviceDetailHistory.Children.Add(headerRow);

        if (samples.Length == 0)
        {
            deviceDetailHistory.Children.Add(Card(new TextBlock { Text = "取得有效的电量百分比后会开始记录。", FontSize = 12, TextWrapping = TextWrapping.Wrap }));
            return;
        }
        deviceDetailHistory.Children.Add(Card(BuildHistoryChart(samples, preferVendor, historyWindow, threshold)));
        var stats = BatteryJournal.Summarize(samples, preferVendor);
        if (BuildBatteryStatsCard(stats) is { } statsCard)
            deviceDetailHistory.Children.Add(statsCard);
        deviceDetailHistory.Children.Add(SectionLabel("最近记录"));
        var recent = new StackPanel { Spacing = 8 };
        foreach (var sample in samples.Reverse().Take(10))
        {
            int level = sample.Level(preferVendor);
            var itemRow = new Grid { ColumnSpacing = 8 };
            itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemRow.Children.Add(new TextBlock { Text = $"{sample.Time.ToLocalTime():MM-dd HH:mm}", FontSize = 12, Opacity = .72, VerticalAlignment = VerticalAlignment.Center });
            var valueText = new TextBlock
            {
                Text = $"{level}% · {(sample.Charging ? "充电中" : "未充电")}",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (level <= threshold) valueText.Foreground = lowBatteryBrush;
            else if (sample.Charging) valueText.Foreground = chargingBrush;
            Grid.SetColumn(valueText, 1);
            itemRow.Children.Add(valueText);
            recent.Children.Add(itemRow);
        }
        deviceDetailHistory.Children.Add(Card(recent));
    }

    private void RebuildDeviceLinkSection(string canonicalId, string[] linkedSecondaries)
    {
        if (deviceDetailLinks is null) return;
        deviceDetailLinks.Children.Clear();
        var candidates = controller.LinkableDevicesFor(canonicalId);
        if (linkedSecondaries.Length == 0 && candidates.Count == 0) return;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "多模连接关联", FontSize = 13, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "可将同一外设的 USB、2.4G 或蓝牙条目合并，共享别名、排序与 7 天电量曲线。",
            FontSize = 11.5,
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });

        foreach (string secondaryId in linkedSecondaries)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = controller.DescribeDeviceId(secondaryId),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var unlink = new Button { Content = "解除关联", FontSize = 11.5, Padding = new Thickness(8, 3, 8, 3), MinHeight = 0 };
            unlink.Click += (_, _) =>
            {
                controller.UnlinkDevice(secondaryId);
                renderedHistory = null;
                UpdateDeviceDetail();
            };
            Grid.SetColumn(unlink, 1);
            row.Children.Add(unlink);
            content.Children.Add(row);
        }

        if (candidates.Count > 0)
        {
            var addRow = new Grid { ColumnSpacing = 8 };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var picker = new ComboBox
            {
                PlaceholderText = "选择同一设备的其他连接",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12,
                ItemsSource = candidates.Select(c => c.Label).ToArray()
            };
            var linkButton = new Button { Content = "关联", FontSize = 12, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            picker.SelectionChanged += (_, _) => linkButton.IsEnabled = picker.SelectedIndex >= 0;
            linkButton.Click += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || picker.SelectedIndex >= candidates.Count) return;
                if (controller.LinkDevice(candidates[picker.SelectedIndex].Id, canonicalId))
                {
                    renderedHistory = null;
                    UpdateDeviceDetail();
                }
            };
            addRow.Children.Add(picker);
            Grid.SetColumn(linkButton, 1);
            addRow.Children.Add(linkButton);
            content.Children.Add(addRow);
        }

        deviceDetailLinks.Children.Add(Card(content));
    }

    private Border? BuildBatteryStatsCard(BatteryStats stats)
    {
        var metrics = new List<(string Label, string Value)>();
        if (stats.LastChargedAt is DateTimeOffset chargedAt)
            metrics.Add(("上次充电", chargedAt.ToLocalTime().ToString("MM-dd HH:mm")));
        if (stats.DischargeRatePerHour is double rate)
            metrics.Add(("平均耗电", rate >= 1 ? $"约 {rate:0.0}% / 时" : $"约 {rate * 24:0}% / 天"));
        if (stats.EstimatedRemaining is TimeSpan remaining)
            metrics.Add(("保守续航", remaining.TotalHours >= 36 ? $"约 {remaining.TotalDays:0.0} 天" : $"约 {Math.Max(1, (int)Math.Round(remaining.TotalHours))} 小时"));
        if (metrics.Count == 0) return null;

        var grid = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < metrics.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(new TextBlock { Text = metrics[i].Label, FontSize = 11, Opacity = .62 });
            item.Children.Add(new TextBlock { Text = metrics[i].Value, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(item, i);
            grid.Children.Add(item);
        }
        return Card(grid);
    }

    private Border BuildHistoryRangeSwitch()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Button RangeButton(string label, TimeSpan window)
        {
            bool active = historyWindow == window;
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Background = active ? pinnedTintBrush : new SolidColorBrush(Colors.Transparent),
                BorderBrush = active ? pinnedBorderBrush : new SolidColorBrush(Colors.Transparent),
                Opacity = active ? 1 : .7
            };
            AutomationProperties.SetName(btn, $"切换趋势图到{label}");
            btn.Click += (_, _) =>
            {
                if (historyWindow == window) return;
                historyWindow = window;
                UpdateDeviceDetail();
            };
            return btn;
        }
        panel.Children.Add(RangeButton("24 小时", TimeSpan.FromHours(24)));
        panel.Children.Add(RangeButton("7 天", BatteryJournal.Retention));
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Background = iconBadgeBackground,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel
        };
    }

    private Grid BuildHistoryChart(BatterySample[] allSamples, bool preferVendor, TimeSpan window, int lowThreshold)
    {
        DateTimeOffset end = DateTimeOffset.Now;
        DateTimeOffset start = end - window;
        var samples = allSamples.Where(sample => sample.Time >= start).ToArray();
        var chart = new Grid { RowSpacing = 6, ColumnSpacing = 8 };
        AutomationProperties.SetName(chart, "最近 7 天电量趋势图");
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (samples.Length == 0)
        {
            var empty = new TextBlock { Text = "最近 24 小时内暂无记录，可切换到 7 天查看。", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumnSpan(empty, 2);
            chart.Children.Add(empty);
            return chart;
        }

        foreach (int level in new[] { 100, 50, 0 })
            chart.Children.Add(new TextBlock
            {
                Text = $"{level}%",
                FontSize = 10,
                Opacity = .65,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = level switch { 100 => VerticalAlignment.Top, 50 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom }
            });

        var plot = new Canvas
        {
            Height = 116,
            Background = new SolidColorBrush(Colors.Transparent),
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) }
        };
        Grid.SetColumn(plot, 1);
        chart.Children.Add(plot);

        var hoverReadout = new TextBlock
        {
            Text = $"低电量线 {lowThreshold}% · 悬停曲线查看详细读数",
            FontSize = 11,
            Opacity = .62,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(hoverReadout, 2);
        Grid.SetColumn(hoverReadout, 1);
        chart.Children.Add(hoverReadout);

        (double X, double Y, BatterySample Sample)[] plotted = [];
        Line? hoverLine = null;
        Ellipse? hoverDot = null;

        plot.SizeChanged += (_, _) =>
        {
            plot.Children.Clear();
            double width = Math.Max(0, plot.ActualWidth - 8);
            double height = plot.Height - 8;
            const double pad = 4;
            foreach (double y in new[] { pad, pad + height / 2, pad + height })
                plot.Children.Add(new Line { X1 = pad, X2 = width + pad, Y1 = y, Y2 = y, Stroke = separator, StrokeThickness = 1 });

            double thresholdY = pad + height * (1 - Math.Clamp(lowThreshold, 0, 100) / 100d);
            plot.Children.Add(new Line
            {
                X1 = pad, X2 = width + pad, Y1 = thresholdY, Y2 = thresholdY,
                Stroke = lowBatteryBrush, StrokeThickness = 1, Opacity = .45,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            plotted = new (double, double, BatterySample)[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                double ratio = Math.Clamp((sample.Time - start).TotalSeconds / window.TotalSeconds, 0, 1);
                double x = pad + width * ratio;
                double y = pad + height * (1 - sample.Level(preferVendor) / 100d);
                plotted[i] = (x, y, sample);
            }

            PointCollection? fillPoints = null;
            double segmentStartX = 0;
            double segmentLastX = 0;
            void FlushFill()
            {
                if (fillPoints is { Count: >= 2 })
                {
                    fillPoints.Add(new Windows.Foundation.Point(segmentLastX, pad + height));
                    fillPoints.Add(new Windows.Foundation.Point(segmentStartX, pad + height));
                    plot.Children.Add(new Polygon { Points = fillPoints, Fill = Accent, Opacity = .10 });
                }
                fillPoints = null;
            }

            PointCollection? linePoints = null;
            bool lineCharging = false;
            void FlushLine()
            {
                if (linePoints is { Count: >= 2 })
                    plot.Children.Add(new Polyline
                    {
                        Points = linePoints,
                        Stroke = lineCharging ? chargingBrush : Accent,
                        StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round
                    });
                linePoints = null;
            }

            for (int i = 0; i < plotted.Length; i++)
            {
                var (x, y, sample) = plotted[i];
                bool starts = i == 0 || sample.StartsSegment;
                if (starts)
                {
                    FlushFill();
                    FlushLine();
                    fillPoints = new PointCollection();
                    segmentStartX = x;
                }
                fillPoints!.Add(new Windows.Foundation.Point(x, y));
                segmentLastX = x;

                if (starts)
                {
                    lineCharging = sample.Charging;
                    linePoints = new PointCollection { new(x, y) };
                }
                else if (sample.Charging != lineCharging)
                {
                    linePoints!.Add(new Windows.Foundation.Point(x, y));
                    FlushLine();
                    lineCharging = sample.Charging;
                    linePoints = new PointCollection { new(x, y) };
                }
                else
                {
                    linePoints!.Add(new Windows.Foundation.Point(x, y));
                }

                int level = sample.Level(preferVendor);
                bool showDot = starts || i == plotted.Length - 1 || plotted[i + 1].Sample.StartsSegment || level <= lowThreshold;
                if (showDot)
                {
                    var dotBrush = level <= lowThreshold ? lowBatteryBrush : sample.Charging ? chargingBrush : Accent;
                    var point = new Ellipse { Width = 5, Height = 5, Fill = dotBrush };
                    Canvas.SetLeft(point, x - 2.5);
                    Canvas.SetTop(point, y - 2.5);
                    plot.Children.Add(point);
                }
            }
            FlushFill();
            FlushLine();

            hoverLine = new Line { Y1 = pad, Y2 = pad + height, Stroke = scrollDivider, StrokeThickness = 1, Visibility = Visibility.Collapsed };
            hoverDot = new Ellipse { Width = 8, Height = 8, Fill = Accent, Stroke = cardBackground, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
            plot.Children.Add(hoverLine);
            plot.Children.Add(hoverDot);
        };

        plot.PointerMoved += (_, e) =>
        {
            if (plotted.Length == 0 || hoverLine is null || hoverDot is null) return;
            double pointerX = e.GetCurrentPoint(plot).Position.X;
            var nearest = plotted[0];
            double bestDistance = Math.Abs(nearest.X - pointerX);
            for (int i = 1; i < plotted.Length; i++)
            {
                double distance = Math.Abs(plotted[i].X - pointerX);
                if (distance < bestDistance) { bestDistance = distance; nearest = plotted[i]; }
            }
            hoverLine.X1 = hoverLine.X2 = nearest.X;
            hoverLine.Visibility = Visibility.Visible;
            int level = nearest.Sample.Level(preferVendor);
            hoverDot.Fill = level <= lowThreshold ? lowBatteryBrush : nearest.Sample.Charging ? chargingBrush : Accent;
            Canvas.SetLeft(hoverDot, nearest.X - 4);
            Canvas.SetTop(hoverDot, nearest.Y - 4);
            hoverDot.Visibility = Visibility.Visible;
            hoverReadout.Text = $"{nearest.Sample.Time.ToLocalTime():MM-dd HH:mm} · {level}% · {(nearest.Sample.Charging ? "充电中" : "未充电")}";
            hoverReadout.Opacity = .9;
        };

        plot.PointerExited += (_, _) =>
        {
            if (hoverLine is not null) hoverLine.Visibility = Visibility.Collapsed;
            if (hoverDot is not null) hoverDot.Visibility = Visibility.Collapsed;
            hoverReadout.Text = $"低电量线 {lowThreshold}% · 悬停曲线查看详细读数";
            hoverReadout.Opacity = .62;
        };

        bool shortWindow = window <= TimeSpan.FromHours(24);
        var from = new TextBlock { Text = shortWindow ? $"{start:HH:mm}" : $"{start:MM-dd}", FontSize = 10, Opacity = .65 };
        var mid = new TextBlock
        {
            Text = shortWindow ? $"{start.AddHours(12):HH:mm}" : $"{start.AddDays(3.5):MM-dd}",
            FontSize = 10,
            Opacity = .5,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var to = new TextBlock { Text = shortWindow ? "现在" : $"{end:MM-dd}", FontSize = 10, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var date in new[] { from, mid, to })
        {
            Grid.SetRow(date, 1);
            Grid.SetColumn(date, 1);
            chart.Children.Add(date);
        }
        return chart;
    }
}
