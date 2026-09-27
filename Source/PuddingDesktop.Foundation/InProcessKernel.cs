namespace PuddingDesktop.Foundation;

/// <summary>Serializes host lifetimes; Stop first cancels any in-flight initialization.</summary>
public sealed class InProcessKernel(IKernelSessionFactory factory) : IDesktopKernel
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _startup;
    private IKernelSession? _session;
    private CancellationTokenRegistration _stoppingRegistration;
    private string? _dataRoot;
    private volatile bool _disposed;
    private DesktopKernelSnapshot _snapshot = new(DesktopKernelState.Stopped, "内核未启动");
    private readonly SettingsOperationGate _settings = new();
    public DesktopKernelSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public SettingsOperationGate Settings => _settings;
    public event EventHandler? StateChanged;

    private void Set(DesktopKernelState state, string description, Uri? address = null)
    {
        Volatile.Write(ref _snapshot, new(state, description, address));
        _settings.SetState(state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<T> RunSettingsAsync<T>(string operationId,
        Func<ISettingsScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(body);
        // Readiness and stopping are decided by the gate, not by a racy snapshot read.
        _settings.EnsureAvailable();
        if (_session is not ISettingsOperationHost host)
            throw new SettingsUnavailableException(SettingsUnavailable.KernelNotConfigured,
                "当前内核会话未提供设置操作边界，设置页暂时只能浏览。");
        return await _settings.RunAsync<T>((_, _) => host.RunAsync(body, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }


    public async Task StartAsync(string dataRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = Path.GetFullPath(dataRoot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                if (Snapshot.State == DesktopKernelState.Ready && string.Equals(_dataRoot, root, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("请先停止当前内核并完成资源释放。");
            }
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync) _startup = startup;
            try
            {
                // A new generation invalidates every stamp and selection captured from the previous session.
                _settings.KernelChanged(DesktopKernelState.Starting);
                Set(DesktopKernelState.Starting, "正在初始化进程内 Core…");
                _session = await Task.Run(() => factory.StartAsync(root, startup.Token), CancellationToken.None).ConfigureAwait(false);
                _dataRoot = root;
                startup.Token.ThrowIfCancellationRequested();
                Set(DesktopKernelState.Ready, "Core 已就绪 · 与 Desktop 同进程", _session.WorkbenchAddress);
                var observedSession = _session;
                _stoppingRegistration = observedSession.Stopping.Register(() =>
                {
                    if (Snapshot.State != DesktopKernelState.Ready) return;
                    Set(DesktopKernelState.Failed, "Core 请求停止，正在释放资源。");
                    _ = Task.Run(async () =>
                    {
                        try { await StopCoreAsync(CancellationToken.None, observedSession).ConfigureAwait(false); }
                        catch { Set(DesktopKernelState.Failed, "Core 停止失败，请重试停止。"); }
                    });
                });
            }
            catch
            {
                _settings.KernelChanged(DesktopKernelState.Failed);
                Set(DesktopKernelState.Failed, "内核启动失败或已取消，请查看诊断日志。");
                if (_session is not null) { await _session.DisposeAsync().ConfigureAwait(false); _session = null; }
                throw;
            }
            finally { lock (_sync) _startup = null; }
        }
        finally { _gate.Release(); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => StopCoreAsync(cancellationToken, null);

    private async Task StopCoreAsync(CancellationToken cancellationToken, IKernelSession? expectedSession)
    {
        if (expectedSession is null) { lock (_sync) _startup?.Cancel(); }
        // Refuse new settings work immediately, then cancel and drain accepted work before releasing the session.
        _settings.SetState(DesktopKernelState.Stopping);
        await _settings.DrainAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedSession is not null && !ReferenceEquals(_session, expectedSession)) return;
            Set(DesktopKernelState.Stopping, "正在停止内核并释放资源…");
            _stoppingRegistration.Dispose();
            if (_session is not null)
            {
                try
                {
                    await _session.StopAsync(cancellationToken).ConfigureAwait(false);
                    await _session.DisposeAsync().ConfigureAwait(false);
                    _session = null;
                }
                catch { _settings.KernelChanged(DesktopKernelState.Failed); Set(DesktopKernelState.Failed, "内核停止未完成；请重试停止。"); throw; }
            }
            _dataRoot = null;
            _settings.KernelChanged(DesktopKernelState.Stopped);
            Set(DesktopKernelState.Stopped, "内核已停止");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _disposed = true; _startup?.Cancel(); }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
