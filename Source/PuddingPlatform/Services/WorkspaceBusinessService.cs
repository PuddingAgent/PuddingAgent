using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// Workspace 业务层服务。
/// 在 PlatformApiClient 的基础上，封装 Workspace 级业务规则：
/// 服务暴露策略判定、审计治理合规校验、工作流绑定能力查询。
/// </summary>
public sealed class WorkspaceBusinessService
{
    private readonly PlatformApiClient _api;

    public WorkspaceBusinessService(PlatformApiClient api) => _api = api;

    // ── 基础查询 ─────────────────────────────────────────────

    /// <summary>返回所有启用且未冻结的 Workspace。</summary>
    public async Task<List<WorkspaceDefinition>> GetActiveWorkspacesAsync(CancellationToken ct = default)
    {
        var all = await _api.GetWorkspacesAsync(ct);
        return all.Where(w => w.IsEnabled && !w.IsFrozen).ToList();
    }

    // ── 服务暴露策略 ─────────────────────────────────────────

    /// <summary>
    /// 判断某个渠道（channelId）在指定 Workspace 内是否暴露了指定 Agent 模板。
    /// </summary>
    public async Task<bool> IsAgentExposedOnChannelAsync(
        string workspaceId, string channelId, string agentTemplateId,
        CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null || !ws.IsEnabled || ws.IsFrozen) return false;

        var binding = ws.ChannelBindings.FirstOrDefault(cb => cb.ChannelId == channelId);
        if (binding is null) return false;

        // 若 AllowedAgentTemplateIds 为空，意味不限制（允许所有已绑定 template）
        if (binding.AllowedAgentTemplateIds.Count == 0)
            return ws.AgentTemplateIds.Contains(agentTemplateId);

        return binding.AllowedAgentTemplateIds.Contains(agentTemplateId);
    }

    /// <summary>
    /// 返回某个渠道在指定 Workspace 可调用的全部 Agent 模板 ID。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetExposedAgentTemplateIdsAsync(
        string workspaceId, string channelId,
        CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null || !ws.IsEnabled || ws.IsFrozen) return [];

        var binding = ws.ChannelBindings.FirstOrDefault(cb => cb.ChannelId == channelId);
        if (binding is null) return [];

        return binding.AllowedAgentTemplateIds.Count > 0
            ? binding.AllowedAgentTemplateIds
            : ws.AgentTemplateIds;
    }

    // ── 审计治理合规 ─────────────────────────────────────────

    /// <summary>
    /// 返回 Workspace 的审计合规状态：
    /// 合规 = 启用 + 未冻结 + 至少配置了 1 个审计 Agent 模板。
    /// </summary>
    public async Task<WorkspaceGovernanceStatus> GetGovernanceStatusAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null)
            return new WorkspaceGovernanceStatus(workspaceId, false, false, false, "Workspace 不存在");

        bool hasAuditAgent = ws.AuditAgentTemplateIds.Count > 0;

        if (ws.IsFrozen)
            return new WorkspaceGovernanceStatus(workspaceId, ws.IsEnabled, ws.IsFrozen, false, "Workspace 已冻结");

        if (!ws.IsEnabled)
            return new WorkspaceGovernanceStatus(workspaceId, ws.IsEnabled, ws.IsFrozen, false, "Workspace 已停用");

        if (!hasAuditAgent)
            return new WorkspaceGovernanceStatus(workspaceId, ws.IsEnabled, ws.IsFrozen, false, "缺少审计 Agent 模板（至少需要 1 个）");

        return new WorkspaceGovernanceStatus(workspaceId, ws.IsEnabled, ws.IsFrozen, true, null);
    }

    // ── 工作流绑定 ────────────────────────────────────────

    /// <summary>返回 Workspace 中所有工作流绑定。</summary>
    public async Task<IReadOnlyList<WorkflowBindingDefinition>> GetWorkflowBindingsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        return ws?.WorkflowBindings ?? [];
    }

    /// <summary>
    /// 返回指定渠道可触发的工作流绑定列表
    /// （TriggerChannelIds 为空时认为所有渠道均可触发）。
    /// </summary>
    public async Task<IReadOnlyList<WorkflowBindingDefinition>> GetWorkflowsForChannelAsync(
        string workspaceId, string channelId, CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null || !ws.IsEnabled || ws.IsFrozen) return [];

        return ws.WorkflowBindings
            .Where(w => w.TriggerChannelIds.Count == 0
                     || w.TriggerChannelIds.Contains(channelId))
            .ToList();
    }

    // ── 知识基础设施 ──────────────────────────────────────

    /// <summary>
    /// 返回 Workspace 的知识库能力概况：
    /// 是否启用、已索引文档数。
    /// </summary>
    public async Task<WorkspaceKnowledgeCapability> GetKnowledgeCapabilityAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var ws = await _api.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null)
            return new WorkspaceKnowledgeCapability(workspaceId, false, false, false, 0);

        bool kbEnabled = ws.KnowledgeBase?.Enabled == true;
        bool graphEnabled = ws.KnowledgeGraph?.Enabled == true;
        bool storageEnabled = ws.StorageBinding is not null;

        int docCount = 0;
        if (kbEnabled)
        {
            var docs = await _api.GetKnowledgeDocumentsAsync(workspaceId, ct);
            docCount = docs.Count;
        }

        return new WorkspaceKnowledgeCapability(
            workspaceId, kbEnabled, graphEnabled, storageEnabled, docCount);
    }
}

/// <summary>Workspace 治理状态。</summary>
public sealed record WorkspaceGovernanceStatus(
    string WorkspaceId,
    bool IsEnabled,
    bool IsFrozen,
    bool IsCompliant,
    string? NonComplianceReason
);

/// <summary>Workspace 知识基础设施能力概况。</summary>
public sealed record WorkspaceKnowledgeCapability(
    string WorkspaceId,
    bool KnowledgeBaseEnabled,
    bool KnowledgeGraphEnabled,
    bool StorageEnabled,
    int IndexedDocumentCount
);

