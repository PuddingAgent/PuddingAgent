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
    protected override IDisposable? BeginExecutionScope(ToolExecutionRequest request)
        => PushOrigin(request);

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
