using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Hooks;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.TaskPlanning;
using PuddingRuntime.Services.Tools;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;
using System.Threading.Channels;

namespace PuddingRuntime;

public static class RuntimeServiceExtensions
{
    public static IServiceCollection AddPuddingRuntime(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<PuddingCode.Configuration.TaskPlanningOptions>(configuration.GetSection(PuddingCode.Configuration.TaskPlanningOptions.SectionName));
            services.Configure<SubconsciousOptions>(configuration.GetSection(SubconsciousOptions.SectionName));
        }
        else
        {
            services.AddOptions<PuddingCode.Configuration.TaskPlanningOptions>();
            services.AddOptions<SubconsciousOptions>();
        }

        var enableLegacyConsolidationHook = configuration?.GetValue<bool>(
            $"{SubconsciousOptions.SectionName}:{nameof(SubconsciousOptions.EnableLegacyConsolidationHook)}") == true;

        services.AddScoped<ITaskDelegationPolicy, TaskDelegationPolicy>();

        services.AddSingleton<AgentSessionManager>();
        services.AddSingleton<InMemoryRuntimeSessionStore>();
        services.AddSingleton<IdleDetector>();
        services.AddSingleton<IIdleDetector>(sp => sp.GetRequiredService<IdleDetector>());
        services.AddHostedService(sp => sp.GetRequiredService<IdleDetector>());

        // ── 多 Agent 心跳唤醒队列 ──
        services.AddSingleton<AgentWakeQueue>();

        services.AddSingleton<SessionMemoryStore>();
        services.AddSingleton<WorkspaceMemoryStore>();
        services.AddSingleton<MemoryBoundaryService>();
        services.AddSingleton<MemoryEngine>();
        services.AddSingleton<IMemoryEngine>(sp => sp.GetRequiredService<MemoryEngine>());
        services.AddSingleton<IMemoryIndexer, TagTreeIndexer>();

        services.AddSingleton<AgentExecutionGuardrails>();
        services.AddSingleton<ExecutionControlRegistry>();
        services.AddSingleton<IRuntimeControlService, RuntimeControlService>();
        services.AddSingleton<ISessionExecutionGate, SessionExecutionGate>();
        services.AddSingleton<IAgentExecutionStateRegistry, AgentExecutionStateRegistry>();
        services.AddSingleton<ExecutionJournal>();
        services.AddSingleton<CompletionPolicy>();

        services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
        services.TryAddSingleton<IHookPublisher, HookPublisher>();

        var subconsciousChannel = Channel.CreateUnbounded<ConsolidationJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        services.AddSingleton(subconsciousChannel);
        services.AddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();
        services.TryAddSingleton<ISubconsciousJobQueue, SubconsciousJobQueue>();
        services.AddOptions<SubconsciousDiagnosticLogOptions>();
        services.TryAddSingleton<ISubconsciousDiagnosticLog, SubconsciousDiagnosticLog>();
        services.TryAddSingleton<ISubconsciousRuntimeControl, SubconsciousRuntimeControlService>();
        services.TryAddSingleton<SubconsciousJobScheduler>();
        if (enableLegacyConsolidationHook)
        {
            services.AddSingleton<SubconsciousConsolidationHook>();
            services.AddSingleton<IAgentLoopHook>(sp => sp.GetRequiredService<SubconsciousConsolidationHook>());
        }
        services.AddHostedService<SubconsciousWorkerService>();
        services.AddHostedService<SessionCompressedMemoryMaintenanceHook>();

        services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
        services.AddSingleton<ILlmInvocationService, LlmInvocationService>();
        services.AddSingleton<ILlmProfileResolver, Services.LlmProfileResolver>();
        services.AddSingleton<IMemoryLlmClient, MemoryLlmInvocationClient>();
        services.TryAddSingleton<MemoryMaintenancePlanValidator>();
        services.TryAddSingleton<MemoryWriteCommandValidator>();
        services.TryAddSingleton<IMemoryWriteCoordinator, MemoryWriteCoordinator>();
        services.TryAddSingleton<SubconsciousPlanGenerationService>();
        services.TryAddSingleton<MemoryWikiPageUpdateService>();
        services.TryAddSingleton<WikiPageWriteEntry>();
        services.AddSingleton<ISubconsciousTextProcessingService, SubconsciousTextProcessingService>();
                services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();
        services.AddSingleton<ProviderRateLimiter>();

        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<AgentSkillFileService>();
                services.AddSingleton<SessionSummaryStore>();
        services.AddSingleton<AgentCompactionNotifier>();
        services.AddSingleton<SubconsciousRecallPipeline>();
        services.AddSingleton<AgentMemorySummaryContextBuilder>();
        services.AddSingleton<AgentLogRecallService>();
        services.AddSingleton<IExecutionEnvironmentProvider, DefaultExecutionEnvironmentProvider>();
        services.AddSingleton<WorkspaceAgentsContextBuilder>();
        services.AddSingleton<TaskPlannerContextBuilder>();
        services.AddSingleton<ContextPipeline>();
        services.AddSingleton<ContextUsageSnapshotStore>();
        services.AddSingleton<CroppedLayersProvider>();
        services.AddSingleton<TimeClusterAnalyzer>();
        services.AddSingleton<MemorySnippetRelevanceCalculator>();
        services.AddSingleton<IContextAssemblyService, ContextAssemblyService>();
        services.AddSingleton<ExtractiveContextCompactionSummaryGenerator>();
        services.AddSingleton<FlashContextCompactionSummaryGenerator>();
        services.AddSingleton<AgentContextCompactionSummaryGenerator>();
        services.AddSingleton<IPreCompactionFlushService, PreCompactionFlushService>();
        services.AddSingleton<ContextCompactionOptions>();
        services.AddSingleton<IContextCompactionSummaryGenerator, CompositeContextCompactionSummaryGenerator>();
        services.AddSingleton<IContextCompactionService, ContextCompactionService>();
        services.AddSingleton<IToolInvocationService, ToolInvocationService>();
        services.AddSingleton<IRuntimeExecutionConfigService, RuntimeExecutionConfigService>();
        services.AddSingleton<ISubAgentInvocationService, SubAgentInvocationService>();
        services.AddSingleton<ContextWindowManager>();
        services.TryAddSingleton<ITerminalCommandPolicy, DefaultTerminalCommandPolicy>();

        services.AddSingleton<SessionArchiver>();

        services.AddSingleton<AgentExecutionService>();
        services.AddSingleton<IRuntimeAgentDispatcher, RuntimeAgentDispatcher>();
        services.AddSingleton<IAgentExecutionAvailabilityProvider, DefaultAgentExecutionAvailabilityProvider>();
        services.AddSingleton<AuditLogger>();
        services.AddPuddingToolsFromAssembly(typeof(RuntimeServiceExtensions).Assembly);

        // ICodeIndexStore must be registered before AddPuddingCodeIntelligence，
        // so that the Runtime composition root owns the DB path decision.
        services.TryAddSingleton<ICodeIndexStore>(sp =>
        {
            var paths = sp.GetRequiredService<PuddingCode.Configuration.PuddingDataPaths>();
            var dbPath = Path.Combine(paths.DatabasesRoot, "code-index", "code_index.db");
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new SqliteCodeIndexStore(dbPath);
        });

        services.AddPuddingCodeIntelligence();

        services.AddPuddingToolRegistry(configuration);

        return services;
    }
}
