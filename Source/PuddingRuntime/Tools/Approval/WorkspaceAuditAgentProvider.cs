using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Runtime-side adapter that delegates to Platform's WorkspaceAgentFileService
/// to resolve workspace audit agents for automatic tool approval.
/// </summary>
/// <remarks>
/// This adapter exists to decouple Runtime tools from the concrete Platform service.
/// The IWorkspaceAuditAgentProvider abstraction lives in PuddingCore.
/// The implementation is registered in PuddingAgent startup (Program.cs) via DI.
/// </remarks>
public sealed class WorkspaceAuditAgentProviderAdapter : IWorkspaceAuditAgentProvider
{
    private readonly Func<string, CancellationToken, Task<WorkspaceAuditAgentProfile?>> _finder;

    public WorkspaceAuditAgentProviderAdapter(
        Func<string, CancellationToken, Task<WorkspaceAuditAgentProfile?>> finder)
    {
        _finder = finder;
    }

    public Task<WorkspaceAuditAgentProfile?> FindFirstEnabledAuditAgentAsync(
        string workspaceId,
        CancellationToken ct = default)
        => _finder(workspaceId, ct);
}
