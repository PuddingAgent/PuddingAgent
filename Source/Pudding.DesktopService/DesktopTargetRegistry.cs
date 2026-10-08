using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 已登记的页面目标状态：版本、就绪度、可信级别与「是否为 Agent 目标」由 Desktop 侧（WinUI）维护。
/// </summary>
/// <param name="IsAgentTarget">
/// 该页面是否为用户选定的 <b>Agent 目标页</b>。准入需要它：<see cref="DesktopContextTrust"/> 是
/// <b>context 级</b>，不能把整个 <see cref="DesktopContextTrust.AgentAuthorized"/> 上下文当作
/// 「里面所有页面都已获授权」（审计 2026-10-08 结论 #2）。
/// </param>
public sealed record DesktopTargetState(
    DesktopPageTarget Target,
    DesktopPageVersion Version,
    DesktopPageReadiness Readiness,
    DesktopContextTrust Trust,
    bool IsOpen,
    bool IsAgentTarget = false);

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
        DesktopPageReadiness readiness = DesktopPageReadiness.Unknown,
        bool isAgentTarget = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (!_contexts.TryGetValue(target.ContextId, out var trust))
            {
                throw new InvalidOperationException(
                    $"Context '{target.ContextId}' must be registered before its pages.");
            }

            _pages[target.Key] = new DesktopTargetState(
                target, version, readiness, trust, IsOpen: true, IsAgentTarget: isAgentTarget);
        }
    }

    /// <summary>
    /// 设置/撤销「用户选定的 Agent 目标页」（同一时刻至多一个；<c>null</c> 表示没有目标）。
    ///
    /// 为什么必须是一等操作：<see cref="RegisterPage"/> 只能把标记设成给定值，
    /// 若没有撤销路径，用户切换目标或接管浏览器之后，注册表会**继续替旧页面背书**，
    /// 而这一点从只读状态无法自证。
    ///
    /// 未登记（或已关闭）的页面**不会**被标记 —— 不凭一个 id 凭空造出目标（fail closed）。
    /// 返回是否真的标记到了页面。
    /// </summary>
    public bool SetAgentTarget(string? contextId, string? pageId)
    {
        lock (_sync)
        {
            foreach (var key in _pages.Where(pair => pair.Value.IsAgentTarget).Select(pair => pair.Key).ToArray())
            {
                _pages[key] = _pages[key] with { IsAgentTarget = false };
            }

            if (string.IsNullOrWhiteSpace(contextId) || string.IsNullOrWhiteSpace(pageId))
            {
                return false;
            }

            var target = new DesktopPageTarget(contextId, pageId);
            if (!_pages.TryGetValue(target.Key, out var state) || !state.IsOpen)
            {
                return false;
            }

            _pages[target.Key] = state with { IsAgentTarget = true };
            return true;
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
