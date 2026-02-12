using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// 潜意识后台消费服务：串行消费 ConsolidationJob 队列并调用编排器。
/// </summary>
public sealed class SubconsciousWorkerService : BackgroundService
{
    private static readonly TimeSpan DurableLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

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
    private readonly ILogger<SubconsciousWorkerService> _logger;
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
        ISubconsciousRuntimeControl? runtimeControl = null)
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
        _logger = logger;
    }

    /// <summary>
    /// 后台执行循环：读取任务并调用潜意识编排器。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("[Startup] SubconsciousWorkerService.ExecuteAsync started");
        _logger.LogInformation(
            "[SubconsciousWorker] Started leaseOwner={LeaseOwner} durableQueue={HasDurableQueue}.",
            _leaseOwner,
            _jobQueue is not null);

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

        _logger.LogInformation("[SubconsciousWorker] Stopped.");
    }

    private async Task ProcessDurableJobAsync(
        SubconsciousJobQueueItem queueItem,
        CancellationToken stoppingToken)
    {
        try
        {
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
                var memoryCfg = await _llmConfigResolver.ResolveMemoryAsync(
                    job.AgentTemplateId,
                    job.WorkspaceId,
                    stoppingToken);

                if (memoryCfg is not null)
                {
                    mode = memoryCfg.SearchMode;

                    if (!string.IsNullOrWhiteSpace(memoryCfg.Endpoint)
                        || !string.IsNullOrWhiteSpace(memoryCfg.ModelId))
                    {
                        memoryLlmConfig = new MemoryLlmConfig(
                            memoryCfg.Endpoint,
                            memoryCfg.ApiKey,
                            memoryCfg.ModelId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[SubconsciousWorker] Resolve memory LLM config failed template={Template} workspace={Workspace}; job will fail without fallback",
                    job.AgentTemplateId, job.WorkspaceId);
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
        // 启动后延迟 10 分钟，先让 Auto-Dream 跑完首次
        await Task.Delay(TimeSpan.FromMinutes(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_runtimeControl?.IsPaused == true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), ct);
                    continue;
                }

                _logger.LogInformation("[PatternExtraction] Triggering periodic pattern scan");
                await _orchestrator.ExtractPatternsAsync("default", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PatternExtraction] Timer loop error");
            }

            // 每 12 小时检查一次
            await Task.Delay(TimeSpan.FromHours(12), ct);
        }
    }

    // ── Auto-Dream 定时循环 ──
    private async Task AutoDreamLoopAsync(CancellationToken ct)
    {
        // 启动后延迟 5 分钟，让系统稳定
        await Task.Delay(TimeSpan.FromMinutes(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_runtimeControl?.IsPaused == true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), ct);
                    continue;
                }

                _logger.LogInformation("[AutoDream] Triggering periodic maintenance");
                await _orchestrator.AutoDreamAsync("default", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoDream] Timer loop error");
            }

            // 每 6 小时检查一次
            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }
    // ── Skill Self-Improvement 定时循环 ──
    private async Task SkillImprovementLoopAsync(CancellationToken ct)
    {
        // 启动后延迟 15 分钟，先让 Auto-Dream 和 PatternExtraction 跑完首次
        await Task.Delay(TimeSpan.FromMinutes(15), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_runtimeControl?.IsPaused == true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), ct);
                    continue;
                }

                _logger.LogInformation("[SkillImprovement] Triggering periodic skill self-improvement");
                await _orchestrator.ImproveSkillsAsync("default", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillImprovement] Timer loop error");
            }

            // 每 4 小时检查一次
            await Task.Delay(TimeSpan.FromHours(4), ct);
        }
    }
}
