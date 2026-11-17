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
            DebugService.Enabled = args.Contains("--debug") || Preferences.Load().DebugMode;
            DebugService.Log("app", "start", new { Version = UpdateService.CurrentLabel });
            if (args.Contains("--check-update"))
            {
                var result = UpdateService.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "update-check.json"), JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = result.Success ? 0 : 1;
                return;
            }
            if (args.Contains("--diagnose") || args.Contains("--export-diagnostics"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var report = Task.Run(() => BatteryService.ScanAsync(timeout.Token)).GetAwaiter().GetResult();
                if (args.Contains("--export-diagnostics"))
                {
                    DebugService.Enabled = true;
                    DebugService.Observe(HidBattery.Enumerate(timeout.Token));
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics-export.txt"), DebugService.Export(report));
                    return;
                }
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
            DebugService.Log("app", "fatal", new { Type = ex.GetType().Name, ex.HResult });
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
        UnhandledException += (_, e) =>
        {
            DebugService.Log("ui", "unhandled", new { Type = e.Exception.GetType().Name, e.Exception.HResult });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.txt"), e.Exception.ToString());
        };
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
            controller = new TrayController(args.Contains("--show") || args.Contains("--ui-preview") || args.Contains("--capture-ui") || args.Contains("--ui-self-test"), args.Contains("--ui-preview") || args.Contains("--ui-self-test"));
            if (args.Contains("--debug")) { controller.Preferences.DebugMode = true; controller.Save(); }
            int health = Array.IndexOf(args, "--update-health");
            if (health >= 0 && health + 1 < args.Length)
            {
                string path = Path.GetFullPath(args[health + 1]);
                string parent = Path.GetDirectoryName(path)!;
                if (Path.GetDirectoryName(parent)?.TrimEnd('\\') == Path.GetTempPath().TrimEnd('\\')
                    && Path.GetFileName(parent).StartsWith("PeriRing-update-") && Path.GetFileName(path) == "healthy")
                    File.WriteAllText(path, UpdateService.CurrentLabel);
            }
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-startup.log"), "Tray controller ready\n");
            if (args.Contains("--ui-self-test")) VerifyAndExit();
            else if (args.Contains("--capture-ui")) CaptureAndExit();
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
    private async void VerifyAndExit()
    {
        try { await controller!.RunInteractionChecksAsync(); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-self-test-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { controller?.Quit(); }
    }
}
