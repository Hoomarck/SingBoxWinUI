using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SingBoxWinUI.Services;

/// <summary>GitHub 最新 Release 中与当前架构匹配的资产。</summary>
public sealed record KernelReleaseInfo(
    string Version,
    string ReleaseUrl,
    string AssetName,
    string AssetUrl,
    long AssetSize);

/// <summary>更新阶段，由 ViewModel 映射成本地化文案。</summary>
public enum KernelUpdateStage
{
    Downloading,
    Extracting,
    Validating,
    Stopping,
    Swapping,
    Restarting,
    RollingBack,
}

public sealed record KernelUpdateProgress(KernelUpdateStage Stage, int Percent);

public sealed record KernelUpdateOutcome(string Version, bool WasRunning, bool RolledBack);

/// <summary>
/// sing-box 内核版本检测与热替换。
/// 只通过 App.Main 现有的 Start/Stop 入口停起内核，不碰 SingBoxService 的
/// AttachConsole + CTRL_BREAK 优雅退出实现，也不改热重启世代号。
/// </summary>
public sealed class KernelUpdateService
{
    private const string ReleaseApi = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";

    private static readonly Regex VersionPattern =
        new(@"sing-box\s+version\s+([0-9][A-Za-z0-9.\-+]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CoreDirectory => SingBoxService.DefaultCoreDirectory;

    private static string ExePath => Path.Combine(CoreDirectory, "sing-box.exe");

    private static string NewExePath => Path.Combine(CoreDirectory, "sing-box.exe.new");

    private static string BackupPath => Path.Combine(CoreDirectory, "sing-box.exe.bak");

    private static string ZipPath => Path.Combine(CoreDirectory, "sing-box-update.zip");

    private static string VerifyPath => Path.Combine(CoreDirectory, "sing-box-verify.exe");

    /// <summary>读取本地内核版本；未安装/执行失败返回 null。</summary>
    public async Task<string?> GetLocalVersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var match = VersionPattern.Match(output);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 查询最新 Release。mixedPort &gt; 0 时先借道本地代理，失败自动回落直连。
    /// </summary>
    public async Task<KernelReleaseInfo> CheckLatestAsync(int? mixedPort, CancellationToken ct = default)
    {
        var json = await FetchWithFallbackAsync(
            mixedPort,
            TimeSpan.FromSeconds(10),
            async (client, token) =>
            {
                using var response = await client.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token);
            },
            ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = GetString(root, "tag_name");
        var version = tag.TrimStart('v', 'V');
        var releaseUrl = GetString(root, "html_url");
        var assetName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "windows-arm64.zip"
            : "windows-amd64.zip";

        string assetUrl = string.Empty;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (!name.EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assetUrl = GetString(asset, "browser_download_url");
                if (asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                {
                    assetSize = bytes;
                }

                break;
            }
        }

        return new KernelReleaseInfo(version, releaseUrl, assetName, assetUrl, assetSize);
    }

    /// <summary>版本号比较（带预发布后缀时按字符串兜底）。</summary>
    public static bool IsNewer(string local, string latest)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(local))
        {
            return true;
        }

        if (Version.TryParse(local, out var a) && Version.TryParse(latest, out var b))
        {
            return b > a;
        }

        return !string.Equals(local, latest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 下载 → 解压 → 预校验 → 停机换核 → 自动恢复。
    /// 任何一步失败都会清理临时文件；启动失败时把 .bak 还原回去。
    /// </summary>
    public async Task<KernelUpdateOutcome> UpdateKernelAsync(
        KernelReleaseInfo release,
        int? mixedPort,
        IProgress<KernelUpdateProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.AssetUrl))
        {
            throw new InvalidDataException("no-asset");
        }

        var wasRunning = App.Main.IsRunning;

        try
        {
            progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Downloading, 0));
            await DownloadWithFallbackAsync(release, mixedPort, progress, ct);

            progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Extracting, 100));
            ExtractCoreBinary();

            progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Validating, 100));
            if (!await ValidateNewBinaryAsync(ct))
            {
                throw new InvalidDataException("version-check-failed");
            }

            if (wasRunning)
            {
                progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Stopping, 100));
                // 走原有优雅停止流程，卸载 TUN 并释放 sing-box.exe 文件锁
                await App.Main.StopCoreAsync();
                await WaitForKernelExitAsync(ct);
            }

            progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Swapping, 100));
            File.Move(ExePath, BackupPath, overwrite: true);
            File.Move(NewExePath, ExePath, overwrite: true);
            File.Delete(ZipPath);

            if (!wasRunning)
            {
                return new KernelUpdateOutcome(release.Version, false, false);
            }

            progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Restarting, 100));
            await App.Main.StartCoreAsync();

            // 启动是异步落状态的：给进程一点时间真正起来，再判断是否需要回滚
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (!App.Main.IsRunning || !App.Main.IsKernelProcessAlive)
            {
                progress?.Report(new KernelUpdateProgress(KernelUpdateStage.RollingBack, 100));
                await RollbackAsync(ct);
                throw new InvalidOperationException("restart-failed");
            }

            return new KernelUpdateOutcome(release.Version, true, false);
        }
        finally
        {
            TryDelete(NewExePath);
            TryDelete(ZipPath);
            TryDelete(VerifyPath);
        }
    }

    private static async Task DownloadWithFallbackAsync(
        KernelReleaseInfo release,
        int? mixedPort,
        IProgress<KernelUpdateProgress>? progress,
        CancellationToken ct)
    {
        await FetchWithFallbackAsync(mixedPort, TimeSpan.FromMinutes(10), async (client, token) =>
        {
            using var response = await client.GetAsync(
                release.AssetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.AssetSize;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(
                ZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long read = 0;
            int lastPercent = -1;

            while (true)
            {
                var count = await source.ReadAsync(buffer, token);
                if (count == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, count), token);
                read += count;

                if (total > 0)
                {
                    var percent = (int)Math.Clamp(read * 100 / total, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(new KernelUpdateProgress(KernelUpdateStage.Downloading, percent));
                    }
                }
            }

            return true;
        }, ct);
    }

    private static void ExtractCoreBinary()
    {
        using var archive = ZipFile.OpenRead(ZipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("sing-box.exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException("sing-box.exe not in archive");
        }

        using var input = entry.Open();
        using var output = new FileStream(NewExePath, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>
    /// 预校验：跑一次新二进制的 version。
    /// Windows 对非 .exe 后缀有时拒绝 CreateProcess，失败时复制成临时 .exe 再试一次。
    /// </summary>
    private static async Task<bool> ValidateNewBinaryAsync(CancellationToken ct)
    {
        if (await ProbeVersionAsync(NewExePath, ct))
        {
            return true;
        }

        File.Copy(NewExePath, VerifyPath, overwrite: true);
        return await ProbeVersionAsync(VerifyPath, ct);
    }

    private static async Task<bool> ProbeVersionAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return process.ExitCode == 0 && VersionPattern.IsMatch(output);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task WaitForKernelExitAsync(CancellationToken ct)
    {
        for (var i = 0; i < 50 && App.Main.IsKernelProcessAlive; i++)
        {
            await Task.Delay(200, ct);
        }
    }

    private static async Task RollbackAsync(CancellationToken ct)
    {
        if (App.Main.IsRunning)
        {
            await App.Main.StopCoreAsync();
            await WaitForKernelExitAsync(ct);
        }

        if (File.Exists(BackupPath))
        {
            File.Move(BackupPath, ExePath, overwrite: true);
            await App.Main.StartCoreAsync();
        }
    }

    private static async Task<T> FetchWithFallbackAsync<T>(
        int? mixedPort,
        TimeSpan timeout,
        Func<HttpClient, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        // 内核在跑且有 mixed 端口时优先借道代理；失败再直连，避免 GitHub 被墙时卡死
        if (mixedPort is > 0)
        {
            try
            {
                using var proxied = CreateClient(mixedPort, timeout);
                return await action(proxied, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                TryDelete(ZipPath);
            }
        }

        using var direct = CreateClient(null, timeout);
        return await action(direct, ct);
    }

    private static HttpClient CreateClient(int? mixedPort, TimeSpan timeout)
    {
        var useProxy = mixedPort is > 0;
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            Proxy = useProxy ? new WebProxy($"http://127.0.0.1:{mixedPort}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SingBoxWinUI");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件清不掉不影响主流程
        }
    }
}
