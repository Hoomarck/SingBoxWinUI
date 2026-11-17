using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace SingBoxWinUI.Services;

/// <summary>出口 IP、属地与运营商。Region 可能是「中国 山东 济南」或「United States」这类松散文本。</summary>
public sealed record ExitIpInfo(string Ip, string Region, string Isp);

/// <summary>一条通道（直连 / 代理）的双栈结果；某一栈没有公网地址时为 null。</summary>
public sealed record ChannelIpResult(ExitIpInfo? Ipv4, ExitIpInfo? Ipv6)
{
    public static readonly ChannelIpResult Empty = new(null, null);

    public bool HasIp => Ipv4 is not null || Ipv6 is not null;
}

/// <summary>
/// 一次连通性检测的结果。
/// Direct = 绕过系统代理的国内直连双栈；Proxy = 经本地 mixed 端口的代理出口双栈。
/// 延迟为 -1 表示失败或超时；ProxyAvailable = false 表示内核未运行、代理通道未探测。
/// </summary>
public sealed record ConnectivityResult(
    ChannelIpResult Direct,
    ChannelIpResult Proxy,
    long GoogleMs,
    long GitHubMs,
    long CloudflareMs,
    bool ProxyAvailable);

/// <summary>
/// 出口 IP 与多站点连通性探测（双通道 × 双栈）。
/// 只做单次短请求：4 秒超时、ResponseHeadersRead 拿到响应头即结束，
/// 不给后台轻量模式留长连接或定时器。
///
/// 双栈分离的做法是「纯 v4 域名 / 纯 v6 域名」物理隔离，**不在代理客户端上用 ConnectCallback 限族**：
/// 本地 mixed 端口是 127.0.0.1（IPv4），一旦给 proxyClient 的 socket 限制成 InterNetworkV6
/// 就连不上本地代理本身。直连通道同理靠域名定族，避免额外 P/Invoke 式干预。
///
/// 源链顺序为 2026-11-16 本机实测结果（见各行注释），不是照抄文档：
///   - 4.ipw.cn / 6.ipw.cn 域名已失效（ENOTFOUND），不采用；
///   - api-ipv4.ip.sb 连续返回 403（Cloudflare 拦截），只保留为末位兜底；
///   - 百度 opendata 对 IPv6 返回空 data，v6 属地图改走 ip-api / ipwho.is。
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

    /// <summary>国内直连 IPv4：全是国内站点，避免被 TUN Rule 模式当成境外流量代理掉。</summary>
    private static readonly string[] DirectV4Sources =
    [
        "https://myip.ipip.net",          // 实测 200：当前 IP：x 来自于：中国 山东 济南 电信（一次拿到 IP + 属地 + 运营商）
        "http://ip.3322.net",             // 实测 200 text/plain：纯 IP，属地另查
        "https://ddns.oray.com/checkip",  // 实测 200：Current IP Address: x，属地另查
    ];

    /// <summary>国内直连 IPv6：纯 v6 域名，没有 v6 出口时应当快速失败而不是拖住 v4。</summary>
    private static readonly string[] DirectV6Sources =
    [
        "https://api6.ipify.org?format=json",
        "https://v6.ident.me/json",
        "https://ipv6.icanhazip.com",
    ];

    /// <summary>代理出口 IPv4：纯 v4 域名，保证拿到的是代理侧 v4 而不是 v6。</summary>
    private static readonly string[] ProxyV4Sources =
    [
        "https://api-ipv4.ip.sb/geoip",
        "https://v4.ident.me/json",
        "https://api4.ipify.org?format=json",
        "https://ipinfo.io/json",
        "https://ipwho.is/",
    ];

    /// <summary>代理出口 IPv6：纯 v6 域名。</summary>
    private static readonly string[] ProxyV6Sources =
    [
        "https://api-ipv6.ip.sb/geoip",
        "https://v6.ident.me/json",
        "https://api6.ipify.org?format=json",
        "https://ipwho.is/",
    ];

    /// <summary>
    /// 直连通道永远探测（内核是否运行无关）；代理通道仅在 mixedPort &gt; 0 时探测。
    /// 5 路任务全部并发，任一路失败只影响自己那一格。
    /// </summary>
    public async Task<ConnectivityResult> CheckAsync(int? mixedPort, CancellationToken ct)
    {
        var proxyConfigured = mixedPort is > 0;

        // 端口通不代表代理能用，但不通就一定不能用：先花最多 700ms 摸一下本地端口，
        // 免得内核刚崩、端口还在监听却没上游时，5 路代理请求各等 4 秒把整次检测拖到十几秒。
        var proxyReachable = proxyConfigured && await IsLocalPortReachableAsync(mixedPort!.Value, ct);

        using var directClient = CreateClient(useProxy: false, mixedPort: null);
        using var proxyClient = proxyReachable
            ? CreateClient(useProxy: true, mixedPort: mixedPort!.Value)
            : null;

        // 延迟胶囊代表「经代理的实际体验」，代理不可用时退回直连值，至少让用户看到链路是否通。
        var latencyClient = proxyClient ?? directClient;

        var directV4 = ResolveChannelIpAsync(directClient, DirectV4Sources, isV6: false, ct);
        var directV6 = ResolveChannelIpAsync(directClient, DirectV6Sources, isV6: true, ct);
        var proxyV4 = proxyClient is null
            ? Task.FromResult<ExitIpInfo?>(null)
            : ResolveChannelIpAsync(proxyClient, ProxyV4Sources, isV6: false, ct);
        var proxyV6 = proxyClient is null
            ? Task.FromResult<ExitIpInfo?>(null)
            : ResolveChannelIpAsync(proxyClient, ProxyV6Sources, isV6: true, ct);
        var latency = MeasureLatencyAsync(latencyClient, ct);

        // 上层取消（停止 / 退出 / 切轻量模式）时 Task.WhenAll 会抛 OperationCanceledException，
        // 原样向上抛即可，由 ViewModel 决定保持旧结果。
        await Task.WhenAll(directV4, directV6, proxyV4, proxyV6, latency);

        return new ConnectivityResult(
            new ChannelIpResult(directV4.Result, directV6.Result),
            new ChannelIpResult(proxyV4.Result, proxyV6.Result),
            latency.Result[0],
            latency.Result[1],
            latency.Result[2],
            proxyConfigured);
    }

    /// <summary>
    /// 本地 mixed 端口是否在监听。只测 TCP 握手，不发 HTTP，成功即立刻断开。
    /// 这是纯优化：失败只表示"这次别去探代理"，ProxyAvailable 仍按是否给了端口来报。
    /// </summary>
    private static async Task<bool> IsLocalPortReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(700);
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return tcp.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HttpClient CreateClient(bool useProxy, int? mixedPort)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = useProxy && mixedPort is > 0 ? new WebProxy($"http://127.0.0.1:{mixedPort}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>
    /// 按顺序试每一源，第一个拿到 IP 的结果胜出。
    /// 拿到 IP 但属地全空时补一次 GeoIP；连 GeoIP 都失败也要返回只带 IP 的结果，让界面能显示地址。
    /// </summary>
    private static async Task<ExitIpInfo?> ResolveChannelIpAsync(
        HttpClient client,
        string[] sources,
        bool isV6,
        CancellationToken ct)
    {
        foreach (var url in sources)
        {
            ct.ThrowIfCancellationRequested();

            RawProbe? raw;
            try
            {
                var text = await GetStringAsync(client, url, ct);
                raw = ParsePayload(text, isV6);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue; // 单源失败换下一个，不把异常抛到 UI
            }

            if (raw is null || string.IsNullOrWhiteSpace(raw.Ip))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw.Region) || !string.IsNullOrWhiteSpace(raw.Isp))
            {
                return new ExitIpInfo(raw.Ip, raw.Region, raw.Isp);
            }

            var (region, isp) = await TryGeoLookupAsync(client, raw.Ip, isV6, ct);
            return new ExitIpInfo(raw.Ip, region, isp);
        }

        return null;
    }

    /// <summary>属地补查：v4 先用百度（中文、国内），v6 百度返回空所以先用 ip-api。</summary>
    private static async Task<(string Region, string Isp)> TryGeoLookupAsync(
        HttpClient client,
        string ip,
        bool isV6,
        CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(ip);
        var sources = isV6
            ? new[]
            {
                $"http://ip-api.com/json/{encoded}?lang=zh-CN",
                $"https://ipwho.is/{encoded}",
            }
            : new[]
            {
                $"https://opendata.baidu.com/api.php?query={encoded}&co=&resource_id=6006&oe=utf8",
                $"http://ip-api.com/json/{encoded}?lang=zh-CN",
                $"https://ipwho.is/{encoded}",
            };

        foreach (var url in sources)
        {
            try
            {
                var text = await GetStringAsync(client, url, ct);
                var geo = ParseGeoOnly(text);
                if (!string.IsNullOrWhiteSpace(geo.Region) || !string.IsNullOrWhiteSpace(geo.Isp))
                {
                    return (geo.Region, geo.Isp);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 换下一个源
            }
        }

        return (string.Empty, string.Empty);
    }

    private sealed record RawProbe(string Ip, string Region, string Isp);

    /// <summary>解析探测响应：JSON 走字段提取，纯文本走「找 IP + 找 来自于」。</summary>
    private static RawProbe? ParsePayload(string text, bool isV6)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var geo = ParseGeoElement(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(geo.Ip))
                {
                    return new RawProbe(geo.Ip, geo.Region, geo.Isp);
                }
            }
            catch (JsonException)
            {
                // 落到纯文本解析
            }
        }

        var ip = ExtractIp(text, isV6);
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var region = string.Empty;
        var isp = string.Empty;

        // myip.ipip.net：当前 IP：x 来自于：中国 山东 济南 电信
        var marker = text.IndexOf("来自于", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var tail = text[(marker + 3)..].Trim(' ', '：', ':', '\r', '\n', '\t');
            (region, isp) = SplitRegionIsp(tail);
        }

        return new RawProbe(ip, region, isp);
    }

    /// <summary>只取属地字段（已知 IP 时用）。</summary>
    private static (string Ip, string Region, string Isp) ParseGeoOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        try
        {
            if (text.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                return ParseGeoElement(doc.RootElement);
            }
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        return (string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// 各源字段名不一致，这里统一尝试常见组合：
    /// ip / query；country / country_name / countryCode；region / regionName / province；city；isp / org / aso / organization。
    /// 百度把「属地 + 运营商」塞进 data[0].location，单独处理。
    /// </summary>
    private static (string Ip, string Region, string Isp) ParseGeoElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        // 百度：data[0].location = "山东省济南市 电信"
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0)
        {
            var first = data[0];
            var location = FirstString(first, "location");
            var baiduIp = FirstString(first, "origip", "Origip", "OriginQuery");
            if (!string.IsNullOrWhiteSpace(location))
            {
                var (baiduRegion, baiduIsp) = SplitRegionIsp(location);
                return (baiduIp, baiduRegion, baiduIsp);
            }

            return (string.Empty, string.Empty, string.Empty);
        }

        var ip = FirstString(root, "ip", "query", "originip");

        var country = FirstString(root, "country", "country_name");
        var regionName = FirstString(root, "region", "regionName", "province", "region_name");
        var city = FirstString(root, "city");

        // ipinfo 只给两字母国家码，有省市时优先显示省市，避免出现「SG Singapore」这种半截文案。
        var region = BuildRegion(country, regionName, city);

        var isp = FirstString(root, "isp", "org", "organization", "asn_organization", "aso");
        if (string.IsNullOrWhiteSpace(isp))
        {
            isp = NestedString(root, "connection", "isp", "org");
        }

        if (string.IsNullOrWhiteSpace(isp))
        {
            isp = NestedString(root, "asn", "organization", "name");
        }

        return (ip, region, isp);
    }

    /// <summary>「山东省济南市 电信」→（山东省济南市, 电信）：最后一个空白段当运营商。</summary>
    private static (string Region, string Isp) SplitRegionIsp(string text)
    {
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, string.Empty);
        }

        var parts = value.Split([' ', '\t', ',', '，'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return (value, string.Empty);
        }

        var isp = parts[^1];
        var region = string.Join(' ', parts[..^1]);
        return (region, isp);
    }

    /// <summary>拼属地文案：两字母国家码在有省市信息时丢弃，其余去重后按「国家 省 市」空格连接。</summary>
    private static string BuildRegion(string country, string regionName, string city)
    {
        var isCountryCode = country.Length == 2 && country.All(char.IsAsciiLetter);
        var hasDetail = !string.IsNullOrWhiteSpace(regionName) || !string.IsNullOrWhiteSpace(city);
        return isCountryCode && hasDetail
            ? JoinDistinct(regionName, city)
            : JoinDistinct(country, regionName, city);
    }

    private static string JoinDistinct(params string[] values)
    {
        var parts = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!parts.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(value);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>从自由文本里挑出第一个符合指定地址族的 IP。</summary>
    private static string ExtractIp(string text, bool isV6)
    {
        var wanted = isV6 ? System.Net.Sockets.AddressFamily.InterNetworkV6 : System.Net.Sockets.AddressFamily.InterNetwork;

        foreach (var token in text.Split([' ', '\t', '\r', '\n', '：', ':', '，', ',', '"', '{', '}', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            // IPv6 里的冒号被当分隔符后会碎掉，所以这里只对 v4 逐段匹配；
            // v6 单独扫整段文本里的候选串。
            if (!isV6 && IPAddress.TryParse(token, out var v4) && v4.AddressFamily == wanted)
            {
                return v4.ToString();
            }
        }

        if (isV6)
        {
            foreach (var candidate in IpCandidates(text))
            {
                if (IPAddress.TryParse(candidate, out var v6) && v6.AddressFamily == wanted)
                {
                    return v6.ToString();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>按出现顺序回退所有可能的 IPv6 候选串（用 System 的解析器校验，避免手写正则漏判）。</summary>
    private static IEnumerable<string> IpCandidates(string text)
    {
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (Uri.IsHexDigit(ch) || ch == ':')
            {
                current.Append(ch);
            }
            else
            {
                if (current.Length >= 4)
                {
                    yield return current.ToString();
                }

                current.Clear();
            }
        }

        if (current.Length >= 4)
        {
            yield return current.ToString();
        }
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

    /// <summary>单次短请求：显式 UTF-8 解码，避免中文属地在 GBK 源上变乱码。</summary>
    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeoutMs);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(timeout.Token);
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
