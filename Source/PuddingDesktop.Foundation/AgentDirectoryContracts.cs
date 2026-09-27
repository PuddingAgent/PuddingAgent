namespace PuddingDesktop.Foundation;

public sealed record AgentWorkspaceOption(string WorkspaceId, string Name);

/// <summary>Global template row. Prompt/Markdown documents belong to the DS-04 document tab, not here.</summary>
public sealed record AgentTemplateSummary(
    string TemplateId, string Name, string Role, string Description,
    bool IsEnabled, bool IsBuiltIn, int SortOrder, string AvatarId,
    int SelectedCapabilityCount, int SelectedSkillPackageCount);

/// <summary>Preset shipped with the product but not yet imported into the template directory.</summary>
public sealed record AgentTemplatePreset(string TemplateId, string Name, string Role, string Description);

/// <summary>
/// Basic template edit. The adapter reads the stored template first and submits it back with these
/// fields replaced, so prompt/Markdown documents and undisplayed policy fields survive.
/// </summary>
public sealed record AgentTemplateEdit(
    string TemplateId, string Name, string Role, string Description,
    bool IsEnabled, int SortOrder, string AvatarId);

/// <summary>Workspace role instance. Freeze and enable are separate states and are shown separately.</summary>
public sealed record AgentInstanceSummary(
    string AgentId, string Name, string DisplayName, string Role, string Description,
    string SourceTemplateId, string AvatarId, bool IsEnabled, bool IsFrozen, bool HasMainSession, DateTimeOffset UpdatedAt);

public sealed record AgentInstanceCreate(
    string WorkspaceId, string Name, string Description, string SourceTemplateId);

/// <summary>
/// Basic instance edit. The template identity is immutable after creation, matching the Core contract;
/// the adapter reloads the instance and submits the full profile so nothing else is cleared.
/// </summary>
public sealed record AgentInstanceEdit(
    string WorkspaceId, string AgentId, string Name, string Description, string Role, bool IsEnabled, string AvatarId);

/// <summary>
/// Task-shaped operations for the agent directory tab, implemented in Composition against
/// AgentTemplateFileService / WorkspaceAgentFileService. No HTTP, JWT or DTO relay.
/// </summary>
public interface IAgentDirectorySettings
{
    Task<IReadOnlyList<AgentWorkspaceOption>> ListWorkspacesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentAvatarOption>> ListAvatarsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTemplatePreset>> ListPresetsAsync(CancellationToken cancellationToken = default);
    Task ImportPresetAsync(string templateId, CancellationToken cancellationToken = default);
    Task SaveTemplateAsync(AgentTemplateEdit edit, CancellationToken cancellationToken = default);
    Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentInstanceSummary>> ListInstancesAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task CreateInstanceAsync(AgentInstanceCreate create, CancellationToken cancellationToken = default);
    Task SaveInstanceAsync(AgentInstanceEdit edit, CancellationToken cancellationToken = default);
    Task DeleteInstanceAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task SetInstanceFrozenAsync(string workspaceId, string agentId, bool frozen, CancellationToken cancellationToken = default);

    /// <summary>Template documents plus the fingerprint the editor must send back on save.</summary>
    Task<AgentTemplateDocuments> ReadTemplateDocumentsAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SettingsConflictException"/> when the stored template changed since it was read.</summary>
    Task SaveTemplateDocumentsAsync(AgentTemplateDocuments documents, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInstanceDocument>> ReadInstanceDocumentsAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SettingsConflictException"/> when the stored document changed since it was read.</summary>
    Task SaveInstanceDocumentAsync(string workspaceId, string agentId, string key, string content, string expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>Provider/model catalogue used by the model & memory pickers; embedding entries included.</summary>
    Task<IReadOnlyList<AgentModelCatalogEntry>> ListModelCatalogAsync(CancellationToken cancellationToken = default);

    Task<AgentModelPolicy> ReadTemplateModelPolicyAsync(string templateId, CancellationToken cancellationToken = default);
    Task SaveTemplateModelPolicyAsync(string templateId, AgentModelPolicy policy, CancellationToken cancellationToken = default);
    Task<AgentModelPolicy> ReadInstanceModelPolicyAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task SaveInstanceModelPolicyAsync(string workspaceId, string agentId, AgentModelPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>Smart sub-agent routes keyed by role id. Only role instances carry them.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadSmartRoutesAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task SaveSmartRoutesAsync(string workspaceId, string agentId, IReadOnlyDictionary<string, string> routes, CancellationToken cancellationToken = default);

    /// <summary>Execution budgets and container image override, for the template and for an instance.</summary>
    Task<AgentGuardrailPolicy> ReadTemplateGuardrailsAsync(string templateId, CancellationToken cancellationToken = default);
    Task SaveTemplateGuardrailsAsync(string templateId, AgentGuardrailPolicy policy, CancellationToken cancellationToken = default);
    Task<AgentGuardrailPolicy> ReadInstanceGuardrailsAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task SaveInstanceGuardrailsAsync(string workspaceId, string agentId, AgentGuardrailPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>
/// An avatar the template store can actually persist. Templates reference an avatar by id from the
/// product avatar catalog; there is no emoji field in the template contract, so the UI must not offer one.
/// </summary>
public sealed record AgentAvatarOption(string AvatarId, string Name, string RecommendedUse, bool IsEnabled);

/// <summary>Pure form helpers for the directory tab. No Core call, no persistence.</summary>
public static class AgentDirectoryText
{

    public static IReadOnlyList<string> Validate(AgentTemplateEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.TemplateId)) errors.Add("模板 ID 不能为空。");
        else if (edit.TemplateId.Length > 80 || edit.TemplateId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            errors.Add("模板 ID 只能包含字母、数字、'-' 和 '_'，且不超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("模板名称不能为空。");
        if (string.IsNullOrWhiteSpace(edit.Role)) errors.Add("角色标识不能为空。");
        if (edit.SortOrder < 0) errors.Add("排序不能为负数。");

        return errors;
    }

    public static IReadOnlyList<string> Validate(AgentInstanceCreate create)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(create.WorkspaceId)) errors.Add("请先选择工作区。");
        if (string.IsNullOrWhiteSpace(create.Name)) errors.Add("角色名称不能为空。");
        if (string.IsNullOrWhiteSpace(create.SourceTemplateId)) errors.Add("请选择来源模板。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(AgentInstanceEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.WorkspaceId)) errors.Add("请先选择工作区。");
        if (string.IsNullOrWhiteSpace(edit.AgentId)) errors.Add("请先选择角色实例。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("角色名称不能为空。");
        return errors;
    }

    /// <summary>Freeze and disable are different states; the UI must never collapse them into one label.</summary>
    public static string DescribeState(bool isEnabled, bool isFrozen) => (isEnabled, isFrozen) switch
    {
        (true, false) => "已启用",
        (true, true) => "已冻结（不可执行）",
        (false, true) => "已停用且已冻结",
        _ => "已停用"
    };
}
