namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Runtime 侧 Agent Skill 接口。
/// 与 PuddingCore.Skills 的 CLI Skill 体系独立——被 SkillRuntime 管理并在 Agent 执行过程中调用。
/// </summary>
public interface IAgentSkill
{
    /// <summary>唯一 ID，对应 AgentTemplateDefinition.SkillIds 中的条目。</summary>
    string SkillId { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>若 true，则需要 CapabilityPolicy.AllowShellExecution = true 方可使用。</summary>
    bool RequiresShellExecution { get; }

    Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default);
}

// ── 请求/结果 ──────────────────────────────────────────────────────────

public sealed record SkillInvokeRequest
{
    public required string AgentInstanceId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    /// <summary>主输入（bash 命令、文件路径等）。</summary>
    public required string Input { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>();
}

public sealed record SkillResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public string? Error { get; init; }
    public int ExitCode { get; init; }
}
