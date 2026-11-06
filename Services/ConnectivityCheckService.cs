using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SingBoxWinUI.Services;

/// <summary>出口 IP、地区与运营商信息。</summary>
public sealed record ExitIpInfo(string Ip, string Country, string Isp);

/// <summary>一次连接性检测的结果；延迟为 -1 表示失败或超时。</summary>
public sealed record ConnectivityResult(
    ExitIpInfo? ExitIp,
    long GoogleMs,
    long GitHubMs,
    long CloudflareMs,
    bool ViaProxy,
    string? Error);

/// <summary>
/// 出口 IP 与多站点连通性探测。
/// 只做单次短请求：4 秒超时、ResponseHeadersRead 拿到响应头即结束，
/// 不给后台轻量模式留长连接或定时器。
/// </summary>
public sealed class ConnectivityCheckService
{
    private const int RequestTimeoutMs = 4000;

    private static readonly (string Name, string Url)[] LatencyTargets =
    [
        ("Google", "https://www.gstatic.com/generate_204"),
        ("GitHub", "https://api.github.com"),
        ("Cloudflare", "https://cp.cloudflare.com/generate_204"),
    ];

    /// <summary>
    /// mixedPort &gt; 0 时走本地 mixed/http 代理，否则直连。
    /// IP 查询按多源顺序回退，延迟探测 3 个目标并发执行。
    /// </summary>
    public async Task<ConnectivityResult> CheckAsync(int? mixedPort, CancellationToken ct)
    {
        var useProxy = mixedPort is > 0;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = useProxy ? new WebProxy($"http://127.0.0.1:{mixedPort}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AllowAutoRedirect = true,
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        var ipTask = ResolveExitIpAsync(client, ct);
        var latencyTask = MeasureLatencyAsync(client, ct);

        await Task.WhenAll(ipTask, latencyTask);

        var ip = await ipTask;
        var latency = await latencyTask;

        string? error = null;
        if (ip is null)
        {
            error = "exit-ip-unavailable";
        }

        return new ConnectivityResult(
            ip,
            latency[0],
            latency[1],
            latency[2],
            useProxy,
            error);
    }

    /// <summary>出口 IP 多源回退：任一成功即返回。</summary>
    private static async Task<ExitIpInfo?> ResolveExitIpAsync(HttpClient client, CancellationToken ct)
    {
        foreach (var source in new[] { IpSource.IpSb, IpSource.IpWhoIs, IpSource.IpInfo })
        {
            try
            {
                var text = await GetStringAsync(client, UrlOf(source), ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var ip = FirstString(root, "ip");
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }

                var country = FirstString(root, "country", "country_code");
                var isp = source switch
                {
                    IpSource.IpSb => FirstString(root, "isp", "organization"),
                    IpSource.IpWhoIs => NestedString(root, "connection", "isp", "org"),
                    _ => FirstString(root, "org"),
                };

                return new ExitIpInfo(ip, country, isp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 单个源失败就换下一个，不把异常抛到 UI
            }
        }

        // 纯文本兜底：Cloudflare trace 在 DNS/REST 全崩时通常还能拿到 IP
        try
        {
            var text = await GetStringAsync(client, "https://1.1.1.1/cdn-cgi/trace", ct);
            string? ip = null;
            string? loc = null;

            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0].Trim() == "ip") ip = parts[1].Trim();
                if (parts[0].Trim() == "loc") loc = parts[1].Trim();
            }

            return string.IsNullOrWhiteSpace(ip)
                ? null
                : new ExitIpInfo(ip, loc ?? string.Empty, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private enum IpSource
    {
        IpSb,
        IpWhoIs,
        IpInfo,
    }

    private static string UrlOf(IpSource source) => source switch
    {
        IpSource.IpSb => "https://api.ip.sb/geoip",
        IpSource.IpWhoIs => "https://ipwho.is/",
        _ => "https://ipinfo.io/json",
    };

    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeoutMs);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private static async Task<long[]> MeasureLatencyAsync(HttpClient client, CancellationToken ct)
    {
        var tasks = LatencyTargets.Select(async target =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeoutMs);

                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(
                    target.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                watch.Stop();
                return response.IsSuccessStatusCode ? watch.ElapsedMilliseconds : -1L;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return -1L;
            }
        }).ToArray();

        try
        {
            return await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return [-1L, -1L, -1L];
        }
    }

    private static string FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text!;
                }
            }
        }

        return string.Empty;
    }

    private static string NestedString(JsonElement root, string parent, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(parent, out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return FirstString(nested, names);
        }

        return string.Empty;
    }
}
