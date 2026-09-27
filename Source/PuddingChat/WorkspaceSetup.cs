namespace PuddingChat;

public sealed record ModelChoice(string ProviderId, string ModelId, string Label);
public sealed record WorkspaceSetupResult(string WorkspaceId, string AgentId);
public sealed record WorkspaceSetupRequest(string WorkspaceId, string WorkspaceName, string RoleName, ModelChoice? Model)
{
    public WorkspaceSetupRequest Normalize()
    {
        var id = WorkspaceId.Trim();
        if (id.Length is < 1 or > 48 || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("工作空间标识须为 1–48 位英文字母、数字或连字符，以字母或数字开头。");
        var name = WorkspaceName.Trim(); var role = RoleName.Trim();
        if (name.Length is < 1 or > 128 || role.Length is < 1 or > 80)
            throw new ArgumentException("请填写工作空间名称（至多 128 字）与角色名称（至多 80 字）。");
        return this with { WorkspaceId = id.ToLowerInvariant(), WorkspaceName = name, RoleName = role };
    }
}

/// <summary>Local first-use port. No web account or credential is created.</summary>
public interface IWorkspaceSetupClient
{
    Task<ModelChoice[]> GetSetupModelsAsync(CancellationToken ct);
    Task<WorkspaceSetupResult> SetupWorkspaceAsync(WorkspaceSetupRequest request, CancellationToken ct);
}
