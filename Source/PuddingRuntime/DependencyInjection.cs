using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Orchestration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.GoalMode;
using PuddingRuntime.Services.Hooks;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Orchestration;
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
        // 注意：心跳编排器（HeartbeatOrchestrator）由产品组合根 PuddingHost 注册并运行
        // （PuddingHost/Services/HeartbeatService.cs + PuddingServiceCollectionExtensions.Runtime.cs），
        // 而产品组合根**不调用** AddPuddingRuntime（见 PuddingServiceCollectionExtensions.Runtime.cs）。
        // 此处不得再注册一份，否则一旦组合方式变化会出现两个编排器争抢同一唤醒队列
        // （重复心跳、重复投递）。

        // ── Goal 模式：连续自主任务循环（pi follow-up 注入模式，默认关闭）──
        if (configuration is not null)
            services.Configure<GoalModeOptions>(configuration.GetSection(GoalModeOptions.SectionName));
        else
            services.AddOptions<GoalModeOptions>();
        services.AddSingleton<IGoalModeService, GoalModeService>();

        services.AddSingleton<SessionMemoryStore>();
        services.AddSingleton<WorkspaceMemoryStore>();
        services.AddSingleton<MemoryBoundaryService>();
        services.AddSingleton<MemoryEngine>();
        services.AddSingleton<IMemoryEngine>(sp => sp.GetRequiredService<MemoryEngine>());
        services.AddSingleton<IMemoryIndexer, TagTreeIndexer>();

        // Agent Loop 护栏：优先读系统配置 AgentLoop:Guardrails（可覆盖 MaxToolCallsTotal 等默认值），缺省回退代码默认
        services.AddSingleton(configuration is not null
            ? configuration.GetSection(AgentExecutionGuardrails.SectionName).Get<AgentExecutionGuardrails>() ?? new AgentExecutionGuardrails()
            : new AgentExecutionGuardrails());
        services.AddSingleton<ExecutionControlRegistry>();
        services.AddSingleton<IRuntimeControlService, RuntimeControlService>();
        services.AddSingleton<ISessionExecutionGate, SessionExecutionGate>();
        services.AddSingleton<IAgentExecutionStateRegistry, AgentExecutionStateRegistry>();
        services.AddSingleton<AgentExecutionAdmissionCoordinator>();
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
        services.TryAddSingleton<AgentSkillFileService>();
        services.TryAddSingleton<ISkillEvolutionTrajectorySource, ConversationSkillEvolutionTrajectorySource>();
        services.TryAddSingleton<IAgentSkillEvolutionStore, AgentSkillEvolutionStore>();
        services.TryAddSingleton<SkillEvolutionDeduplicationService>();
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

        // V5：冻结视觉上下文的进程内通道；Agent 执行入口 push、DirectLlmClient 消费。
        services.AddSingleton<FrozenVisionContextAccessor>();
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
        // Jev 决策模型：契约在 PuddingCore，实现在 Services；端点/密钥/模型经
        // IJevDecisionOptionsProvider 注入（默认读 IConfiguration 的 Jev 节）。
        services.AddHttpClient(JevDecisionService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<IJevDecisionOptionsProvider, JevDecisionOptionsProvider>();
        services.AddSingleton<IJevDecisionService, JevDecisionService>();

        services.AddSingleton<IUserPreferenceService, UserPreferenceService>();

        services.AddSingleton<SystemPromptBuilder>();
        services.TryAddSingleton<AgentSkillFileService>();
                services.AddSingleton<SessionSummaryStore>();
        services.AddSingleton<AgentCompactionNotifier>();
        services.AddSingleton<SubconsciousRecallPipeline>();
        services.AddSingleton<AgentMemorySummaryContextBuilder>();
        services.AddSingleton<IExecutionEnvironmentProvider, DefaultExecutionEnvironmentProvider>();
        services.AddSingleton<WorkspaceAgentsContextBuilder>();
        services.AddSingleton<TaskPlannerContextBuilder>();
        services.AddSingleton<ContextPipeline>();
        services.AddSingleton<ContextUsageSnapshotStore>();
        services.AddSingleton<LlmInvocationPurposeAccessor>();
        services.AddSingleton<CroppedLayersProvider>();
        services.AddSingleton<TimeClusterAnalyzer>();
        services.AddSingleton<MemorySnippetRelevanceCalculator>();
        services.AddSingleton<IContextAssemblyService, ContextAssemblyService>();
        services.AddSingleton<ExtractiveContextCompactionSummaryGenerator>();
        services.AddSingleton<FlashContextCompactionSummaryGenerator>();
        services.AddSingleton<AgentContextCompactionSummaryGenerator>();
        services.AddSingleton<ContextCompactionOptions>();
        services.AddSingleton<IPreCompactionFlushService, PreCompactionFlushService>();
        services.AddSingleton<IContextCompactionSummaryGenerator, CompositeContextCompactionSummaryGenerator>();
        // 压缩协调器：per-session 单飞锁 + 冷却限流 + 历史失效回调。
        // 回调延迟解析 ContextWindowManager，避免 ContextCompactionService → CompactionCoordinator → ContextWindowManager 的构造期循环依赖。
        services.AddSingleton(sp => new CompactionCoordinator(
            onHistoryInvalidated: sid => sp.GetRequiredService<ContextWindowManager>().InvalidateHistory(sid)));
        services.AddSingleton<IContextCompactionService, ContextCompactionService>();
        services.AddSingleton<IToolInvocationService, ToolInvocationService>();
        services.AddSingleton<FileMutationQueue>();
        services.AddSingleton<IRuntimeExecutionConfigService, RuntimeExecutionConfigService>();
        services.AddSingleton<IExecutionProgressRegistry, ExecutionProgressRegistry>();
        services.AddSingleton<ISubAgentInvocationService, SubAgentInvocationService>();
        services.TryAddSingleton<DesignCouncilRunStateMachine>();
        services.TryAddSingleton<ISubAgentOrchestrationRunStore, InMemorySubAgentOrchestrationRunStore>();
        services.TryAddSingleton<IDesignCouncilRuntimeService, DesignCouncilRuntimeService>();
        services.AddSingleton<IAgentOrchestrationNodeExecutor, SubAgentOrchestrationNodeExecutor>();
        services.AddSingleton<IAgentOrchestrationNodeExecutor, ImageGenerateOrchestrationNodeExecutor>();
        services.AddSingleton<IAgentOrchestrationNodeExecutor, ImagePreviewOrchestrationNodeExecutor>();
        services.AddHostedService<AgentOrchestrationWorkerService>();
                services.TryAddSingleton<IContextTierPlanner, ContextTierPlanner>();
        services.AddSingleton<ContextWindowManager>();

        // P0-5 步骤 1：Composition 不可变持久化存储（SQLite，落 MemoryDbContext 的 CompositionSnapshots 表）。
        // 宿主已注册 IDbContextFactory<MemoryDbContext>（MemoryDbInitializer 同源）；TryAdd 防宿主重复注册。
                services.TryAddSingleton<ICompositionStore>(sp =>
            new SqliteCompositionStore(sp.GetRequiredService<IDbContextFactory<MemoryDbContext>>()));

        // P0-5 步骤 2：Composition 版本登记表（DI 单例 + 写穿持久化）。
        // ICompositionStore 可能未注册（宿主/测试环境）→ GetService 返回 null → Persistent 降级纯内存。
        services.TryAddSingleton<ICompositionVersionRegistry>(sp =>
            new PersistentCompositionVersionRegistry(
                sp.GetService<ICompositionStore>(),
                sp.GetService<ILogger<PersistentCompositionVersionRegistry>>()));

        // P0-5 步骤 5：Composition 恢复服务（跨 1h 超时 / Core 重启水合工具集合）。
        // ICompositionStore 可能未注册 → GetService 返回 null → 恢复静默降级为空集合，不阻断执行。
        services.TryAddSingleton<CompositionRecoveryService>(sp => new CompositionRecoveryService(
            sp.GetRequiredService<AgentSessionManager>(),
            sp.GetService<ICompositionStore>(),
            sp.GetService<ILogger<CompositionRecoveryService>>()));

        // ADR-092 §13.4：Goal 受控检查与 terminal 工具共用同一准入实现（同一实例）。
        // 先注册具体类型再映射两个接口：接口→接口的隐式转换编译期不允许，且具体类型可实现两个接口。
        services.TryAddSingleton<DefaultTerminalCommandPolicy>();
        services.TryAddSingleton<ITerminalCommandPolicy>(
            sp => sp.GetRequiredService<DefaultTerminalCommandPolicy>());
        services.TryAddSingleton<PuddingCode.Abstractions.ITerminalCommandAdmission>(
            sp => sp.GetRequiredService<DefaultTerminalCommandPolicy>());

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
