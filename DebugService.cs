using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatteryDisplay;

internal sealed record DeviceInventory(string Key, string Name, string Vendor, string Product, ushort UsagePage, ushort Usage,
    ushort InputLength, ushort OutputLength, ushort FeatureLength, string[] Adapters);

internal static class DebugService
{
    private static readonly object gate = new();
    private static string lastInventory = "";
    public static bool Enabled { get; set; }
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data", "debug");
    public static string ExportDirectory => Path.Combine(AppContext.BaseDirectory, "data", "diagnostics");
    public static IReadOnlyList<DeviceInventory> Inventory { get; private set; } = [];
    internal static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    public static void Log(string area, string action, object? details = null)
    {
        if (!Enabled) return;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "session.jsonl");
                string line = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Area = area, Action = action, Details = details }) + "\n";
                if (Encoding.UTF8.GetByteCount(line) > 64 * 1024)
                    line = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Area = area, Action = action, Truncated = true }) + "\n";
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > 2 * 1024 * 1024)
                {
                    for (int i = 3; i >= 1; i--)
                    {
                        string source = i == 1 ? path : path + "." + (i - 1);
                        if (File.Exists(source)) File.Move(source, path + "." + i, true);
                    }
                }
                File.AppendAllText(path, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static void Observe(IReadOnlyList<HidDescriptor> devices)
    {
        if (!Enabled) return;
        var inventory = devices.Select(d => new DeviceInventory(Key(d.Path), d.Name, $"{d.Vendor:X4}", $"{d.Product:X4}",
            d.Caps.UsagePage, d.Caps.Usage, d.Caps.InputLength, d.Caps.OutputLength, d.Caps.FeatureLength,
            UsbProvider.Adapters.Where(a => a.Matches(d)).Select(a => a.Name).ToArray())).OrderBy(d => d.Key).ToArray();
        string json = JsonSerializer.Serialize(inventory);
        Inventory = inventory;
        if (json != lastInventory) { lastInventory = json; Log("devices", "inventory-changed", inventory); }
    }
    public static string Export(ScanReport report)
    {
        lock (gate) return ExportLocked(report);
    }
    private static string ExportLocked(ScanReport report)
    {
        Directory.CreateDirectory(ExportDirectory);
        string zip = Path.Combine(ExportDirectory, "PeriRing-diagnostics-" + Guid.NewGuid().ToString("N") + ".zip");
        string partial = zip + ".partial";
        try
        {
        using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
        using (var writer = new StreamWriter(archive.CreateEntry("report.json").Open())) writer.Write(JsonSerializer.Serialize(new
        {
            Time = DateTimeOffset.Now, Version = UpdateService.CurrentLabel, OS = Environment.OSVersion.VersionString,
            Devices = report.Devices.Select(d => new { Key = Key(d.Id), NameKey = Key(d.Name), d.Source, d.Transport, d.Connected, d.Battery, d.Gauge, d.IsCached }),
            Providers = report.Providers.Select(p => new { p.Name, p.DeviceCount, HasWarning = p.Warning is not null }),
            Interfaces = Inventory
        }, new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(DirectoryPath))
                foreach (var path in LogFiles()) archive.CreateEntryFromFile(path, Path.GetFileName(path));
        using (var writer = new StreamWriter(archive.CreateEntry("README.txt").Open())) writer.Write("环电诊断包：仅在本机生成，不会自动上传。设备路径和设备实例标识已哈希；USB 产品名称、VID/PID、接口能力和电量可能包含在内。分享前请检查内容。未知设备未试发厂商指令，也未采集键盘、鼠标或手柄输入内容。");
        }
        File.Move(partial, zip);
        CacheMaintenance.PruneExports(ExportDirectory, DateTime.UtcNow);
        return zip;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    private static IEnumerable<string> LogFiles() => Enumerable.Range(0, 4).Select(i => Path.Combine(DirectoryPath, "session.jsonl" + (i == 0 ? "" : "." + i))).Where(File.Exists);
    public static void Cleanup(bool clear = false)
    {
        lock (gate)
        {
            if (Directory.Exists(DirectoryPath))
                foreach (var path in LogFiles())
                    if (clear || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (clear)
            {
                lastInventory = "";
                if (Directory.Exists(ExportDirectory))
                    foreach (var path in Directory.EnumerateFiles(ExportDirectory)) CacheMaintenance.DeleteOwned(path, ExportDirectory, false);
            }
        }
    }
}
