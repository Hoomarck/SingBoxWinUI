using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Services;

/// <summary>Clash API 返回的策略组（已过滤出 Selector / URLTest）。</summary>
public sealed record ProxyGroupInfo(string Name, string Type, string Now, IReadOnlyList<ProxyNodeInfo> Nodes);

/// <summary>策略组下的节点。Delay 语义：-1 未知，0 超时，>0 毫秒。</summary>
public sealed record ProxyNodeInfo(string Name, string Type, int Delay);

/// <summary>一条活动连接（sing-box /connections 的单条记录）。</summary>
public sealed record ConnectionInfo(
    string Id,
    DateTimeOffset Start,
    long Upload,
    long Download,
    string Rule,
    string RulePayload,
    IReadOnlyList<string> Chains,
    string Network,
    string InboundType,
    string InboundTag,
    string Host,
    string DestinationIp,
    int DestinationPort,
    string Process,
    string ProcessPath);

/// <summary>连接表快照：总量用于顶部统计。</summary>
public sealed record ConnectionsSnapshot(
    long UploadTotal,
    long DownloadTotal,
    IReadOnlyList<ConnectionInfo> Connections);

/// <summary>
/// 对接内核的 Clash API（external_controller）。
/// 只负责 HTTP 与解析；把结果刷到 UI 上时由调用方提供 DispatcherQueue。
/// </summary>
public sealed class ClashApiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _streamHttp;
    private readonly string _baseUrl;

    public ClashApiService(string controller, string? secret)
    {
        var normalized = string.IsNullOrWhiteSpace(controller) ? "127.0.0.1:9090" : controller.Trim();
        _baseUrl = normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? normalized.TrimEnd('/')
            : $"http://{normalized}";

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // /traffic 与 /memory 是长连接流，用 10 秒超时的客户端会在 10 秒后自己断掉
        _streamHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            var auth = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            _http.DefaultRequestHeaders.Authorization = auth;
            _streamHttp.DefaultRequestHeaders.Authorization = auth;
        }
    }

    /// <summary>测速目标地址，来自设置页（SpeedTestUrl）。</summary>
    public string SpeedTestUrl { get; set; } = AppSettings.DefaultSpeedTestUrl;

    /// <summary>统一延迟：连测两次取第二次，消除首次握手差异。来自设置页。</summary>
    public bool UnifiedDelay { get; set; } = true;

    public string BaseUrl => _baseUrl;

    /// <summary>UI 里的 Rule/Global/Direct 统一转成 API 要求的小写值。</summary>
    public static string ToApiMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "global" => "global",
        "direct" => "direct",
        _ => "rule",
    };

    /// <summary>切换代理模式（PATCH /configs）。</summary>
    public async Task SetClashModeAsync(string mode, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { mode = ToApiMode(mode) });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}/configs")
        {
            Content = content,
        };

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>拉取策略组。只保留 Selector / URLTest；存在其他 Selector 时丢掉冗余的 GLOBAL。</summary>
    public async Task<IReadOnlyList<ProxyGroupInfo>> GetProxyGroupsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/proxies", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!document.RootElement.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ProxyGroupInfo>();
        }

        // 第一遍：所有 proxy 的协议类型与最近一次延迟
        var nodeMeta = new Dictionary<string, (string Type, int Delay)>(StringComparer.Ordinal);

        foreach (var entry in proxies.EnumerateObject())
        {
            var type = entry.Value.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            var delay = -1;
            if (entry.Value.TryGetProperty("history", out var history)
                && history.ValueKind == JsonValueKind.Array
                && history.GetArrayLength() > 0)
            {
                var last = history[history.GetArrayLength() - 1];
                if (last.TryGetProperty("delay", out var delayElement) && delayElement.TryGetInt32(out var value))
                {
                    delay = value;
                }
            }

            nodeMeta[entry.Name] = (type, delay);
        }

        // 第二遍：带 all 数组的才是策略组
        var groups = new List<ProxyGroupInfo>();

        foreach (var entry in proxies.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var type = entry.Value.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (!IsSupportedGroupType(type))
            {
                continue;
            }

            var now = entry.Value.TryGetProperty("now", out var nowElement)
                ? nowElement.GetString() ?? string.Empty
                : string.Empty;

            var children = new List<ProxyNodeInfo>();
            foreach (var item in all.EnumerateArray())
            {
                var name = item.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var meta = nodeMeta.TryGetValue(name, out var found) ? found : (Type: string.Empty, Delay: -1);
                children.Add(new ProxyNodeInfo(name, meta.Type, meta.Delay));
            }

            groups.Add(new ProxyGroupInfo(entry.Name, type, now, children));
        }

        // GLOBAL 是 Clash 自带的兜底 Selector，和真实策略组并列时属于噪音
        if (groups.Count > 1)
        {
            groups.RemoveAll(g => string.Equals(g.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase));
        }

        return groups;
    }

    /// <summary>
    /// 订阅实时流量（每秒一行 {"up":n,"down":n}）。
    /// 断线后若 ct 未取消则 1 秒后自动重连。
    /// </summary>
    public async Task StreamTrafficAsync(Action<long, long> onTraffic, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadLinesAsync($"{_baseUrl}/traffic", line =>
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    onTraffic(GetLong(root, "up"), GetLong(root, "down"));
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 内核重启/断网：落到下面重连
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>逐行读取流式响应，行内容交给回调。</summary>
    private async Task ReadLinesAsync(string url, Action<string> onLine, CancellationToken ct)
    {
        using var response = await _streamHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                onLine(line);
            }
            catch (Exception)
            {
                // 单行解析失败不影响后续
            }
        }
    }

    /// <summary>拉取活动连接表。</summary>
    public async Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/connections", ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var list = new List<ConnectionInfo>();
        if (root.TryGetProperty("connections", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var info = ParseConnection(element);
                if (info is not null)
                {
                    list.Add(info);
                }
            }
        }

        return new ConnectionsSnapshot(
            GetLong(root, "uploadTotal"),
            GetLong(root, "downloadTotal"),
            list);
    }

    /// <summary>掐断单条连接。</summary>
    public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"{_baseUrl}/connections/{Uri.EscapeDataString(id)}",
            ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>清空所有活动连接。</summary>
    public async Task CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"{_baseUrl}/connections", ct);
        response.EnsureSuccessStatusCode();
    }

    private static ConnectionInfo? ParseConnection(JsonElement element)
    {
        var id = GetString(element, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var metadata = element.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? meta
            : default;

        var chains = new List<string>();
        if (element.TryGetProperty("chains", out var chainArray) && chainArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var chain in chainArray.EnumerateArray())
            {
                if (chain.ValueKind == JsonValueKind.String)
                {
                    var tag = chain.GetString();
                    if (!string.IsNullOrEmpty(tag))
                    {
                        chains.Add(tag);
                    }
                }
            }
        }

        DateTimeOffset.TryParse(
            GetString(element, "start"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var start);

        return new ConnectionInfo(
            id,
            start,
            GetLong(element, "upload"),
            GetLong(element, "download"),
            GetString(element, "rule"),
            GetString(element, "rulePayload"),
            chains,
            GetString(metadata, "network"),
            GetString(metadata, "type"),
            GetString(metadata, "inbound"),
            GetString(metadata, "host"),
            GetString(metadata, "destinationIP"),
            GetPort(metadata, "destinationPort"),
            GetString(metadata, "process"),
            GetString(metadata, "processPath"));
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>端口在 Clash 系 API 里可能是字符串，兼容两种写法。</summary>
    private static int GetPort(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var port) ? port : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    /// <summary>切换 Selector 组的当前节点。</summary>
    public async Task SelectProxyAsync(string groupName, string proxyName, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { name = proxyName });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _http.PutAsync(
            $"{_baseUrl}/proxies/{Uri.EscapeDataString(groupName)}",
            content,
            ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 单节点延迟测试。返回毫秒，超时/失败返回 0。
    /// 开启 UnifiedDelay 时连续探测两次，取第二次结果：第一次用于完成握手预热，
    /// 抹平不同协议首次连接建立耗时的差异。
    /// </summary>
    public async Task<int> GetNodeDelayAsync(string nodeName, CancellationToken ct = default)
    {
        var first = await ProbeOnceAsync(nodeName, ct);

        if (!UnifiedDelay)
        {
            return first;
        }

        // 第一次就超时/失败，直接判死，不再发第二次（否则超时节点要等两个 timeout）
        if (first <= 0)
        {
            return 0;
        }

        var second = await ProbeOnceAsync(nodeName, ct);

        // 第二次意外失败时回退用第一次的结果
        return second > 0 ? second : first;
    }

    /// <summary>发起一次延迟探测。</summary>
    private async Task<int> ProbeOnceAsync(string nodeName, CancellationToken ct)
    {
        var url = $"{_baseUrl}/proxies/{Uri.EscapeDataString(nodeName)}/delay"
                  + $"?url={Uri.EscapeDataString(SpeedTestUrl)}&timeout=5000";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);

            return document.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>并发（上限 10）测整组，每测完一个立刻通过 DispatcherQueue 刷新卡片。</summary>
    public async Task TestGroupDelayAsync(ProxyGroupItem group, DispatcherQueue dispatcher, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(10);

        var tasks = group.Nodes.Select(async node =>
        {
            // 只有真的拿到许可才能 Release：否则取消路径会把信号量计数顶高，
            // 并发上限被逐渐侵蚀（最多能同时跑 11、12… 个探测）。
            var acquired = false;

            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                var delay = await GetNodeDelayAsync(node.Name, ct).ConfigureAwait(false);
                dispatcher.TryEnqueue(() => node.Delay = delay);
            }
            catch (OperationCanceledException)
            {
                // 取消（切页 / 停内核）：保留上一次的延迟值，
                // 不能把整组节点误标成 Timeout(0)，否则用户回来看到一片红。
            }
            catch (Exception)
            {
                dispatcher.TryEnqueue(() => node.Delay = 0);
            }
            finally
            {
                if (acquired)
                {
                    gate.Release();
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static bool IsSupportedGroupType(string type) =>
        string.Equals(type, "Selector", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "URLTest", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
    }
}
