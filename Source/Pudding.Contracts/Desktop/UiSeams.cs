namespace Pudding.Contracts.Desktop;

/// <summary>
/// 页面目标的可信级别，由 Desktop 侧在页面创建/授权时登记，**不接受调用方填写**
/// （计划 §4/§7：普通网页与第三方客户端不得调用 Shell，任意脚本只限获授权的 Agent 浏览器目标）。
/// </summary>
public enum DesktopContextTrust
{
    /// <summary>普通第三方网页：只读状态与导航，禁止脚本注入与 Shell 交互能力。</summary>
    Untrusted,

    /// <summary>已授权的 Agent 浏览器页面：可以执行脚本与导航。</summary>
    AgentAuthorized,

    /// <summary>可信工作台/登录态/产物预览：可以使用 Shell 交互能力，但**禁止**被注入脚本。</summary>
    Workbench,
}

/// <summary>
/// 把动作调度到 UI 线程的抽象（实现方持有 WinUI DispatcherQueue 或 WPF Dispatcher）。
///
/// 契约要求：
/// · <see cref="HasThreadAccess"/> 为真时同步执行，不额外排队；
/// · 排队被拒绝、窗口退出或队列不可用时必须让任务<b>完成</b>（抛/取消都行），绝不悬挂；
/// · 回调内的异常与取消必须传播到返回的 Task（调用方据此映射领域错误）。
/// </summary>
public interface IDesktopUiDispatcher
{
    bool HasThreadAccess { get; }

    Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);

    Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

/// <summary>
/// 真正执行桌面 UI 动作的表面：由 WinUI 实现（WebView2、窗口、通知）。
///
/// 实现方负责：控件访问只在 UI 线程、页面版本随状态更新、错误用
/// <see cref="CapabilityResult{T}"/> 表达而不是异常。准入/目标校验/取消与竞态后的复检
/// 由 DesktopService 负责，实现方不必重复实现。
/// </summary>
public interface IDesktopUiSurface
{
    Task<CapabilityResult<NavigateResult>> NavigateAsync(
        DesktopCallContext context, NavigateRequest request, CancellationToken cancellationToken);

    Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        DesktopCallContext context, JavascriptRequest request, CancellationToken cancellationToken);

    Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context, DesktopPageTarget target, CancellationToken cancellationToken);

    /// <summary>对元素执行交互；实现方必须在交互后返回<b>新的</b>页面状态（旧 Ref 随之作废）。</summary>
    Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        DesktopCallContext context, BrowserInteractRequest request, CancellationToken cancellationToken);

    /// <summary>按定位描述符查找元素；实现方必须为每个命中项标注当前 PageVersion。</summary>
    Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        DesktopCallContext context, BrowserLocateRequest request, CancellationToken cancellationToken);

    /// <summary>读取页面快照；预算由请求给出，实现方必须截断并如实标注 <c>Truncated</c>。</summary>
    Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        DesktopCallContext context, BrowserSnapshotRequest request, CancellationToken cancellationToken);

    Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 只读 Shell 状态：实现方只报告「只有它知道」的部分（窗口形态/托盘可见性）；
    /// 自动化状态与打开页面数由 DesktopService 依自身权威状态补齐。
    /// </summary>
    Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context, CancellationToken cancellationToken);
}
