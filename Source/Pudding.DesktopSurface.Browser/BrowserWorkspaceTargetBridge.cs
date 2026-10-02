using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace Pudding.DesktopSurface.Browser;

/// <summary>
/// 把「浏览器工作区的页面生命周期」翻译成能力通道需要的<b>两个</b>目标注册表。
///
/// 为什么要有这一层（而不是在窗口代码里逐处调用注册表）：
/// · 存在两个注册表，且它们都只回答真话才有意义——<see cref="DesktopTargetRegistry"/> 管
///   页面版本/就绪度（准入与「引用随版本失效」的依据），<see cref="BrowserTargetRegistry"/> 管
///   上下文可信级别 / Agent 目标 / 活动页（能不能注入脚本、能不能动这个页面）。
///   两处各自更新必然漂移：一边说页面开着、另一边说没有，或者更糟——引用已失效但版本还停在旧的。
/// · 页面生命周期的**唯一写入者**是浏览器工作区控制器。把翻译放在这里，语义就能脱离 UI 环境测试，
///   窗口代码只剩「在事件点各调一行」。
///
/// 本类刻意不做异步、不持有 UI：调用方（UI 线程）在事件点顺序调用即可。注册表自身线程安全。
/// </summary>
public sealed class BrowserWorkspaceTargetBridge
{
    private readonly BrowserTargetRegistry _browserTargets;
    private readonly DesktopTargetRegistry _pageTargets;
    private readonly Lock _sync = new();
    private readonly HashSet<string> _contexts = new(StringComparer.Ordinal);

    public BrowserWorkspaceTargetBridge(
        BrowserTargetRegistry browserTargets,
        DesktopTargetRegistry pageTargets)
    {
        _browserTargets = browserTargets ?? throw new ArgumentNullException(nameof(browserTargets));
        _pageTargets = pageTargets ?? throw new ArgumentNullException(nameof(pageTargets));
    }

    /// <summary>
    /// 登记上下文。Agent 浏览器工作区的页面是「已授权可被驱动」的，
    /// 因此缺省 <see cref="DesktopContextTrust.AgentAuthorized"/>；
    /// 其他（例如工作台/第三方页面）必须由调用方显式给出自己的级别，不要沿用缺省值。
    /// </summary>
    public void OnContextCreated(string contextId, DesktopContextTrust trust = DesktopContextTrust.AgentAuthorized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            // **两个**注册表都要登记：页面注册表用它校验"先有上下文后有页面"，
            // 浏览器目标注册表用它决定可信级别。只登记一边会让另一边把页面当成非法目标
            // （这一处正是本类测试第一轮抓出来的缺陷）。
            _pageTargets.RegisterContext(contextId, trust);
            _browserTargets.RegisterContext(contextId, trust);
            _contexts.Add(contextId);
        }
    }

    /// <summary>注销上下文：它的页面、Agent 目标与活动页一并清除（不留悬空引用）。</summary>
    public void OnContextClosed(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            _browserTargets.UnregisterContext(contextId);
            _pageTargets.CloseContext(contextId);
            _contexts.Remove(contextId);
        }
    }

    /// <summary>
    /// 登记新页面并带上它**当下**的版本。上下文未登记会直接抛
    /// （<see cref="DesktopTargetRegistry.RegisterPage"/> 的既有不变式）——这是有意的：
    /// 「先有页面后有上下文」说明写入顺序错了，静默补登记会让不可信上下文被当成可信。
    /// </summary>
    public void OnPageCreated(
        string contextId,
        string pageId,
        DesktopPageVersion version,
        bool isActive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            _pageTargets.RegisterPage(new DesktopPageTarget(contextId, pageId), version);
            _browserTargets.RegisterPage(contextId, pageId, isAgentTarget: false, isActive: isActive);
        }
    }

    /// <summary>切换活动页（同时把它的当前版本登记进页面注册表）。</summary>
    public void OnPageActivated(string contextId, string pageId, DesktopPageVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            _browserTargets.SetActivePage(contextId, pageId);
            AdvanceLocked(contextId, pageId, version, readiness: null);
        }
    }

    /// <summary>
    /// 版本推进（导航开始/完成、交互提交后）。版本只前进：迟到的旧版本事件不会把注册表拉回去，
    /// 否则已经作废的 Ref 会重新"有效"。
    /// </summary>
    public void OnPageVersionAdvanced(
        string contextId,
        string pageId,
        DesktopPageVersion version,
        DesktopPageReadiness readiness = DesktopPageReadiness.Interactive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            AdvanceLocked(contextId, pageId, version, readiness);
        }
    }

    /// <summary>关闭页面：两个注册表同时移除（活跃页若正是它则一并清除）。</summary>
    public void OnPageClosed(string contextId, string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        lock (_sync)
        {
            _browserTargets.UnregisterPage(contextId, pageId);
            _pageTargets.ClosePage(new DesktopPageTarget(contextId, pageId));
        }
    }

    /// <summary>
    /// 设置/撤销「已授权的 Agent 目标页」。<c>null</c> 表示当前没有 Agent 目标
    /// （用户接管、会话结束、页面被关闭都应当走这里）。
    ///
    /// 撤销必须真的发生：注册表若继续对旧页面回答 <c>IsAgentTarget=true</c>，
    /// 就等于替一个不该再被驱动的页面背书，而只读状态无法自证这件事。
    /// </summary>
    public void OnAgentTargetChanged(string? contextId, string? pageId)
    {
        lock (_sync)
        {
            _browserTargets.SetAgentTarget(contextId, pageId);
        }
    }

    /// <summary>清空两个注册表（窗口退出、浏览器工作区被销毁）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _browserTargets.Clear();

            // 页面注册表没有整表清空：逐个注销本桥登记过的上下文（各上下文会带走自己的页面）。
            foreach (var contextId in _contexts.ToArray())
            {
                _pageTargets.CloseContext(contextId);
            }

            _contexts.Clear();
        }
    }

    private void AdvanceLocked(
        string contextId,
        string pageId,
        DesktopPageVersion version,
        DesktopPageReadiness? readiness)
    {
        // 未登记（或已关闭）的页面：不补登记。补登记等于凭一个事件凭空造出页面目标，
        // 而"页面是否存在"只能由工作区说了算（工作区没登记过，就是它的状态有问题）。
        if (_pageTargets.Resolve(new DesktopPageTarget(contextId, pageId)) is not { } current)
        {
            return;
        }

        _pageTargets.UpdatePage(
            current.Target,
            version,
            readiness ?? current.Readiness);
    }
}
