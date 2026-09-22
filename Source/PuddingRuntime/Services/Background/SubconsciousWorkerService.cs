using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Skills.Portfolio;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// 潜意识后台消费服务：串行消费 ConsolidationJob 队列并调用编排器。
/// 同时运行四个定时循环：经验提取(12h)、记忆整理(6h)、Skill 改进(4h)、Skill 治理报告(4h)。
/// </summary>
public sealed class SubconsciousWorkerService : BackgroundService
{
    private static readonly TimeSpan DurableLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>G8-D5：堆积“未评估”字样（⛔ 不得与 <c>false</c> 混用：“未测”不是“测过且安全”）。</summary>
    private const string BacklogNotEvaluated = "not_evaluated";

    private readonly Channel<ConsolidationJob> _channel;
    private readonly ISubconsciousOrchestrator _orchestrator;
    private readonly ILLMConfigResolver? _llmConfigResolver;
    private readonly ISubconsciousJobQueue? _jobQueue;
    private readonly SubconsciousJobScheduler? _scheduler;
    private readonly SubconsciousPlanGenerationService? _planGenerationService;
    private readonly IMemoryWriteCoordinator? _memoryWriteCoordinator;
    private readonly MemoryWikiPageUpdateService? _wikiPageUpdateService;
    private readonly WikiPageWriteEntry? _wikiPageWriteEntry;
    private readonly ISubconsciousRuntimeControl? _runtimeControl;
    private readonly SubconsciousSchedulingOptions _scheduling;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubconsciousWorkerService> _logger;
    /// <summary>
    /// G8-D3：节奏策略（**尾随可选**，默认 <c>null</c>）。
    /// <para>⛔ 组合根刻意**不注册**它：阈值/窗口必须来自版本化策略对象，不得由 DI 凭空编造。
    /// 因此生产默认下本字段为 <c>null</c> ⇒ 不产生任何节奏记录（零回归）。</para>
    /// </summary>
    private readonly SubconsciousRhythmPolicy? _rhythmPolicy;
    /// <summary>G8-D5：堆积信号（**尾随可选**，默认 <c>null</c> ⇒ 记 <c>not_evaluated</c>，⛔ 不伪装成 false）。</summary>
    private readonly ISkillPortfolioBacklogSignal? _backlogSignal;
    /// <summary>G8-D5：阈值来源（<c>SoftTarget</c> 的**唯一**来源；未注入 ⇒ 无法判定 ⇒ <c>not_evaluated</c>）。</summary>
    private readonly SkillPortfolioPolicy? _portfolioPolicy;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public SubconsciousWorkerService(
        Channel<ConsolidationJob> channel,
        ISubconsciousOrchestrator orchestrator,
        ILogger<SubconsciousWorkerService> logger,
        ILLMConfigResolver? llmConfigResolver = null,
        ISubconsciousJobQueue? jobQueue = null,
        SubconsciousJobScheduler? scheduler = null,
        SubconsciousPlanGenerationService? planGenerationService = null,
        IMemoryWriteCoordinator? memoryWriteCoordinator = null,
        MemoryWikiPageUpdateService? wikiPageUpdateService = null,
        WikiPageWriteEntry? wikiPageWriteEntry = null,
        ISubconsciousRuntimeControl? runtimeControl = null,
        IOptions<SubconsciousOptions>? options = null,
        TimeProvider? timeProvider = null,
        SubconsciousRhythmPolicy? rhythmPolicy = null,
        ISkillPortfolioBacklogSignal? backlogSignal = null,
        SkillPortfolioPolicy? portfolioPolicy = null)
    {
        _channel = channel;
        _orchestrator = orchestrator;
        _llmConfigResolver = llmConfigResolver;
        _jobQueue = jobQueue;
        _scheduler = scheduler;
        _planGenerationService = planGenerationService;
        _memoryWriteCoordinator = memoryWriteCoordinator;
        _wikiPageUpdateService = wikiPageUpdateService;
        _wikiPageWriteEntry = wikiPageWriteEntry;
        _runtimeControl = runtimeControl;
        _scheduling = options?.Value.Scheduling ?? new SubconsciousSchedulingOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rhythmPolicy = rhythmPolicy;
        _backlogSignal = backlogSignal;
        _portfolioPolicy = portfolioPolicy;
        _logger = logger;
    }

    /// <summary>
    /// 后台执行循环：并行运行队列消费 + 四个定时循环（`skill.extract_patterns` /
    /// `memory.auto_dream` / `skill.improve` / `skill.curate`）。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[SubconsciousWorker] Started leaseOwner={LeaseOwner} durableQueue={HasDurableQueue}.",
            _leaseOwner,
            _jobQueue is not null);

        var loops = new List<Task> { ConsumeQueueLoopAsync(stoppingToken) };
        if (_scheduling.PeriodicJobsEnabled && _jobQueue is not null)
        {
            loops.Add(PatternExtractionLoopAsync(stoppingToken));
            loops.Add(AutoDreamLoopAsync(stoppingToken));
            loops.Add(SkillImprovementLoopAsync(stoppingToken));
            loops.Add(SkillCurationLoopAsync(stoppingToken));
        }
        else
        {
            _logger.LogWarning(
                "[SubconsciousWorker] Periodic jobs disabled enabled={Enabled} durableQueue={HasDurableQueue}",
                _scheduling.PeriodicJobsEnabled,
                _jobQueue is not null);
        }

        await Task.WhenAll(loops);

        _logger.LogInformation("[SubconsciousWorker] Stopped.");
    }

    /// <summary>
    /// 队列消费循环：从持久队列和 Channel 中消费 ConsolidationJob。
    /// </summary>
    private async Task ConsumeQueueLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_runtimeControl?.IsPaused == true)
                {
                    await Task.Delay(IdlePollDelay, stoppingToken);
                    continue;
                }

                if (_jobQueue is not null)
                {
                    var durableJob = _scheduler is not null
                        ? await _scheduler.TryLeaseNextAsync(_leaseOwner, DurableLeaseDuration, stoppingToken)
                        : await _jobQueue.LeaseNextAsync(
                            _leaseOwner,
                            DurableLeaseDuration,
                            ct: stoppingToken);

                    if (durableJob is not null)
                    {
                        await ProcessDurableJobAsync(durableJob, stoppingToken);
                        continue;
                    }
                }

                if (_channel.Reader.TryRead(out var legacyJob))
                {
                    await ProcessConsolidationJobAsync(legacyJob, stoppingToken);
                    continue;
                }

                await Task.Delay(IdlePollDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SubconsciousWorker] Worker loop failed; retrying after idle delay");

                await Task.Delay(IdlePollDelay, stoppingToken);
            }
        }
    }

    private async Task ProcessDurableJobAsync(
        SubconsciousJobQueueItem queueItem,
        CancellationToken stoppingToken)
    {
        try
        {
            if (await TryProcessPeriodicJobAsync(queueItem, stoppingToken))
                return;

            if (_wikiPageUpdateService is not null && _wikiPageWriteEntry is not null)
            {
                await ProcessDurableWikiPageUpdateAsync(queueItem, stoppingToken);
                return;
            }

            if (_planGenerationService is not null && _memoryWriteCoordinator is not null)
            {
                await ProcessDurablePlanDryRunAsync(queueItem, stoppingToken);
                return;
            }

            await ProcessConsolidationJobAsync(queueItem.Job, stoppingToken);

            if (_jobQueue is not null)
                await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_jobQueue is null)
                throw;

            var status = await _jobQueue.RetryAsync(
                queueItem.JobId,
                _leaseOwner,
                ex.Message,
                RetryDelay,
                CancellationToken.None);

            _logger.LogWarning(
                ex,
                "[SubconsciousWorker] Durable job failed jobId={JobId} status={Status} session={SessionId} workspace={WorkspaceId}",
                queueItem.JobId,
                status,
                queueItem.Job.SessionId,
                queueItem.Job.WorkspaceId);
        }
    }

    private async Task<bool> TryProcessPeriodicJobAsync(
        SubconsciousJobQueueItem queueItem,
        CancellationToken ct)
    {
        if (queueItem.JobType is not (
                SubconsciousJobTypes.AutoDream
                or SubconsciousJobTypes.ExtractPatterns
                or SubconsciousJobTypes.ImproveSkills
                or SubconsciousJobTypes.SkillCurate))
        {
            return false;
        }

        var (mode, memoryLlmConfig) = await ResolveMemoryOptionsAsync(queueItem.Job, ct);
        if (mode == "off")
        {
            if (_jobQueue is not null)
            {
                var disabledResult = CreateCompletedPeriodicResult(
                    queueItem.JobType,
                    0,
                    "Subconscious role is disabled for this Agent.",
                    CreatePeriodicResultMetadata(queueItem, 0, DateTime.UtcNow));
                await _jobQueue.RecordResultAsync(queueItem.JobId, _leaseOwner, disabledResult, ct);
                await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, ct);
            }
            return true;
        }

        SubconsciousJobResultEnvelope result;
        switch (queueItem.JobType)
        {
            case SubconsciousJobTypes.AutoDream:
                var autoDreamReport = await _orchestrator.AutoDreamAsync(
                    queueItem.Job.WorkspaceId,
                    memoryLlmConfig,
                    ct);
                result = CreateAutoDreamResultEnvelope(queueItem, autoDreamReport);
                break;
            case SubconsciousJobTypes.ExtractPatterns:
                var patternReport = await _orchestrator.ExtractPatternsAsync(
                    queueItem.Job.WorkspaceId,
                    queueItem.Job.AgentId,
                    memoryLlmConfig,
                    ct);
                result = CreatePatternExtractionResultEnvelope(queueItem, patternReport);
                break;
            case SubconsciousJobTypes.ImproveSkills:
                var improvementReport = await _orchestrator.ImproveSkillsAsync(
                    queueItem.Job.WorkspaceId,
                    queueItem.Job.AgentId,
                    memoryLlmConfig,
                    ct);
                result = CreateSkillImprovementResultEnvelope(queueItem, improvementReport);
                break;
            case SubconsciousJobTypes.SkillCurate:
                var curationReport = await _orchestrator.SkillCurateAsync(
                    queueItem.Job.WorkspaceId,
                    queueItem.Job.AgentId,
                    memoryLlmConfig,
                    ct: ct);
                result = CreateSkillCurationResultEnvelope(queueItem, curationReport);
                break;
            default:
                return false;
        }

        if (_jobQueue is not null)
        {
            await _jobQueue.RecordResultAsync(queueItem.JobId, _leaseOwner, result, ct);
            await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, ct);
        }
        return true;
    }

    private static SubconsciousJobResultEnvelope CreateAutoDreamResultEnvelope(
        SubconsciousJobQueueItem queueItem,
        AutoDreamReport report)
    {
        var metadata = CreatePeriodicResultMetadata(queueItem, report.DurationMs, report.Timestamp);
        metadata["suggested_count"] = report.Suggested.ToString();
        metadata["executed_count"] = report.Executed.ToString();
        metadata["merged_count"] = report.Merged.ToString();
        metadata["archived_count"] = report.Archived.ToString();
        metadata["deleted_count"] = report.Deleted.ToString();

        return CreateCompletedPeriodicResult(
            SubconsciousJobResultKinds.MemoryAutoDream,
            report.Executed,
            report.Summary,
            metadata);
    }

    private static SubconsciousJobResultEnvelope CreatePatternExtractionResultEnvelope(
        SubconsciousJobQueueItem queueItem,
        PatternExtractionReport report)
    {
        var metadata = CreatePeriodicResultMetadata(queueItem, report.DurationMs, report.Timestamp);
        metadata["candidates_found_count"] = report.CandidatesFound.ToString();
        metadata["promoted_count"] = report.Promoted.ToString();
        metadata["merged_count"] = report.Merged.ToString();
        metadata["deferred_count"] = report.Deferred.ToString();
        metadata["demoted_to_memory_count"] = report.DemotedToMemory.ToString();
        metadata["skipped_count"] = report.Skipped.ToString();
        metadata["created_skill_ids"] = string.Join(",", report.CreatedSkillIds);
        metadata["updated_skill_ids"] = string.Join(",", report.UpdatedSkillIds);

        return CreateCompletedPeriodicResult(
            SubconsciousJobResultKinds.SkillPatternExtraction,
            report.Promoted + report.Merged + report.DemotedToMemory,
            report.Summary,
            metadata);
    }

    private static SubconsciousJobResultEnvelope CreateSkillImprovementResultEnvelope(
        SubconsciousJobQueueItem queueItem,
        SkillImprovementReport report)
    {
        var metadata = CreatePeriodicResultMetadata(queueItem, report.DurationMs, report.Timestamp);
        metadata["evaluated_count"] = report.Evaluated.ToString();
        metadata["patched_count"] = report.Patched.ToString();
        metadata["consolidated_count"] = report.Consolidated.ToString();
        metadata["skipped_count"] = report.Skipped.ToString();
        metadata["improved_skill_ids"] = string.Join(",", report.ImprovedSkillIds);
        metadata["disabled_duplicate_skill_ids"] = string.Join(",", report.DisabledDuplicateSkillIds);

        return CreateCompletedPeriodicResult(
            SubconsciousJobResultKinds.SkillImprovement,
            report.Patched + report.Consolidated,
            report.Summary,
            metadata);
    }

    private static SubconsciousJobResultEnvelope CreateSkillCurationResultEnvelope(
        SubconsciousJobQueueItem queueItem,
        SkillCurationReport report)
    {
        var metadata = CreatePeriodicResultMetadata(queueItem, report.DurationMs, report.Timestamp);
        metadata["n_before"] = report.NBefore.ToString();
        metadata["n_after"] = report.NAfter.ToString();
        metadata["not_reduced_reason"] = report.NotReducedReason;
        metadata["candidate_count"] = report.CandidateCount.ToString();
        metadata["retire_suggestion_count"] = report.RetireSuggestionCount.ToString();
        metadata["report_version"] = "v1";
        // G7：门禁裁决计数必须能从**作业结果**观测到 —— 只写日志不算"被记录"
        // （接线级探针用例据此断言"被拒"确实被记录，而不是静默放过）。
        metadata["curated_products"] = report.CuratedProductCount.ToString();
        metadata["curated_shadow"] = report.CuratedShadowCount.ToString();
        metadata["curated_rejected"] = report.CuratedRejectedCount.ToString();
        // G4-D7：家族评审计数同样必须能从**作业结果**观测到（同 G7 的教训：只写日志不算“被记录”）。
        // ⚠️ 非 0 只表示“产生了待裁决记录”，不代表任何技能被禁用/合并。
        metadata["family_review_count"] = report.FamilyReviewCount.ToString();

        // 零写盘（G6 I3）：本作业 OperationCount 恒为 0，不产生任何技能写操作。
        return CreateCompletedPeriodicResult(
            SubconsciousJobResultKinds.SkillCuration,
            0,
            report.Summary,
            metadata);
    }

    private static SubconsciousJobResultEnvelope CreateCompletedPeriodicResult(
        string kind,
        int operationCount,
        string? summary,
        IReadOnlyDictionary<string, string> metadata)
        => new()
        {
            Kind = kind,
            Status = SubconsciousJobResultStatuses.Completed,
            Decision = SubconsciousJobResultDecisions.ExecutionCompleted,
            NextAction = SubconsciousJobResultNextActions.CompleteJob,
            Valid = true,
            OperationCount = operationCount,
            Summary = summary,
            Metadata = metadata,
        };

    private static Dictionary<string, string> CreatePeriodicResultMetadata(
        SubconsciousJobQueueItem queueItem,
        long durationMs,
        DateTime timestamp)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subconscious_job_id"] = queueItem.JobId,
            ["job_type"] = queueItem.JobType,
            ["workspace_id"] = queueItem.Job.WorkspaceId,
            ["session_id"] = queueItem.Job.SessionId,
            ["agent_instance_id"] = queueItem.Job.AgentId,
            ["duration_ms"] = durationMs.ToString(),
            ["timestamp_utc"] = timestamp.ToUniversalTime().ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(queueItem.SourceCompactionId))
            metadata["request_id"] = queueItem.SourceCompactionId!;

        // G8-D3：把入队时刻写下的节奏**提案**记录透传到结果信封的**既有** metadata 通道
        //（同一张表，⛔ 不新建并行遥测通道）；键名原样保留 proposed/configured 语义。
        // 未配置节奏策略时 Job.Metadata 为 null ⇒ 这里一个键也不加（P1 零回归）。
        if (queueItem.Job.Metadata is { Count: > 0 } rhythmMetadata)
        {
            foreach (var pair in rhythmMetadata)
                metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

    private async Task ProcessDurableWikiPageUpdateAsync(
        SubconsciousJobQueueItem queueItem,
        CancellationToken stoppingToken)
    {
        var (mode, memoryLlmConfig) = await ResolveMemoryOptionsAsync(queueItem.Job, stoppingToken);
        if (mode == "off")
        {
            _logger.LogInformation(
                "[SubconsciousWorker] Skip durable page-update jobId={JobId} session={SessionId} workspace={WorkspaceId} because mode=off",
                queueItem.JobId,
                queueItem.Job.SessionId,
                queueItem.Job.WorkspaceId);
            if (_jobQueue is not null)
                await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, stoppingToken);
            return;
        }

        if (queueItem.Job.MemoryNotes.Count == 0)
        {
            if (_jobQueue is not null)
            {
                await _jobQueue.RecordResultAsync(
                    queueItem.JobId,
                    _leaseOwner,
                    CreateWikiPageUpdateEnvelope(
                        status: SubconsciousJobResultStatuses.Accepted,
                        valid: true,
                        updateCount: 0,
                        errorCodes: [],
                        summary: "No memory notes; completed no-op.",
                        queueItem,
                        writeResults: []),
                    stoppingToken);
                await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, stoppingToken);
            }
            return;
        }

        var updateResult = await _wikiPageUpdateService!.GenerateAsync(
            CreateWikiPageUpdateRequest(queueItem, memoryLlmConfig),
            stoppingToken);

        if (!updateResult.IsValid || updateResult.Plan is null)
        {
            if (_jobQueue is not null)
            {
                await _jobQueue.RecordResultAsync(
                    queueItem.JobId,
                    _leaseOwner,
                    CreateWikiPageUpdateEnvelope(
                        status: SubconsciousJobResultStatuses.Rejected,
                        valid: false,
                        updateCount: 0,
                        errorCodes: updateResult.Errors,
                        summary: "Memory wiki page update JSON rejected.",
                        queueItem,
                        writeResults: []),
                    stoppingToken);
                await _jobQueue.RetryAsync(
                    queueItem.JobId,
                    _leaseOwner,
                    string.Join(",", updateResult.Errors),
                    RetryDelay,
                    stoppingToken);
            }
            return;
        }

        var writeResults = await _wikiPageWriteEntry!.WriteAsync(
            new WikiPageWriteRequest
            {
                WorkspaceId = queueItem.Job.WorkspaceId,
                LibraryId = null,
                AgentId = queueItem.Job.AgentId,
                SessionId = queueItem.Job.SessionId,
                Plan = updateResult.Plan,
            },
            stoppingToken);

        if (_jobQueue is not null)
        {
            await _jobQueue.RecordResultAsync(
                queueItem.JobId,
                _leaseOwner,
                CreateWikiPageUpdateEnvelope(
                    status: SubconsciousJobResultStatuses.Accepted,
                    valid: true,
                    updateCount: updateResult.Plan.Updates.Count,
                    errorCodes: [],
                    summary: "Memory wiki page update executed.",
                    queueItem,
                    writeResults),
                stoppingToken);
            await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, stoppingToken);
        }
    }

    private async Task ProcessDurablePlanDryRunAsync(
        SubconsciousJobQueueItem queueItem,
        CancellationToken stoppingToken)
    {
        var (mode, memoryLlmConfig) = await ResolveMemoryOptionsAsync(queueItem.Job, stoppingToken);
        if (mode == "off")
        {
            _logger.LogInformation(
                "[SubconsciousWorker] Skip durable dry-run jobId={JobId} session={SessionId} workspace={WorkspaceId} because mode=off",
                queueItem.JobId,
                queueItem.Job.SessionId,
                queueItem.Job.WorkspaceId);
            return;
        }

        var planResult = await _planGenerationService!.GenerateDryRunAsync(
            CreatePlanGenerationRequest(queueItem, memoryLlmConfig),
            stoppingToken);

        var writeResults = new List<MemoryWriteResultEnvelope>();
        if (planResult.Validation.IsValid && planResult.Plan is not null)
        {
            foreach (var operation in planResult.Plan.Operations)
            {
                var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
                    planResult.Plan,
                    operation,
                    MemoryWriteExecutionModes.DryRun);

                var writeResult = await _memoryWriteCoordinator!.CoordinateAsync(command, stoppingToken);
                writeResults.Add(writeResult);
            }
        }

        if (_jobQueue is not null)
        {
            var envelope = planResult.ToJobResultEnvelope(writeResults);
            await _jobQueue.RecordResultAsync(
                queueItem.JobId,
                _leaseOwner,
                envelope,
                stoppingToken);

            if (envelope.NextAction == SubconsciousJobResultNextActions.RetryJob)
            {
                await _jobQueue.RetryAsync(
                    queueItem.JobId,
                    _leaseOwner,
                    string.Join(",", envelope.ErrorCodes),
                    RetryDelay,
                    stoppingToken);
            }
            else
            {
                await _jobQueue.CompleteAsync(queueItem.JobId, _leaseOwner, stoppingToken);
            }
        }
    }

    private async Task ProcessConsolidationJobAsync(
        ConsolidationJob job,
        CancellationToken stoppingToken)
    {
        var (mode, memoryLlmConfig) = await ResolveMemoryOptionsAsync(job, stoppingToken);

        if (mode == "off")
        {
            _logger.LogInformation(
                "[SubconsciousWorker] Skip session={SessionId} workspace={WorkspaceId} because mode=off",
                job.SessionId,
                job.WorkspaceId);
            return;
        }

        _logger.LogInformation(
            "[SubconsciousWorker] Processing session={SessionId} workspace={WorkspaceId} mode={Mode}",
            job.SessionId,
            job.WorkspaceId,
            mode);

        _logger.LogDebug(
            "[SubconsciousWorker] Job detail: agentId={AgentId} templateId={TemplateId} hasLlmConfig={HasConfig}",
            job.AgentId, job.AgentTemplateId, memoryLlmConfig is not null);

        await _orchestrator.ConsolidateAsync(job, mode, memoryLlmConfig, stoppingToken);
    }

    private async Task<(string Mode, MemoryLlmConfig? MemoryLlmConfig)> ResolveMemoryOptionsAsync(
        ConsolidationJob job,
        CancellationToken stoppingToken)
    {
        var mode = "deep";
        MemoryLlmConfig? memoryLlmConfig = null;

        if (_llmConfigResolver is not null)
        {
            try
            {
                var roleRoute = await _llmConfigResolver.ResolveRoleAsync(
                    job.WorkspaceId,
                    job.AgentId,
                    AgentLlmRoleIds.Subconscious,
                    stoppingToken);
                mode = roleRoute.SearchMode;
                memoryLlmConfig = new MemoryLlmConfig(
                    roleRoute.Config.Endpoint,
#pragma warning disable CS0618
                    roleRoute.Config.ApiKey,
#pragma warning restore CS0618
                    roleRoute.ModelId)
                {
                    ProviderId = roleRoute.ProviderId,
                    ProfileId = roleRoute.ProfileId,
                    WorkspaceId = job.WorkspaceId,
                    SessionId = job.SessionId,
                    AgentInstanceId = job.AgentId,
                    Stage = "subconscious-job",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[SubconsciousWorker] Resolve subconscious role failed agent={AgentId} workspace={Workspace}; job will fail without fallback",
                    job.AgentId, job.WorkspaceId);
                throw;
            }
        }

        if (mode is not ("off" or "instant" or "deep"))
            mode = "deep";

        return (mode, memoryLlmConfig);
    }

    private static SubconsciousPlanGenerationRequest CreatePlanGenerationRequest(
        SubconsciousJobQueueItem queueItem,
        MemoryLlmConfig? memoryLlmConfig)
    {
        var job = queueItem.Job;
        return new SubconsciousPlanGenerationRequest
        {
            WorkspaceId = job.WorkspaceId,
            SessionId = job.SessionId,
            AgentId = job.AgentId,
            AgentTemplateId = job.AgentTemplateId,
            MemoryScope = new SubconsciousMemoryScope
            {
                WorkspaceId = job.WorkspaceId,
                AgentId = job.AgentId,
                AgentTemplateId = job.AgentTemplateId,
                SessionId = job.SessionId,
            },
            HookEventId = queueItem.SourceEventId,
            SubconsciousJobId = queueItem.JobId,
            EvidenceSummary = BuildEvidenceSummary(job),
            MemoryLlmConfig = memoryLlmConfig,
        };
    }

    private static MemoryWikiPageUpdateRequest CreateWikiPageUpdateRequest(
        SubconsciousJobQueueItem queueItem,
        MemoryLlmConfig? memoryLlmConfig)
    {
        var job = queueItem.Job;
        return new MemoryWikiPageUpdateRequest
        {
            WorkspaceId = job.WorkspaceId,
            SessionId = job.SessionId,
            AgentId = job.AgentId,
            AgentTemplateId = job.AgentTemplateId,
            MemoryScope = new SubconsciousMemoryScope
            {
                WorkspaceId = job.WorkspaceId,
                AgentId = job.AgentId,
                AgentTemplateId = job.AgentTemplateId,
                SessionId = job.SessionId,
            },
            HookEventId = queueItem.SourceEventId,
            SubconsciousJobId = queueItem.JobId,
            MemoryNotes = job.MemoryNotes,
            MemoryLlmConfig = memoryLlmConfig,
        };
    }

    private static SubconsciousJobResultEnvelope CreateWikiPageUpdateEnvelope(
        string status,
        bool valid,
        int updateCount,
        IReadOnlyList<string> errorCodes,
        string summary,
        SubconsciousJobQueueItem queueItem,
        IReadOnlyList<WikiPageWriteResult> writeResults)
    {
        var decision = valid
            ? SubconsciousJobResultDecisions.AcceptForExecution
            : SubconsciousJobResultDecisions.RetryLater;
        var nextAction = valid
            ? "complete_executed"
            : SubconsciousJobResultNextActions.RetryJob;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = queueItem.Job.WorkspaceId,
            ["session_id"] = queueItem.Job.SessionId,
            ["agent_id"] = queueItem.Job.AgentId,
            ["agent_template_id"] = queueItem.Job.AgentTemplateId,
            ["memory_note_count"] = queueItem.Job.MemoryNotes.Count.ToString(),
            ["written_page_count"] = writeResults.Count.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(queueItem.SourceEventId))
            metadata["hook_event_id"] = queueItem.SourceEventId!;
        if (!string.IsNullOrWhiteSpace(queueItem.JobId))
            metadata["subconscious_job_id"] = queueItem.JobId;

        return new SubconsciousJobResultEnvelope
        {
            Kind = SubconsciousJobResultKinds.MemoryWikiPageUpdate,
            Status = status,
            Decision = decision,
            NextAction = nextAction,
            Valid = valid,
            OperationCount = updateCount,
            ErrorCount = errorCodes.Count,
            ErrorCodes = errorCodes,
            Summary = summary,
            Metadata = metadata,
        };
    }

    private static string BuildEvidenceSummary(ConsolidationJob job)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.LastUserMessage))
            parts.Add($"user: {job.LastUserMessage}");
        if (!string.IsNullOrWhiteSpace(job.LastAssistantReply))
            parts.Add($"assistant: {job.LastAssistantReply}");

        return parts.Count == 0
            ? $"session_id={job.SessionId}; workspace_id={job.WorkspaceId}"
            : string.Join("\n", parts);
    }


    // ── Pattern Extraction 定时循环 ──
    private async Task PatternExtractionLoopAsync(CancellationToken ct)
    {
        await RunPeriodicEnqueueLoopAsync(
            SubconsciousJobTypes.ExtractPatterns,
            TimeSpan.FromSeconds(Math.Max(0, _scheduling.PatternExtractionInitialDelaySeconds)),
            TimeSpan.FromSeconds(Math.Max(1, _scheduling.PatternExtractionIntervalSeconds)),
            ct);
    }

    // ── Auto-Dream 定时循环 ──
    private async Task AutoDreamLoopAsync(CancellationToken ct)
    {
        await RunPeriodicEnqueueLoopAsync(
            SubconsciousJobTypes.AutoDream,
            TimeSpan.FromSeconds(Math.Max(0, _scheduling.AutoDreamInitialDelaySeconds)),
            TimeSpan.FromSeconds(Math.Max(1, _scheduling.AutoDreamIntervalSeconds)),
            ct);
    }
    // ── Skill Self-Improvement 定时循环 ──
    private async Task SkillImprovementLoopAsync(CancellationToken ct)
    {
        await RunPeriodicEnqueueLoopAsync(
            SubconsciousJobTypes.ImproveSkills,
            TimeSpan.FromSeconds(Math.Max(0, _scheduling.SkillImprovementInitialDelaySeconds)),
            TimeSpan.FromSeconds(Math.Max(1, _scheduling.SkillImprovementIntervalSeconds)),
            ct);
    }

    // ── Skill Curation 定时循环 ──
    private async Task SkillCurationLoopAsync(CancellationToken ct)
    {
        await RunPeriodicEnqueueLoopAsync(
            SubconsciousJobTypes.SkillCurate,
            TimeSpan.FromSeconds(Math.Max(0, _scheduling.SkillCurationInitialDelaySeconds)),
            TimeSpan.FromSeconds(Math.Max(1, _scheduling.SkillCurationIntervalSeconds)),
            ct);
    }

    private async Task RunPeriodicEnqueueLoopAsync(
        string jobType,
        TimeSpan initialDelay,
        TimeSpan interval,
        CancellationToken ct)
    {
        await Task.Delay(initialDelay, _timeProvider, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_runtimeControl?.IsPaused != true)
                    await EnqueuePeriodicJobAsync(jobType, interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SubconsciousWorker] Periodic enqueue failed jobType={JobType}", jobType);
            }

            await Task.Delay(interval, _timeProvider, ct);
        }
    }

    private async Task EnqueuePeriodicJobAsync(
        string jobType,
        TimeSpan interval,
        CancellationToken ct)
    {
        if (_jobQueue is null)
            return;

        var workspaceId = _scheduling.DefaultWorkspaceId.Trim();
        var agentInstanceId = _scheduling.DefaultAgentInstanceId.Trim();
        var bucket = _timeProvider.GetUtcNow().UtcTicks / Math.Max(1, interval.Ticks);
        var idempotencyKey = $"periodic:{jobType}:{workspaceId}:{agentInstanceId}:{bucket}";
        if (await _jobQueue.FindLatestAsync(new SubconsciousJobLookupQuery
            {
                IdempotencyKey = idempotencyKey,
            }, ct) is not null)
        {
            _logger.LogDebug(
                "[SubconsciousWorker] Periodic job already exists key={IdempotencyKey}",
                idempotencyKey);
            return;
        }

        var job = new ConsolidationJob
        {
            SessionId = $"periodic:{jobType}",
            WorkspaceId = workspaceId,
            AgentId = agentInstanceId,
            AgentTemplateId = agentInstanceId,
        };

        // ── G8-D3/D5：把这一轮的节奏决定写成**提案型**记录（⛔ 不改 interval / bucket / delay）──
        var rhythmMetadata = await BuildRhythmDecisionMetadataAsync(interval, agentInstanceId, ct);
        if (rhythmMetadata.Count > 0)
            job = job with { Metadata = rhythmMetadata };

        await _jobQueue.EnqueueAsync(new SubconsciousJobEnqueueRequest
        {
            JobType = jobType,
            IdempotencyKey = idempotencyKey,
            SourceHookName = "subconscious.periodic",
            Job = job,
        }, ct);

        _logger.LogInformation(
            "[SubconsciousWorker] Enqueued periodic job type={JobType} workspace={WorkspaceId} agent={AgentInstanceId} bucket={Bucket}",
            jobType,
            workspaceId,
            agentInstanceId,
            bucket);
    }

    /// <summary>
    /// G8-D3：把这一轮“若启用节奏策略会采用哪个间隔”写成可解释记录。
    /// <para>
    /// ⛔ <b>提案语义</b>：全部键名带 <c>proposed</c>/<c>configured</c> 前缀，且**不改变任何调度** —— 采用间隔
    /// 不会传给 <c>Task.Delay</c>，也不参与幂等键 <c>bucket</c> 的计算。原因是实测：周期作业速率由
    /// <c>bucket</c> 除数（= 配置间隔）决定，只缩短 delay 是空操作（任务书 §4.4 裁决 1/2、不变式 P9）。
    /// </para>
    /// <para>
    /// ⛔ 未配置策略（<see cref="_rhythmPolicy"/> 为 <c>null</c>）时返回空字典 ⇒ 记录整体不出现（P1 / P4）。
    /// </para>
    /// <para>
    /// ⚠️ <b>诚实边界</b>：堆积条件（<c>isBacklog</c>）由注入的
    /// <see cref="ISkillPortfolioBacklogSignal"/> + <see cref="SkillPortfolioPolicy.SoftTarget"/> 提供；
    /// **未注入或未取得事实时记 </b><c>not_evaluated</c><b>，而不是 </b><c>false</c> —— 读到 <c>false</c>
    /// 才是「测过且未堆积」，“未测”不得被读成安全。
    /// </para>
    /// </summary>
    /// <param name="configuredInterval">配置间隔（即 <c>TryEnqueue</c> 实际使用的间隔；不会因本记录而改变）。</param>
    private async Task<Dictionary<string, string>> BuildRhythmDecisionMetadataAsync(
        TimeSpan configuredInterval,
        string agentInstanceId,
        CancellationToken ct)
    {
        var policy = _rhythmPolicy;
        if (policy is null)
            return [];

        var window = SubconsciousOffPeakEvaluator.From(policy);
        var isOffPeak = window is not null
            && SubconsciousOffPeakEvaluator.IsOffPeak(_timeProvider, window);

        var (isBacklog, backlogState) = await ResolveBacklogSignalAsync(agentInstanceId, ct);

        // configuredInterval 已由调用方的 Math.Max(1, …) 保证 ≥ 1 秒，这里再夹取一次以免策略
        // 层抛 ArgumentOutOfRange 把整个入队循环打断（宁可记录退化为默认，也不得阻断调度）。
        var configuredSeconds = (int)Math.Max(1d, Math.Floor(configuredInterval.TotalSeconds));
        var decision = policy.Resolve(configuredSeconds, isOffPeak, isBacklog);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rhythm_proposed_interval_seconds"] = decision.IntervalSeconds.ToString(),
            ["rhythm_configured_interval_seconds"] = decision.ConfiguredIntervalSeconds.ToString(),
            ["rhythm_reason_code"] = decision.ReasonCode,
            ["rhythm_off_peak"] = decision.IsOffPeak ? "true" : "false",
            ["rhythm_backlog"] = backlogState,
            ["rhythm_shortened"] = decision.IsShortened ? "true" : "false",
            ["rhythm_policy"] = $"{policy.PolicyId}@{policy.Version}",
        };
    }

    /// <summary>
    /// G8-D5：解析堆积信号。<c>enabled &gt; SoftTarget</c> ⇒ 堆积（阈值口径与
    /// <c>SkillPortfolioPolicy</c> 同源，此处不另造口径）。
    /// <para>⛔ 拿不到事实 ⇒ <c>not_evaluated</c>（既不是 <c>false</c>，也不抛）。</para>
    /// <para>⛔ 信号失败**不得**打断入队循环：记录退化为未评估，调度照跑。</para>
    /// </summary>
    private async Task<(bool IsBacklog, string State)> ResolveBacklogSignalAsync(
        string agentInstanceId,
        CancellationToken ct)
    {
        if (_backlogSignal is null || _portfolioPolicy is null)
            return (false, BacklogNotEvaluated);

        int? enabledCount;
        try
        {
            enabledCount = await _backlogSignal.TryGetEnabledSkillCountAsync(agentInstanceId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SubconsciousWorker] Backlog signal failed agent={AgentInstanceId}",
                agentInstanceId);
            return (false, BacklogNotEvaluated);
        }

        if (enabledCount is not int enabled)
            return (false, BacklogNotEvaluated);

        var isBacklog = enabled > _portfolioPolicy.SoftTarget;
        return (isBacklog, isBacklog ? "true" : "false");
    }
}
