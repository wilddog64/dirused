using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dirused.Common.Update;

public sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    private static readonly Regex VersionTag = new("^v(\\d+)\\.(\\d+)\\.(\\d+)$", RegexOptions.Compiled);
    private readonly HttpClient _client;
    public GitHubReleaseSource(string version)
    {
        var handler = new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dirused", version));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }
    public async Task<ReleasePackage?> FindLatestAsync(string currentVersion, string rid, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _client.GetAsync("https://api.github.com/repos/wilddog64/dirused/releases?per_page=100", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        var releases = doc.RootElement.EnumerateArray().Select(e =>
        {
            var tag = e.GetProperty("tag_name").GetString() ?? "";
            var assets = e.GetProperty("assets").EnumerateArray().Select(x => new ReleaseAsset(x.GetProperty("name").GetString() ?? "", new Uri(x.GetProperty("browser_download_url").GetString() ?? ""))).ToArray();
            return new ReleaseInfo(tag, e.GetProperty("draft").GetBoolean(), assets);
        });
        return ReleaseSelector.Select(releases, rid);
    }
    public async Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        if (!ValidUrl(uri, false)) throw new InvalidDataException("Asset URL is not allowed.");
        using var response = await GetAssetAsync(uri, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Download exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var ms = new MemoryStream(); var buffer = new byte[81920]; int n;
        while ((n = await stream.ReadAsync(buffer, timeout.Token)) > 0) { if (ms.Length + n > maxBytes) throw new InvalidDataException("Download exceeds size limit."); ms.Write(buffer, 0, n); }
        return ms.ToArray();
    }
    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null || redirects == 5) throw new InvalidDataException("Too many or invalid asset redirects.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!ValidUrl(current, true)) throw new InvalidDataException("Asset redirect host is not allowed.");
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many asset redirects.");
    }
    private static bool ValidUrl(Uri u, bool redirected) => u.Scheme == Uri.UriSchemeHttps && (redirected ? new[] { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" }.Contains(u.Host, StringComparer.OrdinalIgnoreCase) : u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/wilddog64/dirused/releases/download/", StringComparison.Ordinal));
    public static int[]? Parse(string s) { var m = VersionTag.Match(s.StartsWith('v') ? s : "v" + s); return m.Success ? new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) } : null; }
    internal static int Compare(int[]? a, int[]? b) { if (a == null) return b == null ? 0 : -1; if (b == null) return 1; for (var i = 0; i < 3; i++) { var c = a[i].CompareTo(b[i]); if (c != 0) return c; } return 0; }
    public void Dispose() => _client.Dispose();
}
