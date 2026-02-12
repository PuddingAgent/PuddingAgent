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
    private readonly ILogger<SubconsciousTriggerTool> _logger;

    public SubconsciousTriggerTool(
        ISubconsciousOrchestrator orchestrator,
        ILogger<SubconsciousTriggerTool> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SubconsciousTriggerArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action ?? "all";
        var workspaceId = args.WorkspaceId ?? "default";

        _logger.LogInformation("[SubconsciousTrigger] Manual trigger action={Action} workspace={Workspace}",
            action, workspaceId);

        try
        {
            var result = action switch
            {
                "auto_dream" => await RunAutoDreamAsync(workspaceId, ct),
                "extract_patterns" => await RunExtractPatternsAsync(workspaceId, ct),
                "improve_skills" => await RunImproveSkillsAsync(workspaceId, ct),
                "consolidate" => SkipConsolidate(),
                "all" => await RunAllAsync(workspaceId, ct),
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

    private async Task<object> RunAutoDreamAsync(string workspaceId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.AutoDreamAsync(workspaceId, null, ct);
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

    private async Task<object> RunExtractPatternsAsync(string workspaceId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.ExtractPatternsAsync(workspaceId, null, ct);
        return new
        {
            action = "extract_patterns",
            duration_ms = sw.ElapsedMilliseconds,
            candidates_found = report.CandidatesFound,
            promoted = report.Promoted,
            demoted_to_memory = report.DemotedToMemory,
            skipped = report.Skipped,
            created_skill_ids = report.CreatedSkillIds,
            summary = report.Summary
        };
    }

    private async Task<object> RunImproveSkillsAsync(string workspaceId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = await _orchestrator.ImproveSkillsAsync(workspaceId, null, ct);
        return new
        {
            action = "improve_skills",
            duration_ms = sw.ElapsedMilliseconds,
            evaluated = report.Evaluated,
            patched = report.Patched,
            skipped = report.Skipped,
            improved_skill_ids = report.ImprovedSkillIds,
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

    private async Task<object> RunAllAsync(string workspaceId, CancellationToken ct)
    {
        var results = new List<object>();
        var totalSw = Stopwatch.StartNew();

        // 安全顺序：清理 → 提取 → 改进
        var steps = new (string name, Func<string, CancellationToken, Task<object>> runner)[]
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
                var result = await runner(workspaceId, ct);
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
}

public sealed record SubconsciousTriggerArgs
{
    [ToolParam("Pipeline name: auto_dream, extract_patterns, improve_skills, consolidate, or all. Default: all.")]
    public string? Action { get; init; }

    [ToolParam("Workspace ID. Default: 'default'.")]
    public string? WorkspaceId { get; init; }
}
