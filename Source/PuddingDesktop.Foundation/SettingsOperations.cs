namespace PuddingDesktop.Foundation;

/// <summary>The workspace/agent pair a settings page is currently bound to. Presentation identity only.</summary>
public sealed record SettingsSelection(string WorkspaceId, string AgentId)
{
    public static SettingsSelection None { get; } = new("", "");
    public bool IsSpecified => WorkspaceId.Length > 0 && AgentId.Length > 0;
}

/// <summary>
/// Identifies the exact kernel instance and selection an operation was issued against.
/// A stamp captured before a kernel swap or a selection change must never write into the new context.
/// </summary>
public readonly record struct SettingsStamp(long KernelEpoch, long SelectionEpoch, SettingsSelection Selection)
{
    public bool IsUnbound => KernelEpoch <= 0;
}

public enum SettingsUnavailable
{
    KernelNotConfigured,
    KernelStopped,
    KernelStarting,
    KernelStopping,
    KernelFailed
}

/// <summary>Base for refusals that the settings UI must display as a real reason instead of a fake success.</summary>
public abstract class SettingsOperationException(string message) : InvalidOperationException(message);

public sealed class SettingsUnavailableException(SettingsUnavailable reason, string message)
    : SettingsOperationException(message)
{
    public SettingsUnavailable Reason { get; } = reason;
}

/// <summary>The kernel instance or the selected workspace/agent changed while the operation was in flight.</summary>
public sealed class SettingsSupersededException(string message) : SettingsOperationException(message);

/// <summary>A write carried a version that no longer matches the stored one.</summary>
public sealed class SettingsConflictException(string message) : SettingsOperationException(message);

/// <summary>One operation's isolated service scope. Composition owns the container; Foundation only sees the provider.</summary>
public interface ISettingsScope : IAsyncDisposable
{
    IServiceProvider Services { get; }
}

/// <summary>
/// Implemented by Composition against the live in-process Core. The shell never sees Core types,
/// DbContext, controllers or HTTP: it hands over a delegate that consumes the scoped provider.
/// </summary>
public interface ISettingsOperationHost
{
    Task<T> RunAsync<T>(Func<ISettingsScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken);
}

/// <summary>
/// Serializes settings operations against one kernel generation and one selection without referencing Core.
/// Owns: readiness refusal, stop-time refusal of new work, drain before release, and epoch invalidation.
/// </summary>
public sealed class SettingsOperationGate
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _operations = [];
    private long _kernelEpoch;
    private long _selectionEpoch;
    private SettingsSelection _selection = SettingsSelection.None;
    private DesktopKernelState _state = DesktopKernelState.Stopped;

    public DesktopKernelState State { get { lock (_sync) return _state; } }
    public SettingsSelection Selection { get { lock (_sync) return _selection; } }
    public long KernelEpoch { get { lock (_sync) return _kernelEpoch; } }
    public int OutstandingOperations { get { lock (_sync) return _operations.Count; } }

    /// <summary>A new kernel generation invalidates every earlier stamp and clears the bound selection.</summary>
    public void KernelChanged(DesktopKernelState state)
    {
        lock (_sync) { _kernelEpoch++; _selectionEpoch++; _selection = SettingsSelection.None; _state = state; }
    }

    public void SetState(DesktopKernelState state) { lock (_sync) _state = state; }

    public SettingsStamp Capture() { lock (_sync) return CaptureLocked(); }

    /// <summary>Returns true when the selection actually changed and therefore invalidated in-flight work.</summary>
    public bool SetSelection(SettingsSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        lock (_sync)
        {
            if (_selection == selection) return false;
            _selection = selection;
            _selectionEpoch++;
            return true;
        }
    }

    public bool IsCurrent(in SettingsStamp stamp)
    {
        lock (_sync) return IsCurrentLocked(stamp);
    }

    public void EnsureCurrent(in SettingsStamp stamp)
    {
        lock (_sync)
            if (!IsCurrentLocked(stamp))
                throw new SettingsSupersededException("内核实例或所选工作区/角色已变化，本次结果已作废，请重新读取。");
    }

    /// <summary>Refuses new work unless the kernel is ready; converts kernel state into a displayable reason.</summary>
    public void EnsureAvailable()
    {
        lock (_sync) EnsureAvailableLocked();
    }

    public Task<T> RunAsync<T>(Func<SettingsStamp, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        Task<T> operation;
        lock (_sync)
        {
            EnsureAvailableLocked();
            var stamp = CaptureLocked();
            operation = Task.Run(() => body(stamp, cancellationToken), CancellationToken.None);
            TrackLocked(operation);
        }
        return operation;
    }

    /// <summary>Waits for every accepted operation so the kernel session is not released under a live caller.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task[] operations;
        lock (_sync) operations = [.. _operations];
        if (operations.Length == 0) return;
        try { await Task.WhenAll(operations).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch when (!cancellationToken.IsCancellationRequested) { /* each caller observes its own failure */ }
    }

    private void TrackLocked(Task operation)
    {
        _operations.Add(operation);
        _ = operation.ContinueWith(completed => { lock (_sync) _operations.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private SettingsStamp CaptureLocked() => new(_kernelEpoch, _selectionEpoch, _selection);

    private bool IsCurrentLocked(in SettingsStamp stamp)
        => stamp.KernelEpoch == _kernelEpoch && stamp.SelectionEpoch == _selectionEpoch;

    private void EnsureAvailableLocked()
    {
        switch (_state)
        {
            case DesktopKernelState.Ready:
                return;
            case DesktopKernelState.Stopping:
                throw new SettingsUnavailableException(SettingsUnavailable.KernelStopping,
                    "Core 正在停止，已拒绝新的设置操作。");
            case DesktopKernelState.Starting:
                throw new SettingsUnavailableException(SettingsUnavailable.KernelStarting,
                    "Core 尚未就绪，设置页暂时只能浏览。");
            case DesktopKernelState.Failed:
                throw new SettingsUnavailableException(SettingsUnavailable.KernelFailed,
                    "Core 未能就绪，请在运行中心查看原因后重试。");
            case DesktopKernelState.NotConfigured:
                throw new SettingsUnavailableException(SettingsUnavailable.KernelNotConfigured,
                    "尚未配置数据目录，设置页暂时只能浏览。");
            default:
                throw new SettingsUnavailableException(SettingsUnavailable.KernelStopped,
                    "Core 未启动，设置页暂时只能浏览。");
        }
    }
}

/// <summary>Optimistic concurrency for settings writes that carry a stored version.</summary>
public static class SettingsVersionGuard
{
    public static void EnsureCurrent(int? expected, int actual, string target)
    {
        if (expected is null || expected == actual) return;
        throw new SettingsConflictException(
            $"{target} 已在他处更新（当前版本 {actual}，本次提交版本 {expected}）。请重新读取后再保存。");
    }
}
