using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingCode.Diagnostics;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Execution;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.Diagnostics;
using PuddingPlatform.Services.Snapshot;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.MessageGateway;
using PuddingPlatform.Services.Mcp;
using PuddingPlatform.Services.TaskPlanning;
using PuddingController;
using PuddingController.Data;
using PuddingController.Services;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Events;
using PuddingRuntime.Services.Hooks;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Observability;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.SubAgents;
using PuddingRuntime.Services.Tools;
using PuddingRuntime.Services.TaskPlanning;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingAgent.P2P;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Text;
using PuddingAgent.Connectors;
using PuddingAgent.Services.Events;
using System.Threading.Channels;

namespace PuddingAgent.Services;

public static partial class PuddingServiceCollectionExtensions
{
    private static void AddRuntimeServices(
        WebApplicationBuilder builder,
        PuddingDataPaths dataPaths,
        IConfiguration bootstrapConfiguration)
    {
        // ── Runtime 核心服务 ─────────────────────────────────
        builder.Services.Configure<TaskPlanningOptions>(bootstrapConfiguration.GetSection(TaskPlanningOptions.SectionName));
        builder.Services.AddScoped<ITaskDelegationPolicy, TaskDelegationPolicy>();
        builder.Services.AddSingleton<AgentSessionManager>();
        builder.Services.AddSingleton<InMemoryRuntimeSessionStore>();
        builder.Services.AddSingleton<SessionMemoryStore>();
        builder.Services.AddSingleton<WorkspaceMemoryStore>();
        builder.Services.AddSingleton<MemoryBoundaryService>();
        builder.Services.AddSingleton<MemoryEngine>();
        builder.Services.AddSingleton<IMemoryEngine>(sp => sp.GetRequiredService<MemoryEngine>());
        builder.Services.AddSingleton<IMemoryIndexer, TagTreeIndexer>();
        builder.Services.AddSingleton<IMemoryLibrary, MemoryLibrary>();
        builder.Services.AddSingleton<IMemoryLibraryConvenience>(sp =>
            new MemoryLibraryConvenience(
                sp.GetRequiredService<IMemoryLibrary>(),
                sp.GetService<IMemoryLlmClient>(),
                sp.GetService<ILLMConfigResolver>()));
        builder.Services.AddSingleton<MemoryQualityFilter>();
        builder.Services.AddSingleton<IMemoryLibrarian, MemoryLibrarian>();
        builder.Services.AddSingleton<FactMemoryService>();
        builder.Services.AddScoped<PuddingPlatform.Services.IMemoryLibraryAdminService, PuddingPlatform.Services.MemoryLibraryAdminService>();
        builder.Services.AddSingleton<MemoryRecallService>();
        builder.Services.AddSingleton<IMemoryRecallService>(sp => sp.GetRequiredService<MemoryRecallService>());
        builder.Services.AddSingleton<PuddingCode.Services.JsonlSessionWriter>();
        builder.Services.AddSingleton<PuddingCode.Services.JsonlSessionReader>();
        builder.Services.AddSingleton<ISubconsciousTextProcessingService, SubconsciousTextProcessingService>();
        builder.Services.AddSingleton<AgentConversationLogService>();
        builder.Services.AddSingleton<AgentRawLogMirrorService>();
        builder.Services.AddSingleton<AgentDailySummaryService>();
        builder.Services.AddSingleton<AgentDailySummaryBatchService>();
        builder.Services.AddSingleton<AgentContentSummaryService>();
        builder.Services.AddSingleton<IAgentContentSummaryService>(sp => sp.GetRequiredService<AgentContentSummaryService>());
        builder.Services.AddSingleton<ChatTranscriptWriter>();
        builder.Services.AddSingleton<IChatTranscriptWriter>(sp => sp.GetRequiredService<ChatTranscriptWriter>());
        builder.Services.AddSingleton<IRawSessionLogService>(sp =>
            new RawSessionLogService(
                sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
                sp.GetRequiredService<IFullTextSearchEngine>(),
                sp.GetRequiredService<PuddingDataPaths>()));
        builder.Services.AddSingleton<AgentExecutionGuardrails>();
        builder.Services.AddSingleton<AgentProfileProvider>();
        builder.Services.AddSingleton<IAgentWorkspaceGuard, AgentWorkspaceGuard>();
        builder.Services.AddSingleton<ExecutionControlRegistry>();
        builder.Services.AddSingleton<IRuntimeControlService>(sp =>
        {
            var config = sp.GetRequiredService<PuddingDataPaths>();
            var fileConfigLoader = new PuddingFileConfigLoader(config);
            PuddingFuseConfig? fuseConfig = null;
            try
            {
                var sysConfig = fileConfigLoader.LoadSystemAsync().GetAwaiter().GetResult();
                fuseConfig = sysConfig.Config?.Runtime?.Fuse;
            }
            catch { /* use defaults */ }
            return new RuntimeControlService(
                sp.GetService<ILogger<RuntimeControlService>>(),
                maxErrorsInWindow: fuseConfig?.MaxErrorsInWindow,
                warningThreshold: fuseConfig?.WarningThreshold,
                windowSeconds: fuseConfig?.WindowSeconds);
        });
        builder.Services.AddHostedService<YoloSignalService>();
        builder.Services.AddSingleton<ExecutionJournal>();
        builder.Services.AddSingleton<CompletionPolicy>();
        builder.Services.AddSingleton<SandboxExecutor>();
        builder.Services.AddSingleton<AgentSkillPackageRegistry>();
        builder.Services.AddSingleton<AgentSkillFileService>();
        builder.Services.AddSingleton<ISkillEvolutionTrajectorySource, ConversationSkillEvolutionTrajectorySource>();
        builder.Services.AddSingleton<IAgentSkillEvolutionStore, AgentSkillEvolutionStore>();
        builder.Services.AddSingleton<SkillEvolutionDeduplicationService>();
        builder.Services.AddSingleton<SkillEnforcerService>();
        builder.Services.AddSingleton<SessionSummaryStore>();
        builder.Services.AddSingleton<SessionRedirectStore>();
        builder.Services.AddSingleton<SessionStateStore>();
        builder.Services.AddSingleton<AgentMemorySummaryContextBuilder>();
        builder.Services.AddSingleton<AgentLogRecallService>();
        builder.Services.AddSingleton<SkillPackageDownloadService>();
        builder.Services.AddPuddingAgentTool<HttpFetchSkill>();
        // TerminalSkill: registered via assembly scan (AddPuddingToolsFromAssembly) below
        builder.Services.AddSingleton<FullTextIndexOptions>();
        builder.Services.AddSingleton<IFullTextSearchEngine, LuceneSearchEngine>();
        // HOSTED-DISABLED: builder.Services.AddHostedService<IndexPrebuildService>();
        builder.Services.AddPuddingAgentTool<SearchGrepTool>();

        // ── Smart 工作流工具（角色化子代理）──
        builder.Services.AddPuddingAgentTool<SmartExploreTool>();
        builder.Services.AddPuddingAgentTool<SmartResearchTool>();
        builder.Services.AddPuddingAgentTool<SmartPlanTool>();
        builder.Services.AddPuddingAgentTool<SmartReviewTool>();
        builder.Services.AddPuddingAgentTool<SmartDevelopTool>();
        builder.Services.AddPuddingAgentTool<SmartDeployTool>();
        builder.Services.AddPuddingAgentTool<SmartTestTool>();

        builder.Services.AddPuddingAgentTool<LlmResourcePoolTool>();
        builder.Services.AddPuddingAgentTool<ReadOfficeDocumentTool>();
        builder.Services.AddPuddingAgentTool<TaskManagerTool>();
        builder.Services.AddPuddingTool<SubAgentTool>();
        builder.Services.AddSingleton<SubAgentPool>();
        builder.Services.AddSingleton<ISubAgentPool>(
            services => services.GetRequiredService<SubAgentPool>());

        builder.Services.AddSingleton<MemoryExplorerSubAgent>();
        builder.Services.AddPuddingTool<MemoryLibraryTool>();

        // ── 记忆增强 Tools（P0：save / manage / grep）──────────
        builder.Services.AddPuddingTool<SaveMemoryTool>();

        builder.Services.AddPuddingTool<ManageMemoryTool>();

        builder.Services.AddPuddingTool<GrepMemoryTool>();

        builder.Services.AddPuddingTool<QuerySessionsTool>();

        builder.Services.AddPuddingTool<QuerySessionLogsTool>();

        // ── 消息系统工具：Agent 可通过消息系统双向发送/拉取消息 ───────
        builder.Services.AddSingleton<SendMessageTool>(sp =>
            new SendMessageTool(sp.GetRequiredService<IServiceScopeFactory>()));
        builder.Services.AddPuddingAgentTool<SendMessageTool>();

        builder.Services.AddSingleton<ReceiveMessagesTool>(sp =>
            new ReceiveMessagesTool(sp.GetRequiredService<IServiceScopeFactory>()));
        builder.Services.AddPuddingAgentTool<ReceiveMessagesTool>();

        builder.Services.AddSingleton<ListAgentsTool>(sp =>
            new ListAgentsTool(sp.GetRequiredService<IServiceScopeFactory>()));
        builder.Services.AddPuddingAgentTool<ListAgentsTool>();

        // ── 子代理管理工具（ADR-016 扩展）──────────────────────
        builder.Services.AddPuddingAgentTool<QuerySubAgentsTool>();

        // ── 主动心跳系统工具：sleep / goal_read / goal_update ────
        builder.Services.AddPuddingTool<AgentSleepTool>();

        builder.Services.AddPuddingTool<GoalReadTool>();

        builder.Services.AddPuddingTool<GoalUpdateTool>();

        // ── Agent 自我诊断工具：工具耗时统计 / 缓存健康检查 ──────────
        builder.Services.AddPuddingTool<AgentDiagnosticsTool>();

        // ── 会话上下文手动压缩工具：Agent 自主决定压缩时机 ──────────
        builder.Services.AddPuddingTool<SessionCompactTool>();

        // ── 潜意识管道触发工具：手动触发 Auto-Dream / 经验提取 / 技能改进 ──────────
        builder.Services.AddPuddingTool<SubconsciousTriggerTool>();

        // ── 统一 Tool 注册表：Agent 工具统一通过 IPuddingTool/native registry 暴露 ──────────
        builder.Services.AddPuddingToolsFromAssembly(typeof(PuddingHost.PuddingHostAssemblyMarker).Assembly);
        builder.Services.AddPuddingToolsFromAssembly(typeof(PuddingRuntime.RuntimeServiceExtensions).Assembly);
        builder.Services.AddSingleton<McpConnectionManager>();
        builder.Services.AddSingleton<IMcpConnectionManager>(sp => sp.GetRequiredService<McpConnectionManager>());
        builder.Services.AddSingleton<IWorkspacePuddingToolSource>(sp => sp.GetRequiredService<McpConnectionManager>());
        builder.Services.AddHostedService<McpWorkspaceSkillHostedService>();
        builder.Services.AddPuddingToolRegistry(builder.Configuration);
        builder.Services.AddSingleton<IToolInvocationService, ToolInvocationService>();
        builder.Services.AddSingleton<FileMutationQueue>();
        builder.Services.AddSingleton<FileChunkService>();

        // ── 会话历史查询服务 (Repository → Service 分层) ────
        builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();
        builder.Services.AddSingleton<MessageTopicService>();

        builder.Services.AddSingleton<SkillRuntime>();
        builder.Services.AddSingleton<ITerminalProcessManager, TerminalProcessManager>();
        builder.Services.AddSingleton<ITerminalCommandPolicy, DefaultTerminalCommandPolicy>();
        builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
        builder.Services.AddSingleton<IAgentLoopHook, EmbeddingGenerationHook>();
        builder.Services.AddSingleton<ISessionChunkIndexer, SessionChunkIndexer>();
        builder.Services.Configure<SubconsciousOptions>(
            bootstrapConfiguration.GetSection(SubconsciousOptions.SectionName));

        // ── SessionChunkVectors 存量回填 job（WP-L2d，默认关闭）─────────────
        builder.Services.Configure<SessionChunkBackfillOptions>(
            bootstrapConfiguration.GetSection(SessionChunkBackfillOptions.SectionName));
        if (bootstrapConfiguration.GetValue<bool>(
                $"{SessionChunkBackfillOptions.SectionName}:{nameof(SessionChunkBackfillOptions.Enabled)}"))
        {
            builder.Services.AddHostedService<SessionChunkBackfillService>();
        }

        // ── 潜意识记忆系统（阶段 2：LLM 抽取与后台整合）────────────────
        var subconsciousChannel = Channel.CreateUnbounded<ConsolidationJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        builder.Services.AddSingleton(subconsciousChannel);
        builder.Services.AddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();
        if (bootstrapConfiguration.GetValue<bool>(
                $"{SubconsciousOptions.SectionName}:{nameof(SubconsciousOptions.EnableLegacyConsolidationHook)}"))
        {
            builder.Services.AddSingleton<SubconsciousConsolidationHook>();
            builder.Services.AddSingleton<IAgentLoopHook>(sp => sp.GetRequiredService<SubconsciousConsolidationHook>());
        }
        if (bootstrapConfiguration.GetValue<bool>(
                $"{SubconsciousOptions.SectionName}:{nameof(SubconsciousOptions.EnableWorker)}"))
        {
            builder.Services.AddHostedService<SubconsciousWorkerService>();
        }

        // ── 流式事件总线（可观测性基础设施）────────────────────────────
        builder.Services.AddSingleton<StreamingEventBus>();
        builder.Services.AddSingleton<IStreamingEventBus>(sp => sp.GetRequiredService<StreamingEventBus>());
        builder.Services.AddSingleton<SseEventForwarder>();

        // ── 内部事件系统（ADR-016 V3：纯管道架构）─────────────────────
        // 事件系统只依赖 IEventHandler 接口，不感知 Cron/Connector/Agent 等外部系统。

        // 核心管道组件
        builder.Services.AddSingleton<EventPreprocessor>();
        builder.Services.AddSingleton<IEventPreprocessor>(sp => sp.GetRequiredService<EventPreprocessor>());
        builder.Services.AddSingleton<PriorityEventQueue>();
        builder.Services.AddSingleton<IPriorityEventQueue>(sp => sp.GetRequiredService<PriorityEventQueue>());

        // 事件总线（进程内 pub/sub）
        builder.Services.AddSingleton<InternalEventBus>();
        builder.Services.AddSingleton<IInternalEventBus>(sp => sp.GetRequiredService<InternalEventBus>());
        builder.Services.AddSingleton<IHookPublisher, HookPublisher>();
        builder.Services.AddSingleton<ISubconsciousJobQueue, SubconsciousJobQueue>();
        builder.Services.AddOptions<SubconsciousDiagnosticLogOptions>();
        builder.Services.AddSingleton<ISubconsciousDiagnosticLog, SubconsciousDiagnosticLog>();
        builder.Services.AddSingleton<ISubconsciousRuntimeControl, SubconsciousRuntimeControlService>();
        builder.Services.AddSingleton<SubconsciousJobScheduler>();
        builder.Services.TryAddSingleton<MemoryMaintenancePlanValidator>();
        builder.Services.TryAddSingleton<MemoryWriteCommandValidator>();
        builder.Services.TryAddSingleton<IMemoryWriteCoordinator, MemoryWriteCoordinator>();
        builder.Services.TryAddSingleton<SubconsciousPlanGenerationService>();
        builder.Services.TryAddSingleton<MemoryWikiPageUpdateService>();
        builder.Services.TryAddSingleton<WikiPageWriteEntry>();
        builder.Services.AddSingleton<IdleDetector>();
        builder.Services.AddSingleton<IIdleDetector>(sp => sp.GetRequiredService<IdleDetector>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleDetector>());

        // ── 主动心跳系统（空闲驱动 + 多 Agent 队列 + 尽力模式 + 哲学引导）────
        builder.Services.AddSingleton<AgentWakeQueue>();
        builder.Services.AddSingleton<HeartbeatOrchestrator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatOrchestrator>());
        builder.Services.AddSingleton<IAgentExecutionStateRegistry, AgentExecutionStateRegistry>();
        builder.Services.AddSingleton<IAgentExecutionAvailabilityProvider, DefaultAgentExecutionAvailabilityProvider>();
        builder.Services.AddSingleton<MessageDeliveryDispatcher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MessageDeliveryDispatcher>());
        builder.Services.AddHostedService<ConversationReplyProjectionWorker>();

        // 检查点与订阅管理
        builder.Services.AddSingleton<AgentCheckpointService>();
        builder.Services.AddSingleton<IAgentCheckpointService>(sp => sp.GetRequiredService<AgentCheckpointService>());
        builder.Services.AddPuddingAgentTool<EventSubscriptionTool>();
        builder.Services.AddSingleton<IEventSubscriptionTool>(sp => sp.GetRequiredService<EventSubscriptionTool>());

        // IEventHandler 消费者 — 事件系统的唯一边界
        builder.Services.AddSingleton<IEventHandler, AgentEventHandler>();

        // 入站桥：IInternalEventBus → Preprocessor → PriorityQueue 管道入口
        builder.Services.AddHostedService<EventIngressBridge>();

        // 分发器：PriorityQueue 出队 → IEventHandler.HandleAsync()
        // HOSTED-DISABLED: builder.Services.AddHostedService<EventDispatcher>();
        // Lifecycle hook subscriber: session.compressed -> durable subconscious job.
        // EventIngressBridge deliberately skips hook lifecycle events, so this
        // subscriber is the single owner of memory-maintenance enqueueing.
        builder.Services.AddHostedService<SessionCompressedMemoryMaintenanceHook>();

        builder.Services.AddSingleton<ProviderRateLimiter>();
        builder.Services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
        builder.Services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();

        // ── 统一 LLM 配置服务（data/config/llm.providers.json，唯一来源）──────────
        // 启动时加载一次，不热重载。DB 不再存储 LLM 配置（简化架构）。
        var fileConfigLoader = new PuddingFileConfigLoader(dataPaths);

        var loadResult = fileConfigLoader.LoadLlmProvidersAsync().GetAwaiter().GetResult();
        if (!loadResult.Success)
        {
            var errorSummary = string.Join("\n  - ", loadResult.Errors);
            throw new InvalidOperationException(
                $"LLM providers config validation failed:\n  - {errorSummary}");
        }

        var llmConfigService = new PuddingFileLlmConfigService(loadResult.Config!);
        builder.Services.AddSingleton(fileConfigLoader);
        builder.Services.AddSingleton(llmConfigService);
        builder.Services.AddSingleton<ILlmConfigService>(llmConfigService);

        // ── 文件式 LLM Provider/Model 管理（A方案：Controller → Service → JSON 文件）──
        builder.Services.AddSingleton<LlmProviderFileService>();
        builder.Services.AddSingleton<ILlmResourcePoolService>(sp => sp.GetRequiredService<LlmProviderFileService>());

        // ── 文件式 TTS/ASR 语音 Provider/Model 管理 ──
        builder.Services.AddSingleton<VoiceProviderFileService>();

        // 潜意识/记忆链路只表达“要做哪类 LLM 语义任务”，不再自己实现 provider
        // 解析、密钥注入、协议调用和用量记录。这样后续把子代理、记忆整理或特定
        // Agent 调用封装成工具时，都会经过同一条 LLM 基础设施边界。
        builder.Services.AddSingleton<ILlmProfileResolver, PuddingRuntime.Services.LlmProfileResolver>();
        builder.Services.AddSingleton<ILlmInvocationService, LlmInvocationService>();
        builder.Services.AddSingleton<IMemoryLlmClient, MemoryLlmInvocationClient>();

        // ── 启动环境信息 ──
        builder.Services.AddSingleton(new StartupEnvironmentInfo());
        builder.Services.AddSingleton<SystemPromptBuilder>();
        builder.Services.AddSingleton<ContextAssemblyStore>();
        builder.Services.AddSingleton<ContextUsageSnapshotStore>();
        builder.Services.AddSingleton<IExecutionEnvironmentProvider, DefaultExecutionEnvironmentProvider>();
        builder.Services.AddSingleton<WorkspaceAgentsContextBuilder>();
        builder.Services.AddSingleton<TaskPlannerContextBuilder>();
        builder.Services.AddSingleton<ContextPipeline>();
        builder.Services.AddSingleton<AgentCompactionNotifier>();
        builder.Services.AddSingleton<ContextCompactionOptions>();
        builder.Services.AddSingleton<AgentContextCompactionSummaryGenerator>();
        builder.Services.AddSingleton<ExtractiveContextCompactionSummaryGenerator>();
        builder.Services.AddSingleton<FlashContextCompactionSummaryGenerator>();
        builder.Services.AddSingleton<IContextCompactionSummaryGenerator, CompositeContextCompactionSummaryGenerator>();
        builder.Services.AddSingleton<IContextCompactionService, ContextCompactionService>();
        builder.Services.AddSingleton<ISessionCompactionEventEmitter, PuddingPlatform.Services.SessionCompactionEventEmitter>();
        builder.Services.AddSingleton<ContextWindowManager>();
        // 压缩前冲洗（Pre-Compaction Flush）：压缩前用 Flash LLM 提取关键事实。
        // 此前该组合根未注册此服务，ContextWindowManager 静默跳过冲洗，
        // 导致压缩丢失用户偏好/项目事实。
        builder.Services.AddSingleton<PuddingCode.Runtime.IPreCompactionFlushService, PuddingRuntime.Services.PreCompactionFlushService>();
        builder.Services.AddSingleton<ISessionExecutionGate, SessionExecutionGate>();
        // ── Agent Persona 文件读取器 ──
        builder.Services.AddSingleton(sp =>
        {
            var dataDir = builder.Configuration["Pudding:AgentPersonaDir"]
                ?? dataPaths.AgentTemplatesRoot;
            return new AgentPersonaFileProvider(dataDir,
                sp.GetRequiredService<ILogger<AgentPersonaFileProvider>>());
        });
        builder.Services.AddSingleton<SessionArchiver>();
        builder.Services.AddSingleton<AgentExecutionService>();
        builder.Services.AddSingleton<ITurnExecutor, TurnExecutorAdapter>();
        builder.Services.AddSingleton<IRuntimeAgentDispatcher, RuntimeAgentDispatcher>();

    }

}
