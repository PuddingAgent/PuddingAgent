using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// Base class for all browser Agent tools. Pushes the current BrowserOperationOrigin
/// into the AsyncLocal accessor for the duration of each tool execution, so that
/// the RemoteBrowserRuntime can copy it into every Bridge command it sends.
/// </summary>
public abstract class BrowserAgentToolBase<TArgs>(
    IBrowserOperationOriginAccessor originAccessor)
    : PuddingToolBase<TArgs>
    where TArgs : class
{
    /// <summary>
    /// 本次工具调用携带的权限证据（权限证据链第一阶段）。
    /// <para>
    /// 与 Origin 一样是**每次调用**的状态（工具实例是 DI 单例，不能存字段），因此走
    /// <see cref="BeginExecutionScope"/> 的 AsyncLocal 作用域；工具在构造能力调用上下文时把它传下去。
    /// 本阶段只携带、不据此拒绝任何调用。
    /// </para>
    /// </summary>
    private static readonly AsyncLocal<ToolPermissionEvidence?> CurrentEvidence = new();

    protected IBrowserOperationOriginAccessor OriginAccessor => originAccessor;

    /// <summary>
    /// 当前调用的权限证据摘要（形如 <c>decision=allowed;source=workspace-guard</c>）；
    /// 未评估时返回 <c>null</c>（**不是**“已拒绝”）。
    /// </summary>
    protected static string? CurrentPermissionEvidenceSummary()
        => CurrentEvidence.Value is { } evidence
            ? $"decision={evidence.Decision};source={evidence.Source ?? "unknown"}"
            : null;

    /// <summary>
    /// Pushes the tool call origin derived from the execution context.
    /// The origin is available to RemoteBrowserRuntime via IBrowserOperationOriginAccessor.Current.
    /// </summary>
    protected override IDisposable? BeginExecutionScope(ToolExecutionRequest request)
    {
        var originScope = PushOrigin(request);
        var previousEvidence = CurrentEvidence.Value;
        CurrentEvidence.Value = request.Context.PermissionEvidence;

        // 调用级身份（设计 §3.1）：本次调用只投影**一次**，缺身份时保留具体原因供拒绝结果使用。
        // 身份作用域与审计 Origin 分开存放：Origin 用于诊断，本作用域用于**授权**。
        var identityScope = PushCallerIdentity(request);
        return new CompositeScope(originScope, identityScope, new EvidenceScope(previousEvidence));
    }

    /// <summary>
    /// **浏览器工具的唯一执行入口**：没有可信身份就**不执行**（fail-closed），而不是给个默认身份。
    /// 派生工具实现 <see cref="ExecuteBrowserCoreAsync"/>。
    /// </summary>
    protected sealed override Task<ToolExecutionResult> ExecuteCoreAsync(
        TArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (BrowserCallerIdentityScope.Current is null)
        {
            // 具体原因来自投影（缺运行身份/缺调用者/子代理缺父任务），不伪装成"未授权"以外的含糊失败。
            return Task.FromResult(ToolExecutionResult.Fail(
                "browser tool refused: " + (BrowserCallerIdentityScope.CurrentFailureReason
                    ?? "no trusted caller identity is available for this call")));
        }

        return ExecuteBrowserCoreAsync(args, context, ct);
    }

    /// <summary>派生工具的业务主体（仅在调用者身份可信时被调用）。</summary>
    protected abstract Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
        TArgs args,
        ToolExecutionContext context,
        CancellationToken ct);

    /// <summary>把执行上下文的身份投影进调用级作用域，并返回还原句柄。</summary>
    private static IDisposable PushCallerIdentity(ToolExecutionRequest request)
    {
        var context = request.Context;
        // agent 实例标识与审计 Origin 取同一条规则：保证"审计里的行动者"＝"授权里的行动者"。
        var agentInstanceId = context.ConfigurationAgentInstanceId ?? context.AgentInstanceId;
        var projected = BrowserCallerIdentitySource.TryProject(
            context.ExecutionIdentity,
            agentInstanceId,
            out var identity,
            out var reason);
        return BrowserCallerIdentityScope.Push(projected ? identity : null, projected ? null : reason);
    }

    /// <summary>把三个作用域合成一个，保证退出时都被还原（原作用域可能为 <c>null</c>）。</summary>
    private sealed class CompositeScope(IDisposable? first, IDisposable second, IDisposable third) : IDisposable
    {
        public void Dispose()
        {
            third.Dispose();
            second.Dispose();
            first?.Dispose();
        }
    }

    private sealed class EvidenceScope(ToolPermissionEvidence? previous) : IDisposable
    {
        public void Dispose() => CurrentEvidence.Value = previous;
    }

    private IDisposable PushOrigin(ToolExecutionRequest request)
    {
        var context = request.Context;
        var identity = context.ExecutionIdentity;
        var origin = new BrowserOperationOrigin
        {
            WorkspaceId = context.WorkspaceId,
            AgentInstanceId = context.ConfigurationAgentInstanceId ?? context.AgentInstanceId,
            SessionId = context.SessionId,
            ConversationId = identity?.ConversationId,
            RunId = identity?.RunId,
            ToolCallId = identity?.ToolCallId,
            ToolName = Descriptor.ToolId
        };
        return OriginAccessor.Push(origin);
    }
}
