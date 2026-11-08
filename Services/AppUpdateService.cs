using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SingBoxWinUI.Services;

/// <summary>GitHub 最新 Release 的解析结果（版本号已统一成 vX.Y.Z）。</summary>
public sealed record AppReleaseInfo(string VersionText, string ReleaseUrl, bool IsNewer);

/// <summary>
/// 客户端自身版本与 GitHub Release 检查。
/// 与内核更新服务分开：这里只读取版本信息并给出下载页链接，不下载、不换文件。
/// </summary>
public static class AppUpdateService
{
    public const string RepositoryUrl = "https://github.com/Hoomarck/SingBoxWinUI";

    private const string ApiUrl = "https://api.github.com/repos/Hoomarck/SingBoxWinUI/releases/latest";

    private static readonly Version CurrentVersion =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>v1.0.0（取 Major.Minor.Build，丢掉 Revision）。</summary>
    public static string CurrentVersionText =>
        $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    /// <summary>最近一次检查结果：设置页晚于启动检查创建时也能立刻拿到状态。</summary>
    public static AppReleaseInfo? LastResult { get; private set; }

    /// <summary>启动期后台检查完成后广播，设置页据此刷新状态。</summary>
    public static event EventHandler<AppReleaseInfo>? CheckCompleted;

    /// <summary>
    /// 查询最新 Release。内核在跑就先借道本地 mixed 代理（5s 超时），失败或未运行则直连。
    /// </summary>
    public static async Task<AppReleaseInfo> CheckAsync(int? mixedPort, CancellationToken cancellationToken = default)
    {
        var json = await FetchAsync(mixedPort, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;

        var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString() ?? RepositoryUrl
            : RepositoryUrl;

        var info = new AppReleaseInfo(NormalizeTag(tag), releaseUrl, IsNewer(tag));
        LastResult = info;
        CheckCompleted?.Invoke(null, info);
        return info;
    }

    private static async Task<string> FetchAsync(int? mixedPort, CancellationToken cancellationToken)
    {
        if (mixedPort is > 0)
        {
            try
            {
                return await GetAsync(mixedPort, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 代理借道失败：自动回退直连
            }
        }

        return await GetAsync(null, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetAsync(int? mixedPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = mixedPort is > 0,
            Proxy = mixedPort is > 0 ? new WebProxy($"http://127.0.0.1:{mixedPort}") : null,
        };

        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.ParseAdd("SingBoxWinUI");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeTag(string tag)
    {
        var trimmed = tag.Trim();
        var body = trimmed.TrimStart('v', 'V');
        return Version.TryParse(body, out var version)
            ? $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}"
            : (trimmed.StartsWith('v') ? trimmed : "v" + trimmed);
    }

    private static bool IsNewer(string tag)
    {
        var body = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(body, out var release))
        {
            return false;
        }

        var normalized = new Version(release.Major, release.Minor, Math.Max(release.Build, 0));
        var current = new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(CurrentVersion.Build, 0));
        return normalized > current;
    }
}
