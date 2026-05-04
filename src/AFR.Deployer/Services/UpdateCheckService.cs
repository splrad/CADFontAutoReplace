using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFR.Deployer.Services;

/// <summary>
/// 部署器侧更新检查服务。
/// <para>
/// 通过 GitHub Releases latest API 获取项目最新发行版，并与本地部署器显示版本（X.Y）比较。
/// 网络异常、限流或版本格式异常均静默视为无可用更新，避免影响部署器启动与安装 / 卸载流程。
/// </para>
/// </summary>
internal static class UpdateCheckService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/splrad/CADFontAutoReplace/releases/latest";
    internal const string ReleasesPageUrl = "https://github.com/splrad/CADFontAutoReplace/releases";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>检查 GitHub 最新发行版是否高于本地部署器版本。</summary>
    internal static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AFR-Deployer", DeployerVersionService.GetDisplayVersion()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return UpdateCheckResult.NoUpdate;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: cancellationToken);
            if (release is null || release.Draft || release.Prerelease) return UpdateCheckResult.NoUpdate;

            var latestVersionText = NormalizeTag(release.TagName);
            var localVersionText = DeployerVersionService.GetDisplayVersion();
            if (!TryParseVersion(latestVersionText, out var latestVersion) ||
                !TryParseVersion(localVersionText, out var localVersion))
            {
                return UpdateCheckResult.NoUpdate;
            }

            if (latestVersion <= localVersion) return UpdateCheckResult.NoUpdate;

            var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? ReleasesPageUrl
                : release.HtmlUrl!;
            return new UpdateCheckResult(true, latestVersionText, releaseUrl, release.Name);
        }
        catch
        {
            return UpdateCheckResult.NoUpdate;
        }
    }

    private static string NormalizeTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
        return tagName.StartsWith('v') || tagName.StartsWith('V')
            ? tagName[1..]
            : tagName;
    }

    private static bool TryParseVersion(string text, out Version version)
        => Version.TryParse(text, out version!) && version.Revision < 0;

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}

/// <summary>部署器更新检查结果。</summary>
/// <param name="HasUpdate">是否存在比本地版本更新的正式发行版。</param>
/// <param name="LatestVersion">最新发行版显示版本（X.Y）。</param>
/// <param name="ReleaseUrl">用于打开浏览器的发行版页面地址。</param>
/// <param name="ReleaseName">GitHub Release 标题。</param>
internal sealed record UpdateCheckResult(
    bool HasUpdate,
    string LatestVersion,
    string ReleaseUrl,
    string? ReleaseName)
{
    /// <summary>无可用更新或检查失败时的空结果。</summary>
    internal static readonly UpdateCheckResult NoUpdate = new(false, string.Empty, UpdateCheckService.ReleasesPageUrl, null);
}
