using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BatteryDisplay;

internal sealed record UpdateResult(bool Success, bool Available, string Message, string? LatestVersion = null,
    Uri? ReleasePage = null, Uri? Download = null);

internal static class UpdateService
{
    public const string Repository = "https://github.com/echoinzephyr/PeriRing";
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(12) };
    public static Version CurrentVersion => Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0));
    public static string CurrentLabel => CurrentVersion.ToString(3);

    public static async Task<UpdateResult> CheckAsync(CancellationToken token, HttpClient? transport = null, Version? current = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/echoinzephyr/PeriRing/releases/latest");
            request.Headers.UserAgent.ParseAdd($"PeriRing/{CurrentLabel}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await (transport ?? client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(true, false, "暂时没有已发布的版本。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(false, false, "检查过于频繁或 GitHub 暂不可用，请稍后重试。", ReleasePage: new(Repository + "/releases"));
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
            string json = await response.Content.ReadAsStringAsync(timeout.Token);
            return Parse(json, current ?? CurrentVersion);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, false, "连接 GitHub 超时，请稍后重试。", ReleasePage: new(Repository + "/releases")); }
        catch (HttpRequestException)
        { return new(false, false, "无法连接 GitHub，请检查网络后重试。", ReleasePage: new(Repository + "/releases")); }
        catch (JsonException)
        { return new(false, false, "版本信息暂时无法读取，请稍后重试。", ReleasePage: new(Repository + "/releases")); }
    }

    internal static UpdateResult Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid release");
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
            || release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            return new(true, false, "暂时没有可用的正式更新。");
        string? tag = release.TryGetProperty("tag_name", out var tagValue) && tagValue.ValueKind == JsonValueKind.String ? tagValue.GetString() : null;
        if (!TryVersion(tag, out var latest)) return new(false, false, "发布版本号无法识别，请查看发布页面。", ReleasePage: new(Repository + "/releases"));
        string label = latest.ToString(3);
        Uri page = new(Repository + "/releases");
        if (release.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
            && SafeLink(html.GetString(), "/releases/tag/") is Uri releasePage) page = releasePage;
        Uri? download = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() == $"PeriRing-v{label}-win-x64.zip"
                    && asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String)
                    download = SafeLink(url.GetString(), "/releases/download/");
            }
        bool available = latest > Normalize(current);
        return new(true, available, available ? $"发现新版本 v{label}" : "当前已是最新版本。", label, page, download);
    }
    internal static bool TryVersion(string? text, out Version version)
    {
        version = new(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Version.TryParse(value, out var parsed) || parsed.Build < 0) return false;
        version = Normalize(parsed);
        return true;
    }
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static Uri? SafeLink(string? text, string suffix) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && uri.AbsolutePath.StartsWith("/echoinzephyr/PeriRing" + suffix, StringComparison.Ordinal) ? uri : null;
}
