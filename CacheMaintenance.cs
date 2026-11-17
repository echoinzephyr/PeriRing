using System.Text.RegularExpressions;

namespace BatteryDisplay;

internal static class CacheMaintenance
{
    private static readonly Regex temporaryName = new(@"^PeriRing-(?:diagnostics|update)-[a-f0-9]{32}(?:\.zip)?$", RegexOptions.IgnoreCase);
    private static readonly Regex exportName = new(@"^PeriRing-diagnostics-[a-f0-9]{32}\.zip(?:\.partial)?$", RegexOptions.IgnoreCase);
    internal static bool DeleteOwned(string path, string root, bool temporary)
    {
        try
        {
            path = Path.GetFullPath(path);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!(temporary ? temporaryName : exportName).IsMatch(Path.GetFileName(path))) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return true;
            // Never descend into a junction or symbolic link, including the root itself.
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(item)) pending.Push(child);
            }
            if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static void PruneExports(string root, DateTime utcNow)
    {
        if (!Directory.Exists(root)) return;
        long retained = 0;
        int count = 0;
        foreach (var file in new DirectoryInfo(root).GetFiles().Where(f => exportName.IsMatch(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc))
        {
            bool partial = file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
            bool expired = file.LastWriteTimeUtc < utcNow.AddDays(-7);
            bool overQuota = !partial && (++count > 3 || retained + file.Length > 50L * 1024 * 1024);
            if (expired || overQuota || partial && file.LastWriteTimeUtc < utcNow.AddDays(-1)) DeleteOwned(file.FullName, root, false);
            else if (!partial) retained += file.Length;
        }
    }
    public static void Run()
    {
        try
        {
            DebugService.Cleanup();
            PruneExports(DebugService.ExportDirectory, DateTime.UtcNow);
            string temp = Path.GetTempPath();
            int backups = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(temp, "PeriRing-*").OrderByDescending(p => File.GetLastWriteTimeUtc(p)))
            {
                string name = Path.GetFileName(path);
                if (!temporaryName.IsMatch(name)) continue;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (name.StartsWith("PeriRing-diagnostics-", StringComparison.OrdinalIgnoreCase))
                { if (age > TimeSpan.FromDays(7)) DeleteOwned(path, temp, true); continue; }
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                // The helper owns this exclusive lease throughout replacement and rollback.
                try
                {
                    using var lease = new FileStream(Path.Combine(path, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    bool complete = File.Exists(Path.Combine(path, "complete"));
                    if (!complete && age < TimeSpan.FromDays(1)) continue;
                    if (!complete && File.Exists(Path.Combine(path, "plan.json")))
                    {
                        using var plan = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "plan.json")));
                        if (plan.RootElement.TryGetProperty("ParentId", out var parent))
                        {
                            try { using var process = System.Diagnostics.Process.GetProcessById(parent.GetInt32()); if (!process.HasExited) continue; }
                            catch (ArgumentException) { }
                        }
                    }
                    bool remove = !complete || age > TimeSpan.FromDays(7) || ++backups > 1;
                    lease.Dispose();
                    if (remove) DeleteOwned(path, temp, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
