using System.Diagnostics;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 手动触发潜意识管道，用于调试、验证和手动维护。
/// 与定时器共享同一个 ISubconsciousOrchestrator 入口。
/// </summary>
[Tool(
    id: "subconscious_trigger",
    name: "Subconscious pipeline trigger",
    description: "Manually trigger subconscious pipelines (Auto-Dream, Pattern Extraction, Skill Self-Improvement). Bypasses timer delays for debugging and verification.",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.Destructive)]
public sealed class SubconsciousTriggerTool : PuddingToolBase<SubconsciousTriggerArgs>
{
    private readonly ISubconsciousOrchestrator _orchestrator;
    private readonly ILLMConfigResolver _llmConfigResolver;
    private readonly ILogger<SubconsciousTriggerTool> _logger;

    public SubconsciousTriggerTool(
        ISubconsciousOrchestrator orchestrator,
        ILLMConfigResolver llmConfigResolver,
        ILogger<SubconsciousTriggerTool> logger)
    {
        _orchestrator = orchestrator;
        _llmConfigResolver = llmConfigResolver;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SubconsciousTriggerArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action ?? "all";
        var workspaceId = args.WorkspaceId ?? "default";
        var agentInstanceId = context.AgentInstanceId;

        _logger.LogInformation("[SubconsciousTrigger] Manual trigger action={Action} workspace={Workspace}",
            action, workspaceId);

        try
        {
            var memoryLlmConfig = string.Equals(action, "consolidate", StringComparison.Ordinal)
                ? null
                : await ResolveSubconsciousConfigAsync(workspaceId, agentInstanceId, ct);
            var result = action switch
            {
                "auto_dream" => await RunAutoDreamAsync(workspaceId, agentInstanceId, memoryLlmConfig!, ct),
                "extract_patterns" => await RunExtractPatternsAsync(workspaceId, agentInstanceId, memoryLlmConfig!, ct),
                "improve_skills" => await RunImproveSkillsAsync(workspaceId, agentInstanceId, memoryLlmConfig!, ct),
                "consolidate" => SkipConsolidate(),
                "all" => await RunAllAsync(workspaceId, agentInstanceId, memoryLlmConfig!, ct),
                _ => new { error = $"Unknown action '{action}'. Valid: auto_dream, extract_patterns, improve_skills, consolidate, all." }
            };

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ToolExecutionResult.Fail("操作已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubconsciousTrigger] Failed action={Action}", action);
            return ToolExecutionResult.Fail($"管道 {action} 执行失败: {ex.Message}");
        }
    }

    // ── 单个管道触发（每个方法可被 CLI/Admin API 复用）──

    private async Task<object> RunAutoDreamAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig memoryLlmConfig,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.AutoDreamAsync(workspaceId, memoryLlmConfig, ct);
        return new
        {
            action = "auto_dream",
            duration_ms = sw.ElapsedMilliseconds,
            merged = report.Merged,
            archived = report.Archived,
            deleted = report.Deleted,
            suggested = report.Suggested,
            executed = report.Executed,
            summary = report.Summary
        };
    }

    private async Task<object> RunExtractPatternsAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig memoryLlmConfig,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.ExtractPatternsAsync(workspaceId, agentInstanceId, memoryLlmConfig, ct);
        return new
        {
            action = "extract_patterns",
            duration_ms = sw.ElapsedMilliseconds,
            candidates_found = report.CandidatesFound,
            promoted = report.Promoted,
            merged = report.Merged,
            deferred = report.Deferred,
            demoted_to_memory = report.DemotedToMemory,
            skipped = report.Skipped,
            created_skill_ids = report.CreatedSkillIds,
            updated_skill_ids = report.UpdatedSkillIds,
            summary = report.Summary
        };
    }

    private async Task<object> RunImproveSkillsAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig memoryLlmConfig,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.ImproveSkillsAsync(workspaceId, agentInstanceId, memoryLlmConfig, ct);
        return new
        {
            action = "improve_skills",
            duration_ms = sw.ElapsedMilliseconds,
            evaluated = report.Evaluated,
            patched = report.Patched,
            consolidated = report.Consolidated,
            skipped = report.Skipped,
            improved_skill_ids = report.ImprovedSkillIds,
            disabled_duplicate_skill_ids = report.DisabledDuplicateSkillIds,
            summary = report.Summary
        };
    }

    private static object SkipConsolidate()
    {
        return new
        {
            action = "consolidate",
            status = "skipped",
            reason = "consolidate 由 session.closed HOOK 通过 ISubconsciousJobQueue 驱动，不适合手动触发"
        };
    }

    // ── 全管道串联 ──

    private async Task<object> RunAllAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig memoryLlmConfig,
        CancellationToken ct)
    {
        var results = new List<object>();
        var totalSw = Stopwatch.StartNew();

        // 安全顺序：清理 → 提取 → 改进
        var steps = new (string name, Func<string, string, MemoryLlmConfig, CancellationToken, Task<object>> runner)[]
        {
            ("auto_dream", RunAutoDreamAsync),
            ("extract_patterns", RunExtractPatternsAsync),
            ("improve_skills", RunImproveSkillsAsync),
        };

        foreach (var (name, runner) in steps)
        {
            try
            {
                _logger.LogInformation("[SubconsciousTrigger] all → {Step}", name);
                var result = await runner(workspaceId, agentInstanceId, memoryLlmConfig, ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SubconsciousTrigger] all → {Step} failed, continuing", name);
                results.Add(new { action = name, error = ex.Message });
            }
        }

        return new
        {
            action = "all",
            total_duration_ms = totalSw.ElapsedMilliseconds,
            steps = results
        };
    }

    private async Task<MemoryLlmConfig> ResolveSubconsciousConfigAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct)
    {
        var route = await _llmConfigResolver.ResolveRoleAsync(
            workspaceId,
            agentInstanceId,
            AgentLlmRoleIds.Subconscious,
            ct);
        return new MemoryLlmConfig(
            route.Config.Endpoint,
#pragma warning disable CS0618
            route.Config.ApiKey,
#pragma warning restore CS0618
            route.ModelId)
        {
            ProviderId = route.ProviderId,
            ProfileId = route.ProfileId,
            WorkspaceId = workspaceId,
            SessionId = "semantic-tool:subconscious-trigger",
            AgentInstanceId = agentInstanceId,
            Stage = "subconscious-trigger",
        };
    }
}

public sealed record SubconsciousTriggerArgs
{
    [ToolParam("Pipeline name: auto_dream, extract_patterns, improve_skills, consolidate, or all. Default: all.")]
    public string? Action { get; init; }

    [ToolParam("Workspace ID. Default: 'default'.")]
    public string? WorkspaceId { get; init; }
}
