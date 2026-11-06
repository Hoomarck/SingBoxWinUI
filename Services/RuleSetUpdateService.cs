using System.Text.Json.Serialization;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Services;

/// <summary>
/// 规则集条目状态。这里存的是语言无关的稳定键，界面上由
/// <see cref="LocalizationService.RuleSetStatusText"/> 翻成当前语言 ——
/// 状态会被反复比较（Ready 才参与更新、PendingRestart 要保留），
/// 若直接用中文字面量，切到英文后比较就全断了。
/// </summary>
public static class RuleSetStatus
{
    public const string Ready = "ready";
    public const string Updating = "updating";
    public const string Updated = "updated";
    public const string Skipped = "skipped";
    public const string Missing = "missing";
    public const string Custom = "custom";

    /// <summary>内核运行中，新版本已下到待更新队列，重启内核后生效。</summary>
    public const string PendingRestart = "pending";

    /// <summary>失败态的前缀，后面接下载/校验失败的具体原因。</summary>
    public const string FailedPrefix = "fail:";

    public static string Failed(string reason) => FailedPrefix + reason;
}

/// <summary>一个本地规则集的扫描结果。Status 会在更新过程中被刷新。</summary>
public sealed partial class RuleSetFileInfo : ObservableObject
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    public RuleSetFileInfo(string fileName, string fullPath, long sizeBytes, DateTime lastWriteTime, string remoteUrl, bool isUpdatable)
    {
        FileName = fileName;
        FullPath = fullPath;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
        RemoteUrl = remoteUrl;
        IsUpdatable = isUpdatable;

        var category = RuleSetUpdateService.GetCategory(fileName);
        Category = string.IsNullOrEmpty(category) ? RuleSetStatus.Custom : category;
    }

    public string FileName { get; }

    public string FullPath { get; }

    public string RemoteUrl { get; }

    /// <summary>geosite_/geoip_ 前缀的文件才参与自动更新；direct.json 这类是用户自建，跳过。</summary>
    public bool IsUpdatable { get; }

    public string Category { get; }

    public long SizeBytes { get; }

    public DateTime LastWriteTime { get; }

    [JsonIgnore]
    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.0} KB";

    [JsonIgnore]
    public string ModifiedText => LastWriteTime == default
        ? "—"
        : LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    /// <summary>单文件状态：待更新 / 更新中 / 已更新 / 跳过 / 失败原因。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = RuleSetStatus.Ready;

    /// <summary>界面上显示的状态文案，跟随当前语言。</summary>
    [JsonIgnore]
    public string StatusText => LocalizationService.Instance.RuleSetStatusText(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>切语言后重新取一遍文案（状态键本身不变）。</summary>
    public void RefreshStatusText() => OnPropertyChanged(nameof(StatusText));

    public void ResetStatus() => Status = IsUpdatable ? RuleSetStatus.Ready : RuleSetStatus.Custom;
}

/// <summary>一次批量更新的统计。</summary>
public sealed record RuleSetUpdateResult(
    int Updated,
    int Pending,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Failures)
{
    /// <summary>统一的结果文案，App 定时更新与设置页手动更新共用一份。</summary>
    public string Describe() =>
        LocalizationService.Instance.RuleSetResult(Updated, Pending, Skipped, Failed);
}

/// <summary>
/// 规则集更新：扫描 rulesets\、按模板拼远程地址、优先走自身代理下载、
/// 校验通过后原子替换本地文件。
/// </summary>
public sealed class RuleSetUpdateService
{
    /// <summary>默认远程模板：MetaCubeX/meta-rules-dat 的 sing-box 源格式规则集。</summary>
    public const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/{category}/{short}.json";

    private readonly AppSettingsService _settings;
    private readonly MainViewModel _main;
    private readonly string _rulesetsDirectory;
    private readonly string _pendingDirectory;
    private readonly string _coreDirectory;

    public RuleSetUpdateService(AppSettingsService settings, MainViewModel main, string coreDirectory)
    {
        _settings = settings;
        _main = main;
        _coreDirectory = coreDirectory;

        // running_config 里写的是 ../rulesets，相对 Core\ 解析后就是程序目录下的 rulesets
        _rulesetsDirectory = Path.Combine(Path.GetDirectoryName(coreDirectory) ?? coreDirectory, "rulesets");

        // 内核运行中下载到的新版本先放这里，等内核停下、下次启动前再原子替换。
        // 直接覆盖正在被懒加载的规则集会让内核 exit 1。
        _pendingDirectory = Path.Combine(_rulesetsDirectory, PendingDirectoryName);
    }

    private const string PendingDirectoryName = ".pending";

    public string PendingDirectory => _pendingDirectory;

    public string CoreDirectory => _coreDirectory;

    /// <summary>从文件名推断目录分类：geosite_xxx → geosite，geoip_xxx → geoip。</summary>
    public static string GetCategory(string fileName)
    {
        if (fileName.StartsWith("geosite_", StringComparison.OrdinalIgnoreCase))
        {
            return "geosite";
        }

        return fileName.StartsWith("geoip_", StringComparison.OrdinalIgnoreCase) ? "geoip" : string.Empty;
    }

    /// <summary>geosite_onedrive.json → onedrive（去掉分类前缀，远程仓库就是这么命名的）。</summary>
    public static string GetShortName(string fileName)
    {
        var bare = Path.GetFileNameWithoutExtension(fileName);
        var category = GetCategory(fileName);

        return string.IsNullOrEmpty(category)
            ? bare
            : bare[category.Length..].TrimStart('_');
    }

    /// <summary>按模板拼远程地址；支持 {category} {short} {name} {file}。</summary>
    public static string BuildUrl(string template, string fileName)
    {
        var category = GetCategory(fileName);
        if (string.IsNullOrEmpty(category))
        {
            category = "geosite";
        }

        var url = string.IsNullOrWhiteSpace(template) ? DefaultBaseUrl : template;

        return url
            .Replace("{category}", category, StringComparison.OrdinalIgnoreCase)
            .Replace("{short}", GetShortName(fileName), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", fileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>扫描本地规则集（目录文件 + 配置引用但缺失的）。</summary>
    public List<RuleSetFileInfo> Scan()
    {
        var template = _settings.Current.RuleSetBaseUrl;
        var result = new List<RuleSetFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_rulesetsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_rulesetsDirectory)
                         .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                     || p.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);
                var info = new FileInfo(path);

                result.Add(new RuleSetFileInfo(
                    fileName,
                    path,
                    info.Length,
                    info.LastWriteTime,
                    BuildUrl(template, fileName),
                    !string.IsNullOrEmpty(GetCategory(fileName))));

                seen.Add(fileName);
            }
        }

        // 订阅 + 前置/后置 Mixin + 运行配置里引用、但磁盘上没有的，也要列出来
        foreach (var fileName in ReferencedRuleSetFiles())
        {
            if (seen.Contains(fileName))
            {
                continue;
            }

            result.Add(new RuleSetFileInfo(
                fileName,
                Path.Combine(_rulesetsDirectory, fileName),
                0,
                default,
                BuildUrl(template, fileName),
                !string.IsNullOrEmpty(GetCategory(fileName)))
            {
                Status = RuleSetStatus.Missing,
            });
        }

        foreach (var item in result.Where(i => i.Status == RuleSetStatus.Ready))
        {
            item.ResetStatus();
        }

        // 队列里有新版本的文件，界面直接标"待更新"，避免用户以为已经生效
        foreach (var item in result)
        {
            if (File.Exists(Path.Combine(_pendingDirectory, item.FileName)))
            {
                item.Status = RuleSetStatus.PendingRestart;
            }
        }

        return result;
    }

    /// <summary>批量更新。progress 回报 (已完成, 总数, 当前文件名)。</summary>
    public async Task<RuleSetUpdateResult> UpdateAllAsync(
        IProgress<(int Done, int Total, string FileName)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_rulesetsDirectory);

        var items = Scan().Where(i => i.IsUpdatable).ToList();
        var updated = 0;
        var pending = 0;
        var skipped = 0;
        var failures = new List<string>();
        var done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, items.Count, item.FileName));

            item.Status = RuleSetStatus.Updating;

            try
            {
                switch (await DownloadAndReplaceAsync(item, ct))
                {
                    case ApplyOutcome.Replaced:
                        updated++;
                        item.Status = RuleSetStatus.Updated;
                        break;

                    case ApplyOutcome.Queued:
                        pending++;
                        item.Status = RuleSetStatus.PendingRestart;
                        break;

                    default:
                        skipped++;
                        item.Status = RuleSetStatus.Skipped;
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{item.FileName}: {ex.Message}");
                item.Status = RuleSetStatus.Failed(ex.Message);
            }

            done++;
            progress?.Report((done, items.Count, item.FileName));
        }

        _settings.Current.LastRuleSetUpdate = DateTimeOffset.Now;
        _settings.Save();

        return new RuleSetUpdateResult(updated, pending, skipped, failures.Count, failures);
    }

    /// <summary>单文件落地结果。</summary>
    private enum ApplyOutcome
    {
        /// <summary>已直接替换本地文件（内核没跑）。</summary>
        Replaced,

        /// <summary>内核运行中，已排进待更新队列，下次启动前替换。</summary>
        Queued,

        /// <summary>内容校验不通过，丢弃。</summary>
        Invalid,
    }

    /// <summary>
    /// 下载单个规则集并落地。
    /// 内核在跑时只写进 .pending 队列，绝不覆盖正在被懒加载的线上文件——
    /// 覆盖会让内核 exit 1（实测：更新到一半 router 直接崩）。
    /// </summary>
    private async Task<ApplyOutcome> DownloadAndReplaceAsync(RuleSetFileInfo item, CancellationToken ct)
    {
        var bytes = await DownloadAsync(item.RemoteUrl, item.FileName, ct);
        if (bytes.Length == 0 || !Validate(item.FileName, bytes))
        {
            return ApplyOutcome.Invalid;
        }

        var queueToPending = _main.IsKernelProcessAlive;
        var destination = queueToPending
            ? Path.Combine(_pendingDirectory, item.FileName)
            : item.FullPath;

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destination + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct);

            // 落盘后再校验一次，防止磁盘写入被截断
            if (!Validate(tempPath, await File.ReadAllBytesAsync(tempPath, ct)))
            {
                return ApplyOutcome.Invalid;
            }

            File.Move(tempPath, destination, overwrite: true);
            return queueToPending ? ApplyOutcome.Queued : ApplyOutcome.Replaced;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // 删不掉就算了，下次覆盖
                }
            }
        }
    }

    /// <summary>
    /// 把运行配置里的 remote 规则集改成本地引用。
    /// 订阅常用 { type: "remote", download_detour: 策略组 }：内核启动时要自己去 GitHub 下，
    /// 那一刻代理链还没就绪，下载必然失败并直接 FATAL，内核起不来。
    /// 这里由客户端先下好（直连 → jsDelivr 镜像 → 代理），再把配置改写成 type: local。
    /// </summary>
    public async Task<int> LocalizeRemoteRuleSetsAsync(string runningConfigPath, CancellationToken ct = default)
    {
        if (!File.Exists(runningConfigPath))
        {
            return 0;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(
                File.ReadAllText(runningConfigPath),
                nodeOptions: null,
                documentOptions: JsonDefaults.DocumentOptions);
        }
        catch (Exception)
        {
            return 0;
        }

        if (parsed is not JsonObject root
            || root["route"] is not JsonObject route
            || route["rule_set"] is not JsonArray ruleSets)
        {
            return 0;
        }

        var localized = 0;

        foreach (var node in ruleSets.OfType<JsonObject>())
        {
            if (!string.Equals(GetString(node, "type"), "remote", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = GetString(node, "url");
            var tag = GetString(node, "tag");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var fileName = LocalFileNameFor(tag!, url!);
            var target = Path.Combine(_rulesetsDirectory, fileName);

            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                try
                {
                    var bytes = await DownloadRuleSetBytesAsync(url!, fileName, ct);
                    if (!Validate(fileName, bytes))
                    {
                        // 内容不对就别改配置，让内核按原样报错，至少不会伪装成"本地正常"
                        continue;
                    }

                    Directory.CreateDirectory(_rulesetsDirectory);
                    var temp = target + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes, ct);
                    File.Move(temp, target, overwrite: true);
                }
                catch (Exception)
                {
                    continue;
                }
            }

            node["type"] = "local";
            node["path"] = Path.GetFullPath(target);
            node.Remove("url");
            node.Remove("download_detour");
            node.Remove("update_interval");

            // sing-box 1.14 要求 local 规则集显式声明 format，不再按扩展名猜
            if (node["format"] is null)
            {
                node["format"] = fileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)
                    ? "binary"
                    : "source";
            }

            localized++;
        }

        if (localized > 0)
        {
            File.WriteAllText(
                runningConfigPath,
                root.ToJsonString(JsonDefaults.Indented),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return localized;
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>geosite-cn + .srs → geosite-cn.srs（扩展名取自 URL，缺省按 srs）。</summary>
    private static string LocalFileNameFor(string tag, string url)
    {
        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".srs";
        }

        var safeTag = tag;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safeTag = safeTag.Replace(invalid, '_');
        }

        return safeTag + extension;
    }

    /// <summary>remote 规则集下载：代理 → 直连 → jsDelivr 镜像。</summary>
    private async Task<byte[]> DownloadRuleSetBytesAsync(string url, string fileName, CancellationToken ct)
    {
        var errors = new List<string>();

        if (_main.IsKernelProcessAlive && _main.MixedPort > 0)
        {
            try
            {
                return await GetBytesWithTimeoutAsync(url, $"http://127.0.0.1:{_main.MixedPort}", ProxyTimeoutSeconds, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"代理：{ex.Message}");
            }
        }

        try
        {
            return await GetBytesWithTimeoutAsync(url, proxy: null, DirectTimeoutSeconds, ct);
        }
        catch (Exception ex)
        {
            errors.Add($"直连：{ex.Message}");
        }

        foreach (var mirror in BuildJsDelivrMirrors(url))
        {
            try
            {
                return await GetBytesWithTimeoutAsync(mirror, proxy: null, CdnTimeoutSeconds, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"镜像 {new Uri(mirror).Host}：{ex.Message}");
            }
        }

        throw new HttpRequestException($"下载 {fileName} 失败（{string.Join(" | ", errors)}）");
    }

    /// <summary>
    /// raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
    ///   → https://{host}/gh/{owner}/{repo}@{branch}/{path}
    /// </summary>
    private static IEnumerable<string> BuildJsDelivrMirrors(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 4);
        if (segments.Length < 4)
        {
            yield break;
        }

        var path = $"gh/{segments[0]}/{segments[1]}@{segments[2]}/{segments[3]}";
        yield return $"https://testingcf.jsdelivr.net/{path}";
        yield return $"https://fastly.jsdelivr.net/{path}";
    }

    /// <summary>待更新队列里有多少个文件等着下次启动前替换。</summary>
    public int CountPendingRuleSets() => Directory.Exists(_pendingDirectory)
        ? Directory.EnumerateFiles(_pendingDirectory).Count(IsRuleSetFile)
        : 0;

    /// <summary>
    /// 把待更新队列里的规则集原子替换到 rulesets\。
    /// 只在"内核确实没在跑"时调用（启动流程里、check 之前），返回替换成功的数量。
    /// </summary>
    public int ApplyPendingRuleSets()
    {
        if (!Directory.Exists(_pendingDirectory))
        {
            return 0;
        }

        var applied = 0;

        foreach (var pendingPath in Directory.EnumerateFiles(_pendingDirectory).Where(IsRuleSetFile).ToList())
        {
            var fileName = Path.GetFileName(pendingPath);
            var target = Path.Combine(_rulesetsDirectory, fileName);

            try
            {
                // 排进队列时校验过一次，这里再确认一遍，防止半截文件顶替掉好文件
                if (!Validate(pendingPath, File.ReadAllBytes(pendingPath)))
                {
                    File.Delete(pendingPath);
                    continue;
                }

                Directory.CreateDirectory(_rulesetsDirectory);
                File.Move(pendingPath, target, overwrite: true);
                applied++;
            }
            catch (Exception)
            {
                // 单个失败不影响其它，留在队列里下次再试
            }
        }

        TryDeleteEmptyPendingDirectory();
        return applied;
    }

    private static bool IsRuleSetFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".srs", StringComparison.OrdinalIgnoreCase);

    private void TryDeleteEmptyPendingDirectory()
    {
        try
        {
            if (Directory.Exists(_pendingDirectory)
                && !Directory.EnumerateFileSystemEntries(_pendingDirectory).Any())
            {
                Directory.Delete(_pendingDirectory);
            }
        }
        catch (Exception)
        {
            // 删不掉就留着，空目录无害
        }
    }

    /// <summary>
    /// 免代理 CDN 镜像。内核没起来时直连 raw.githubusercontent.com 经常超时，
    /// 这两条 jsDelivr 线路国内直连是秒开的，用来打破"要拉规则集才能起内核、
    /// 要起内核才能走代理拉规则集"的死锁。
    /// </summary>
    private static readonly string[] CdnTemplates =
    {
        "https://testingcf.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/{category}/{short}.json",
        "https://fastly.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/{category}/{short}.json",
    };

    /// <summary>内核未运行时的直连超时：宁可快速失败换镜像，也别让用户干等 45 秒。</summary>
    private const int DirectTimeoutSeconds = 5;

    private const int ProxyTimeoutSeconds = 45;

    private const int CdnTimeoutSeconds = 10;

    /// <summary>
    /// 下载单个规则集：代理 → 直连(快速超时) → CDN 镜像。
    /// 内核在跑时优先借道自身 mixed 端口；没在跑就只能靠直连和镜像。
    /// </summary>
    private async Task<byte[]> DownloadAsync(string url, string fileName, CancellationToken ct)
    {
        var errors = new List<string>();

        // 1) 自身代理（内核运行中才有意义）
        if (_main.IsRunning && _main.MixedPort > 0)
        {
            try
            {
                return await GetBytesWithTimeoutAsync(
                    url, $"http://127.0.0.1:{_main.MixedPort}", ProxyTimeoutSeconds, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"代理：{ex.Message}");
            }
        }

        // 2) 直连（快速超时）
        try
        {
            return await GetBytesWithTimeoutAsync(url, proxy: null, DirectTimeoutSeconds, ct);
        }
        catch (Exception ex)
        {
            errors.Add($"直连：{ex.Message}");
        }

        // 3) CDN 镜像（仅标准 geosite_/geoip_ 命名有对应地址）
        if (!string.IsNullOrEmpty(GetCategory(fileName)))
        {
            foreach (var template in CdnTemplates)
            {
                var mirror = BuildUrl(template, fileName);
                if (string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    return await GetBytesWithTimeoutAsync(mirror, proxy: null, CdnTimeoutSeconds, ct);
                }
                catch (Exception ex)
                {
                    errors.Add($"镜像 {new Uri(mirror).Host}：{ex.Message}");
                }
            }
        }

        throw new HttpRequestException($"所有下载通道均失败（{string.Join(" | ", errors)}）");
    }

    private static async Task<byte[]> GetBytesWithTimeoutAsync(
        string url, string? proxy, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        return await GetBytesAsync(url, proxy, timeout.Token);
    }

    private static readonly HttpClient DirectClient = CreateClient(proxy: null);
    private static readonly object ProxyClientGate = new();
    private static HttpClient? _proxyClient;
    private static string? _proxyClientAddress;

    private static HttpClient CreateClient(string? proxy)
    {
        var handler = new HttpClientHandler();

        if (proxy is null)
        {
            handler.UseProxy = false;
        }
        else
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SingBoxRunner/1.0");
        return client;
    }

    /// <summary>
    /// 代理客户端按地址缓存：每次下载都 new HttpClient 会让一次批量更新产生
    /// 十几组 socket 进 TIME_WAIT；只有 mixed 端口变化（换配置）时才重建。
    /// </summary>
    private static HttpClient GetProxyClient(string proxyAddress)
    {
        lock (ProxyClientGate)
        {
            if (_proxyClient is not null
                && string.Equals(_proxyClientAddress, proxyAddress, StringComparison.Ordinal))
            {
                return _proxyClient;
            }

            _proxyClient?.Dispose();
            _proxyClient = CreateClient(proxyAddress);
            _proxyClientAddress = proxyAddress;
            return _proxyClient;
        }
    }

    private static async Task<byte[]> GetBytesAsync(string url, string? proxy, CancellationToken ct)
    {
        var client = proxy is null ? DirectClient : GetProxyClient(proxy);

        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>校验规则集内容，挡住 404 HTML / 半截文件。</summary>
    public static bool Validate(string fileName, byte[] bytes)
    {
        if (bytes.Length < 16)
        {
            return false;
        }

        if (fileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
        {
            // SRS 魔数：前三个字节是 ASCII "SRS"
            return bytes[0] == (byte)'S' && bytes[1] == (byte)'R' && bytes[2] == (byte)'S';
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("version", out _)
                   && root.TryGetProperty("rules", out var rules)
                   && rules.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 所有"配置里引用到的本地规则集"文件名，去重合并：
    /// 激活订阅 + 前置 Mixin + 后置 Mixin + running_config。
    /// Mixin 里新加的规则集如果只扫订阅就永远发现不了，这是之前漏掉的根因。
    /// </summary>
    public IEnumerable<string> ReferencedRuleSetFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sources = new List<string?>
        {
            _settings.Current.ConfigPath,
            _settings.Current.EnablePreMixin ? MixinService.GetPath(_coreDirectory, isPre: true) : null,
            _settings.Current.EnablePostMixin ? MixinService.GetPath(_coreDirectory, isPre: false) : null,
            Path.Combine(_coreDirectory, "running_config.json"),
        };

        foreach (var source in sources)
        {
            foreach (var fileName in ReadRuleSetFilesFrom(source))
            {
                if (seen.Add(fileName))
                {
                    yield return fileName;
                }
            }
        }
    }

    /// <summary>从某个配置（订阅 / mixin / running_config）里读出本地规则集文件名。</summary>
    private static IEnumerable<string> ReadRuleSetFilesFrom(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("route", out var route)
                || !route.TryGetProperty("rule_set", out var ruleSets)
                || ruleSets.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var entry in ruleSets.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type)
                    && type.GetString() is { } typeText
                    && !string.Equals(typeText, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.TryGetProperty("path", out var pathElement)
                    && pathElement.GetString() is { Length: > 0 } localPath)
                {
                    yield return Path.GetFileName(localPath.Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
    }

    /// <summary>
    /// 找出某个运行配置里引用、但磁盘上不存在（或为 0 字节）的本地规则集。
    /// 返回 (文件名, 绝对路径)，绝对路径已按配置文件所在目录解析好。
    /// </summary>
    public List<(string FileName, string FullPath)> FindMissingRuleSets(string configPath)
    {
        var result = new List<(string, string)>();
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? _coreDirectory;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in ReadRuleSetPathsFrom(configPath))
        {
            var full = Path.GetFullPath(Path.Combine(
                baseDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar)));

            if (File.Exists(full) && new FileInfo(full).Length > 0)
            {
                continue;
            }

            if (seen.Add(full))
            {
                result.Add((Path.GetFileName(full), full));
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadRuleSetPathsFrom(string configPath)
    {
        if (!File.Exists(configPath))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("route", out var route)
                || !route.TryGetProperty("rule_set", out var ruleSets)
                || ruleSets.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var entry in ruleSets.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type)
                    && type.GetString() is { } typeText
                    && !string.Equals(typeText, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.TryGetProperty("path", out var pathElement)
                    && pathElement.GetString() is { Length: > 0 } localPath)
                {
                    yield return localPath;
                }
            }
        }
    }

    /// <summary>
    /// 确保单个规则集文件就位：已存在且非空直接返回 true；
    /// 否则按文件名映射下载（代理 → 直连 → CDN 镜像）。
    /// </summary>
    public async Task<bool> EnsureFileAsync(string fileName, bool force = false, CancellationToken ct = default)
    {
        var target = Path.Combine(_rulesetsDirectory, fileName);

        // force=true 用于"空骨架兜底之后重试"：文件虽然存在，但内容是假的
        if (!force && File.Exists(target) && new FileInfo(target).Length > 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(GetCategory(fileName)))
        {
            // direct.json / proxy.json 这类自建规则没有远端来源，别去瞎猜
            return false;
        }

        try
        {
            var bytes = await DownloadAsync(BuildUrl(_settings.Current.RuleSetBaseUrl, fileName), fileName, ct);
            if (!Validate(fileName, bytes))
            {
                return false;
            }

            // 内核在跑时同样只排队：这个入口也会在"补空骨架后重试"里被调用，
            // 那次是内核运行中，直接覆盖正在加载的文件同样会让它崩
            var queueToPending = _main.IsKernelProcessAlive;
            var destination = queueToPending
                ? Path.Combine(_pendingDirectory, fileName)
                : target;

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? _rulesetsDirectory);
            var temp = destination + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, destination, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
