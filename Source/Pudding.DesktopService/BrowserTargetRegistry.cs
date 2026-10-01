using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// <see cref="IDesktopBrowserTargetRegistry"/> 的进程内默认实现：由 WinUI 侧在**页面创建/关闭/切换**时驱动。
///
/// 设计要点：
/// • <b>fail closed</b>：未登记的上下文一律 <see cref="DesktopContextTrust.Untrusted"/>，
///   未登记的页面不是 Agent 目标——「没登记」绝不等于「可信」；
/// • <b>不报告陈旧的活跃页</b>：页面或上下文被注销时，若它正是活跃页则一并清除，
///   否则调用方会拿一个已经不存在的"当前页"；
/// • 线程安全：WinUI 侧在 UI 线程更新，映射层在后台线程读取。
/// </summary>
public sealed class BrowserTargetRegistry : IDesktopBrowserTargetRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DesktopContextTrust> _trust = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _agentTargets = new(StringComparer.Ordinal);
    private (string ContextId, string PageId)? _active;

    public void RegisterContext(string contextId, DesktopContextTrust trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            _trust[contextId] = trust;
            _pages.TryAdd(contextId, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    /// <summary>注销上下文（同时清掉它的页面与 Agent 目标；活跃页若在此上下文则一并清除）。</summary>
    public void UnregisterContext(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            _trust.Remove(contextId);
            _pages.Remove(contextId);
            _agentTargets.Remove(contextId);

            if (_active is { } active && string.Equals(active.ContextId, contextId, StringComparison.Ordinal))
            {
                _active = null;
            }
        }
    }

    public void RegisterPage(string contextId, string pageId, bool isAgentTarget = false, bool isActive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            if (!_pages.TryGetValue(contextId, out var pages))
            {
                pages = new HashSet<string>(StringComparer.Ordinal);
                _pages[contextId] = pages;
            }

            pages.Add(pageId);

            if (isAgentTarget)
            {
                if (!_agentTargets.TryGetValue(contextId, out var targets))
                {
                    targets = new HashSet<string>(StringComparer.Ordinal);
                    _agentTargets[contextId] = targets;
                }

                targets.Add(pageId);
            }

            if (isActive)
            {
                _active = (contextId, pageId);
            }
        }
    }

    /// <summary>注销页面；若它正是活跃页则清除（不报告已不存在的活跃页）。</summary>
    public void UnregisterPage(string contextId, string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            if (_pages.TryGetValue(contextId, out var pages))
            {
                pages.Remove(pageId);
            }

            if (_agentTargets.TryGetValue(contextId, out var targets))
            {
                targets.Remove(pageId);
            }

            if (_active is { } active
                && string.Equals(active.ContextId, contextId, StringComparison.Ordinal)
                && string.Equals(active.PageId, pageId, StringComparison.Ordinal))
            {
                _active = null;
            }
        }
    }

    /// <summary>设置/清除活跃页（<c>null</c> 表示当前没有活动页）。</summary>
    public void SetActivePage(string? contextId, string? pageId)
    {
        lock (_sync)
        {
            _active = string.IsNullOrWhiteSpace(contextId) || string.IsNullOrWhiteSpace(pageId)
                ? null
                : (contextId, pageId);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _trust.Clear();
            _pages.Clear();
            _agentTargets.Clear();
            _active = null;
        }
    }

    public DesktopContextTrust TrustFor(string contextId)
    {
        lock (_sync)
        {
            // 未登记 ⇒ 最保守级别（fail closed）。
            return _trust.TryGetValue(contextId ?? string.Empty, out var trust)
                ? trust
                : DesktopContextTrust.Untrusted;
        }
    }

    public bool IsAgentTarget(string contextId, string pageId)
    {
        lock (_sync)
        {
            return _agentTargets.TryGetValue(contextId ?? string.Empty, out var targets)
                && targets.Contains(pageId ?? string.Empty);
        }
    }

    public (string ContextId, string PageId)? ActivePage
    {
        get
        {
            lock (_sync)
            {
                // 只报告仍然登记在册的活跃页；否则视为"没有活动页"。
                if (_active is not { } active)
                {
                    return null;
                }

                return _pages.TryGetValue(active.ContextId, out var pages) && pages.Contains(active.PageId)
                    ? active
                    : null;
            }
        }
    }
}