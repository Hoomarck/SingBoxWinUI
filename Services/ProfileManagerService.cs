using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Models;

namespace SingBoxWinUI.Services;

/// <summary>订阅响应头 Subscription-Userinfo 解析结果（均为可选）。</summary>
public sealed record SubscriptionUserInfo(long? Upload, long? Download, long? Total, long? Expire);

/// <summary>一次解析同时得到节点数与流量/到期兜底提示，避免同一份配置被解析多次。</summary>
public sealed record ProfileSummary(int NodeCount, string TrafficHint, string ExpireHint)
{
    public static readonly ProfileSummary Empty = new(0, string.Empty, string.Empty);
}

/// <summary>
/// 订阅管理：Core\profiles\{id}.json 存配置本体，Core\profiles.json 存元数据。
/// 所有会触发 UI 更新的方法都必须在 UI 线程调用。
/// </summary>
public sealed class ProfileManagerService
{
    private const string UserAgent = "sing-box/1.13.7";

    /// <summary>不算"真实节点"的出站类型。</summary>
    private static readonly HashSet<string> NonNodeOutboundTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "block", "dns", "selector", "urltest",
    };

    // 节点名里的流量/到期提示，编译一次复用（旧实现每次调用都重新编译正则）
    private static readonly Regex RemainingTrafficRegex =
        new(@"(剩余流量|剩余)[:：]\s*([\d.]+\s*[KMGT]B)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UsedTrafficRegex =
        new(@"(已用|已使用)[:：]\s*([\d.]+\s*[KMGT]B)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExpireDateRegex =
        new(@"(套餐到期|到期|过期|Expire)[:：]?\s*(\d{4}[-/.]\d{1,2}[-/.]\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettingsService _settings;
    private readonly string _coreDirectory;
    private readonly string _profilesDirectory;
    private readonly string _storePath;

    public ProfileManagerService(AppSettingsService settings, string coreDirectory)
    {
        _settings = settings;
        _coreDirectory = coreDirectory;
        _profilesDirectory = Path.Combine(coreDirectory, "profiles");
        _storePath = Path.Combine(coreDirectory, "profiles.json");
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    public string ActiveProfileId { get; private set; } = string.Empty;

    public ProfileItem? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    /// <summary>当前激活订阅的配置文件绝对路径。</summary>
    public string? ActiveProfilePath =>
        ActiveProfile is { } profile ? GetProfilePath(profile) : null;

    public event EventHandler? ActiveProfileChanged;

    /// <summary>激活订阅的名称/URL/周期被修改后触发。</summary>
    public event EventHandler? ActiveProfileMetadataChanged;

    /// <summary>激活订阅被后台更新后触发（用于提示/热重启）。</summary>
    public event EventHandler<ProfileItem>? ActiveProfileUpdated;

    public string GetProfilePath(ProfileItem item) => Path.Combine(_profilesDirectory, item.FileName);

    public void Load()
    {
        Directory.CreateDirectory(_profilesDirectory);

        ActiveProfileId = _settings.Current.ActiveProfileId;

        // 恢复备份后会再调一次 Load，先清空避免重复
        Profiles.Clear();

        if (File.Exists(_storePath))
        {
            try
            {
                var store = JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(_storePath), JsonDefaults.IndentedRelaxed);
                foreach (var item in store?.Profiles ?? new List<ProfileItem>())
                {
                    Profiles.Add(item);
                }
            }
            catch (Exception)
            {
                // 元数据损坏就当没有，走迁移重建
                Profiles.Clear();
            }
        }

        if (Profiles.Count == 0)
        {
            MigrateDefaultConfig();
            return;
        }

        if (string.IsNullOrEmpty(ActiveProfileId) || Profiles.All(p => p.Id != ActiveProfileId))
        {
            ActiveProfileId = Profiles[0].Id;
            _settings.Current.ActiveProfileId = ActiveProfileId;
            _settings.Save();
        }

        RefreshActiveFlags();
        BackfillTagHints();
    }

    /// <summary>
    /// 老版本存的 profiles.json 没有流量/到期字段，这里对缺失的订阅补扫一次节点名，
    /// 避免升级后主页卡片一直是空的。
    /// </summary>
    private void BackfillTagHints()
    {
        var changed = false;

        foreach (var profile in Profiles)
        {
            if (!string.IsNullOrEmpty(profile.ParsedTrafficHint)
                || !string.IsNullOrEmpty(profile.ParsedExpireHint)
                || profile.TotalBytes is not null)
            {
                continue;
            }

            var path = GetProfilePath(profile);
            if (!File.Exists(path))
            {
                continue;
            }

            ApplySummary(profile, path);
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>首次使用：把现有的 Core\config.json 复制成第一个订阅。</summary>
    private void MigrateDefaultConfig()
    {
        var source = Path.Combine(_coreDirectory, "config.json");
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.json";
        var target = Path.Combine(_profilesDirectory, fileName);

        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
            }
        }
        catch (Exception)
        {
            // 复制失败也继续登记，后续用户可重新导入
        }

        var item = new ProfileItem
        {
            Id = id,
            Name = "默认配置",
            IsRemote = false,
            FileName = fileName,
            LastUpdated = DateTimeOffset.Now,
        };

        ApplySummary(item, target);

        Profiles.Add(item);
        SetActiveInternal(item);
        Save();
    }

    public async Task<ProfileItem> ImportOnlineAsync(string name, string url, int intervalMinutes)
    {
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.json";
        var path = Path.Combine(_profilesDirectory, fileName);

        var userInfo = await DownloadToFileAsync(url, path);

        var item = new ProfileItem
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"订阅 {Profiles.Count + 1}" : name.Trim(),
            IsRemote = true,
            Url = url.Trim(),
            FileName = fileName,
            UpdateIntervalMinutes = intervalMinutes,
            LastUpdated = DateTimeOffset.Now,
        };

        ApplyUserInfo(item, userInfo);

        // 解析放后台：机场订阅常有上千节点，几次全量 JSON 解析就能把 UI 卡住几百毫秒
        await ApplySummaryAsync(item, path);

        Profiles.Add(item);
        Save();
        return item;
    }

    /// <summary>把本地 .json 复制进 profiles 目录。</summary>
    public ProfileItem ImportLocal(string sourcePath, string? name = null)
    {
        // 先校验，别把垃圾文件塞进来
        var text = File.ReadAllText(sourcePath);
        ValidateJson(text, sourcePath);

        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.json";
        var target = Path.Combine(_profilesDirectory, fileName);

        File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var item = new ProfileItem
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(),
            IsRemote = false,
            FileName = fileName,
            LastUpdated = DateTimeOffset.Now,
        };

        ApplySummary(item, target);

        Profiles.Add(item);
        Save();
        return item;
    }

    /// <summary>重命名 / 改 URL / 改周期。</summary>
    public void UpdateMetadata(ProfileItem item, string name, string url, int intervalMinutes)
    {
        item.Name = string.IsNullOrWhiteSpace(name) ? item.Name : name.Trim();

        if (item.IsRemote)
        {
            item.Url = url.Trim();
        }

        item.UpdateIntervalMinutes = intervalMinutes;
        Save();

        if (item.Id == ActiveProfileId)
        {
            ActiveProfileMetadataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 更新订阅内容。先下到 .tmp 校验通过再替换，避免把坏配置写成正式文件。
    /// </summary>
    public async Task<bool> UpdateProfileAsync(ProfileItem item)
    {
        if (!item.IsRemote || string.IsNullOrWhiteSpace(item.Url))
        {
            return false;
        }

        var target = GetProfilePath(item);
        var temp = target + ".tmp";

        try
        {
            var userInfo = await DownloadToFileAsync(item.Url, temp);
            File.Move(temp, target, overwrite: true);

            ApplyUserInfo(item, userInfo);

            // 一次解析同时拿到节点数与流量/到期兜底提示，且不占用 UI 线程
            await ApplySummaryAsync(item, target);

            item.LastUpdated = DateTimeOffset.Now;
            Save();

            if (item.Id == ActiveProfileId)
            {
                ActiveProfileUpdated?.Invoke(this, item);
            }

            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception)
            {
                // 清理失败无所谓
            }
        }
    }

    public bool IsDue(ProfileItem item)
    {
        if (!item.IsRemote || item.UpdateIntervalMinutes <= 0)
        {
            return false;
        }

        if (item.LastUpdated is not { } last)
        {
            return true;
        }

        return DateTimeOffset.Now - last >= TimeSpan.FromMinutes(item.UpdateIntervalMinutes);
    }

    public void SetActive(ProfileItem item)
    {
        if (item.Id == ActiveProfileId)
        {
            return;
        }

        SetActiveInternal(item);
        Save();
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除订阅文件与条目。调用方负责先校验是否允许删除。</summary>
    public void Delete(ProfileItem item)
    {
        try
        {
            var path = GetProfilePath(item);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 文件占用等，忽略
        }

        Profiles.Remove(item);

        if (item.Id == ActiveProfileId && Profiles.Count > 0)
        {
            SetActiveInternal(Profiles[0]);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }

        Save();
    }

    public void Save()
    {
        try
        {
            var store = new ProfileStore { Profiles = Profiles.ToList() };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(store, JsonDefaults.IndentedRelaxed), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 只读目录等，静默失败
        }
    }

    private void SetActiveInternal(ProfileItem item)
    {
        ActiveProfileId = item.Id;
        _settings.Current.ActiveProfileId = item.Id;
        _settings.Save();
        RefreshActiveFlags();
    }

    private void RefreshActiveFlags()
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = profile.Id == ActiveProfileId;
        }
    }

    /// <summary>下载并校验配置，顺带解析 Subscription-Userinfo 响应头。</summary>
    private async Task<SubscriptionUserInfo?> DownloadToFileAsync(string url, string targetPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await Http.SendAsync(request).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();

        var userInfo = ParseSubscriptionUserInfo(response);

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        // JSON 解析是 CPU 密集的（机场订阅常有上千节点），放后台线程做，别卡 UI
        await Task.Run(() => ValidateJson(text, url)).ConfigureAwait(true);
        await File.WriteAllTextAsync(targetPath, text, new UTF8Encoding(false)).ConfigureAwait(true);

        return userInfo;
    }

    /// <summary>解析 Subscription-Userinfo：upload=1; download=2; total=3; expire=1798761600</summary>
    private static SubscriptionUserInfo? ParseSubscriptionUserInfo(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Subscription-Userinfo", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        long? upload = null;
        long? download = null;
        long? total = null;
        long? expire = null;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], out var number))
            {
                continue;
            }

            switch (pair[0].ToLowerInvariant())
            {
                case "upload":
                    upload = number;
                    break;
                case "download":
                    download = number;
                    break;
                case "total":
                    total = number;
                    break;
                case "expire":
                    expire = number > 0 ? number : null;
                    break;
            }
        }

        return upload is null && download is null && total is null && expire is null
            ? null
            : new SubscriptionUserInfo(upload, download, total, expire);
    }

    /// <summary>
    /// 通道 2：从 outbounds 的节点名里兜底提取流量/到期信息。
    /// 与节点计数共用同一次 JSON 解析（旧实现 ValidateJson / RefreshTagHints / CountNodes
    /// 各解析一遍全文，一份 2–5 MB 的机场订阅等于白白多花两倍 CPU）。
    /// </summary>
    private static ProfileSummary ParseSummary(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return ProfileSummary.Empty;
            }

            var root = JsonNode.Parse(File.ReadAllText(configPath), nodeOptions: null, documentOptions: JsonDefaults.DocumentOptions)
                as JsonObject;

            if (root?["outbounds"] is not JsonArray outbounds)
            {
                return ProfileSummary.Empty;
            }

            var nodeCount = 0;
            string? traffic = null;
            string? expire = null;

            foreach (var node in outbounds.OfType<JsonObject>())
            {
                var type = node["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeText)
                    ? typeText
                    : string.Empty;

                if (!NonNodeOutboundTypes.Contains(type))
                {
                    nodeCount++;
                }

                var tag = node["tag"] is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : string.Empty;

                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                if (traffic is null)
                {
                    // 剩余流量：117.74 GB / 已用流量：1.2 GB / 总流量：100 GB
                    var remaining = RemainingTrafficRegex.Match(tag);
                    if (remaining.Success)
                    {
                        traffic = $"剩余 {remaining.Groups[2].Value.Trim()}";
                    }
                    else
                    {
                        var used = UsedTrafficRegex.Match(tag);
                        if (used.Success)
                        {
                            traffic = $"已用 {used.Groups[2].Value.Trim()}";
                        }
                    }
                }

                if (expire is null)
                {
                    var date = ExpireDateRegex.Match(tag);
                    if (date.Success)
                    {
                        expire = date.Groups[2].Value.Replace('/', '-').Replace('.', '-');
                    }
                }
            }

            return new ProfileSummary(nodeCount, traffic ?? string.Empty, expire ?? string.Empty);
        }
        catch (Exception)
        {
            // 解析失败不影响导入流程
            return ProfileSummary.Empty;
        }
    }

    private static void ApplySummary(ProfileItem item, string configPath)
    {
        var summary = ParseSummary(configPath);
        item.NodeCount = summary.NodeCount;
        item.ParsedTrafficHint = summary.TrafficHint;
        item.ParsedExpireHint = summary.ExpireHint;
    }

    private static Task<ProfileSummary> ParseSummaryAsync(string configPath) =>
        Task.Run(() => ParseSummary(configPath));

    private static async Task ApplySummaryAsync(ProfileItem item, string configPath)
    {
        var summary = await ParseSummaryAsync(configPath);
        item.NodeCount = summary.NodeCount;
        item.ParsedTrafficHint = summary.TrafficHint;
        item.ParsedExpireHint = summary.ExpireHint;
    }

    private static void ApplyUserInfo(ProfileItem item, SubscriptionUserInfo? info)
    {
        if (info is null)
        {
            return;
        }

        item.UploadBytes = info.Upload ?? item.UploadBytes;
        item.DownloadBytes = info.Download ?? item.DownloadBytes;
        item.TotalBytes = info.Total ?? item.TotalBytes;
        item.ExpireTimestamp = info.Expire ?? item.ExpireTimestamp;
    }

    private static void ValidateJson(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"内容为空：{source}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(
                text,
                nodeOptions: null,
                documentOptions: JsonDefaults.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"不是合法的 JSON（{source}）：{ex.Message}");
        }

        if (node is not JsonObject)
        {
            throw new InvalidDataException($"根节点不是 JSON 对象：{source}");
        }
    }
}
