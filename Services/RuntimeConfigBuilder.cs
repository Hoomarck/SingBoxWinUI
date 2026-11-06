using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SingBoxWinUI.Helpers;

namespace SingBoxWinUI.Services;

/// <summary>TUN 入站的详细参数（来自设置页）。</summary>
public sealed record TunSettings(
    string InterfaceName,
    string Stack,
    int Mtu,
    bool AutoRoute,
    bool StrictRoute);

/// <summary>生成运行配置所需的全部输入。</summary>
public sealed record RuntimeConfigOptions(
    string CoreDirectory,
    string RulesetsDirectory,
    bool TunEnabled,
    string ClashMode,
    TunSettings Tun,
    bool EnableIpv6,
    bool EnablePreMixin,
    bool EnablePostMixin,
    IReadOnlyDictionary<string, string>? LastSelectedNodes = null);

/// <summary>运行配置生成结果，Notes 用于回写到日志面板。</summary>
public sealed record RuntimeConfigResult(
    string Path,
    IReadOnlyList<string> Notes,
    string ClashController,
    string ClashSecret,
    int MixedPort);

/// <summary>
/// 启动前把用户的 config.json 加工成运行配置：确保 route 层必要项、按开关接管 tun 入站、
/// 补齐 clash_mode 规则与系统代理所需的 mixed 入站，输出到 Core\running_config.json，源文件保持原样。
/// </summary>
public static class RuntimeConfigBuilder
{
    public const string RuntimeConfigFileName = "running_config.json";

    private const string TunTag = "tun-in";
    private const int DefaultMixedPort = 7890;

    public static RuntimeConfigResult Build(string sourceConfigPath, RuntimeConfigOptions options)
    {
        var notes = new List<string>();
        var root = ParseRootObject(sourceConfigPath);

        // 拓展配置先合并，随后的 TUN / clash_api / sniff 等规范化才有最终兜底权
        if (options.EnablePreMixin)
        {
            ApplyMixinFile(root, options.CoreDirectory, isPrepend: true, notes);
        }

        if (options.EnablePostMixin)
        {
            ApplyMixinFile(root, options.CoreDirectory, isPrepend: false, notes);
        }

        // 内核版本兼容：sing-box 1.13/1.14 移除了一批旧写法，订阅里带这些写法会直接 check 失败
        ApplyKernelCompatMigrations(root, options.CoreDirectory, notes);

        // 占位符展开必须在两个拓展都合并完之后：此时 outbounds 里订阅节点齐全，
        // __AUTO__ / __ALL__ / __REGION:xx__ 才有东西可展开
        ExpandPlaceholders(root, notes);

        // 拓展里可以用 x_outbound_order 声明面板顺序；已存在的同 tag 出站只会原位替换，
        // 光靠 mixin 数组顺序没法把订阅自带的组挪到最前，所以单独做一遍重排
        ApplyOutboundOrder(root, notes);

        var route = GetOrCreateObject(root, "route");

        // TUN 模式下必须开，否则 sing-box 无法判定出站网卡，流量会回环死锁
        if (!TryGetBool(route["auto_detect_interface"]))
        {
            route["auto_detect_interface"] = true;
            notes.Add("route.auto_detect_interface 已设为 true");
        }

        if (options.TunEnabled)
        {
            var inbounds = root["inbounds"] as JsonArray ?? GetOrCreateArray(root, "inbounds");

            // GUI 拥有语义：TUN 开关开着，配置里的 tun 入站就由本程序决定，
            // 已有则整体替换为设置页里的参数
            var replaced = ReplaceTunInbound(inbounds, options.Tun, options.EnableIpv6);
            notes.Add(replaced > 0
                ? $"已用设置页参数替换现有 TUN 入站（移除 {replaced} 个）"
                : $"已注入 TUN 入站（{TunTag}）");
        }
        else if (root["inbounds"] is JsonArray inbounds)
        {
            var removed = RemoveTunInbounds(inbounds);
            if (removed > 0)
            {
                notes.Add($"TUN 已关闭，移除 {removed} 个 tun 入站");
            }
        }

        var rules = GetOrCreateArray(route, "rules");

        var hasSniff = rules.OfType<JsonObject>().Any(o => GetString(o, "action") == "sniff");
        var hasHijackDns = rules.OfType<JsonObject>().Any(o => GetString(o, "action") == "hijack-dns");

        if (!hasSniff)
        {
            rules.Insert(0, new JsonObject { ["action"] = "sniff" });
            hasSniff = true;
            notes.Add("route.rules 头部已补 sniff");
        }

        if (!hasHijackDns)
        {
            rules.Insert(hasSniff ? 1 : 0, new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns",
            });
            notes.Add("route.rules 头部已补 hijack-dns");
        }

        // 拓展规则是插到数组最前面的，会把原本就在头部的 sniff / hijack-dns 挤下去。
        // 这里无条件把它们拎回最顶端，保证嗅探与 DNS 劫持永远先于任何分流规则执行。
        PromoteSniffAndHijackDns(rules);

        EnsureClashModeRules(root, route, rules, notes);

        // Clash API：有就沿用用户配置，没有就注入默认地址，供节点策略页与模式切换调用
        var experimental = GetOrCreateObject(root, "experimental");
        var clashApi = GetOrCreateObject(experimental, "clash_api");

        var controller = GetString(clashApi, "external_controller");
        if (string.IsNullOrWhiteSpace(controller))
        {
            controller = "127.0.0.1:9090";
            clashApi["external_controller"] = controller;
            notes.Add($"已注入 clash_api.external_controller = {controller}");
        }

        var secret = GetString(clashApi, "secret") ?? string.Empty;

        var apiMode = ClashApiService.ToApiMode(options.ClashMode);
        if (!string.Equals(GetString(clashApi, "default_mode"), apiMode, StringComparison.Ordinal))
        {
            clashApi["default_mode"] = apiMode;
            notes.Add($"clash_api.default_mode 已设为 {apiMode}");
        }

        // 第二层保险：把客户端记住的节点写回 selector.default
        ApplySelectorDefaults(root, options.LastSelectedNodes, notes);

        // 第一层保险：cache.db 必须开 store_selected，否则内核不会持久化手动选的节点
        EnsureCacheFile(experimental, options.CoreDirectory, notes);

        EnsureDnsStrategy(root, options.EnableIpv6, notes);

        var mixedPort = EnsureMixedInbound(root, notes);

        // 规则集路径绝对化：配置可能是从别处选来的，../rulesets 会失效；
        // 只要文件能在程序自带的 rulesets 目录里找到，就固化成绝对路径
        var absolutized = AbsolutizeRuleSetPaths(root, options.CoreDirectory, options.RulesetsDirectory);
        if (absolutized > 0)
        {
            notes.Add($"已将 {absolutized} 个 rule_set 相对路径改写为绝对路径");
        }

        var targetPath = Path.Combine(options.CoreDirectory, RuntimeConfigFileName);
        var json = root.ToJsonString(JsonDefaults.Indented);
        File.WriteAllText(targetPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new RuntimeConfigResult(targetPath, notes, controller, secret, mixedPort);
    }


    /// <summary>读取并合并一个拓展文件。</summary>
    private static void ApplyMixinFile(JsonObject root, string coreDirectory, bool isPrepend, List<string> notes)
    {
        var label = isPrepend ? "前置" : "后置";
        var path = MixinService.GetPath(coreDirectory, isPrepend);

        if (!File.Exists(path))
        {
            notes.Add($"未找到全局{label}拓展文件，已跳过（{path}）");
            return;
        }

        JsonObject mixin;
        try
        {
            mixin = MixinService.Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            notes.Add($"[警告] 全局{label}拓展解析失败，已跳过：{ex.Message}");
            return;
        }

        if (mixin.Count == 0)
        {
            notes.Add($"全局{label}拓展为空，已跳过");
            return;
        }

        var changes = ApplyMixin(root, mixin, isPrepend);
        notes.Add(isPrepend
            ? $"已应用全局前置拓展（修改/新增 {changes} 项）"
            : $"已应用全局后置拓展（追加 {changes} 项）");
    }

    /// <summary>
    /// 把 mixin 合并进 target，返回改动条目数。
    /// 前置 = 覆盖优先、数组往头部插；后置 = 只补缺、数组往尾部追加。
    /// </summary>
    private static int ApplyMixin(JsonObject target, JsonObject mixin, bool isPrepend)
    {
        var changes = 0;

        foreach (var pair in mixin.ToList())
        {
            var key = pair.Key;
            var mixinValue = pair.Value;
            if (mixinValue is null)
            {
                continue;
            }

            var targetValue = target[key];

            switch (mixinValue)
            {
                case JsonObject mixinObject when targetValue is JsonObject targetObject:
                    changes += ApplyMixin(targetObject, mixinObject, isPrepend);
                    break;

                case JsonObject mixinObject:
                    target[key] = mixinObject.DeepClone();
                    changes++;
                    break;

                case JsonArray mixinArray:
                    changes += MergeArray(target, key, mixinArray, isPrepend);
                    break;

                default:
                    // 标量：前置直接覆盖，后置只在缺失时补
                    if (isPrepend || targetValue is null)
                    {
                        if (!JsonNode.DeepEquals(targetValue, mixinValue))
                        {
                            target[key] = mixinValue.DeepClone();
                            changes++;
                        }
                    }

                    break;
            }
        }

        return changes;
    }

    /// <summary>数组合并：带 tag 的按 tag 覆盖/插入，不带的按顺序插头或追尾。</summary>
    private static int MergeArray(JsonObject target, string key, JsonArray mixinArray, bool isPrepend)
    {
        if (target[key] is not JsonArray targetArray)
        {
            target[key] = mixinArray.DeepClone();
            return mixinArray.Count;
        }

        // 全部元素都带 tag 才走 keyed 合并
        var isTagged = mixinArray.Count > 0
                       && mixinArray.All(node => node is JsonObject obj && GetString(obj, "tag") is not null);

        if (isTagged)
        {
            foreach (var node in mixinArray)
            {
                var source = (JsonObject)node!;
                var tag = GetString(source, "tag")!;
                var index = IndexOfTag(targetArray, tag);

                if (index >= 0)
                {
                    // 原位替换：既能改原订阅的同名出站，又不会撞 duplicate tag
                    targetArray[index] = source.DeepClone();
                }
                else if (isPrepend)
                {
                    targetArray.Insert(0, source.DeepClone());
                }
                else
                {
                    targetArray.Add(source.DeepClone());
                }
            }

            return mixinArray.Count;
        }

        if (isPrepend)
        {
            for (var i = 0; i < mixinArray.Count; i++)
            {
                targetArray.Insert(i, mixinArray[i]!.DeepClone());
            }
        }
        else
        {
            foreach (var node in mixinArray)
            {
                targetArray.Add(node!.DeepClone());
            }
        }

        return mixinArray.Count;
    }

    private static int IndexOfTag(JsonArray array, string tag)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject obj
                && string.Equals(GetString(obj, "tag"), tag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }


    /// <summary>把 sniff / hijack-dns 挪到规则链最前面（顺序固定为 sniff 在前）。</summary>
    private static void PromoteSniffAndHijackDns(JsonArray rules)
    {
        var sniff = TakeFirstAction(rules, "sniff");
        var hijackDns = TakeFirstAction(rules, "hijack-dns");

        var insertAt = 0;
        if (sniff is not null)
        {
            rules.Insert(insertAt++, sniff);
        }

        if (hijackDns is not null)
        {
            rules.Insert(insertAt, hijackDns);
        }
    }

    /// <summary>取出第一条指定 action 的规则（已脱离父节点，可直接重新插入）。</summary>
    private static JsonNode? TakeFirstAction(JsonArray rules, string action)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is JsonObject obj && string.Equals(GetString(obj, "action"), action, StringComparison.Ordinal))
            {
                var node = rules[i];
                rules.RemoveAt(i);
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// 保证 route.rules 在 sniff / hijack-dns 之后有 clash_mode 的 Direct / Global 分流规则。
    /// 注意 clash_mode 的取值是 sing-box 规定的小写 direct / global。
    /// </summary>
    private static void EnsureClashModeRules(JsonObject root, JsonObject route, JsonArray rules, List<string> notes)
    {
        var hasDirect = rules.OfType<JsonObject>()
            .Any(o => string.Equals(GetString(o, "clash_mode"), "direct", StringComparison.OrdinalIgnoreCase));
        var hasGlobal = rules.OfType<JsonObject>()
            .Any(o => string.Equals(GetString(o, "clash_mode"), "global", StringComparison.OrdinalIgnoreCase));

        if (hasDirect && hasGlobal)
        {
            return;
        }

        var directTag = FindFirstOutboundTag(root, "direct") ?? "direct";
        var globalTag = FindFirstOutboundTag(root, "selector")
                        ?? GetString(route, "final")
                        ?? "GLOBAL";

        // 插在 hijack-dns 之后，避免抢在嗅探/劫持之前生效
        var insertAt = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is JsonObject obj && GetString(obj, "action") == "hijack-dns")
            {
                insertAt = i + 1;
                break;
            }
        }

        if (!hasDirect)
        {
            rules.Insert(insertAt, new JsonObject
            {
                ["clash_mode"] = "direct",
                ["outbound"] = directTag,
            });
            insertAt++;
            notes.Add($"route.rules 已补 clash_mode=direct -> {directTag}");
        }

        if (!hasGlobal)
        {
            rules.Insert(insertAt, new JsonObject
            {
                ["clash_mode"] = "global",
                ["outbound"] = globalTag,
            });
            notes.Add($"route.rules 已补 clash_mode=global -> {globalTag}");
        }
    }

    /// <summary>
    /// 把客户端持久化的「每个策略组上次选的节点」写进 selector 的 default。
    /// 存节点名在换订阅后会失效，所以必须先确认该节点仍在本组的 outbounds 里，
    /// 否则 sing-box check 会直接报 default 指向不存在的出站。
    /// </summary>
    private static void ApplySelectorDefaults(
        JsonObject root,
        IReadOnlyDictionary<string, string>? saved,
        List<string> notes)
    {
        if (saved is null || saved.Count == 0 || root["outbounds"] is not JsonArray outbounds)
        {
            return;
        }

        var applied = 0;

        foreach (var group in outbounds.OfType<JsonObject>())
        {
            if (!string.Equals(GetString(group, "type"), "selector", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = GetString(group, "tag");
            if (string.IsNullOrEmpty(tag) || !saved.TryGetValue(tag, out var savedNode))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(savedNode) || group["outbounds"] is not JsonArray candidates)
            {
                continue;
            }

            var exists = candidates
                .OfType<JsonValue>()
                .Any(value => value.TryGetValue<string>(out var name)
                              && string.Equals(name, savedNode, StringComparison.Ordinal));

            if (!exists)
            {
                notes.Add($"{tag} 记忆的节点「{savedNode}」在当前订阅中已不存在，沿用配置默认值");
                continue;
            }

            if (string.Equals(GetString(group, "default"), savedNode, StringComparison.Ordinal))
            {
                continue;
            }

            group["default"] = savedNode;
            applied++;
        }

        if (applied > 0)
        {
            notes.Add($"已恢复 {applied} 个策略组的上次选择");
        }
    }

    /// <summary>
    /// cache_file 必须显式指定绝对路径：路径不写死会随工作目录变化找不到同一个 cache.db。
    /// store_selected 只在支持它的内核版本上写——sing-box 1.12 起该字段被移除
    /// （选择持久化变成默认行为），强行写进去会让 check 报 unknown field 直接起不来。
    /// </summary>
    private static void EnsureCacheFile(JsonObject experimental, string coreDirectory, List<string> notes)
    {
        var cacheFile = GetOrCreateObject(experimental, "cache_file");
        var changed = false;

        if (!TryGetBool(cacheFile["enabled"]))
        {
            cacheFile["enabled"] = true;
            changed = true;
        }

        var expectedPath = Path.Combine(coreDirectory, "cache.db");
        if (!string.Equals(GetString(cacheFile, "path"), expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            cacheFile["path"] = expectedPath;
            changed = true;
        }

        if (SupportsStoreSelected(coreDirectory))
        {
            if (!TryGetBool(cacheFile["store_selected"]))
            {
                cacheFile["store_selected"] = true;
                changed = true;
            }
        }
        else if (cacheFile.Remove("store_selected"))
        {
            // 旧内核留下的字段必须摘掉，否则这个版本会直接拒绝启动
            changed = true;
        }

        if (changed)
        {
            notes.Add("已启用 cache_file（节点选择随 cache.db 持久化）");
        }
    }

    private static readonly object KernelVersionGate = new();

    private static readonly Dictionary<string, Version?> KernelVersionCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 读内核版本（sing-box version 的第一行）。
    /// 有些差异 check 抓不到、只在 run 阶段才报（比如 DNS detour 必须有解析器），
    /// 所以版本号比"拿配置去 check"更可靠。
    /// </summary>
    private static Version? GetKernelVersion(string coreDirectory)
    {
        var exePath = Path.Combine(coreDirectory, "sing-box.exe");
        if (!File.Exists(exePath))
        {
            return null;
        }

        string stamp;
        try
        {
            var info = new FileInfo(exePath);
            stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            stamp = "0";
        }

        lock (KernelVersionGate)
        {
            if (KernelVersionCache.TryGetValue(stamp, out var cached))
            {
                return cached;
            }
        }

        var version = ReadKernelVersion(exePath);

        lock (KernelVersionGate)
        {
            KernelVersionCache[stamp] = version;
        }

        return version;
    }

    private static Version? ReadKernelVersion(string exePath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("version");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 忽略
                }

                return null;
            }

            var match = Regex.Match(text, @"version\s+(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success)
            {
                return null;
            }

            var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var patch = match.Groups[3].Success
                ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
                : 0;
            return new Version(major, minor, patch);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 内核是否已经走到某个版本之后。拿不到版本号时退回"用最小配置试 check"。
    /// </summary>
    private static bool KernelAtLeast(
        string coreDirectory, int major, int minor, string probeName, string probeConfig)
    {
        var version = GetKernelVersion(coreDirectory);
        if (version is not null)
        {
            return version >= new Version(major, minor);
        }

        return !ProbeAccepts(coreDirectory, probeName, probeConfig);
    }

    private static readonly object ProbeGate = new();

    private static readonly Dictionary<string, bool> ProbeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 用一份最小配置问内核：这种写法它还认不认。
    /// 结果按 sing-box.exe 的大小 + 修改时间缓存，换内核会自动重新探测。
    /// 探测本身失败（进程起不来等）时按"支持"处理，宁可不动配置也别改坏。
    /// </summary>
    private static bool ProbeAccepts(string coreDirectory, string name, string configJson)
    {
        var exePath = Path.Combine(coreDirectory, "sing-box.exe");
        if (!File.Exists(exePath))
        {
            return true;
        }

        string stamp;
        try
        {
            var info = new FileInfo(exePath);
            stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            stamp = "0";
        }

        var cacheKey = $"{name}|{stamp}";

        lock (ProbeGate)
        {
            if (ProbeCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var accepted = RunProbe(exePath, configJson);

        lock (ProbeGate)
        {
            ProbeCache[cacheKey] = accepted;
        }

        return accepted;
    }

    private static bool RunProbe(string exePath, string configJson)
    {
        var probePath = Path.Combine(Path.GetTempPath(), $"singbox-probe-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(probePath, configJson);

            var startInfo = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            startInfo.ArgumentList.Add("check");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(probePath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return true;
            }

            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 忽略
                }

                return true;
            }

            return process.ExitCode == 0 && !stderr.Contains("FATAL", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响功能
            }
        }
    }

    /// <summary>
    /// 探测当前内核是否接受 experimental.cache_file.store_selected。
    /// </summary>
    private static bool SupportsStoreSelected(string coreDirectory) => ProbeAccepts(
        coreDirectory,
        "store-selected",
        """
        {
          "log": { "level": "warn" },
          "outbounds": [ { "type": "direct", "tag": "direct" } ],
          "experimental": {
            "cache_file": { "enabled": true, "path": "cache.db", "store_selected": true }
          }
        }
        """);

    // ==================== 内核版本兼容迁移 ====================

    /// <summary>旧版入站字段，1.13.0 起被移除，改由 route.rules 的 sniff / resolve 动作接管。</summary>
    private static readonly string[] LegacyInboundFields =
    {
        "sniff",
        "sniff_override_destination",
        "sniff_timeout",
        "domain_strategy",
        "udp_disable_domain_unmapping",
    };

    private const string LegacyDnsProbeConfig = """
        {
          "log": { "level": "warn" },
          "dns": { "servers": [ "1.1.1.1" ] },
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    private const string LegacyInboundProbeConfig = """
        {
          "log": { "level": "warn" },
          "dns": { "servers": [ { "type": "local", "tag": "local" } ] },
          "route": { "default_domain_resolver": "local" },
          "inbounds": [
            { "type": "mixed", "tag": "probe-in", "listen": "127.0.0.1", "listen_port": 18999, "sniff": true }
          ],
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    private const string OutboundDnsRuleProbeConfig = """
        {
          "log": { "level": "warn" },
          "dns": {
            "servers": [ { "type": "local", "tag": "local" } ],
            "rules": [ { "outbound": [ "any" ], "server": "local" } ]
          },
          "route": { "default_domain_resolver": "local" },
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    private const string NoDefaultResolverProbeConfig = """
        {
          "log": { "level": "warn" },
          "dns": { "servers": [ { "type": "local", "tag": "local" } ] },
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    /// <summary>
    /// 按内核能力把旧写法就地改写成新写法。
    /// 每一步都先探测：内核还认旧写法就一个字都不动，避免把老版本用户改坏。
    /// </summary>
    private static void ApplyKernelCompatMigrations(JsonObject root, string coreDirectory, List<string> notes)
    {
        MigrateLegacyDnsServers(root, coreDirectory, notes);
        MigrateOutboundDnsRules(root, coreDirectory, notes);
        StripLegacyInboundFields(root, coreDirectory, notes);

        // 1.12 起才有 route.default_domain_resolver / outbound.domain_resolver，
        // 1.14 起缺了它们内核会拒绝启动，所以按版本号决定要不要补
        var modernDns = KernelAtLeast(coreDirectory, 1, 12, "modern-dns", NoDefaultResolverProbeConfig);

        if (modernDns)
        {
            EnsureDefaultDomainResolver(root, notes);
            EnsureDnsDetourResolvers(root, notes);
        }
    }

    private static bool IsRcodeAddress(string address) =>
        address.StartsWith("rcode://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 旧版 DNS 服务器（字符串形式，或只有 address 字段的对象）→ 1.12+ 的 type/server 形式。
    /// rcode:// 这类"假服务器"没法迁移，改成规则上的 reject 动作。
    /// </summary>
    private static void MigrateLegacyDnsServers(JsonObject root, string coreDirectory, List<string> notes)
    {
        if (root["dns"] is not JsonObject dns || dns["servers"] is not JsonArray servers || servers.Count == 0)
        {
            return;
        }

        var hasLegacy = servers.Any(node =>
            (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            || (node is JsonObject obj && obj["type"] is null && GetString(obj, "address") is not null));

        if (!hasLegacy || !KernelAtLeast(coreDirectory, 1, 14, "legacy-dns", LegacyDnsProbeConfig))
        {
            return;
        }

        var tagOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejectTags = new HashSet<string>(StringComparer.Ordinal);
        var migrated = new JsonArray();
        var auto = 0;

        foreach (var node in servers)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                auto++;
                var tag = string.Equals(text, "local", StringComparison.OrdinalIgnoreCase) ? "local" : $"dns-{auto}";
                tagOf[text] = tag;

                if (IsRcodeAddress(text))
                {
                    rejectTags.Add(tag);
                    continue;
                }

                migrated.Add(BuildDnsServer(text, tag));
                continue;
            }

            if (node is not JsonObject obj)
            {
                continue;
            }

            // 已经是新格式
            if (obj["type"] is not null)
            {
                migrated.Add(obj.DeepClone());
                continue;
            }

            var address = GetString(obj, "address");
            if (string.IsNullOrWhiteSpace(address))
            {
                migrated.Add(obj.DeepClone());
                continue;
            }

            auto++;
            var oldTag = GetString(obj, "tag");
            var newTag = string.IsNullOrWhiteSpace(oldTag) ? $"dns-{auto}" : oldTag!;
            tagOf[address] = newTag;
            if (!string.IsNullOrWhiteSpace(oldTag))
            {
                tagOf[oldTag!] = newTag;
            }

            if (IsRcodeAddress(address))
            {
                rejectTags.Add(newTag);
                continue;
            }

            var built = BuildDnsServer(address, newTag);
            if (GetString(obj, "detour") is { } detour)
            {
                built["detour"] = detour;
            }

            if (GetString(obj, "strategy") is { } strategy)
            {
                built["strategy"] = strategy;
            }

            // 1.12 把 address_resolver 改名成 domain_resolver
            if (GetString(obj, "address_resolver") is { } resolver)
            {
                built["domain_resolver"] = resolver;
            }

            migrated.Add(built);
        }

        if (migrated.Count == 0)
        {
            migrated.Add(new JsonObject { ["type"] = "local", ["tag"] = "local" });
            tagOf["local"] = "local";
        }

        dns["servers"] = migrated;

        if (dns["rules"] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (GetString(rule, "server") is not { } reference || !tagOf.TryGetValue(reference, out var mapped))
                {
                    continue;
                }

                if (rejectTags.Contains(mapped))
                {
                    // 旧写法里"指向 rcode 服务器"等于拦掉这条查询
                    rule.Remove("server");
                    rule["action"] = "reject";
                    continue;
                }

                rule["server"] = mapped;
            }
        }

        notes.Add($"已把 {migrated.Count} 个旧版 DNS 服务器迁移为 sing-box 1.12+ 新格式");
    }

    /// <summary>把 DNS 服务器地址字符串翻译成新格式对象。</summary>
    private static JsonObject BuildDnsServer(string address, string tag)
    {
        var server = new JsonObject { ["tag"] = tag };

        string? scheme = null;
        var rest = address;
        var schemeIndex = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            scheme = address[..schemeIndex].ToLowerInvariant();
            rest = address[(schemeIndex + 3)..];
        }

        if (scheme is null)
        {
            if (string.Equals(address, "local", StringComparison.OrdinalIgnoreCase))
            {
                server["type"] = "local";
                return server;
            }

            server["type"] = "udp";
            SplitHostPort(address, server);
            return server;
        }

        switch (scheme)
        {
            case "https":
            case "h3":
            {
                server["type"] = scheme == "h3" ? "http3" : "https";
                var slash = rest.IndexOf('/');
                var hostPart = slash >= 0 ? rest[..slash] : rest;
                if (slash >= 0)
                {
                    server["path"] = rest[slash..];
                }

                SplitHostPort(hostPart, server);
                return server;
            }

            case "dhcp":
                server["type"] = "dhcp";
                if (!string.IsNullOrWhiteSpace(rest) && !string.Equals(rest, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    server["server"] = rest;
                }

                return server;

            case "fakeip":
                server["type"] = "fakeip";
                return server;

            case "tls":
            case "quic":
            case "tcp":
            case "udp":
                server["type"] = scheme;
                SplitHostPort(rest, server);
                return server;

            default:
                // 认不出的协议按 udp 处理，至少保证 check 能过
                server["type"] = "udp";
                SplitHostPort(rest, server);
                return server;
        }
    }

    private static void SplitHostPort(string value, JsonObject target)
    {
        var text = value.Trim();

        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end > 0)
            {
                target["server"] = text[1..end];
                var tail = text[(end + 1)..];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out var bracketedPort))
                {
                    target["server_port"] = bracketedPort;
                }

                return;
            }
        }

        // 裸 IPv6（多个冒号且没有端口）
        if (text.Count(c => c == ':') > 1)
        {
            target["server"] = text;
            return;
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
        {
            target["server"] = text[..colon];
            target["server_port"] = port;
            return;
        }

        target["server"] = text;
    }

    /// <summary>
    /// DNS 规则里的 outbound 条件 1.14 已移除，语义改用 outbound.domain_resolver。
    /// 只带 outbound 的规则迁移后就没有匹配条件了，直接丢掉。
    /// </summary>
    private static void MigrateOutboundDnsRules(JsonObject root, string coreDirectory, List<string> notes)
    {
        if (root["dns"] is not JsonObject dns || dns["rules"] is not JsonArray rules)
        {
            return;
        }

        if (!rules.OfType<JsonObject>().Any(rule => rule["outbound"] is JsonArray)
            || !KernelAtLeast(coreDirectory, 1, 14, "outbound-dns-rule", OutboundDnsRuleProbeConfig))
        {
            return;
        }

        var route = GetOrCreateObject(root, "route");
        var outbounds = root["outbounds"] as JsonArray;
        var kept = new JsonArray();
        var moved = 0;

        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (rule["outbound"] is not JsonArray targets)
            {
                kept.Add(rule.DeepClone());
                continue;
            }

            var resolver = GetString(rule, "server");
            rule.Remove("outbound");
            moved++;

            if (!string.IsNullOrWhiteSpace(resolver))
            {
                foreach (var item in targets)
                {
                    var tag = item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                    if (string.IsNullOrEmpty(tag))
                    {
                        continue;
                    }

                    if (string.Equals(tag, "any", StringComparison.OrdinalIgnoreCase))
                    {
                        if (route["default_domain_resolver"] is null)
                        {
                            route["default_domain_resolver"] = resolver;
                        }

                        continue;
                    }

                    var target = outbounds?.OfType<JsonObject>()
                        .FirstOrDefault(node => string.Equals(GetString(node, "tag"), tag, StringComparison.Ordinal));

                    if (target is not null && target["domain_resolver"] is null)
                    {
                        target["domain_resolver"] = resolver;
                    }
                }
            }

            // 只剩 server/action 说明没有匹配条件了，这条规则已无意义
            var hasMatcher = rule.Any(pair => pair.Key is not ("server" or "action" or "invert"));
            if (hasMatcher)
            {
                kept.Add(rule.DeepClone());
            }
        }

        dns["rules"] = kept;
        notes.Add($"已把 {moved} 条 DNS 规则的 outbound 条件迁移为 domain_resolver");
    }

    /// <summary>入站上的 sniff / domain_strategy 等字段 1.13 起被移除。</summary>
    private static void StripLegacyInboundFields(JsonObject root, string coreDirectory, List<string> notes)
    {
        if (root["inbounds"] is not JsonArray inbounds)
        {
            return;
        }

        var hasLegacy = inbounds.OfType<JsonObject>()
            .Any(inbound => LegacyInboundFields.Any(inbound.ContainsKey));

        if (!hasLegacy || !KernelAtLeast(coreDirectory, 1, 13, "legacy-inbound", LegacyInboundProbeConfig))
        {
            return;
        }

        var stripped = 0;
        foreach (var inbound in inbounds.OfType<JsonObject>())
        {
            foreach (var field in LegacyInboundFields)
            {
                if (inbound.Remove(field))
                {
                    stripped++;
                }
            }
        }

        if (stripped > 0)
        {
            notes.Add($"已移除 {stripped} 个入站旧字段（sniff / domain_strategy），改由 route.rules 的 sniff 动作接管");
        }
    }

    /// <summary>1.14 起 route.default_domain_resolver 是必需的，缺了内核直接拒绝启动。</summary>
    private static void EnsureDefaultDomainResolver(JsonObject root, List<string> notes)
    {
        var route = GetOrCreateObject(root, "route");
        if (route["default_domain_resolver"] is not null)
        {
            return;
        }

        var tag = PickResolverTag(root);
        if (tag is null)
        {
            return;
        }

        route["default_domain_resolver"] = tag;
        notes.Add($"已补 route.default_domain_resolver = {tag}（sing-box 1.14 起必需）");
    }

    /// <summary>
    /// DNS 服务器的 detour 指向 direct 出口时，1.14 要求这个出口自己也有 domain_resolver，
    /// 否则启动阶段直接 FATAL："detour to an empty direct outbound makes no sense"。
    /// 找不到现成的"无 detour 服务器"就补一个 local 的引导解析器。
    /// </summary>
    private static void EnsureDnsDetourResolvers(JsonObject root, List<string> notes)
    {
        if (root["dns"] is not JsonObject dns || dns["servers"] is not JsonArray servers)
        {
            return;
        }

        var outbounds = root["outbounds"] as JsonArray;
        if (outbounds is null)
        {
            return;
        }

        var detourTags = servers.OfType<JsonObject>()
            .Select(server => GetString(server, "detour"))
            .Where(tag => !string.IsNullOrEmpty(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (detourTags.Count == 0)
        {
            return;
        }

        var pending = new List<JsonObject>();

        foreach (var detourTag in detourTags)
        {
            var target = outbounds.OfType<JsonObject>()
                .FirstOrDefault(node => string.Equals(GetString(node, "tag"), detourTag, StringComparison.Ordinal));

            if (target is null
                || !string.Equals(GetString(target, "type"), "direct", StringComparison.OrdinalIgnoreCase)
                || target["domain_resolver"] is not null)
            {
                continue;
            }

            pending.Add(target);
        }

        if (pending.Count == 0)
        {
            return;
        }

        var bootstrap = PickBootstrapResolverTag(servers);
        if (bootstrap is null)
        {
            bootstrap = "dns-bootstrap";
            servers.Add(new JsonObject { ["type"] = "local", ["tag"] = bootstrap });
        }

        foreach (var target in pending)
        {
            target["domain_resolver"] = bootstrap;
        }

        notes.Add($"已为 {pending.Count} 个 direct 出口补 domain_resolver = {bootstrap}（1.14 起 DNS detour 必须有解析器）");
    }

    /// <summary>找一个自身不经过任何出口的解析器，用作引导 DNS。</summary>
    private static string? PickBootstrapResolverTag(JsonArray servers) => servers.OfType<JsonObject>()
        .Where(server => GetString(server, "tag") is { Length: > 0 })
        .Where(server => GetString(server, "detour") is null)
        .Where(server => GetString(server, "type") is not ("fakeip" or "predefined"))
        .Select(server => GetString(server, "tag")!)
        .FirstOrDefault();

    private static string? PickResolverTag(JsonObject root)
    {
        if (root["dns"] is not JsonObject dns || dns["servers"] is not JsonArray servers)
        {
            return null;
        }

        var candidates = servers.OfType<JsonObject>()
            .Where(server => GetString(server, "tag") is { Length: > 0 })
            .Where(server => GetString(server, "type") is not ("fakeip" or "predefined"))
            .Select(server => GetString(server, "tag")!)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // 优先用直连解析器，避免解析 outbound 域名时绕回代理形成环
        return candidates.FirstOrDefault(tag => string.Equals(tag, "local", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(tag => string.Equals(tag, "direct", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    private static string? FindFirstOutboundTag(JsonObject root, string type)
    {
        if (root["outbounds"] is not JsonArray outbounds)
        {
            return null;
        }

        foreach (var node in outbounds.OfType<JsonObject>())
        {
            if (string.Equals(GetString(node, "type"), type, StringComparison.OrdinalIgnoreCase))
            {
                var tag = GetString(node, "tag");
                if (!string.IsNullOrEmpty(tag))
                {
                    return tag;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// IPv6 开关落到 DNS 策略：关闭时强制 ipv4_only；开启时把 ipv4_only 抬成 prefer_ipv4，
    /// 其它值（如 ipv6_only / prefer_ipv6）尊重用户原配置。
    /// </summary>
    private static void EnsureDnsStrategy(JsonObject root, bool enableIpv6, List<string> notes)
    {
        var dns = GetOrCreateObject(root, "dns");
        var strategy = GetString(dns, "strategy");

        if (!enableIpv6)
        {
            if (!string.Equals(strategy, "ipv4_only", StringComparison.OrdinalIgnoreCase))
            {
                dns["strategy"] = "ipv4_only";
                notes.Add("已禁用 IPv6（dns.strategy = ipv4_only）");
            }

            return;
        }

        // 未指定或原值被锁成 ipv4_only 时，显式写成 prefer_ipv4，方便直接看 running_config.json 确认
        if (string.IsNullOrEmpty(strategy)
            || string.Equals(strategy, "ipv4_only", StringComparison.OrdinalIgnoreCase))
        {
            dns["strategy"] = "prefer_ipv4";
            notes.Add("已启用 IPv6（dns.strategy = prefer_ipv4）");
        }
    }

    /// <summary>找出（或注入）一个 mixed/http 入站，返回端口供系统代理使用。</summary>
    private static int EnsureMixedInbound(JsonObject root, List<string> notes)
    {
        var inbounds = root["inbounds"] as JsonArray ?? GetOrCreateArray(root, "inbounds");

        foreach (var node in inbounds.OfType<JsonObject>())
        {
            var type = GetString(node, "type");
            if (!string.Equals(type, "mixed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (node["listen_port"] is JsonValue value && value.TryGetValue<int>(out var port) && port > 0)
            {
                return port;
            }
        }

        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = DefaultMixedPort,
        });

        notes.Add($"配置中没有 mixed/http 入站，已注入 127.0.0.1:{DefaultMixedPort}");
        return DefaultMixedPort;
    }

    private static JsonObject ParseRootObject(string path)
    {
        var text = File.ReadAllText(path);

        var node = JsonNode.Parse(
            text,
            nodeOptions: null,
            documentOptions: JsonDefaults.DocumentOptions);

        return node as JsonObject
            ?? throw new InvalidDataException("配置文件根节点必须是 JSON 对象。");
    }

    private static JsonObject CreateTunInbound(TunSettings tun, bool enableIpv6)
    {
        var inbound = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = TunTag,

            // 关掉 IPv6 时只下发 IPv4 地址，避免网卡拿到一个用不了的 v6 地址
            ["address"] = enableIpv6
                ? new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126")
                : new JsonArray("172.19.0.1/30"),
            ["mtu"] = tun.Mtu <= 0 ? 9000 : tun.Mtu,
            ["auto_route"] = tun.AutoRoute,
            ["strict_route"] = tun.StrictRoute,
            ["stack"] = string.IsNullOrWhiteSpace(tun.Stack) ? "system" : tun.Stack,
        };

        // 空名字交给 sing-box 自己取默认名，不要写空串
        if (!string.IsNullOrWhiteSpace(tun.InterfaceName))
        {
            inbound["interface_name"] = tun.InterfaceName;
        }

        return inbound;
    }

    private static bool IsTunInbound(JsonNode? node) =>
        node is JsonObject obj
        && string.Equals(GetString(obj, "type"), "tun", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把 route.rule_set 里 type=local 的相对路径改写成绝对路径。
    /// 先按工作目录解析，解析不到再按文件名去程序自带 rulesets 目录找。
    /// </summary>
    private static int AbsolutizeRuleSetPaths(JsonObject root, string coreDirectory, string rulesetsDirectory)
    {
        if (root["route"] is not JsonObject route || route["rule_set"] is not JsonArray ruleSets)
        {
            return 0;
        }

        var changed = 0;

        foreach (var node in ruleSets)
        {
            if (node is not JsonObject set)
            {
                continue;
            }

            if (!string.Equals(GetString(set, "type"), "local", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = GetString(set, "path");
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
            {
                continue;
            }

            var resolved = ResolveRuleSetPath(path, coreDirectory, rulesetsDirectory);
            if (resolved is null)
            {
                continue;
            }

            set["path"] = resolved;
            changed++;
        }

        return changed;
    }

    private static string? ResolveRuleSetPath(string path, string coreDirectory, string rulesetsDirectory)
    {
        try
        {
            // 正常工作目录下的解析结果（../rulesets/xxx.json -> <exe>\rulesets\xxx.json）
            var byWorkingDirectory = Path.GetFullPath(Path.Combine(coreDirectory, path));
            if (File.Exists(byWorkingDirectory))
            {
                return byWorkingDirectory;
            }
        }
        catch (Exception)
        {
            // 非法路径字符等，继续走按文件名兜底
        }

        // 兜底：按文件名去程序自带的 rulesets 目录找（配置文件来自别处时常见）
        var byFileName = Path.Combine(rulesetsDirectory, Path.GetFileName(path));
        return File.Exists(byFileName) ? Path.GetFullPath(byFileName) : null;
    }

    /// <summary>删除所有 tun 入站，并在原位置插入规范对象。返回被移除的数量。</summary>
    private static int ReplaceTunInbound(JsonArray inbounds, TunSettings tun, bool enableIpv6)
    {
        var removed = 0;
        var firstIndex = -1;

        // 从后往前删，下标不会失效；最后一次赋值即最早那个 tun 的位置
        for (var i = inbounds.Count - 1; i >= 0; i--)
        {
            if (!IsTunInbound(inbounds[i]))
            {
                continue;
            }

            inbounds.RemoveAt(i);
            firstIndex = i;
            removed++;
        }

        inbounds.Insert(firstIndex < 0 ? inbounds.Count : firstIndex, CreateTunInbound(tun, enableIpv6));
        return removed;
    }

    private static int RemoveTunInbounds(JsonArray inbounds)
    {
        var removed = 0;

        for (var i = inbounds.Count - 1; i >= 0; i--)
        {
            if (IsTunInbound(inbounds[i]))
            {
                inbounds.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool TryGetBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    // ==================== 占位符展开（全局拓展去节点化） ====================

    /// <summary>组类出站，其 outbounds 里可以写占位符。</summary>
    private static readonly HashSet<string> GroupOutboundTypes =
        new(StringComparer.OrdinalIgnoreCase) { "selector", "urltest" };

    /// <summary>非真实节点的出站类型，收集节点池时要排除。</summary>
    private static readonly HashSet<string> NonNodeOutboundTypes =
        new(StringComparer.OrdinalIgnoreCase) { "selector", "urltest", "direct", "block", "dns" };

    /// <summary>机场塞进节点列表里的信息节点（流量、到期、公告）。</summary>
    private static readonly Regex InfoNodePattern =
        new("剩余流量|距离下次重置|套餐到期|到期时间|官网|更新订阅", RegexOptions.Compiled);

    /// <summary>高倍率节点标记，自动测速池要剔除，避免误扣流量。</summary>
    private static readonly Regex MultiplierPattern = new("倍率", RegexOptions.Compiled);

    /// <summary>__REGION:xx__ 形态的地区占位符。</summary>
    private static readonly Regex RegionPlaceholderPattern =
        new(@"^__REGION:([a-z]{2})__$", RegexOptions.Compiled);

    /// <summary>地区识别规则。ASCII 代码用 \b 包住，避免 "Russia" 命中 US 这类误判。</summary>
    private static readonly Dictionary<string, Regex> RegionPatterns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["hk"] = new(@"香港|Hong\s*Kong|\b(?:HK|HKG)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["jp"] = new(@"日本|东京|大阪|Tokyo|Osaka|\b(?:JP|JPN)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["sg"] = new(@"新加坡|狮城|Singapore|\b(?:SG|SGP)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["us"] = new(@"美国|洛杉矶|凤凰城|堪萨斯|西雅图|圣何塞|达拉斯|纽约|Los\s*Angeles|Phoenix|Kansas|\b(?:US|USA)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["kr"] = new(@"韩国|首尔|Korea|\b(?:KR|KOR)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["tw"] = new(@"台湾|台灣|台北|Taipei|\b(?:TW|TWN)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        };

    /// <summary>节点里指向本机的假节点（机场用来放公告）。</summary>
    private static readonly HashSet<string> LocalServerNames =
        new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1" };

    /// <summary>
    /// 展开全局拓展里的占位符：
    /// __ALL__ 全部真实节点、__AUTO__ 再剔高倍率、__REGION:xx__ 按地区筛选、__FAKEIP__ 复用已有 fakeip。
    /// 没用到占位符时直接返回，不改变原有行为。
    /// </summary>
    private static void ExpandPlaceholders(JsonObject root, List<string> notes)
    {
        var fakeIpReplaced = ExpandDnsFakeIp(root, notes);

        if (root["outbounds"] is not JsonArray outbounds || outbounds.Count == 0)
        {
            return;
        }

        var nodes = CollectNodeTags(outbounds);
        var autoNodes = nodes.Where(tag => !MultiplierPattern.IsMatch(tag)).ToList();

        var replaced = 0;

        foreach (var group in outbounds.OfType<JsonObject>())
        {
            var tag = GetString(group, "tag") ?? "(无 tag)";

            if (!IsGroupOutbound(group) || group["outbounds"] is not JsonArray list)
            {
                continue;
            }

            if (!ContainsPlaceholder(list))
            {
                continue;
            }

            var expanded = new JsonArray();

            foreach (var item in list)
            {
                var text = item is JsonValue value && value.TryGetValue<string>(out var str) ? str : null;

                if (text is null)
                {
                    expanded.Add(item?.DeepClone());
                    continue;
                }

                if (!text.StartsWith("__", StringComparison.Ordinal))
                {
                    expanded.Add(JsonValue.Create(text));
                    continue;
                }

                var resolved = ResolvePlaceholder(text, nodes, autoNodes)
                               ?? throw new InvalidDataException(
                                   $"拓展配置里的占位符无法识别：{text}（出站：{tag}）");

                foreach (var nodeTag in resolved)
                {
                    expanded.Add(JsonValue.Create(nodeTag));
                }

                replaced++;
            }

            group["outbounds"] = expanded;
        }

        if (replaced == 0 && !fakeIpReplaced)
        {
            return;
        }

        notes.Add($"已展开全局拓展占位符（替换 {replaced} 处，节点池 {nodes.Count} 个）");

        PruneDanglingOutbounds(root, outbounds, notes);
    }

    private static bool IsGroupOutbound(JsonObject node) =>
        GetString(node, "type") is { } type && GroupOutboundTypes.Contains(type);

    private static bool ContainsPlaceholder(JsonArray list)
    {
        foreach (var item in list)
        {
            if (item is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.StartsWith("__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>收集订阅里的真实节点 tag，剔除机场信息节点与本机假节点。</summary>
    private static List<string> CollectNodeTags(JsonArray outbounds)
    {
        var tags = new List<string>();

        foreach (var node in outbounds.OfType<JsonObject>())
        {
            var tag = GetString(node, "tag");
            var type = GetString(node, "type");

            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(type))
            {
                continue;
            }

            if (NonNodeOutboundTypes.Contains(type))
            {
                continue;
            }

            if (InfoNodePattern.IsMatch(tag))
            {
                continue;
            }

            var server = GetString(node, "server");
            if (server is not null && LocalServerNames.Contains(server))
            {
                continue;
            }

            tags.Add(tag);
        }

        return tags;
    }

    /// <summary>解析单个占位符，无法识别时返回 null 由调用方报错。</summary>
    private static List<string>? ResolvePlaceholder(string text, List<string> all, List<string> auto)
    {
        if (string.Equals(text, "__ALL__", StringComparison.Ordinal))
        {
            return all;
        }

        if (string.Equals(text, "__AUTO__", StringComparison.Ordinal))
        {
            return auto;
        }

        var match = RegionPlaceholderPattern.Match(text);
        if (match.Success && RegionPatterns.TryGetValue(match.Groups[1].Value, out var pattern))
        {
            return all.Where(tag => pattern.IsMatch(tag)).ToList();
        }

        return null;
    }

    /// <summary>
    /// dns.rules 里的 __FAKEIP__ 换成实际 fakeip 服务器 tag。
    /// sing-box 不允许存在多个 fakeip 服务器，所以只能复用订阅已有的，没有才注入。
    /// </summary>
    private static bool ExpandDnsFakeIp(JsonObject root, List<string> notes)
    {
        if (root["dns"] is not JsonObject dns || dns["rules"] is not JsonArray rules)
        {
            return false;
        }

        var pending = rules.OfType<JsonObject>()
            .Where(rule => string.Equals(GetString(rule, "server"), "__FAKEIP__", StringComparison.Ordinal))
            .ToList();

        if (pending.Count == 0)
        {
            return false;
        }

        var servers = dns["servers"] as JsonArray;
        string? fakeIpTag = null;

        if (servers is not null)
        {
            foreach (var server in servers.OfType<JsonObject>())
            {
                if (!string.Equals(GetString(server, "type"), "fakeip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                fakeIpTag = GetString(server, "tag");
                if (!string.IsNullOrEmpty(fakeIpTag))
                {
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(fakeIpTag))
        {
            fakeIpTag = "fakeip-mixin";
            servers ??= GetOrCreateArray(dns, "servers");
            servers.Add(new JsonObject
            {
                ["type"] = "fakeip",
                ["tag"] = fakeIpTag,
                ["inet4_range"] = "198.18.0.0/15",
            });
            notes.Add($"DNS 没有 fakeip 服务器，已注入 {fakeIpTag}（198.18.0.0/15）");
        }
        else
        {
            notes.Add($"DNS 占位符 __FAKEIP__ 复用现有 fakeip 服务器：{fakeIpTag}");
        }

        foreach (var rule in pending)
        {
            rule["server"] = fakeIpTag;
        }

        return true;
    }

    /// <summary>
    /// 收尾清理：删掉展开后为空的组、摘掉指向已不存在出站的引用、修正 route.final 与规则出口。
    /// 占位符是按订阅节点动态展开的，某个地区一个节点都没有时就会走到这里。
    /// </summary>
    private static void PruneDanglingOutbounds(JsonObject root, JsonArray outbounds, List<string> notes)
    {
        var removedGroups = new List<string>();
        var droppedRefs = new HashSet<string>(StringComparer.Ordinal);

        // 删组会连带让其它组的引用失效，所以反复扫到稳定为止
        while (true)
        {
            var existing = outbounds.OfType<JsonObject>()
                .Select(node => GetString(node, "tag"))
                .Where(tag => !string.IsNullOrEmpty(tag))
                .Select(tag => tag!)
                .ToHashSet(StringComparer.Ordinal);

            var changed = false;

            for (var i = outbounds.Count - 1; i >= 0; i--)
            {
                if (outbounds[i] is not JsonObject group || !IsGroupOutbound(group))
                {
                    continue;
                }

                if (group["outbounds"] is not JsonArray list)
                {
                    continue;
                }

                if (list.Count == 0)
                {
                    removedGroups.Add(GetString(group, "tag") ?? "(无 tag)");
                    outbounds.RemoveAt(i);
                    changed = true;
                    continue;
                }

                for (var j = list.Count - 1; j >= 0; j--)
                {
                    var refTag = list[j] is JsonValue value && value.TryGetValue<string>(out var str) ? str : null;

                    if (refTag is null || existing.Contains(refTag))
                    {
                        continue;
                    }

                    list.RemoveAt(j);
                    droppedRefs.Add(refTag);
                    changed = true;
                }

                // default 指向的组被删了会直接启动失败，退回到列表首项
                var groupTag = GetString(group, "tag");
                var defaultTag = GetString(group, "default");
                if (defaultTag is not null && !existing.Contains(defaultTag) && list.Count > 0)
                {
                    var fallbackDefault = list[0] is JsonValue first && first.TryGetValue<string>(out var firstTag)
                        ? firstTag
                        : null;

                    if (fallbackDefault is not null)
                    {
                        group["default"] = fallbackDefault;
                        notes.Add($"{groupTag} 的默认出口 {defaultTag} 不存在，已改为 {fallbackDefault}");
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        if (removedGroups.Count > 0)
        {
            var groupList = string.Join("、", removedGroups.Distinct());
            notes.Add($"已移除空出站组：{groupList}");
        }

        if (droppedRefs.Count > 0)
        {
            var refList = string.Join("、", droppedRefs);
            notes.Add($"[警告] 以下出站引用在本次订阅中不存在，已摘除：{refList}");
        }

        if (removedGroups.Count == 0 && droppedRefs.Count == 0)
        {
            return;
        }

        // route.final 与规则出口也必须指向存在的出站
        if (root["route"] is not JsonObject route)
        {
            return;
        }

        var remaining = outbounds.OfType<JsonObject>()
            .Select(node => GetString(node, "tag"))
            .Where(tag => !string.IsNullOrEmpty(tag))
            .Select(tag => tag!)
            .ToHashSet(StringComparer.Ordinal);

        var finalTag = GetString(route, "final");
        if (!string.IsNullOrEmpty(finalTag) && !remaining.Contains(finalTag))
        {
            var fallback = FindFirstOutboundTag(root, "selector") ?? FindFirstOutboundTag(root, "direct");
            if (!string.IsNullOrEmpty(fallback))
            {
                route["final"] = fallback;
                notes.Add($"route.final 原指向已不存在的 {finalTag}，已改为 {fallback}");
            }
        }

        if (route["rules"] is not JsonArray rules)
        {
            return;
        }

        var newFinal = GetString(route, "final");
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var outboundTag = GetString(rule, "outbound");
            if (outboundTag is null || remaining.Contains(outboundTag) || string.IsNullOrEmpty(newFinal))
            {
                continue;
            }

            rule["outbound"] = newFinal;
            notes.Add($"[警告] 规则出口 {outboundTag} 不存在，已改为 {newFinal}");
        }
    }

    /// <summary>
    /// 按拓展里的 x_outbound_order 重排 outbounds。
    /// 订阅自带的同 tag 出站（如「节点选择」）在合并时只会原位替换，位置无法用数据控制，
    /// 所以这里按声明顺序把它们提到前面，未列出的出站保持原有相对顺序。处理完删除该键。
    /// </summary>
    private static void ApplyOutboundOrder(JsonObject root, List<string> notes)
    {
        if (root["x_outbound_order"] is not JsonArray order)
        {
            return;
        }

        root.Remove("x_outbound_order");

        if (root["outbounds"] is not JsonArray outbounds || outbounds.Count == 0)
        {
            return;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in order)
        {
            if (node is JsonValue value
                && value.TryGetValue<string>(out var tag)
                && !string.IsNullOrEmpty(tag)
                && !rank.ContainsKey(tag))
            {
                rank[tag] = rank.Count;
            }
        }

        if (rank.Count == 0)
        {
            return;
        }

        // 稳定排序：声明过的按声明次序在前，其余按原索引保持不动
        var indexed = outbounds
            .Select((node, index) => new { Node = node, Index = index, Tag = node is JsonObject obj ? GetString(obj, "tag") : null })
            .ToList();

        var sorted = indexed
            .OrderBy(item => item.Tag is not null && rank.TryGetValue(item.Tag, out var r) ? r : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Node)
            .ToList();

        var moved = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(sorted[i], indexed[i].Node))
            {
                moved++;
            }
        }

        outbounds.Clear();
        foreach (var node in sorted)
        {
            outbounds.Add(node);
        }

        var known = indexed.Count(item => item.Tag is not null && rank.ContainsKey(item.Tag));
        notes.Add($"已按 x_outbound_order 重排出站（命中 {known}/{rank.Count} 个，实际位移 {moved} 项）");
    }
}
