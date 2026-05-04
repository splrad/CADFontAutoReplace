using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFR.Deployer.Services;

/// <summary>
/// 部署器侧更新检查服务。
/// <para>
/// 通过 GitHub / Gitee 发行版接口获取项目最新发行版，并与本地部署器显示版本（X.Y）比较。
/// 网络异常、限流或版本格式异常均静默视为无可用更新，避免影响部署器启动与安装 / 卸载流程。
/// </para>
/// </summary>
internal static class UpdateCheckService
{
    private const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/splrad/CADFontAutoReplace/releases/latest";
    private const string GiteeLatestReleaseApiUrl = "https://gitee.com/api/v5/repos/splrad/CADFontAutoReplace/releases/latest";
    internal const string ReleasesPageUrl = "https://github.com/splrad/CADFontAutoReplace/releases";
    internal const string GiteeReleasesPageUrl = "https://gitee.com/splrad/CADFontAutoReplace/releases";

    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(4);

    /// <summary>检查指定发布源的最新发行版是否高于本地部署器版本。</summary>
    internal static async Task<UpdateCheckResult> CheckAsync(UpdateCheckSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(PerAttemptTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, GetLatestReleaseApiUrl(source));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AFR-Deployer", DeployerVersionService.GetDisplayVersion()));
            if (source == UpdateCheckSource.GitHub)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
            else
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode) return UpdateCheckResult.Unreachable(source);

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var release = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, cancellationToken: linked.Token);
            if (release is null) return UpdateCheckResult.Unreachable(source);
            if (release.Draft || release.Prerelease) return UpdateCheckResult.ReachableNoUpdate(source);

            var latestVersionText = NormalizeTag(release.TagName);
            var localVersionText = DeployerVersionService.GetDisplayVersion();
            if (!TryParseVersion(latestVersionText, out var latestVersion) ||
                !TryParseVersion(localVersionText, out var localVersion))
            {
                return UpdateCheckResult.Unreachable(source);
            }

            if (latestVersion <= localVersion) return UpdateCheckResult.ReachableNoUpdate(source);

            var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? GetReleasesPageUrl(source)
                : release.HtmlUrl!;
            return new UpdateCheckResult(true, true, source, latestVersionText, releaseUrl, release.Name);
        }
        catch
        {
            return UpdateCheckResult.Unreachable(source);
        }
    }

    private static string GetLatestReleaseApiUrl(UpdateCheckSource source) => source switch
    {
        UpdateCheckSource.Gitee => GiteeLatestReleaseApiUrl,
        _ => GitHubLatestReleaseApiUrl,
    };

    private static string GetReleasesPageUrl(UpdateCheckSource source) => source switch
    {
        UpdateCheckSource.Gitee => GiteeReleasesPageUrl,
        _ => ReleasesPageUrl,
    };

    private static string NormalizeTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
        return tagName.StartsWith('v') || tagName.StartsWith('V')
            ? tagName[1..]
            : tagName;
    }

    private static bool TryParseVersion(string text, out Version version)
        => Version.TryParse(text, out version!) && version.Revision < 0;

    private sealed class ReleaseDto
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

/// <summary>部署器更新检查的发布源。</summary>
internal enum UpdateCheckSource
{
    /// <summary>GitHub 官方仓库。</summary>
    GitHub,

    /// <summary>Gitee 国内镜像仓库。</summary>
    Gitee,
}

/// <summary>部署器更新检查结果。</summary>
/// <param name="HasUpdate">是否存在比本地版本更新的正式发行版。</param>
/// <param name="IsReachable">发布源是否已成功联通并返回可解析的正式版本信息。</param>
/// <param name="Source">返回结果的发布源。</param>
/// <param name="LatestVersion">最新发行版显示版本（X.Y）。</param>
/// <param name="ReleaseUrl">用于打开浏览器的发行版页面地址。</param>
/// <param name="ReleaseName">GitHub Release 标题。</param>
internal sealed record UpdateCheckResult(
    bool HasUpdate,
    bool IsReachable,
    UpdateCheckSource Source,
    string LatestVersion,
    string ReleaseUrl,
    string? ReleaseName)
{
    /// <summary>无可用更新或检查失败时的空结果。</summary>
    internal static readonly UpdateCheckResult NoUpdate = Unreachable(UpdateCheckSource.GitHub);

    /// <summary>发布源成功联通但未发现新版本。</summary>
    internal static UpdateCheckResult ReachableNoUpdate(UpdateCheckSource source) =>
        new(false, true, source, string.Empty, GetFallbackReleaseUrl(source), null);

    /// <summary>发布源不可用或返回内容不可解析。</summary>
    internal static UpdateCheckResult Unreachable(UpdateCheckSource source) =>
        new(false, false, source, string.Empty, GetFallbackReleaseUrl(source), null);

    private static string GetFallbackReleaseUrl(UpdateCheckSource source) => source switch
    {
        UpdateCheckSource.Gitee => UpdateCheckService.GiteeReleasesPageUrl,
        _ => UpdateCheckService.ReleasesPageUrl,
    };
}
