using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingCode.Diagnostics;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Orchestration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Scheduling;
using PuddingCode.Services;
using PuddingCode.Storage;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;
using PuddingPlatform.Services.StorageManagement;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Execution;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.Snapshot;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.MessageGateway;
using PuddingPlatform.Services.Mcp;
using PuddingPlatform.Services.Orchestration;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Security;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.TaskPlanning;
using PuddingPlatform.Services.Tasks;
using PuddingPlatform.Services.Files;
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
using PuddingRuntime.Services.Orchestration;
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
using PuddingHost.Hosting;
using PuddingHost.Storage;
using System.Threading.Channels;

namespace PuddingAgent.Services;

public static partial class PuddingServiceCollectionExtensions
{
    private static void AddPlatformServices(
        WebApplicationBuilder builder,
        PuddingDataPaths dataPaths,
        string aspnetcoreEnvironment)
    {
        // ── PlatformApiClient（通过 Controller API 操作控制面）──
        builder.Services.AddTransient<PuddingControllerAddressRewriteHandler>();
        builder.Services
            .AddHttpClient<PlatformApiClient>(client =>
            {
                var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:5000";
                client.BaseAddress = new Uri(endpoint);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<PuddingControllerAddressRewriteHandler>();

        // ── Workspace 业务层 ──────────────────────────────────
        builder.Services.AddScoped<WorkspaceBusinessService>();
        builder.Services.AddSingleton<MinioStorageService>();
        builder.Services.AddSingleton<SessionEventHub>();
        builder.Services.AddSingleton<SessionStateManager>();
        builder.Services.AddSingleton<ISessionStateManager>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionEventWriter>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionEventStream, SessionEventStreamService>();
        builder.Services.AddSingleton<ISessionProjectionStore, SessionProjectionStore>();
        builder.Services.AddSingleton<StreamMetrics>();
        builder.Services.AddSingleton<ICommittedEventSignal, CommittedEventSignal>();

        // ── Agent Orchestration V2 contracts + persistence（ADR-070）──
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IAgentOrchestrationComponentRegistry>(
            AgentOrchestrationComponentRegistry.Default);
        builder.Services.TryAddSingleton<AgentOrchestrationGraphCompiler>();
        builder.Services.TryAddSingleton<AgentOrchestrationCommittedEventSignal>();
        builder.Services.TryAddSingleton<IAgentOrchestrationCommittedEventSignal>(sp =>
            sp.GetRequiredService<AgentOrchestrationCommittedEventSignal>());
        builder.Services.TryAddSingleton<SqliteAgentOrchestrationStore>();
        builder.Services.TryAddSingleton<IAgentOrchestrationStore>(sp =>
            sp.GetRequiredService<SqliteAgentOrchestrationStore>());
        builder.Services.TryAddSingleton<IAgentOrchestrationQueryStore>(sp =>
            sp.GetRequiredService<SqliteAgentOrchestrationStore>());
        builder.Services.TryAddSingleton<AgentOrchestrationEventFollower>();
        builder.Services.TryAddSingleton<AgentOrchestrationAuthoringService>();
        builder.Services.TryAddSingleton<AgentOrchestrationHttpHookService>();
        builder.Services.TryAddSingleton<AgentOrchestrationManualRunService>();
        // PuddingHost owns the product composition root and does not call
        // RuntimeServiceExtensions.AddPuddingRuntime. Register orchestration executors and the
        // durable worker here as well, otherwise activated nodes remain Ready in the real product.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentOrchestrationNodeExecutor, SubAgentOrchestrationNodeExecutor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentOrchestrationNodeExecutor, ImageGenerateOrchestrationNodeExecutor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentOrchestrationNodeExecutor, ImagePreviewOrchestrationNodeExecutor>());
        builder.Services.AddHostedService<AgentOrchestrationWorkerService>();

            // ── Execution Lease + Journal + Control（ADR-059）─────────
        builder.Services.AddSingleton<IExecutionLeaseStore, SqliteExecutionLeaseStore>();
        builder.Services.AddSingleton<IExecutionJournal, SqliteExecutionJournal>();
        builder.Services.AddSingleton<IControlInbox, SqliteControlInbox>();
        builder.Services.AddSingleton<IExecutionControlService, ExecutionControlService>();
        builder.Services.AddSingleton<IExecutionCommandReader, ExecutionCommandReader>();
        builder.Services.AddSingleton<IGatewayCommandRouteReader, GatewayCommandRouteReader>();
        builder.Services.AddSingleton<PlatformReadinessProbe>();

            // ── Conversation 命令受理（ADR-059）─────────────
            builder.Services.AddScoped<ISubmitTurnHandler, SubmitTurnHandler>();
            builder.Services.AddScoped<IConversationNotificationStore, ConversationNotificationStore>();
            builder.Services.AddScoped<IRequestTurnCancellationHandler, RequestTurnCancellationHandler>();
            builder.Services.AddScoped<ICreateSteeringHandler, CreateSteeringHandler>();
            builder.Services.AddScoped<IRequestCompactionHandler, RequestCompactionHandler>();
            builder.Services.AddScoped<ICompactionSessionSuccessor, CompactionSessionSuccessor>();
            builder.Services.AddScoped<IConversationAcceptanceStore, ConversationAcceptanceStore>();
            builder.Services.AddScoped<ISystemStatusSnapshotProvider, SystemStatusSnapshotProvider>();
            builder.Services.AddScoped<ISystemCommandHandler, SystemCommandHandler>();

            // ── Goal 持久控制面（ADR-074 G1/G2：持久 Goal + durable 单轮续行）──
            builder.Services.AddSingleton<GoalOutboxSignal>();
            builder.Services.AddSingleton<GoalOutboxStore>();
            builder.Services.AddSingleton<GoalSettlementStore>();
            builder.Services.AddSingleton<IGoalIterationVerifier, ConservativeGoalIterationVerifier>();
            builder.Services.AddScoped<GoalRunStore>();
            builder.Services.AddScoped<IGoalCommandService, GoalCommandService>();
            builder.Services.AddScoped<IGoalQueryService, GoalQueryService>();
            builder.Services.AddScoped<GoalRestartReconciler>();
            builder.Services.Configure<GoalRunOptions>(
                builder.Configuration.GetSection(GoalRunOptions.SectionName));
            builder.Services.AddHostedService<GoalContinuationWorker>();
            builder.Services.AddHostedService<GoalSettlementWorker>();
            builder.Services.TryAddSingleton(TimeProvider.System);

            // ── Conversation Event Store（ADR-057 Phase 2）────
            builder.Services.AddSingleton<IConversationEventStore, ConversationEventStore>();
            builder.Services.AddSingleton<ConversationCatalogWriter>();
            builder.Services.AddSingleton<ConversationProjector>();
            builder.Services.AddSingleton<ConversationCatalogBackfillService>();
            builder.Services.AddHostedService<ConversationProjectionWorker>();
            builder.Services.AddSingleton<ChatTelemetryRecorder>();

            // ── Execution Kernel（ADR-059）─────────────────
            builder.Services.AddScoped<IExecutionRunCoordinator, ExecutionRunCoordinator>();
            builder.Services.AddSingleton<IAgentExecutionSnapshotFactory, AgentExecutionSnapshotFactory>();

        // ── Repository pattern (EF Core → Repository → Service) ──
        builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        // Task Ledger（TB-02 SQLite Task Store）
        builder.Services.AddScoped<SqliteWorkspaceTaskStore>();
        builder.Services.AddScoped<ITaskStore>(sp => sp.GetRequiredService<SqliteWorkspaceTaskStore>());
        // Provider File 引用 Store（ADR-077 V3-S2b-1：llm_provider_file_refs 持久化地基）
        builder.Services.AddSingleton<SqliteProviderFileRefStore>();
        builder.Services.AddSingleton<IFileRefStore>(sp => sp.GetRequiredService<SqliteProviderFileRefStore>());
        // Agent 状态感知、单 Agent 自动工作槽与 Task 依赖（Auto 仍由 feature flag 默认关闭）。
        builder.Services.AddSingleton<AgentAvailabilityProjectionStore>();
        builder.Services.AddSingleton<IAgentAvailabilityProjectionStore>(sp =>
            sp.GetRequiredService<AgentAvailabilityProjectionStore>());
        builder.Services.AddSingleton<AgentExecutionReservationStore>();
        builder.Services.AddSingleton<IAgentExecutionReservationStore>(sp =>
            sp.GetRequiredService<AgentExecutionReservationStore>());
        builder.Services.AddSingleton<TaskDependencyStore>();
        builder.Services.AddSingleton<ITaskDependencyStore>(sp =>
            sp.GetRequiredService<TaskDependencyStore>());
        builder.Services.AddSingleton<IExecutionWindowResolver, ConservativeExecutionWindowResolver>();
        var taskBoundGoalConfig = builder.Configuration
            .GetSection(TaskBoundGoalOptions.SectionName)
            .Get<TaskBoundGoalOptions>() ?? new TaskBoundGoalOptions();
        var goalRunConfig = builder.Configuration
            .GetSection(GoalRunOptions.SectionName)
            .Get<GoalRunOptions>() ?? new GoalRunOptions();
        builder.Services.AddOptions<TaskBoundGoalOptions>()
            .Bind(builder.Configuration.GetSection(TaskBoundGoalOptions.SectionName))
            .Validate(
                options => TaskBoundGoalOptions.Validate(options).Count == 0,
                "Invalid TaskBoundGoals configuration.")
            .ValidateOnStart();
        builder.Services.AddOptions<TaskAutoDispatchOptions>()
            .Bind(builder.Configuration.GetSection(TaskAutoDispatchOptions.SectionName))
            .Validate(
                options => TaskAutoDispatchOptions.Validate(
                    options, taskBoundGoalConfig, goalRunConfig).Count == 0,
                "Invalid TaskAutoDispatch configuration or disabled Goal prerequisite.")
            .ValidateOnStart();
        builder.Services.AddSingleton<TaskGoalDispatchTransactionStore>();
        builder.Services.AddSingleton<ITaskGoalDispatchTransactionStore>(sp =>
            sp.GetRequiredService<TaskGoalDispatchTransactionStore>());
        builder.Services.AddSingleton<TaskAutoDispatchEvaluator>();
        builder.Services.AddSingleton<ITaskAutoDispatchEvaluator>(sp =>
            sp.GetRequiredService<TaskAutoDispatchEvaluator>());
        builder.Services.AddHostedService<TaskAutoDispatchWorker>();
        // Task Command 服务（TB-03：状态机校验 + CAS + Assignment + AppendEvent 原子语义）
        builder.Services.AddScoped<TaskCommandService>();
        // Task Agent 命令服务（TB-06：ClaimAsync / ApplyDispositionAsync / ListMineAsync / GetAsync）。
        // task_* 原生工具由统一 Tool Registry 按 Singleton 托管，因此这里也必须是 Singleton。
        // 服务自身无请求态，只持有 Singleton DbContextFactory/Fence；每次调用都会创建并释放独立 DbContext。
        builder.Services.AddSingleton<TaskAgentCommandService>();
        builder.Services.AddSingleton<ITaskAgentCommandService>(sp => sp.GetRequiredService<TaskAgentCommandService>());
        // Task Admin 命令服务（TB-09：管理者视角跨 Agent CRUD + 命令）。
        // 与 TaskAgentCommandService 同为 Singleton 工具消费；构造仅依赖 Singleton DbContextFactory。
        builder.Services.AddSingleton<WorkspaceTaskAdminService>();
        builder.Services.AddSingleton<IWorkspaceTaskAdminService>(sp => sp.GetRequiredService<WorkspaceTaskAdminService>());
        builder.Services.Configure<WorkspaceTaskFeatureOptions>(_ => { });
        // Task Dispatch Outbox + Dispatcher（TB-05：手工派发闭环）
        builder.Services.AddScoped<TaskDispatchOutboxStore>();
        builder.Services.AddSingleton<IWorkAdmissionFence, ManualAlwaysAllowFence>();
        builder.Services.Configure<TaskDispatcherOptions>(_ => { });
        builder.Services.AddHostedService<TaskDispatcher>();
        builder.Services.AddSingleton<ChatMessageRepository>();
        builder.Services.AddSingleton<IChatMessageRepository>(sp => sp.GetRequiredService<ChatMessageRepository>());
        builder.Services.AddSingleton<ICompactionChatMessageStore>(sp => sp.GetRequiredService<ChatMessageRepository>());
        builder.Services.AddSingleton<ITokenUsageEventRepository, TokenUsageEventRepository>();
        builder.Services.AddSingleton<IChatMessageBackfillSource, BackfillChatMessageSource>();
        builder.Services.AddSingleton<ISkillEvolutionDataAccess, SkillEvolutionDataAccess>();

        // ── User/Team/Workspace member repositories ──
        builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
        builder.Services.AddScoped<ITeamRepository, TeamRepository>();
        builder.Services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();
        builder.Services.AddHostedService<ChatExecutionWorker>();
        builder.Services.AddSingleton<SubAgentManager>();
        builder.Services.AddSingleton<ISubAgentManager>(sp => sp.GetRequiredService<SubAgentManager>());
        builder.Services.AddSingleton<ISubAgentRunStore, FileSubAgentRunStore>();
        builder.Services.AddSingleton<ISubAgentDiagnosticsService, SubAgentDiagnosticsService>();
        builder.Services.AddHostedService<SubAgentConversationProjectionWorker>();
        builder.Services.TryAddSingleton<IRuntimeExecutionConfigService, RuntimeExecutionConfigService>();
        builder.Services.AddHostedService<SubAgentTransientDirectoryGcService>();
        builder.Services.TryAddSingleton<IExecutionProgressRegistry, ExecutionProgressRegistry>();
        builder.Services.TryAddSingleton<ISubAgentInvocationService, SubAgentInvocationService>();
        builder.Services.TryAddSingleton<DesignCouncilRunStateMachine>();
        builder.Services.TryAddSingleton<ISubAgentOrchestrationRunStore, InMemorySubAgentOrchestrationRunStore>();
        builder.Services.TryAddSingleton<IDesignCouncilRuntimeService, DesignCouncilRuntimeService>();
        builder.Services.AddSingleton<IRuntimeTraceAccessor, AmbientRuntimeTraceAccessor>();
        builder.Services.AddSingleton<RuntimeActivitySink>();
        builder.Services.AddSingleton<IRuntimeActivitySink>(sp => sp.GetRequiredService<RuntimeActivitySink>());
        builder.Services.AddSingleton<TelemetryMetricSink>();
        builder.Services.AddSingleton<ITelemetryMetricSink>(sp => sp.GetRequiredService<TelemetryMetricSink>());
        builder.Services.AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
        builder.Services.AddSingleton<IConversationDiagnosticEventProjector, ConversationDiagnosticEventProjector>();
        builder.Services.AddSingleton<IExecutionLifecycleRecorder, RuntimeActivityExecutionLifecycleRecorder>();
        builder.Services.AddSingleton(new SessionTimelineRecorderOptions
        {
            Enabled = IsDiagnosticsTimelineEnabled(aspnetcoreEnvironment),
        });
        builder.Services.AddSingleton<SessionTimelineRecorder>();
        builder.Services.AddSingleton<ISessionTimelineRecorder>(sp => sp.GetRequiredService<SessionTimelineRecorder>());
        builder.Services.AddSingleton<ISessionOutputWriter, SessionOutputWriter>();
        builder.Services.AddScoped<RuntimeTimelineQueryService>();
        builder.Services.AddScoped<SessionBenchmarkDiagnosticsService>();
        builder.Services.AddScoped<IAgentRunProjectionService, AgentRunProjectionService>();
        builder.Services.AddScoped<IAgentConversationProjectionService, AgentConversationProjectionService>();
        builder.Services.AddSingleton<VisionArtifactStorageService>();
        builder.Services.AddSingleton<IVisualArtifactReferenceResolver>(sp => sp.GetRequiredService<VisionArtifactStorageService>());
        builder.Services.AddSingleton<IVisualArtifactLocalFileResolver>(sp => sp.GetRequiredService<VisionArtifactStorageService>());
        // ADR-077：image_reader 按需取图（URL/绝对路径/artifact 引用 → Workspace Artifact）。
        builder.Services.AddHttpClient("image_reader", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddSingleton<PuddingAgent.Tools.ImageReaderSourceResolver>();
        // ── External Access Token（ADR-075 第三方任务看板认证）─────────
        builder.Services.AddSingleton<ExternalTaskApiOptionsProvider>();
        builder.Services.AddSingleton<ExternalAccessTokenStore>();
        builder.Services.AddSingleton<ExternalAccessTokenService>();
        // AddHostedService 同时注册 Singleton 实例；认证 Handler 通过
        // RequestServices.GetService 获取并投递 last-used 合并写。
        builder.Services.AddHostedService<ExternalAccessTokenUsageCoalescer>();

        // ── External Task API v1（ADR-075 P2 基本功能：评价 + 幂等 + 门控）──
        builder.Services.AddSingleton<TaskEvaluationStore>();
        builder.Services.AddSingleton<ExternalApiIdempotencyStore>();
        builder.Services.AddSingleton<PuddingPlatform.Controllers.External.V1.ExternalApiGateFilter>();

        // ── 用户头像上传落盘到 wwwroot/user-avatars/，由静态文件中间件对外提供服务。──
        builder.Services.AddSingleton<UserAvatarStorageService>();
        builder.Services.AddHttpClient(
                RemoteImageArtifactImportService.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(
                RemoteImageArtifactImportService.CreatePublicNetworkHandler);
        builder.Services.AddSingleton<RemoteImageArtifactImportService>();
        builder.Services.AddHttpClient("ImageGeneration", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(3);
        });
        builder.Services.AddSingleton<IImageGenerationProvider, VolcengineArkImageGenerationProvider>();
        builder.Services.AddSingleton<IImageGenerationService, ImageGenerationService>();
        builder.Services.AddSingleton<AudioArtifactStorageService>();
        builder.Services.AddSingleton<IAudioArtifactReferenceResolver>(sp => sp.GetRequiredService<AudioArtifactStorageService>());
        builder.Services.AddSingleton<IAudioArtifactLocalFileResolver>(sp => sp.GetRequiredService<AudioArtifactStorageService>());
        builder.Services.AddSingleton<FeishuInboundMessageMapper>();
        builder.Services.AddSingleton<IVisualArtifactResolver, VisualArtifactResolverBridge>();
        builder.Services.AddSingleton<IAudioArtifactResolver, AudioArtifactResolverBridge>();
        builder.Services.AddScoped<SessionTitleService>();
        builder.Services.AddScoped<TokenCostService>();
        builder.Services.AddScoped<IVisualReasoningService, DefaultVisualReasoningService>();
        builder.Services.AddHttpClient("DashScopeVisualReasoning");

        // ── LLM Provider 余额查询（多服务商计费适配器注册表）──
        // DeepSeek 适配器：GET {baseUrl}/user/balance；新服务商实现 ILlmBalanceProvider 后在此注册。
        builder.Services.AddHttpClient(
            DeepSeekLlmBalanceProvider.BalanceHttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton<ILlmBalanceProvider, DeepSeekLlmBalanceProvider>();
        builder.Services.AddScoped<IVisualReasoningProvider>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var endpoint = config["DashScope:VisualReasoningEndpoint"]
                ?? config["DashScope__VisualReasoningEndpoint"]
                ?? "https://dashscope.aliyuncs.com/compatible-mode/v1";
            var apiKey = config["DashScope:ApiKey"]
                ?? config["DashScope__ApiKey"]
                ?? Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY")
                ?? string.Empty;
            return new DashScopeVisualReasoningProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("DashScopeVisualReasoning"),
                new DashScopeVisualReasoningOptions(endpoint, apiKey));
        });
        builder.Services.AddPuddingController();

        // ── 代码智能索引与查询服务 ─────────────────────────
        // ICodeIndexStore must be registered before AddPuddingCodeIntelligence,
        // so the composition root owns the DB path decision.
        builder.Services.TryAddSingleton<ICodeIndexStore>(sp =>
        {
            var dbPath = Path.Combine(dataPaths.DatabasesRoot, "code-index", "code_index.db");
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new SqliteCodeIndexStore(dbPath);
        });
        builder.Services.AddPuddingCodeIntelligence();

        // ── EF Core / 数据库 ──────────────────────────────────
        var connStr = builder.Configuration.GetConnectionString("Default")
            ?? $"Data Source={Path.Combine(dataPaths.DatabasesRoot, "pudding_platform.db")}";
        var controllerConnStr = builder.Configuration.GetConnectionString("Controller")
            ?? $"Data Source={Path.Combine(dataPaths.DatabasesRoot, "pudding_controller.db")}";
        var memoryConnStr = builder.Configuration.GetConnectionString("Memory")
            ?? $"Data Source={Path.Combine(dataPaths.DatabasesRoot, "pudding_memory.db")}";
        builder.Services.AddSingleton<PlatformSqliteConnectionInterceptor>();
        builder.Services.AddSingleton<DbContextOptions<PlatformDbContext>>(sp =>
        {
            var opt = new DbContextOptionsBuilder<PlatformDbContext>();
            opt.UseSqlite(connStr);
            opt.AddInterceptors(sp.GetRequiredService<PlatformSqliteConnectionInterceptor>());
            opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            return opt.Options;
        });
        builder.Services.AddSingleton<IDbContextFactory<PlatformDbContext>, PlatformDbContextFactory>();
        builder.Services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());

        // Core-owned database/index analysis and explicit previewed cleanup API.
        builder.Services.AddSingleton<StorageMaintenanceService>();
        builder.Services.AddSingleton<IStorageMaintenanceService>(sp =>
            sp.GetRequiredService<StorageMaintenanceService>());

        // ── ADR-076 存储管理：语义目录 / 快照采样 / 单 writer 协调器 / 策略 ──
        // StorageMaintenanceCoordinator 是唯一在线维护写 hosted service：
        // 自动保留调度（RetentionPruningService）、Web 人工清理与旧 /databases 端点
        // 的全部删除都经它串行执行；StorageInventorySampler 是只读 reader 不占 writer。
        builder.Services.AddSingleton<StorageRetentionPolicyService>();
        builder.Services.AddSingleton<StorageInventorySnapshotStore>();
        builder.Services.AddHostedService<StorageInventorySampler>();
        builder.Services.AddSingleton<StorageMaintenanceJobStore>();
        builder.Services.AddSingleton<StorageCleanupExecutor>();
        builder.Services.AddSingleton<StorageMaintenanceCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageMaintenanceCoordinator>());
        builder.Services.AddSingleton<IStorageDerivedTargetHandler, CodeIndexScopeCleanupHandler>();
        builder.Services.AddSingleton<IStorageDerivedTargetHandler, RedundantIndexCleanupHandler>();

        // ── platform.db 自动保留调度（ADR-076：调度壳，写全部经协调器）────
        // 策略来自 <DataRoot>/config/system.json storageManagement 段；
        // 证据流表（conversation_events）DELETE 前先归档到 WORM jsonl。
        builder.Services.AddSingleton<RetentionArchiveWriter>();
        builder.Services.AddHostedService<RetentionPruningService>();

        // ── 双向消息系统（事件系统之上的聊天室/Agent 消息抽象）──────────
        builder.Services.AddScoped<IMessageRouter, MessageRouter>();
        builder.Services.AddScoped<MessageFabricStore>();
        builder.Services.AddScoped<IMessageInbox>(sp => sp.GetRequiredService<MessageFabricStore>());
        builder.Services.AddScoped<WorkspaceRoomParticipantProvider>();
        builder.Services.AddScoped<MessageQueueProjectionService>();
        builder.Services.AddScoped<IMessageSystem, MessageSystem>();
        builder.Services.AddScoped<TaskPlanStore>();
        builder.Services.AddScoped<ITaskPlanStore>(sp => sp.GetRequiredService<TaskPlanStore>());

        builder.Services.AddSingleton<Sm2JwtSigner>();
        builder.Services.AddSingleton<IKeyVaultService, KeyVaultService>();
        // ── DB 种子服务（启动时从 default-data/ 导入配置到 DB）──
        builder.Services.AddSingleton<DataSeedService>();
        // ── Agent 模板文件服务（模板 manifest 读写 + 头像解析）──
        builder.Services.AddSingleton(sp => new AgentTemplateFileService(
            sp.GetRequiredService<PuddingDataPaths>(),
            sp.GetRequiredService<AgentAvatarCatalog>(),
            sp.GetRequiredService<ILogger<AgentTemplateFileService>>(),
            Path.Combine(AppContext.BaseDirectory, "default-data", "agent-template-presets")));
        // ── Hermes 基准试题文件服务（运行配置 JSON 读写）──
        builder.Services.AddSingleton<BenchmarkCaseCatalogService>();
        builder.Services.AddSingleton<BenchmarkCaseFileService>();
        builder.Services.AddSingleton<BenchmarkWorkspaceSeedService>();
        builder.Services.AddSingleton<BenchmarkRunService>();
        builder.Services.AddScoped<BenchmarkEvaluationService>();
        // ── Workspace Agent 实例配置与运行目录写入权威 ──
        builder.Services.AddSingleton<WorkspaceAgentFileService>();
        builder.Services.AddSingleton<IWorkspaceAgentCatalog>(sp => sp.GetRequiredService<WorkspaceAgentFileService>());
        builder.Services.AddSingleton<IAgentMainSessionBinder>(sp =>
            sp.GetRequiredService<WorkspaceAgentFileService>());
        builder.Services.AddSingleton<IAgentChannelBinder>(sp =>
            sp.GetRequiredService<WorkspaceAgentFileService>());
        builder.Services.AddSingleton<ChannelConfigurationFileService>();
        builder.Services.AddSingleton<AgentManifestCatalog>();
        builder.Services.AddSingleton<MessageGatewayIngress>();
        builder.Services.AddSingleton<IMessageGatewayIngress>(
            sp => sp.GetRequiredService<MessageGatewayIngress>());
        builder.Services.AddSingleton<IAgentSelfMaintenanceService>(
            sp => sp.GetRequiredService<WorkspaceAgentFileService>());
        builder.Services.AddSingleton<IWorkspaceAgentQueryService, WorkspaceAgentQueryServiceAdapter>();
        builder.Services.AddSingleton<IAgentRosterProvider, WorkspaceAgentRosterProvider>();
        builder.Services.AddSingleton<IWorkspaceAuditAgentProvider>(sp =>
        {
            var fileService = sp.GetRequiredService<WorkspaceAgentFileService>();
            return new WorkspaceAuditAgentProviderAdapter(
                async (workspaceId, ct) =>
                {
                    var candidate = await fileService.FindFirstEnabledAuditAgentAsync(workspaceId, ct);
                    if (candidate is null) return null;
                    return new WorkspaceAuditAgentProfile
                    {
                        WorkspaceId = candidate.WorkspaceId,
                        AgentInstanceId = candidate.AgentInstanceId,
                        AgentTemplateId = candidate.AgentTemplateId,
                        ProviderId = candidate.ProviderId,
                        ProfileId = candidate.ProfileId,
                        ModelId = candidate.ModelId,
                    };
                });
        });

        // ── 重要记忆文件服务 ──
        builder.Services.AddSingleton<ImportantMemoryService>();

        // ── 遗留 DB-backed Template/LLM 服务（逐步废弃）────────────────────
        builder.Services.AddSingleton<AgentTemplateProvider>();
        builder.Services.AddSingleton<IAgentTemplateProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());
        builder.Services.AddSingleton<IWorkspaceProfileProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());
        builder.Services.AddSingleton<AgentLLMConfigResolver>();
        builder.Services.AddSingleton<ILLMConfigResolver>(sp => sp.GetRequiredService<AgentLLMConfigResolver>());
        builder.Services.AddScoped<AgentRuntimeProfileResolver>();
        builder.Services.AddScoped<IAgentRuntimeProfileResolver>(sp => sp.GetRequiredService<AgentRuntimeProfileResolver>());
        builder.Services.AddScoped<IContextCapacityResolver, ContextCapacityResolver>();
        builder.Services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();

        // ── ADR-043：Token 使用统计闭环 ────────────────────────────────
        builder.Services.AddSingleton<TokenUsageNormalizer>();
        builder.Services.AddSingleton<TokenUsageRecorder>();
        builder.Services.AddSingleton<ITokenUsageRecorder>(sp => sp.GetRequiredService<TokenUsageRecorder>());
        builder.Services.AddSingleton<LlmGatewayUsageRecorder>();
        builder.Services.AddSingleton<ILlmGatewayUsageRecorder>(sp => sp.GetRequiredService<LlmGatewayUsageRecorder>());
        builder.Services.AddSingleton<TokenUsageDailyAggregateService>();
        builder.Services.AddSingleton<ContextLayerDailyRollupService>();
        builder.Services.AddSingleton<TokenUsageRebuildService>();
        builder.Services.AddSingleton<SessionSteeringService>();
        builder.Services.AddSingleton<ISessionSteeringService>(sp => sp.GetRequiredService<SessionSteeringService>());
        builder.Services.AddScoped<CacheDiagnosticsService>();
        builder.Services.AddScoped<ICacheDiagnosticsService>(sp => sp.GetRequiredService<CacheDiagnosticsService>());

        builder.Services.AddDbContextFactory<ControllerDbContext>(opt =>
        {
            opt.UseSqlite(controllerConnStr);
            opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        }, ServiceLifetime.Singleton);

        builder.Services.AddDbContextFactory<MemoryDbContext>(opt =>
        {
            opt.UseSqlite(memoryConnStr);
            opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        }, ServiceLifetime.Singleton);

        builder.Services.AddDbContextFactory<MemoryLibraryDbContext>(opt =>
        {
            opt.UseSqlite(memoryConnStr);
            opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        }, ServiceLifetime.Singleton);

        // ── Session（用于 Auth API 的轻量登录态）──────────────
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromHours(8);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

    }


    private static bool IsDiagnosticsTimelineEnabled(string aspnetcoreEnvironment)
    {
        var value = Environment.GetEnvironmentVariable("PUDDING_DIAGNOSTICS_TIMELINE");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        return aspnetcoreEnvironment.Equals("Development", StringComparison.OrdinalIgnoreCase);
    }
}
