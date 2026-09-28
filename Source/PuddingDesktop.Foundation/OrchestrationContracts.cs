namespace PuddingDesktop.Foundation;

/// <summary>A graph head as Core's discovery query reports it.</summary>
public sealed record OrchestrationGraphSummary(
    string GraphId, string WorkspaceId, string RootSessionId, string CreatedByAgentId, string Objective,
    int CurrentRevision, string CurrentRevisionId, int RunCount, int ActiveRunCount,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public string LineText =>
        $"{GraphId} · 修订 {CurrentRevision} · 运行 {RunCount}（进行中 {ActiveRunCount}）· {Objective}";
}

public sealed record OrchestrationRevisionSummary(
    string GraphId, string RevisionId, int Revision, string ParentRevisionId, string SchemaVersion,
    string ContentHash, string CreatedByAgentId, DateTimeOffset CreatedAtUtc)
{
    public string LineText =>
        $"修订 {Revision} · {RevisionId} · {ContentHash[..Math.Min(12, ContentHash.Length)]} · " +
        $"{CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · by {CreatedByAgentId}";
}

public sealed record OrchestrationNodeSummary(string NodeId, string ComponentType, string Version, string Title)
{
    public string LineText => $"{NodeId} · {(Title.Length == 0 ? ComponentType : Title)}（{ComponentType}@{Version}）";
}

public sealed record OrchestrationEdgeSummary(string FromNodeId, string ToNodeId, string Port)
{
    public string LineText => Port.Length == 0
        ? $"{FromNodeId} → {ToNodeId}"
        : $"{FromNodeId} → {ToNodeId}（端口 {Port}）";
}

public sealed record OrchestrationGraphInput(string InputId, string ValueType, bool Required)
{
    public string LineText => $"{InputId} · {ValueType}" + (Required ? " · 必填" : "");
}

/// <summary>
/// A configured trigger. Configuration values are deliberately not surfaced - only their keys - because a
/// trigger's configuration may reference credentials, and Core treats hook credentials as server-side facts.
/// </summary>
public sealed record OrchestrationTriggerSummary(
    string TriggerId, string TriggerType, string Version, bool Enabled,
    IReadOnlyList<string> ConfigurationKeys, IReadOnlyList<string> InputBindings)
{
    public string StateText => Enabled ? "已启用" : "已停用";
    public string LineText =>
        $"{TriggerId} · {TriggerType}@{Version} · {StateText} · 输入映射 {InputBindings.Count} 项 · " +
        $"配置键 {(ConfigurationKeys.Count == 0 ? "无" : string.Join("、", ConfigurationKeys))}";
    public string InvocationPath(string graphId) => $"/api/orchestrations/hooks/{graphId}/{TriggerId}";
}

public sealed record OrchestrationGraphDetail(
    string GraphId, string RevisionId, int Revision, string WorkspaceId, string Objective, string SchemaVersion,
    string ContentHash, int MaxConcurrency, bool RequiresExplicitActivation,
    IReadOnlyList<OrchestrationNodeSummary> Nodes, IReadOnlyList<OrchestrationEdgeSummary> Edges,
    IReadOnlyList<OrchestrationTriggerSummary> Triggers, IReadOnlyList<OrchestrationGraphInput> Inputs)
{
    public string HeadlineText =>
        $"{GraphId} · 修订 {Revision} · 节点 {Nodes.Count} · 边 {Edges.Count} · 触发器 {Triggers.Count} · 输入 {Inputs.Count}";
    public string MetaText =>
        $"工作区 {WorkspaceId} · schema {SchemaVersion} · " +
        // 定义本身没有内容哈希；哈希属于修订摘要（修订列表里显示）。
        (ContentHash.Length == 0 ? "哈希见修订列表" : $"哈希 {ContentHash[..Math.Min(12, ContentHash.Length)]}") + " · " +
        $"最大并发 {MaxConcurrency} · " +
        (RequiresExplicitActivation ? "需要显式激活" : "创建后自动激活");
    public int EnabledTriggerCount => Triggers.Count(trigger => trigger.Enabled);
}

public sealed record OrchestrationValidationResult(bool IsValid, IReadOnlyList<string> Issues, IReadOnlyList<string> TopologicalNodeIds)
{
    public string DescribeText => IsValid
        ? $"校验通过 · 拓扑顺序 {TopologicalNodeIds.Count} 个节点"
        : $"校验失败 · {Issues.Count} 个问题";
    public string IssuesText => Issues.Count == 0 ? "没有问题" : string.Join("\n", Issues);
}

public sealed record OrchestrationRevisionDraft(string GraphId, int ExpectedCurrentRevision, string DefinitionJson);

public interface IOrchestrationSettings
{
    Task<IReadOnlyList<OrchestrationGraphSummary>> ListGraphsAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task<OrchestrationGraphDetail?> GetLatestAsync(string graphId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrchestrationRevisionSummary>> ListRevisionsAsync(string graphId, CancellationToken cancellationToken = default);
    /// <summary>Compiles a draft without persisting anything.</summary>
    Task<OrchestrationValidationResult> ValidateAsync(OrchestrationRevisionDraft draft, CancellationToken cancellationToken = default);
    /// <summary>Appends the next immutable revision (CAS on ExpectedCurrentRevision).</summary>
    Task PublishAsync(OrchestrationRevisionDraft draft, CancellationToken cancellationToken = default);
    /// <summary>Starts a manual run of the given revision; run detail belongs to a work page.</summary>
    Task<string> StartManualRunAsync(string graphId, string revisionId, CancellationToken cancellationToken = default);
}

public static class OrchestrationText
{
    public const string ScopeNotice =
        "本卡是编排的**管理入口**：图列表、修订结构、校验/发布、手动运行。画布编辑与运行详情按卡片行为说明属于**独立原生工作页**，" +
        "目前尚未实现。";

    public const string HookNotice =
        "HTTP Hook **没有独立的增删改/启停接口**：触发器是图修订的一部分，" +
        "启停或改输入映射都要**发布一个新修订**（Core 的 hooks 路由只提供外部调用入口）。";

    public const string HookSecretNotice =
        "触发器配置里可能引用凭据：界面只显示**配置键名**，不回显配置值，也不显示任何密钥——凭据保持服务端事实。";

    public const string CasNotice =
        "发布是 CAS：草稿里的 ExpectedCurrentRevision 必须是图当前修订号，否则返回冲突并阻止覆盖。";

    public const string ValidateNotice =
        "校验只编译草稿、不落盘；发布前先校验能拿到具体问题与拓扑顺序。";

    public const string TriggerSemanticsNotice =
        "触发器用于**启动新的运行**，它不是 DAG 里的长期节点。";

    public static string DescribeValidationIssue(string issue) => issue;

    /// <summary>A draft must carry a graph id and a JSON object; parse errors are reported, not guessed.</summary>
    public static IReadOnlyList<string> Validate(OrchestrationRevisionDraft draft)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.GraphId)) errors.Add("必须给出 GraphId（触发器与修订都挂在图上）。");
        if (string.IsNullOrWhiteSpace(draft.DefinitionJson)) errors.Add("定义 JSON 不能为空。");
        else if (!LooksLikeJsonObject(draft.DefinitionJson)) errors.Add("定义必须是一个 JSON 对象（以 { 开头、以 } 结尾）。");
        if (draft.ExpectedCurrentRevision < 0) errors.Add("期望修订号不能为负（新建图用 0）。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(string graphId, string revisionId) =>
        string.IsNullOrWhiteSpace(graphId) || string.IsNullOrWhiteSpace(revisionId)
            ? ["手动运行需要图与修订（Core 的 RunCreate 必填 RevisionId）。"]
            : [];

    public static bool LooksLikeJsonObject(string json)
    {
        var trimmed = json.Trim();
        return trimmed.StartsWith('{') && trimmed.EndsWith('}');
    }

    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, 200);

    public static string DescribeRunState(string? state) => state switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "pending", StringComparison.OrdinalIgnoreCase) => "待激活",
        var value when string.Equals(value, "running", StringComparison.OrdinalIgnoreCase) => "运行中",
        var value when string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase) => "已完成",
        var value when string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "cancelled", StringComparison.OrdinalIgnoreCase) => "已取消",
        var value => value
    };
}
