using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.Helpers;

/// <summary>
/// 一次文件选择的结果。Path 为 null 时：Error 为 null 表示用户取消，非 null 表示对话框真的失败了，
/// 调用方据此决定「静默返回」还是「明确提示」，避免再次出现「点了按钮毫无反应」。
/// </summary>
public sealed class FilePickResult
{
    private FilePickResult(string? path, string? error)
    {
        Path = path;
        Error = error;
    }

    public string? Path { get; }

    public string? Error { get; }

    public bool IsSuccess => Path is not null;

    public string Name => Path is null ? string.Empty : System.IO.Path.GetFileName(Path);

    internal static FilePickResult Success(string path) => new(path, null);

    internal static FilePickResult Cancelled() => new(null, null);

    internal static FilePickResult Failed(string error) => new(null, error);
}

/// <summary>
/// 原生 Win32 通用文件对话框（comdlg32.dll）。
///
/// 为什么不用 Windows.Storage.Pickers：Unpackaged 应用在提权（TUN / 始终管理员）进程里
/// 激活 FileOpenPicker / FileSavePicker 会抛 COMException，旧实现 catch 后静默返回 null，
/// 界面表现就是「Import Local File 点了没反应」。通用对话框是纯 user32/comdlg32，
/// 不依赖 WinRT 激活，提权与否都能正常弹出。
///
/// OFN_NOCHANGEDIR 必须带：否则用户选中文件后进程 CWD 会被改成该文件所在目录，
/// 之后所有相对路径（Core\、rulesets\）会指向错误位置。
/// </summary>
public static class FilePickerHelper
{
    private const int MaxFileChars = 32768;

    private const int OfnOverwritePrompt = 0x00000002;
    private const int OfnHideReadOnly = 0x00000004;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;

    /// <summary>UI 线程队列：首次在 UI 线程调用时缓存，万一从后台线程调用也能把对话框切回去。</summary>
    private static DispatcherQueue? _uiQueue;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int LStructSize;
        public IntPtr HwndOwner;
        public IntPtr HInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrFilter;
        public IntPtr LpstrCustomFilter;
        public int NMaxCustFilter;
        public int NFilterIndex;
        public IntPtr LpstrFile;
        public int NMaxFile;
        public IntPtr LpstrFileTitle;
        public int NMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrTitle;
        public int Flags;
        public short NFileOffset;
        public short NFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrDefExt;
        public IntPtr LCustData;
        public IntPtr LpfnHook;
        public IntPtr LpTemplateName;
        public IntPtr PvReserved;
        public int DwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    /// <summary>打开单个文件；extensions 形如 ".json"。</summary>
    public static Task<FilePickResult> PickOpenFileAsync(params string[] extensions)
    {
        var types = new (string Label, string Extension)[extensions.Length + 1];
        for (var i = 0; i < extensions.Length; i++)
        {
            var ext = NormalizeExtension(extensions[i]);
            types[i] = (LocalizationService.Instance.PickerFilterLabel(ext), ext);
        }

        types[^1] = (LocalizationService.Instance.PickerAllFiles, ".*");

        return InvokeAsync(() => ShowDialog(isSave: false, suggestedFileName: null, types));
    }

    /// <summary>另存为；types 为 (显示名, 扩展名) 列表，第一个作为默认类型。</summary>
    public static Task<FilePickResult> PickSaveFileAsync(
        string suggestedFileName,
        params (string Label, string Extension)[] types)
    {
        var normalized = new (string Label, string Extension)[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            normalized[i] = (types[i].Label, NormalizeExtension(types[i].Extension));
        }

        return InvokeAsync(() => ShowDialog(isSave: true, suggestedFileName, normalized));
    }

    private static FilePickResult ShowDialog(
        bool isSave,
        string? suggestedFileName,
        (string Label, string Extension)[] types)
    {
        var action = LocalizationService.Instance.Say("打开文件选择器", "Open file dialog");
        var buffer = Marshal.AllocHGlobal(MaxFileChars * sizeof(char));
        try
        {
            // 清零后写入建议文件名（保存对话框用它预填）
            Marshal.Copy(new byte[MaxFileChars * sizeof(char)], 0, buffer, MaxFileChars * sizeof(char));
            if (isSave && !string.IsNullOrEmpty(suggestedFileName))
            {
                var chars = suggestedFileName.ToCharArray();
                Marshal.Copy(chars, 0, buffer, Math.Min(chars.Length, MaxFileChars - 1));
            }

            var flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist | OfnHideReadOnly;
            flags |= isSave ? OfnOverwritePrompt : OfnFileMustExist;

            var ofn = new OpenFileName
            {
                LStructSize = Marshal.SizeOf<OpenFileName>(),
                HwndOwner = WindowHelper.MainWindowHandle,
                LpstrFilter = BuildFilter(types),
                NFilterIndex = 1,
                LpstrFile = buffer,
                NMaxFile = MaxFileChars,
                LpstrTitle = isSave
                    ? LocalizationService.Instance.PickerSaveTitle
                    : LocalizationService.Instance.PickerOpenTitle,
                Flags = flags,
                LpstrDefExt = types.Length > 0 ? types[0].Extension.TrimStart('.') : null,
            };

            var ok = isSave ? GetSaveFileName(ref ofn) : GetOpenFileName(ref ofn);
            if (!ok)
            {
                var code = CommDlgExtendedError();
                if (code == 0)
                {
                    return FilePickResult.Cancelled();
                }

                var reason = $"0x{code:X}";
                LogFailure(action, new InvalidOperationException($"CommDlgExtendedError = {reason}"));
                return FilePickResult.Failed(LocalizationService.Instance.PickerFailedMessage(reason));
            }

            var path = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(path))
            {
                return FilePickResult.Cancelled();
            }

            // 用户切了文件类型又没写扩展名时补上，避免导出出无后缀文件
            if (isSave && types.Length > 0 && string.IsNullOrEmpty(System.IO.Path.GetExtension(path)))
            {
                var index = Math.Clamp(ofn.NFilterIndex - 1, 0, types.Length - 1);
                var ext = types[index].Extension;
                if (ext != ".*")
                {
                    path += ext;
                }
            }

            return FilePickResult.Success(path);
        }
        catch (Exception ex)
        {
            LogFailure(action, ex);
            return FilePickResult.Failed(LocalizationService.Instance.PickerFailedMessage(ex.Message));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// lpstrFilter 是一串以 \0 分隔的「显示名 / 通配符」对，最后再用一个 \0 收尾。
    /// 字符串里的内嵌 \0 会由 LPWStr 封送原样复制（封送器另补一个终止符）。
    /// </summary>
    private static string BuildFilter((string Label, string Extension)[] types)
    {
        var sb = new StringBuilder();
        foreach (var (label, ext) in types)
        {
            var pattern = ext == ".*" ? "*.*" : "*" + ext;
            sb.Append(label).Append('\0').Append(pattern).Append('\0');
        }

        return sb.Append('\0').ToString();
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".*";
        }

        var ext = extension.Trim();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    /// <summary>通用对话框必须跑在带消息泵的 UI 线程；所有调用点本来就是 UI 事件处理器。</summary>
    private static Task<FilePickResult> InvokeAsync(Func<FilePickResult> action)
    {
        _uiQueue ??= DispatcherQueue.GetForCurrentThread();
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return Task.FromResult(action());
        }

        var tcs = new TaskCompletionSource<FilePickResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            return Task.FromResult(FilePickResult.Failed(
                LocalizationService.Instance.Say("界面线程不可用", "UI thread unavailable")));
        }

        return tcs.Task;
    }

    private static void LogFailure(string action, Exception ex)
    {
        try
        {
            App.Main?.AppendLog($"[提示] {action}失败：{ex.Message}");
        }
        catch (Exception)
        {
            // 日志系统本身不可用时不再抛
        }
    }
}
