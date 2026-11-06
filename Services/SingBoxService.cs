using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SingBoxWinUI.Services;

public enum StopOutcome
{
    NotRunning,
    /// <summary>收到 Ctrl+C 后自行退出，已清理虚拟网卡与路由。</summary>
    Graceful,
    /// <summary>超时未退出，被强制终止（TUN 模式下可能残留路由）。</summary>
    Forced,
}

/// <summary>sing-box check 的结果，Output 已剥离 ANSI、按行拆分。</summary>
public sealed record ConfigCheckResult(int ExitCode, IReadOnlyList<string> Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 只负责 sing-box 进程的生命周期与输出转发，不碰任何 UI。
/// 所有事件都在后台线程触发，调用方需自行 marshal 回 UI 线程。
/// </summary>
public sealed class SingBoxService : IDisposable
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;

    /// <summary>内核 Go 运行时的 GC 目标百分比（默认 100，调低=更积极回收）。</summary>
    private const string KernelGoGcPercent = "40";

    /// <summary>
    /// 内核 Go 堆的软上限。当前配置带 10 个本地 JSON 规则集，编译后的常驻存活堆约 50~60 MB，
    /// 上限必须明显高于它，否则高流量下 GC 会持续空转抖动。规则集再多就把这个值继续调大。
    /// </summary>
    private const string KernelGoMemoryLimit = "80MiB";

    /// <summary>
    /// 附加到子进程控制台期间，本进程会被同一控制台上的信号波及，
    /// 用这个处理器把所有控制台事件吞掉；必须是静态字段，否则委托被 GC 后进程会被信号打死。
    /// </summary>
    private static readonly ConsoleCtrlHandler SwallowAllHandler = _ => true;

    private static readonly IntPtr SwallowAllHandlerPtr =
        Marshal.GetFunctionPointerForDelegate(SwallowAllHandler);

    /// <summary>剥离 ANSI 颜色控制符（如 ESC[36m），防止日志面板出现乱码。</summary>
    private static readonly Regex AnsiEscape = new(@"\u001B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private Process? _process;

    public SingBoxService()
    {
        CoreDirectory = DefaultCoreDirectory;
    }

    /// <summary>内核目录固定为程序目录下的 Core。</summary>
    public static string DefaultCoreDirectory => Path.Combine(AppContext.BaseDirectory, "Core");

    public string CoreDirectory { get; }

    public string ExecutablePath => Path.Combine(CoreDirectory, "sing-box.exe");

    public string DefaultConfigPath => Path.Combine(CoreDirectory, "config.json");

    /// <summary>当前内核进程 Id，未运行时为 null（用于读取 WorkingSet 内存占用）。</summary>
    public int? ProcessId
    {
        get
        {
            try
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 配置里 rule_set 用的相对路径是 ../rulesets/，相对的是工作目录（CoreDirectory），
    /// 所以实际位置是 Core 的同级目录。
    /// </summary>
    public string RulesetsDirectory =>
        Path.GetFullPath(Path.Combine(CoreDirectory, "..", "rulesets"));

    /// <summary>
    /// 结束「指向同一个 sing-box.exe」的残留进程，返回 (已结束数, 因权限不足没能结束的数)。
    /// 场景：上次 App 被强杀 / 崩溃 / 关机中断，内核成了孤儿还在跑。
    /// 它一直持有 Core\cache.db 的排他锁（bbolt flock），新内核拿不到锁就会
    /// 报 "initialize cache-file: timeout" 并以退出码 1 直接退出。
    /// 只按 exe 绝对路径匹配，不会误杀用户自己另装的 sing-box。
    /// </summary>
    public (int Killed, int Denied) KillStrayInstances(int waitMs = 3000)
    {
        string expected;
        try
        {
            expected = Path.GetFullPath(ExecutablePath);
        }
        catch (Exception)
        {
            return (0, 0);
        }

        var killed = 0;
        var denied = 0;

        foreach (var process in Process.GetProcessesByName("sing-box"))
        {
            var matched = false;

            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var path = GetProcessImagePath(process);
                if (path is null
                    || !string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched = true;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(waitMs);
                killed++;
            }
            catch (Exception)
            {
                // 路径读到了但结束失败：几乎都是权限问题（对方是提权进程）
                if (matched)
                {
                    denied++;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return (killed, denied);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    // 用 char[] 而不是 StringBuilder：后者会触发 CA1838（P/Invoke 里的 StringBuilder 无法复用）
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 取进程可执行文件路径。优先 Process.MainModule；读不到（对方权限更高）时
    /// 退回 QueryFullProcessImageName —— 它只要 PROCESS_QUERY_LIMITED_INFORMATION，
    /// 跨权限也能拿到路径，否则提权孤儿内核会被漏掉、cache.db 锁一直解不开。
    /// </summary>
    private static string? GetProcessImagePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        catch (Exception)
        {
            // 落到下面的 API 兜底
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? new string(buffer, 0, size)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>内核输出的一行文本（已剥离 ANSI）。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>内核进程退出，参数为退出码。</summary>
    public event EventHandler<int>? Exited;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 启动前的配置校验：sing-box check -c &lt;configPath&gt;。
    /// 与内核同工作目录执行，保证配置里的相对路径（如 rule_set 的 ../rulesets/xxx.json）能解析。
    /// </summary>
    public async Task<ConfigCheckResult> CheckConfigAsync(string configPath, int timeoutMilliseconds = 20000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = CoreDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("check");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        // 显式指定工作目录：提权（runas）后进程的默认 CWD 可能是 System32，
        // 不指定会让配置里的 ../rulesets/ 解析到 C:\Windows\rulesets
        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(CoreDirectory);
        startInfo.Environment["ENABLE_COLOR"] = "false";
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
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

            // 杀掉进程后把两个读取任务收掉：既保证不留下未观察的 Task，
            // 也让"超时前已经打出来的错误行"不会因为提前 return 而丢失。
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 管道已断开 / 读取被取消，忽略
            }

            return new ConfigCheckResult(-1, new[] { $"配置校验超时（{timeoutMilliseconds} ms）。" });
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var lines = new List<string>();
        foreach (var block in new[] { stderr, stdout })
        {
            foreach (var line in block.Split('\n'))
            {
                var clean = StripAnsi(line).TrimEnd('\r', ' ');
                if (clean.Length > 0)
                {
                    lines.Add(clean);
                }
            }
        }

        return new ConfigCheckResult(process.ExitCode, lines);
    }

    /// <summary>拉起内核。调用方负责在此之前做文件存在性预检与运行配置生成。</summary>
    public bool Start(string configPath)
    {
        if (IsRunning)
        {
            return true;
        }

        _process?.Dispose();
        _process = null;

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = CoreDirectory,
            UseShellExecute = false,
            // 隐藏窗口但保留控制台，否则无法通过 GenerateConsoleCtrlEvent 优雅中断
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 用 ArgumentList 交给运行时处理引号，避免手拼字符串在含空格的路径上出错
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(CoreDirectory);

        // 双保险：既关掉内核自己的着色，也在接收端剥 ANSI
        startInfo.Environment["ENABLE_COLOR"] = "false";
        startInfo.Environment["NO_COLOR"] = "1";

        // Go 运行时内存控制：sing-box 是 Go 写的，默认 GOGC=100 且没有软上限，
        // 堆涨上去以后即使空闲也不会主动还给系统（工作集一路挂着）。
        // GOGC=40：每次回收把目标堆压到上次存活量的 1.4 倍，回收更积极；
        // GOMEMLIMIT=48MiB：软上限，堆接近它时 GC 会持续工作并调用 madvise 归还空闲页。
        // 两者都不限制可用内存，只是提高回收频率，突发流量不会因此 OOM（最多是多花点 CPU）。
        startInfo.Environment["GOGC"] = KernelGoGcPercent;
        startInfo.Environment["GOMEMLIMIT"] = KernelGoMemoryLimit;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) => RaiseOutput(e.Data);
        process.ErrorDataReceived += (_, e) => RaiseOutput(e.Data);
        process.Exited += (_, _) =>
        {
            int exitCode = -1;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // 进程对象已释放，退出码不可用
            }

            Exited?.Invoke(this, exitCode);
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }
        }
        catch (Exception)
        {
            process.Dispose();
            throw;
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return true;
    }

    public Task<StopOutcome> StopAsync(int gracefulTimeoutMs = 3000) =>
        Task.Run(() => Stop(gracefulTimeoutMs));

    /// <summary>
    /// 先发 Ctrl+C 让内核自己卸载 Wintun 网卡、还原路由表，超时才强杀。
    /// 同步阻塞（最多 gracefulTimeoutMs + 3 秒），窗口关闭时也用它做兜底。
    /// </summary>
    public StopOutcome Stop(int gracefulTimeoutMs = 3000)
    {
        var process = _process;
        if (process is null)
        {
            return StopOutcome.NotRunning;
        }

        if (HasExited(process))
        {
            return StopOutcome.NotRunning;
        }

        int processId;
        try
        {
            processId = process.Id;
        }
        catch (InvalidOperationException)
        {
            return StopOutcome.NotRunning;
        }

        var deadline = Environment.TickCount64 + gracefulTimeoutMs;

        // 只附加一次，两轮信号都发在同一个控制台上。
        // 信号是异步投递的，处理器必须一直挂着，提前 detach 会被自己发出的信号打死。
        var attached = AttachToConsole(processId);

        if (attached)
        {
            try
            {
                // 第一段：规范要求的 CTRL_C。
                // 注意：子进程是 CREATE_NO_WINDOW 起的隐藏控制台，没有控制台输入句柄，
                // 实测 CTRL_C 不会被投递（AttachConsole / GenerateConsoleCtrlEvent 都返回成功，内核却收不到），
                // 所以这里只作短暂探测，不能当作唯一手段。
                GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                if (WaitForExit(process, Math.Min(300, Remaining(deadline))))
                {
                    return StopOutcome.Graceful;
                }

                // 第二段：CTRL_BREAK。隐藏控制台下它能正常投递，
                // Go 运行时把 CTRL_C 与 CTRL_BREAK 都映射为 SIGINT，内核走的仍是同一条优雅退出路径。
                GenerateConsoleCtrlEvent(CtrlBreakEvent, 0);
                if (WaitForExit(process, Remaining(deadline)))
                {
                    return StopOutcome.Graceful;
                }
            }
            finally
            {
                // 子进程已退出（控制台随之销毁）或即将被强杀，此时 detach 才安全
                FreeConsole();
            }
        }

        // 兜底：内核彻底卡死，只能强杀进程树
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 进程已退出 / 已被回收，忽略
        }

        return StopOutcome.Forced;
    }

    private static int Remaining(long deadline) =>
        (int)Math.Max(0, deadline - Environment.TickCount64);

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool WaitForExit(Process process, int milliseconds)
    {
        try
        {
            return process.WaitForExit(milliseconds);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// 把自己挂到子进程的隐藏控制台上，并注册一个吞掉所有控制台事件的处理器。
    /// CTRL_C / CTRL_BREAK 只能广播给"当前控制台上的所有进程"，所以必须先 AttachConsole；
    /// 而本进程也在广播范围内，不注册处理器就会被自己发出的信号打死。
    /// </summary>
    private static bool AttachToConsole(int processId)
    {
        FreeConsole();

        if (!AttachConsole((uint)processId))
        {
            return false;
        }

        // 必须在 AttachConsole 之后注册：没有控制台时注册不生效
        SetConsoleCtrlHandler(SwallowAllHandlerPtr, add: true);
        return true;
    }

    private void RaiseOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var clean = StripAnsi(line).TrimEnd();
        if (clean.Length == 0)
        {
            return;
        }

        OutputReceived?.Invoke(this, clean);
    }

    private static string StripAnsi(string text) => AnsiEscape.Replace(text, string.Empty);

    public void Dispose()
    {
        Stop(2000);
        _process?.Dispose();
        _process = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool ConsoleCtrlHandler(uint ctrlType);
}
