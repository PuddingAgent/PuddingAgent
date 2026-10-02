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
        return new CompositeScope(originScope, new EvidenceScope(previousEvidence));
    }

    /// <summary>把两个作用域合成一个，保证退出时两者都被还原（原作用域可能为 <c>null</c>）。</summary>
    private sealed class CompositeScope(IDisposable? first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
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
