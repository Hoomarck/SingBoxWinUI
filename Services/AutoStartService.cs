using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SingBoxWinUI.Services;

/// <summary>
/// 开机自启。
/// 管理员：写一个 ONLOGON + HIGHEST 的计划任务，开机即带管理员令牌，TUN 不用再弹 UAC。
/// 非管理员：降级到 HKCU Run 键，只能普通权限启动。
///
/// 注意：schtasks 是子进程调用，冷启动可达数百毫秒，安全软件拦截时更久，
/// 因此所有命令都走异步（旧实现在 UI 线程同步 WaitForExit(10000)，会让窗口卡死）。
/// </summary>
public sealed class AutoStartService
{
    private const string TaskName = "SingBoxWinUI_AutoStart";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SingBoxWinUI";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly string _exePath;

    public AutoStartService()
    {
        _exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SingBoxWinUI.exe");
    }

    public async Task<bool> IsTaskCreatedAsync(CancellationToken ct = default)
    {
        var (exitCode, _) = await RunAsync("schtasks.exe", ct, "/Query", "/TN", TaskName);
        return exitCode == 0;
    }

    /// <summary>
    /// 同步检测系统自启项，供首次生成默认设置时对齐用。
    /// 只读 HKCU Run 键（安装向导写入的正是它）；不跑 schtasks 子进程，
    /// 否则首次启动会被几百毫秒的进程调用卡住。计划任务形态由
    /// <see cref="IsEnabledAsync"/> 负责，启动后 UI 侧另有异步校准。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool IsRunKeyPresent() => IsEnabled();

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await IsTaskCreatedAsync(ct) || IsRunKeyPresent();

    /// <summary>当前自启方式的实际权限级别，用于设置页提示。</summary>
    public async Task<string> GetModeDescriptionAsync(CancellationToken ct = default) =>
        await IsTaskCreatedAsync(ct) ? LocalizationService.Instance.Say("计划任务（最高权限）", "Scheduled task (highest privileges)")
        : IsRunKeyPresent() ? LocalizationService.Instance.Say("注册表 Run（普通权限）", "Registry Run (normal privileges)")
        : LocalizationService.Instance.Say("未开启", "Off");

    /// <summary>开启/关闭自启。返回是否成功，message 用于界面提示。</summary>
    public async Task<(bool Ok, string Message)> ApplyAsync(
        bool enabled,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (!enabled)
        {
            var taskOk = await DeleteTaskAsync(ct);
            var keyOk = RemoveRunKey();
            return (true, taskOk && keyOk
                ? LocalizationService.Instance.Say("已关闭开机自启。", "Auto-start disabled.")
                : LocalizationService.Instance.Say("已关闭开机自启（部分残留可能需手动清理）。", "Auto-start disabled (some leftovers may need manual cleanup)."));
        }

        if (isAdmin)
        {
            var (ok, error) = await CreateTaskAsync(ct);

            // 计划任务与 Run 键同时存在会启动两次，提权成功后清掉降级项
            if (ok)
            {
                RemoveRunKey();
                return (true, LocalizationService.Instance.Say(
                    "已创建高权限计划任务，开机将以管理员身份启动（TUN 不再弹 UAC）。",
                    "Created an elevated scheduled task; the app starts as administrator at sign-in (no UAC prompt for TUN)."));
            }

            return (false, LocalizationService.Instance.Say($"创建计划任务失败：{error}", $"Failed to create scheduled task: {error}"));
        }

        if (WriteRunKey(out var keyError))
        {
            return (true, LocalizationService.Instance.Say(
                "已写入注册表自启（普通权限）。若需开机自动开启 TUN，请先以管理员身份重启后再开启此开关。",
                "Auto-start written to the registry (normal privileges). To start TUN at boot, elevate first and then enable this switch."));
        }

        return (false, LocalizationService.Instance.Say($"写入注册表失败：{keyError}", $"Failed to write the registry: {keyError}"));
    }

    private async Task<(bool Ok, string Error)> CreateTaskAsync(CancellationToken ct)
    {
        // schtasks /Create /TN <name> /TR "\"<exe>\" --autorun" /SC ONLOGON /RL HIGHEST /IT /F
        var (exitCode, output) = await RunAsync(
            "schtasks.exe",
            ct,
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{_exePath}\" --autorun",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/IT",
            "/F");

        return exitCode == 0 ? (true, string.Empty) : (false, output.Trim());
    }

    private async Task<bool> DeleteTaskAsync(CancellationToken ct)
    {
        var (exitCode, _) = await RunAsync("schtasks.exe", ct, "/Delete", "/TN", TaskName, "/F");

        // 任务不存在也算成功
        return exitCode == 0 || !await IsTaskCreatedAsync(ct);
    }

    private bool WriteRunKey(out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开 Run 键。";
                return false;
            }

            key.SetValue(RunValueName, $"\"{_exePath}\" --autorun");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is null)
            {
                return true;
            }

            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        CancellationToken ct,
        params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "进程启动失败。");
            }

            // 先挂上读取，避免管道缓冲区写满导致子进程阻塞
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 进程已退出，忽略
                }

                return (-1, "命令执行超时。");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
