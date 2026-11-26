using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatteryDisplay;

internal sealed record UpdatePlan(int ParentId, string Target, string Stage, string Backup, string Health, string[] Files);

internal static class AutoUpdate
{
    internal static bool RecentFailure()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "data", "last-update.json");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow.AddHours(-24)) return false;
            using var result = JsonDocument.Parse(File.ReadAllText(path));
            return result.RootElement.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    internal static bool IsOwnedUpdateWorkspace(string workspace, string? tempRoot = null)
    {
        try
        {
            string full = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetFullPath(tempRoot ?? Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("PeriRing-update-", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool IsValidHealthPath(string candidate, string? tempRoot = null)
    {
        try
        {
            string full = Path.GetFullPath(candidate);
            string? parent = Path.GetDirectoryName(full);
            return parent is not null && IsOwnedUpdateWorkspace(parent, tempRoot)
                && string.Equals(Path.GetFileName(full), "healthy", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    internal const long MaximumDownload = 150 * 1024 * 1024;
    internal static bool ValidPath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/');
        return parts.Length > 0 && parts.All(p => p.Length > 0 && p is not "." and not ".."
            && !p.EndsWith('.') && !p.EndsWith(' ') && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            && !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)
            && !parts[0].StartsWith('.')
            && (Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".pri" or ".xbf" or ".png" or ".ico" or ".md" or ".pdb" || name == "LICENSE");
    }
    internal static string[] Extract(string zip, string stage)
    {
        using var archive = ZipFile.OpenRead(zip);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (archive.Entries.Count > 4000) throw new InvalidDataException("更新包文件数量异常");
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!ValidPath(entry.FullName) || !names.Add(entry.FullName.Replace('\\', '/'))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (total += entry.Length) > 500L * 1024 * 1024) throw new InvalidDataException("更新包路径或大小无效");
        }
        foreach (var required in new[] { "BatteryDisplay.exe", "BatteryDisplay.dll", "BatteryDisplay.deps.json", "BatteryDisplay.runtimeconfig.json", "resources.pri", "App.xbf", "periring.ico" })
            if (!names.Contains(required)) throw new InvalidDataException("更新包缺少文件：" + required);
        Directory.CreateDirectory(stage);
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            string path = Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        return names.ToArray();
    }
    internal static async Task VerifyHashAsync(string path, string expected, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA256 校验失败");
    }
    public static async Task<UpdatePlan> PrepareAsync(UpdateResult release, IProgress<string> progress, CancellationToken token,
        HttpClient? transport = null, Version? current = null)
    {
        if (!release.Available || release.Download is null || release.Sha256 is not { Length: 64 }
            || release.Size is <= 0 or > MaximumDownload || !UpdateService.TryVersion(release.LatestVersion, out var version)
            || version <= (current ?? UpdateService.CurrentVersion)) throw new InvalidDataException("此版本缺少可验证的自动更新附件");
        var expected = new Uri($"{UpdateService.Repository}/releases/download/v{release.LatestVersion}/PeriRing-v{release.LatestVersion}-win-x64.zip");
        if (release.Download != expected) throw new InvalidDataException("更新地址与版本不匹配");
        string workspace = Path.Combine(Path.GetTempPath(), "PeriRing-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
        using var lease = new FileStream(Path.Combine(workspace, "active.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        string zip = Path.Combine(workspace, "update.zip");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var ownedClient = transport is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        var http = transport ?? ownedClient!;
        using var request = new HttpRequestMessage(HttpMethod.Get, release.Download);
        request.Headers.UserAgent.ParseAdd("PeriRing/" + UpdateService.CurrentLabel);
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = File.Create(zip);
            byte[] buffer = new byte[65536];
            long count = 0;
            int last = -1;
            int bytes;
            while ((bytes = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                count += bytes;
                if (count > release.Size || count > MaximumDownload) throw new InvalidDataException("下载文件大小异常");
                await output.WriteAsync(buffer.AsMemory(0, bytes), timeout.Token);
                int percent = (int)(count * 100 / release.Size);
                if (percent / 5 != last) { last = percent / 5; progress.Report($"正在下载更新…{percent}%"); }
            }
            if (count != release.Size) throw new InvalidDataException("更新下载不完整");
        }
        progress.Report("正在校验更新包…");
        await VerifyHashAsync(zip, release.Sha256, timeout.Token);
        string stage = Path.Combine(workspace, "stage");
        var files = Extract(zip, stage);
        string productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "BatteryDisplay.dll")).ProductVersion?.Split('+')[0] ?? "";
        if (!UpdateService.TryVersion(productVersion, out var actual) || actual != version) throw new InvalidDataException("更新包内版本不匹配");
        return new(Environment.ProcessId, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), stage,
            Path.Combine(workspace, "backup"), Path.Combine(workspace, "healthy"), files);
        }
        catch
        {
            try
            {
                if (IsOwnedUpdateWorkspace(workspace)) Directory.Delete(workspace, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    public static void Launch(UpdatePlan plan)
    {
        string workspace = Path.GetDirectoryName(plan.Stage)!;
        using var resource = typeof(AutoUpdate).Assembly.GetManifestResourceStream("BatteryDisplay.tools.Apply-Update.ps1")!;
        using (var file = File.Create(Path.Combine(workspace, "apply.ps1"))) resource.CopyTo(file);
        string planPath = Path.Combine(workspace, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(workspace, "apply.ps1"), "-PlanPath", planPath }) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }
}
