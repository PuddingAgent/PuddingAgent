using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker;

/// <summary>
/// Core 侧**可信调用方身份**（计划 §7）：由 Core 的可信运行上下文产生，
/// <b>绝不</b>取自模型输出、工具参数或网页字段——那些都可以被伪造。
/// </summary>
public sealed record DesktopCallerIdentity
{
    public required string SessionId { get; init; }

    public string? AgentInstanceId { get; init; }

    public string? ExecutionId { get; init; }

    public string? UserId { get; init; }

    public string? WorkspaceId { get; init; }

    public string? SubAgentId { get; init; }
}

/// <summary>
/// 身份提供方。实现方从 Core 的运行上下文取（例如 Runtime 执行 Agent/工具时压栈的
/// `RuntimeTraceContext`）；组件只依赖这个端口，因此判定逻辑可以脱宿主测试。
/// </summary>
public interface IDesktopCallerIdentityProvider
{
    /// <summary>当前异步执行上下文里的身份；不在任何可信上下文里时返回 <c>null</c>。</summary>
    DesktopCallerIdentity? TryGetCurrent();
}

/// <summary>
/// 基于**可信调用方身份**的授权器：RPC 可达 ≠ 获得桌面操作授权（计划 §7）。
///
/// 判定规则（全部 fail closed，无法判定即拒绝，且理由可诊断）：
/// ① 当前上下文没有身份 / 没有会话 ⇒ 拒绝（后台任务、无执行上下文的调用一律不放行）；
/// ② 声明为 <see cref="DesktopCapabilityTraits.RequiresTrustedContext"/> 的能力 ⇒ 必须有 Agent 身份；
/// ③ 变更类（<c>Mutating</c>）与有副作用（<c>HasSideEffects</c>）能力 ⇒ 必须来自**某次可识别的 Agent 执行**
///    （`ExecutionId` + `AgentInstanceId` 齐备）——否则事后无从审计「是谁动的手」；
/// ④ 需要用户在场的能力（对话框 / 文件选择器）⇒ 必须有用户身份（弹窗要能归因到人）。
///
/// <b>诚实边界</b>：这是**身份门禁**，不是完整的 Tool Runtime 权限/审批链。
/// 把「该 Agent/会话是否有权使用浏览器工具」也接进来属于切片 D 的调用点迁移；
/// 在接好之前，本授权器只放行**来源可识别**的执行，其余一律拒绝——
/// 这比用 `AllowAll` 顶上安全，也比 `DenyAll` 更接近可用（后者让启用态永远什么都做不了）。
/// </summary>
public sealed class TrustedCallerDesktopCapabilityAuthorizer : IDesktopCapabilityAuthorizer
{
    private readonly IDesktopCallerIdentityProvider _identityProvider;

    public TrustedCallerDesktopCapabilityAuthorizer(IDesktopCallerIdentityProvider identityProvider)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
    }

    public ValueTask<DesktopCapabilityError?> AuthorizeAsync(
        DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = _identityProvider.TryGetCurrent();
        if (identity is null || string.IsNullOrWhiteSpace(identity.SessionId))
        {
            return Deny("capability call has no trusted caller identity in the current execution context");
        }

        var traits = context.Capability.Traits;

        if ((traits & DesktopCapabilityTraits.RequiresTrustedContext) != 0
            && string.IsNullOrWhiteSpace(identity.AgentInstanceId))
        {
            // 可信上下文能力（脚本注入等）：没有 Agent 身份就没有"谁被授权"这回事。
            return Deny("capability requires a trusted agent context, but the caller has no agent identity");
        }

        if ((traits & (DesktopCapabilityTraits.Mutating | DesktopCapabilityTraits.HasSideEffects)) != 0
            && (string.IsNullOrWhiteSpace(identity.ExecutionId) || string.IsNullOrWhiteSpace(identity.AgentInstanceId)))
        {
            return Deny("mutating capability requires an identifiable agent execution");
        }

        if ((traits & DesktopCapabilityTraits.RequiresUserInteraction) != 0
            && string.IsNullOrWhiteSpace(identity.UserId))
        {
            // 需要用户在场：弹窗必须能归因到人，否则宁可拒绝。
            return Deny("capability requires user presence, but the caller has no user identity");
        }

        return ValueTask.FromResult<DesktopCapabilityError?>(null);
    }

    /// <summary>拒绝理由只带规则要点，**不含**页面内容、URL、剪贴板或凭据。</summary>
    private static ValueTask<DesktopCapabilityError?> Deny(string detail) =>
        ValueTask.FromResult<DesktopCapabilityError?>(
            DesktopCapabilityError.Unauthorized($"desktop capability not authorized: {detail}"));
}
