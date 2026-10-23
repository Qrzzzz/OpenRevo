using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenRevo.Core;

public enum UpdateStatus { Available, Current, Ahead }
public sealed record UpdateRelease(UpdateStatus Status, string Version, Uri ReleaseUrl, Uri? DownloadUrl, string Notes);

/// <summary>A user-initiated public release check. No token, installation or background polling.</summary>
public sealed class GitHubUpdates(HttpClient client)
{
    public const string Repository = "Qrzzzz/OpenRevo";
    public static readonly Uri LatestEndpoint = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesUrl = new($"https://github.com/{Repository}/releases");
    public static readonly Uri LatestReleaseUrl = new($"https://github.com/{Repository}/releases/latest");

    public async Task<UpdateRelease> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!TryVersion(currentVersion, out var current)) throw new ArgumentException("Invalid application version.", nameof(currentVersion));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestEndpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenRevo", currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return await CheckReleasePage(current, currentVersion, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("GitHub 暂未提供可检查的正式版本，请打开发布页查看。", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("GitHub 返回的版本信息过大。");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub 返回的不是正式发行版。");
        var tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !TryVersion(tag[1..], out var latest))
            throw new InvalidDataException("无法识别 GitHub 返回的版本号。");
        var releaseUrl = new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
        if (release.GetProperty("html_url").GetString() != releaseUrl.AbsoluteUri)
            throw new InvalidDataException("GitHub 返回的发布地址与 OpenRevo 仓库不一致。");
        var name = $"OpenRevo-{tag[1..]}-win-x64.zip";
        var expectedDownload = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        Uri? download = null;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
                if (asset.GetProperty("name").GetString() == name && asset.GetProperty("browser_download_url").GetString() == expectedDownload)
                    download = new Uri(expectedDownload);
        var notes = release.TryGetProperty("body", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        var comparison = latest.CompareTo(current);
        return new(comparison > 0 ? UpdateStatus.Available : comparison == 0 ? UpdateStatus.Current : UpdateStatus.Ahead,
            tag[1..], releaseUrl, download, notes);
    }

    private async Task<UpdateRelease> CheckReleasePage(Version current, string currentText, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenRevo", currentText));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var target = response.RequestMessage?.RequestUri;
        var prefix = $"https://github.com/{Repository}/releases/tag/v";
        if (target is null || !target.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal)
            || !TryVersion(target.AbsoluteUri[prefix.Length..], out var latest))
            throw new InvalidDataException("无法从 GitHub 最新发布页确认版本。");
        var version = target.AbsoluteUri[prefix.Length..];
        var comparison = latest.CompareTo(current);
        return new(comparison > 0 ? UpdateStatus.Available : comparison == 0 ? UpdateStatus.Current : UpdateStatus.Ahead,
            version, target, null, "GitHub API 暂时限流，已通过 GitHub 官方最新发布页确认版本。请打开发布页查看说明并下载便携包。");
    }

    internal static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length > 9 || (p.Length > 1 && p[0] == '0') || p.Any(c => c is < '0' or > '9')))
            return false;
        return Version.TryParse(value, out version!);
    }
}
