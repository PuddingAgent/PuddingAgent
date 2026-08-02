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
    protected IBrowserOperationOriginAccessor OriginAccessor => originAccessor;

    /// <summary>
    /// Pushes the tool call origin derived from the execution context.
    /// The origin is available to RemoteBrowserRuntime via IBrowserOperationOriginAccessor.Current.
    /// </summary>
    public override async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        using var _ = PushOrigin(request);
        return await base.ExecuteAsync(request, ct);
    }

    private IDisposable PushOrigin(ToolExecutionRequest request)
    {
        var context = request.Context;
        var identity = context.ExecutionIdentity;
        var origin = new BrowserOperationOrigin
        {
            WorkspaceId = identity.WorkspaceId ?? "",
            AgentInstanceId = identity.ConfigurationAgentInstanceId ?? identity.AgentInstanceId ?? "",
            SessionId = identity.SessionId ?? "",
            ConversationId = identity.ConversationId,
            RunId = identity.RunId,
            ToolCallId = identity.ToolCallId,
            ToolName = Descriptor.ToolId
        };
        return OriginAccessor.Push(origin);
    }
}
