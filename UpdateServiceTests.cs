using System.Net;
using System.Net.Http;

namespace BatteryDisplay;

internal static class UpdateServiceTests
{
    public static int Run(List<string> failures)
    {
        int count = 0;
        void Check(bool condition, string name) { count++; if (!condition) failures.Add(name); }
        const string newer = """
            {"tag_name":"v0.2.0","draft":false,"prerelease":false,
             "html_url":"https://github.com/echoinzephyr/PeriRing/releases/tag/v0.2.0",
             "assets":[{"name":"PeriRing-v0.2.0-win-x64.zip","browser_download_url":"https://github.com/echoinzephyr/PeriRing/releases/download/v0.2.0/PeriRing-v0.2.0-win-x64.zip"}]}
            """;
        Check(UpdateService.Parse(newer, new(0, 1, 0)).Available, "Update checker detects newer release");
        Check(!UpdateService.Parse(newer, new(0, 2, 0, 0)).Available, "Three/four-part equal versions compare equally");
        Check(!UpdateService.Parse(newer, new(0, 3, 0)).Available, "Update checker never offers a downgrade");
        Check(UpdateService.Parse(newer, new(0, 1, 0)).Download?.Host == "github.com", "Update selects Windows x64 release asset");
        Check(UpdateService.Parse(newer.Replace("https://github.com/", "https://example.org/"), new(0, 1, 0)).Download is null,
            "External download URL is rejected");
        Check(!UpdateService.Parse(newer.Replace("\"draft\":false", "\"draft\":true"), new(0, 1, 0)).Available, "Draft release is not an update");
        Check(!UpdateService.Parse(newer.Replace("\"prerelease\":false", "\"prerelease\":true"), new(0, 1, 0)).Available, "Prerelease is not a stable update");
        Check(!UpdateService.Parse(newer.Replace("\"v0.2.0\"", "\"latest\""), new(0, 1, 0)).Success, "Malformed version gives a useful error");
        Check(UpdateService.TryVersion("v0.10.0", out var version) && version > new Version(0, 2, 0), "Version comparison is numeric");
        using var notFound = new HttpClient(new StubHandler(_ => new(HttpStatusCode.NotFound)));
        Check(UpdateService.CheckAsync(CancellationToken.None, notFound).GetAwaiter().GetResult() is { Success: true, Available: false }, "No release yet is handled");
        using var limited = new HttpClient(new StubHandler(_ => new(HttpStatusCode.Forbidden)));
        Check(!UpdateService.CheckAsync(CancellationToken.None, limited).GetAwaiter().GetResult().Success, "API rate limit is handled");
        using var offline = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        Check(!UpdateService.CheckAsync(CancellationToken.None, offline).GetAwaiter().GetResult().Success, "Offline update check does not crash");
        using var malformed = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not json") }));
        Check(!UpdateService.CheckAsync(CancellationToken.None, malformed).GetAwaiter().GetResult().Success, "Broken API payload does not crash");
        bool correctRequest = false;
        using var ok = new HttpClient(new StubHandler(request =>
        {
            correctRequest = request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/echoinzephyr/PeriRing/releases/latest"
                && request.Headers.UserAgent.ToString().StartsWith("PeriRing/") && request.Headers.Authorization is null;
            return new(HttpStatusCode.OK) { Content = new StringContent(newer) };
        }));
        Check(UpdateService.CheckAsync(CancellationToken.None, ok, new(0, 1, 0)).GetAwaiter().GetResult().Available && correctRequest,
            "Update request uses public release API without personal credentials");
        return count;
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
}
