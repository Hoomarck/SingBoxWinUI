namespace SingBoxWinUI.Services;

/// <summary>
/// ClashApiService 的统一创建与释放。
/// 之前 MainViewModel / ProxiesViewModel / ConnectionsViewModel 各写一份 EnsureApi，
/// 失效判定互不一致（MainViewModel 用 BaseUrl 拼串比较，遇到 "http://" 前缀会每次重建），
/// 且后两者退出时从不释放 HttpClient。
/// </summary>
public sealed class ClashApiClientProvider : IDisposable
{
    private ClashApiService? _current;
    private string _controller = string.Empty;
    private string _secret = string.Empty;
    private bool _disposed;

    /// <summary>当前实例，可能为 null（尚未创建）。</summary>
    public ClashApiService? Current => _current;

    /// <summary>按 controller + secret 复用；任一变化则重建并释放旧实例。</summary>
    public ClashApiService? Get(string? controller, string? secret)
    {
        if (_disposed || string.IsNullOrWhiteSpace(controller))
        {
            return null;
        }

        var normalizedSecret = secret ?? string.Empty;

        if (_current is not null
            && string.Equals(_controller, controller, StringComparison.Ordinal)
            && string.Equals(_secret, normalizedSecret, StringComparison.Ordinal))
        {
            return _current;
        }

        var previous = _current;
        _current = new ClashApiService(controller, normalizedSecret);
        _controller = controller;
        _secret = normalizedSecret;

        previous?.Dispose();
        return _current;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current?.Dispose();
        _current = null;
    }
}
