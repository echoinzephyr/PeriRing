using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace BatteryDisplay;

internal static class AutoUpdateTests
{
    internal static int Run(List<string> failures)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) failures.Add(message); }
        foreach (string path in new[] { "../bad.dll", "C:\\bad.exe", "data/settings.json", "folder/../../bad.exe", "a.dll:stream", "folder./bad.exe", "a/../b.dll", "./bad.exe" })
            Check(!AutoUpdate.ValidPath(path), "Reject unsafe archive path: " + path);
        Check(AutoUpdate.ValidPath("Assets/periring.png") && AutoUpdate.ValidPath("BatteryDisplay.exe"), "Valid package paths are accepted");
        Check(StandardBatteryProvider.Percentage(50, 100) == 50 && StandardBatteryProvider.Percentage(0, 100) == 0, "Standard battery supports valid and empty readings");
        Check(StandardBatteryProvider.Percentage(1000, 0) is null && StandardBatteryProvider.Percentage(101, 100) is null
            && StandardBatteryProvider.Percentage(null, 100) is null, "Standard battery rejects invalid capacity");
        Check(DebugService.Key("one") == DebugService.Key("one") && DebugService.Key("one") != DebugService.Key("two"), "Device keys are stable without exposing identifiers");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (string name in new[] { "BatteryDisplay.exe", "BatteryDisplay.dll", "BatteryDisplay.deps.json", "BatteryDisplay.runtimeconfig.json", "resources.pri", "App.xbf", "periring.ico" })
            {
                using var stream = zip.CreateEntry(name).Open();
                if (name == "BatteryDisplay.dll") { using var dll = File.OpenRead(Path.Combine(AppContext.BaseDirectory, name)); dll.CopyTo(stream); }
                else stream.WriteByte(1);
            }
        }
        byte[] bytes = memory.ToArray();
        var release = new UpdateResult(true, true, "test", UpdateService.CurrentLabel, Download: new Uri($"{UpdateService.Repository}/releases/download/v{UpdateService.CurrentLabel}/PeriRing-v{UpdateService.CurrentLabel}-win-x64.zip"),
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length);
        using var client = new HttpClient(new Handler(bytes));
        var progress = new Progress<string>();
        Task<UpdatePlan> Prepare(UpdateResult value, CancellationToken token = default) => AutoUpdate.PrepareAsync(value, progress, token, client, new Version(0, 0, 0));
        bool Rejected(UpdateResult value, CancellationToken token = default)
        {
            try { Prepare(value, token).GetAwaiter().GetResult(); return false; }
            catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException) { return true; }
        }
        var plan = Prepare(release).GetAwaiter().GetResult();
        try
        {
            Check(plan.Files.Length == 7 && File.Exists(Path.Combine(plan.Stage, "resources.pri")), "Download, hash, extract and version validation complete before replacement");
        }
        finally
        {
            string? workspace = Path.GetDirectoryName(plan.Stage);
            if (workspace is not null && AutoUpdate.IsOwnedUpdateWorkspace(workspace) && Directory.Exists(workspace))
                Directory.Delete(workspace, true);
        }
        Check(AutoUpdate.IsValidHealthPath(@"c:\temp\PeriRing-update-0123456789abcdef\healthy", @"C:\TEMP"),
            "Update health path comparison is case-insensitive on Windows");
        Check(!AutoUpdate.IsValidHealthPath(@"C:\temp\other\healthy", @"C:\temp")
            && !AutoUpdate.IsValidHealthPath(@"C:\temp\PeriRing-update-0123456789abcdef\other.txt", @"C:\temp"),
            "Update health path rejects unrelated directories and filenames");
        Check(Rejected(release with { Sha256 = new string('0', 64) }), "Corrupt download is rejected");
        Check(Rejected(release with { Size = bytes.Length + 1 }), "Truncated download is rejected");
        Check(Rejected(release with { Size = AutoUpdate.MaximumDownload + 1 }), "Oversized release is rejected");
        Check(Rejected(release with { Sha256 = null }), "Missing digest cannot install automatically");
        Check(Rejected(release with { Download = new Uri("https://example.org/bad.zip") }), "Mismatched download origin is rejected");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(Rejected(release, cancelled.Token), "Cancelled update leaves installation untouched");
        Check(typeof(AutoUpdate).Assembly.GetManifestResourceStream("BatteryDisplay.tools.Apply-Update.ps1") is not null, "External replacement helper is packaged");
        return count;
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
