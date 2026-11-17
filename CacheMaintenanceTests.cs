namespace BatteryDisplay;

internal static class CacheMaintenanceTests
{
    internal static int Run(List<string> failures)
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) failures.Add(name); }
        string root = Path.Combine(AppContext.BaseDirectory, "cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTime.UtcNow;
        var files = Enumerable.Range(0, 5).Select(i =>
        {
            string path = Path.Combine(root, "PeriRing-diagnostics-" + Guid.NewGuid().ToString("N") + ".zip");
            File.WriteAllText(path, "fixture"); File.SetLastWriteTimeUtc(path, now.AddMinutes(-i)); return path;
        }).ToArray();
        CacheMaintenance.PruneExports(root, now);
        Check(files.Take(3).All(File.Exists) && files.Skip(3).All(p => !File.Exists(p)), "Only three newest diagnostic bundles remain");
        File.SetLastWriteTimeUtc(files[0], now.AddDays(-8));
        CacheMaintenance.PruneExports(root, now);
        Check(!File.Exists(files[0]) && File.Exists(files[1]), "Expired diagnostic bundle is removed independently of quota");
        using (var file = new FileStream(files[1], FileMode.Open)) file.SetLength(30 * 1024 * 1024);
        using (var file = new FileStream(files[2], FileMode.Open)) file.SetLength(30 * 1024 * 1024);
        File.SetLastWriteTimeUtc(files[1], now.AddMinutes(-1));
        File.SetLastWriteTimeUtc(files[2], now.AddMinutes(-2));
        CacheMaintenance.PruneExports(root, now);
        Check(File.Exists(files[1]) && !File.Exists(files[2]), "Diagnostic archive quota stays below 50 MB");
        string unrelated = Path.Combine(root, "user.zip"); File.WriteAllText(unrelated, "keep");
        Check(!CacheMaintenance.DeleteOwned(unrelated, root, false) && File.Exists(unrelated), "Cleanup preserves unrelated files");
        Check(!CacheMaintenance.DeleteOwned(files[1], Path.Combine(root, "other"), false), "Cleanup refuses paths outside its named root");
        using (var locked = new FileStream(files[1], FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!CacheMaintenance.DeleteOwned(files[1], root, false), "Busy bundle is skipped without crashing cleanup");
        Check(CacheMaintenance.DeleteOwned(files[1], root, false), "Owned bundle can be reclaimed after it is unlocked");
        string update = Path.Combine(root, "PeriRing-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(update, "stage")); File.WriteAllText(Path.Combine(update, "stage", "fixture"), "test");
        Check(CacheMaintenance.DeleteOwned(update, root, true) && !Directory.Exists(update), "Verified owned update directory is reclaimed recursively");
        bool enabled = DebugService.Enabled;
        try
        {
            DebugService.Enabled = true;
            DebugService.Cleanup(true);
            Directory.CreateDirectory(DebugService.DirectoryPath);
            string log = Path.Combine(DebugService.DirectoryPath, "session.jsonl");
            File.WriteAllText(log, new string('x', 2 * 1024 * 1024));
            DebugService.Log("test", "rotate");
            Check(File.Exists(log + ".1") && new FileInfo(log).Length < 4096, "Log rotates before exceeding its size budget");
            DebugService.Log("test", "oversized-event", new string('x', 100000));
            Check(new FileInfo(log).Length < 4096, "Single oversized debug events are bounded");
            DebugService.Enabled = false;
            long length = new FileInfo(log).Length;
            DebugService.Log("test", "disabled");
            Check(new FileInfo(log).Length == length, "Disabled debug mode does not generate logs");
            string zip = DebugService.Export(new([], []));
            Check(File.Exists(zip) && Path.GetDirectoryName(zip) == DebugService.ExportDirectory, "Export writes directly to the managed diagnostics directory");
            DebugService.Cleanup(true);
            Check(!File.Exists(log) && !File.Exists(zip), "Manual cleanup removes logs and diagnostic bundles");
        }
        finally { DebugService.Enabled = enabled; }
        return checks;
    }
}
