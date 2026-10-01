using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>已登记的页面目标状态：版本、就绪度与可信级别由 Desktop 侧（WinUI）维护。</summary>
public sealed record DesktopTargetState(
    DesktopPageTarget Target,
    DesktopPageVersion Version,
    DesktopPageReadiness Readiness,
    DesktopContextTrust Trust,
    bool IsOpen);

/// <summary>
/// 页面/上下文目标登记表（唯一真源）。调用方**不能**声明可信级别：
/// 可信级别只由 Desktop 侧在创建上下文/页面时登记，随后由本表判定。
///
/// 线程安全：UI 线程写入（页面创建/导航/关闭），连接线程读取（命令校验）。
/// </summary>
public sealed class DesktopTargetRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DesktopContextTrust> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesktopTargetState> _pages = new(StringComparer.Ordinal);

    /// <summary>登记上下文（可信级别在此时确定，之后不可由调用方改写）。</summary>
    public void RegisterContext(string contextId, DesktopContextTrust trust)
    {
        if (!IsValidIdentifier(contextId))
        {
            throw new ArgumentException("Context id must be a non-empty identifier.", nameof(contextId));
        }

        lock (_sync)
        {
            _contexts[contextId] = trust;
        }
    }

    /// <summary>登记页面并绑定到已登记的上下文。</summary>
    public void RegisterPage(
        DesktopPageTarget target,
        DesktopPageVersion version = default,
        DesktopPageReadiness readiness = DesktopPageReadiness.Unknown)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (!_contexts.TryGetValue(target.ContextId, out var trust))
            {
                throw new InvalidOperationException(
                    $"Context '{target.ContextId}' must be registered before its pages.");
            }

            _pages[target.Key] = new DesktopTargetState(target, version, readiness, trust, IsOpen: true);
        }
    }

    /// <summary>更新页面版本/就绪度（导航开始、DOM 就绪、交互提交后都会调用）。</summary>
    public bool UpdatePage(
        DesktopPageTarget target,
        DesktopPageVersion version,
        DesktopPageReadiness readiness = DesktopPageReadiness.Interactive)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (!_pages.TryGetValue(target.Key, out var state) || !state.IsOpen)
            {
                return false;
            }

            // 版本只能前进：回退会让旧的 Snapshot/Locator 重新"有效"，必须拒绝。
            if (version.IsKnown && state.Version.IsKnown && version.Value < state.Version.Value)
            {
                return false;
            }

            _pages[target.Key] = state with { Version = version, Readiness = readiness };
            return true;
        }
    }

    public bool ClosePage(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            return _pages.Remove(target.Key);
        }
    }

    /// <summary>关闭上下文的全部页面（工作台上下文销毁）。</summary>
    public int CloseContext(string contextId)
    {
        lock (_sync)
        {
            _contexts.Remove(contextId);
            var keys = _pages
                .Where(pair => string.Equals(pair.Value.Target.ContextId, contextId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var key in keys)
            {
                _pages.Remove(key);
            }

            return keys.Length;
        }
    }

    /// <summary>解析目标状态；未知或已关闭返回 <c>null</c>。</summary>
    public DesktopTargetState? Resolve(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            return _pages.TryGetValue(target.Key, out var state) && state.IsOpen ? state : null;
        }
    }

    public int ContextCount
    {
        get
        {
            lock (_sync)
            {
                return _contexts.Count;
            }
        }
    }

    public int OpenPageCount
    {
        get
        {
            lock (_sync)
            {
                return _pages.Count;
            }
        }
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= DesktopPageTarget.MaxLength;
}
