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
    private static void AddPlatformServices(
        WebApplicationBuilder builder,
        PuddingDataPaths dataPaths,
        string aspnetcoreEnvironment)
    {
        // ── PlatformApiClient（通过 Controller API 操作控制面）──
        builder.Services.AddHttpClient<PlatformApiClient>(client =>
        {
            var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:5000";
            client.BaseAddress = new Uri(endpoint);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Workspace 业务层 ──────────────────────────────────
        builder.Services.AddScoped<WorkspaceBusinessService>();
        builder.Services.AddSingleton<MinioStorageService>();
        builder.Services.AddSingleton<SessionEventHub>();
        builder.Services.AddSingleton<SessionStateManager>();
        builder.Services.AddSingleton<ISessionStateManager>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionEventWriter>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionEventReader>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionHeadNotifier>(sp => sp.GetRequiredService<SessionStateManager>());
        builder.Services.AddSingleton<ISessionEventStream, SessionEventStreamService>();
        builder.Services.AddSingleton<ISessionProjectionStore, SessionProjectionStore>();
        builder.Services.AddSingleton<StreamMetrics>();
        builder.Services.AddSingleton<ICommittedEventSignal, CommittedEventSignal>();

            // ── Execution Lease + Journal + Control（ADR-059）─────────
        builder.Services.AddSingleton<IExecutionLeaseStore, SqliteExecutionLeaseStore>();
        builder.Services.AddSingleton<IExecutionJournal, SqliteExecutionJournal>();
        builder.Services.AddSingleton<IControlInbox, SqliteControlInbox>();
        builder.Services.AddSingleton<IExecutionControlService, ExecutionControlService>();
        builder.Services.AddSingleton<IExecutionCommandReader, ExecutionCommandReader>();
        builder.Services.AddSingleton<PlatformReadinessProbe>();

            // ── Conversation 命令受理（ADR-059）─────────────
            builder.Services.AddScoped<ISubmitTurnHandler, SubmitTurnHandler>();
            builder.Services.AddScoped<IRequestTurnCancellationHandler, RequestTurnCancellationHandler>();
            builder.Services.AddScoped<ICreateSteeringHandler, CreateSteeringHandler>();
            builder.Services.AddScoped<IRequestCompactionHandler, RequestCompactionHandler>();
            builder.Services.AddScoped<ICompactionSessionSuccessor, CompactionSessionSuccessor>();
            builder.Services.AddScoped<IConversationAcceptanceStore, ConversationAcceptanceStore>();
            builder.Services.AddScoped<ISystemStatusSnapshotProvider, SystemStatusSnapshotProvider>();
            builder.Services.AddScoped<ISystemCommandHandler, SystemCommandHandler>();

            // ── Conversation Event Store（ADR-057 Phase 2）────
            builder.Services.AddSingleton<IConversationEventStore, ConversationEventStore>();
            builder.Services.AddSingleton<ConversationProjector>();
            builder.Services.AddHostedService<ConversationProjectionWorker>();
            builder.Services.AddSingleton<ChatTelemetryRecorder>();

            // ── Execution Kernel（ADR-059）─────────────────
            builder.Services.AddScoped<IExecutionRunCoordinator, ExecutionRunCoordinator>();
            builder.Services.AddSingleton<IAgentExecutionSnapshotFactory, AgentExecutionSnapshotFactory>();

        // ── Repository pattern (EF Core → Repository → Service) ──
        builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        builder.Services.AddSingleton<ChatMessageRepository>();
        builder.Services.AddSingleton<IChatMessageRepository>(sp => sp.GetRequiredService<ChatMessageRepository>());
        builder.Services.AddSingleton<ICompactionChatMessageStore>(sp => sp.GetRequiredService<ChatMessageRepository>());
        builder.Services.AddSingleton<ITokenUsageEventRepository, TokenUsageEventRepository>();

        // ── User/Team/Workspace member repositories ──
        builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
        builder.Services.AddScoped<ITeamRepository, TeamRepository>();
        builder.Services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();
        builder.Services.AddScoped<ISessionEventLogRepository, SessionEventLogRepository>();
        builder.Services.AddHostedService<ChatExecutionWorker>();
        builder.Services.AddSingleton<SubAgentManager>();
        builder.Services.AddSingleton<ISubAgentManager>(sp => sp.GetRequiredService<SubAgentManager>());
        builder.Services.AddSingleton<ISubAgentRunStore, FileSubAgentRunStore>();
        builder.Services.AddSingleton<ISubAgentDiagnosticsService, SubAgentDiagnosticsService>();
        builder.Services.AddHostedService<SubAgentConversationProjectionWorker>();
        builder.Services.TryAddSingleton<IRuntimeExecutionConfigService, RuntimeExecutionConfigService>();
        builder.Services.TryAddSingleton<IExecutionProgressRegistry, ExecutionProgressRegistry>();
        builder.Services.TryAddSingleton<ISubAgentInvocationService, SubAgentInvocationService>();
        builder.Services.AddSingleton<IRuntimeTraceAccessor, AmbientRuntimeTraceAccessor>();
        builder.Services.AddSingleton<RuntimeActivitySink>();
        builder.Services.AddSingleton<IRuntimeActivitySink>(sp => sp.GetRequiredService<RuntimeActivitySink>());
        builder.Services.AddSingleton<TelemetryMetricSink>();
        builder.Services.AddSingleton<ITelemetryMetricSink>(sp => sp.GetRequiredService<TelemetryMetricSink>());
        builder.Services.AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
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
        builder.Services.AddSingleton<FeishuInboundMessageMapper>();
        builder.Services.AddSingleton<IVisualArtifactResolver, VisualArtifactResolverBridge>();
        builder.Services.AddScoped<SessionTitleService>();
        builder.Services.AddScoped<TokenCostService>();
        builder.Services.AddScoped<IVisualReasoningService, DefaultVisualReasoningService>();
        builder.Services.AddHttpClient("DashScopeVisualReasoning");
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
        builder.Services.AddScoped<IAgentInvocationDispatchFactory, AgentInvocationDispatchFactory>();

        // ── ADR-043：Token 使用统计闭环 ────────────────────────────────
        builder.Services.AddSingleton<TokenUsageNormalizer>();
        builder.Services.AddSingleton<TokenUsageRecorder>();
        builder.Services.AddSingleton<ITokenUsageRecorder>(sp => sp.GetRequiredService<TokenUsageRecorder>());
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
