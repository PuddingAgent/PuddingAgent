using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// 潜意识后台消费服务：串行消费 ConsolidationJob 队列并调用编排器。
/// </summary>
public sealed class SubconsciousWorkerService : BackgroundService
{
    private readonly Channel<ConsolidationJob> _channel;
    private readonly ISubconsciousOrchestrator _orchestrator;
    private readonly ILLMConfigResolver? _llmConfigResolver;
    private readonly ILogger<SubconsciousWorkerService> _logger;

    public SubconsciousWorkerService(
        Channel<ConsolidationJob> channel,
        ISubconsciousOrchestrator orchestrator,
        ILogger<SubconsciousWorkerService> logger,
        ILLMConfigResolver? llmConfigResolver = null)
    {
        _channel = channel;
        _orchestrator = orchestrator;
        _llmConfigResolver = llmConfigResolver;
        _logger = logger;
    }

    /// <summary>
    /// 后台执行循环：读取任务并调用潜意识编排器。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SubconsciousWorker] Started.");

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
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
                        _logger.LogWarning(ex,
                            "[SubconsciousWorker] Resolve memory LLM config failed template={Template} workspace={Workspace}, fallback default memory client config",
                            job.AgentTemplateId, job.WorkspaceId);
                    }
                }

                if (mode is not ("off" or "instant" or "deep"))
                    mode = "deep";

                if (mode == "off")
                {
                    _logger.LogInformation(
                        "[SubconsciousWorker] Skip session={SessionId} workspace={WorkspaceId} because mode=off",
                        job.SessionId,
                        job.WorkspaceId);
                    continue;
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SubconsciousWorker] Job failed session={SessionId} workspace={WorkspaceId}",
                    job.SessionId,
                    job.WorkspaceId);
            }
        }

        _logger.LogInformation("[SubconsciousWorker] Stopped.");
    }
}
