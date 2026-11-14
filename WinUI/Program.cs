using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatteryDisplay;

internal static class Program
{
    private static BatteryApplication? application;
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (args.Contains("--check-update"))
            {
                var result = UpdateService.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "update-check.json"), JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = result.Success ? 0 : 1;
                return;
            }
            if (args.Contains("--diagnose"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var report = Task.Run(() => BatteryService.ScanAsync(timeout.Token)).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics.json"), JsonSerializer.Serialize(
                    new { Time = DateTimeOffset.Now, report.Devices, report.Providers,
                        HidInterfaces = HidBattery.Enumerate(timeout.Token).Select(d => new { d.Path, d.Name, d.Vendor, d.Product,
                            d.Caps.UsagePage, d.Caps.Usage, d.Caps.InputLength, d.Caps.OutputLength, d.Caps.FeatureLength })
                    }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (args.Contains("--self-test")) { CoreSelfTest.Run(); return; }
            using var mutex = new Mutex(true, "Local\\BatteryDisplay.Tray", out bool first);
            if (!first)
            {
                try { using var show = EventWaitHandle.OpenExisting("Local\\BatteryDisplay.ShowWindow"); show.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                application = new BatteryApplication(args);
            });
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString());
            NativeWindow.Error(AppBrand.Name + "启动失败", ex.Message);
            Environment.ExitCode = 1;
        }
    }
}

public sealed partial class BatteryApplication : Application
{
    private readonly string[] args;
    private TrayController? controller;
    private Window? diagnosticWindow;
    public BatteryApplication(string[] args)
    {
        this.args = args;
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.txt"), e.Exception.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Initializing WinUI resources\n");
        InitializeComponent();
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "WinUI resources initialized\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        try
        {
            if (args.Contains("--minimal-ui"))
            {
                diagnosticWindow = new Window { Title = AppBrand.Name + " · UI 检查", Content = new TextBlock { Text = "WinUI 3", FontSize = 32 } };
                diagnosticWindow.Activate();
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Minimal UI ready\n");
                return;
            }
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Creating tray controller\n");
            controller = new TrayController(args.Contains("--show") || args.Contains("--ui-preview") || args.Contains("--capture-ui"), args.Contains("--ui-preview"));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Tray controller ready\n");
            if (args.Contains("--capture-ui")) CaptureAndExit();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString());
            Environment.ExitCode = 1;
            Exit();
        }
    }
    private async void CaptureAndExit()
    {
        try { await controller!.CaptureAsync(); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "capture-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { controller?.Quit(); }
    }
}
