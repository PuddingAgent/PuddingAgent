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
    /// <exception cref="InvalidOperationException">
    /// 同一 context 已登记且**可信级别不同**。级别由 Desktop 侧在创建上下文时确定，
    /// 允许"重新登记"覆盖它等于给出了一条静默提权路径（Untrusted → AgentAuthorized）；
    /// 需要换级别必须先 <see cref="CloseContext"/>。相同级别重复登记是幂等的。
    /// </exception>
    public void RegisterContext(string contextId, DesktopContextTrust trust)
    {
        if (!IsValidIdentifier(contextId))
        {
            throw new ArgumentException("Context id must be a non-empty identifier.", nameof(contextId));
        }

        lock (_sync)
        {
            if (_contexts.TryGetValue(contextId, out var existing))
            {
                if (existing != trust)
                {
                    throw new InvalidOperationException(
                        $"Context '{contextId}' is already registered as {existing}; its trust level is decided at creation and cannot be rewritten to {trust}.");
                }

                return;
            }

            _contexts[contextId] = trust;
        }
    }

    /// <summary>
    /// 登记页面并绑定到已登记的上下文。
    ///
    /// <b>对已登记页面是非破坏性的</b>（设计方案 §3.3「原子发布登记与授权事实」）：
    /// 页面生命周期事件会重复触发（标签重建、重新播种、窗口重连），而"再次登记"绝不能让
    /// 已经发布的事实变弱：
    /// · <b>版本只进不退</b> —— 否则 <see cref="UpdatePage"/> 的"版本只能前进"不变式会被绕过，
    ///   旧 Snapshot/Locator 重新"有效"；
    /// · <b>就绪度</b>：<see cref="DesktopPageReadiness.Unknown"/> 表示"没有新信息"，保留原值；
    /// · <b>Agent 目标标记</b>：<paramref name="isAgentTarget"/> 为 <c>null</c> 表示"不表态"，
    ///   保留原值；<c>true</c>/<c>false</c> 才是显式设置/清除。
    ///   默认值 <c>null</c> 很关键：创建事件对"这一页是否是用户选定的 Agent 目标"没有意见，
    ///   若默认按 <c>false</c> 处理，一次重复的创建事件就会**静默关掉页级准入闸门**。
    /// </summary>
    public void RegisterPage(
        DesktopPageTarget target,
        DesktopPageVersion version = default,
        DesktopPageReadiness readiness = DesktopPageReadiness.Unknown,
        bool? isAgentTarget = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (!_contexts.TryGetValue(target.ContextId, out var trust))
            {
                throw new InvalidOperationException(
                    $"Context '{target.ContextId}' must be registered before its pages.");
            }

            if (!_pages.TryGetValue(target.Key, out var existing) || !existing.IsOpen)
            {
                _pages[target.Key] = new DesktopTargetState(
                    target,
                    version,
                    readiness,
                    trust,
                    IsOpen: true,
                    IsAgentTarget: isAgentTarget ?? false);
                return;
            }

            // 已登记的开放页面：合并新信息，绝不削弱已有事实。
            var mergedVersion = version.IsKnown
                && (!existing.Version.IsKnown || version.Value > existing.Version.Value)
                    ? version
                    : existing.Version;

            _pages[target.Key] = existing with
            {
                Version = mergedVersion,
                Readiness = readiness == DesktopPageReadiness.Unknown ? existing.Readiness : readiness,
                IsAgentTarget = isAgentTarget ?? existing.IsAgentTarget,
            };
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

    /// <summary>
    /// 某上下文下**当前仍登记**的页面目标。
    /// 关闭上下文前必须先把清单取出来：注册表移除之后就查不到了，而授权是按页绑定的，
    /// 逐个撤销需要这份清单（缺口会表现为"页面没了但授权还在"）。
    /// </summary>
    public IReadOnlyList<DesktopPageTarget> PagesInContext(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            return _pages.Values
                .Where(page => string.Equals(page.Target.ContextId, contextId, StringComparison.Ordinal))
                .Select(page => page.Target)
                .ToArray();
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

    /// <summary>
    /// 其中被标记为「用户选定的 Agent 目标」的数量。
    /// 设计方案 §3.3 要求**目标数与授权数分别统计**（不混为一项）：授权数属于 grant 模型，
    /// 目前尚未接入（见 code_map 已知缺口），因此这里只给出目标数。
    /// </summary>
    public int AgentTargetCount
    {
        get
        {
            lock (_sync)
            {
                return _pages.Values.Count(page => page.IsAgentTarget);
            }
        }
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= DesktopPageTarget.MaxLength;
}
