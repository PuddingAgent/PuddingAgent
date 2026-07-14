using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
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
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.Diagnostics;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;
using PuddingPlatform.Services.MessageFabric;
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
using PuddingAgent.Services;
using PuddingAgent.Services.Events;
using Serilog;
using Serilog.Events;
using System.Threading.Channels;

// ── Serilog 结构化日志 ─────────────────────────────
var aspnetcoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
var dataRoot = GetDataRoot(args)
    ?? Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
var dataPaths = PuddingDataPaths.FromRoot(dataRoot);
EnsureDefaultData(dataPaths.DataRoot, Path.Combine(AppContext.BaseDirectory, "default-data"));
EnsureRuntimeDirectories(dataPaths);
EnsureDefaultAgentInstance(dataPaths);

var bootstrapConfiguration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{aspnetcoreEnvironment}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

// PUDDING_LOG_LEVEL 环境变量控制日志级别（默认 Information；设为 Debug 可诊断管线细节）
var logLevel = Environment.GetEnvironmentVariable("PUDDING_LOG_LEVEL") ?? "Information";
var minLevel = logLevel.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? LogEventLevel.Debug : LogEventLevel.Information;

const long MaxFileSize = 1_048_576;
const int RetainedFiles = 200;
var fileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [trace:{TraceId}] [session:{SessionId}] {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(bootstrapConfiguration)
    .MinimumLevel.Is(minLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.With<TraceContextEnricher>()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [trace:{TraceId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.Connector)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("connector"), "connector",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.EventQueue)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("event_queue"), "event_queue",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.EventDispatcher)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("event_dispatcher"), "event_dispatcher",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.SessionState)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("session_state"), "session_state",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.AgentExecution)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("agent_execution"), "agent_execution",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.ContextPipeline)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("context_pipeline"), "context_pipeline",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.LlmGateway)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("llm_gateway"), "llm_gateway",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.ToolRunner)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("tool_runner"), "tool_runner",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.SubAgent)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("sub_agent"), "sub_agent",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingComponent(RuntimeActivityComponents.Memory)
        .WriteTo.Sink(new SizeRollingFileSink(
            dataPaths.ComponentLogsRoot("memory"), "memory",
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(evt => evt.Level >= LogEventLevel.Error)
        .WriteTo.Sink(new SizeRollingFileSink(
            Path.GetDirectoryName(dataPaths.ErrorLogFile)!,
            Path.GetFileNameWithoutExtension(dataPaths.ErrorLogFile)!,
            maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
            outputTemplate: fileOutputTemplate)))
    .WriteTo.Sink(new SizeRollingFileSink(
        Path.GetDirectoryName(dataPaths.SystemLogFile)!,
        Path.GetFileNameWithoutExtension(dataPaths.SystemLogFile)!,
        maxFileSizeBytes: MaxFileSize, retainedFileCountLimit: RetainedFiles,
        outputTemplate: fileOutputTemplate))
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(dataPaths);

// EF Core 10: AddDbContextFactory 的 Singleton factory 消费 Scoped DbContextOptions
// 需要关闭 scope validation。
if (aspnetcoreEnvironment == "Development")
{
    builder.Host.UseDefaultServiceProvider(o => o.ValidateScopes = false);
}
builder.Host.UseSerilog();

// ── 端口 ─────────────────────────────────────────────
// 默认监听 8080（生产环境）；dev-up.ps1 通过 ASPNETCORE_URLS 覆盖为 localhost:5000
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

// ── HTTP 请求日志 ────────────────────────────────────
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
                    | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
});

// ── CORS（允许 Admin SPA 跨域访问）───────────────────
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"]
    ?? "http://localhost:8000;http://localhost:8001;http://localhost:8004;http://localhost:3000;http://localhost:8080")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminSpa", policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.AddControllersWithViews()
    .AddApplicationPart(typeof(BootstrapApiController).Assembly)
    // Runtime owns execution-adjacent APIs such as native capabilities and plugin catalog.
    // The host only publishes that controller surface; it must not duplicate Runtime catalog logic.
    .AddApplicationPart(typeof(PuddingRuntime.Controllers.RuntimeSessionController).Assembly);

// ── JWT 认证 ──────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
if (builder.Environment.IsProduction() && jwtKey.Contains("MUST-CHANGE", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("JWT Key 必须修改！生产环境禁止使用默认密钥。请设置环境变量 Jwt__Key 或 JWT_KEY。");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"] ?? "pudding-platform",
            ValidateAudience         = true,
            ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "pudding-admin",
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew                = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

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

// ── Chat 执行命令队列（ADR-056）─────────────────
builder.Services.AddSingleton<IChatCommandStore, ChatCommandStore>();
builder.Services.AddSingleton<ChatCommandAcceptanceService>();
builder.Services.AddSingleton<ChatTelemetryRecorder>();
builder.Services.AddSingleton<ChatSystemCommandService>();
builder.Services.AddSingleton<ChatDispatchService>();
builder.Services.AddSingleton<ChatMessageExecutionService>();
builder.Services.AddHostedService<ChatExecutionWorker>();
builder.Services.AddSingleton<SubAgentManager>();
builder.Services.AddSingleton<ISubAgentManager>(sp => sp.GetRequiredService<SubAgentManager>());
builder.Services.AddSingleton<ISubAgentRunStore, FileSubAgentRunStore>();
builder.Services.TryAddSingleton<IRuntimeExecutionConfigService, RuntimeExecutionConfigService>();
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
builder.Services.AddScoped<VisionArtifactStorageService>();
builder.Services.AddScoped<IVisualArtifactReferenceResolver>(sp => sp.GetRequiredService<VisionArtifactStorageService>());
builder.Services.AddScoped<ChatVisualReasoningRequestFactory>();
builder.Services.AddScoped<ChatVisualReasoningSessionRunner>();
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
builder.Services.AddDbContext<PlatformDbContext>((sp, opt) =>
{
    opt.UseSqlite(connStr);
    opt.AddInterceptors(sp.GetRequiredService<PlatformSqliteConnectionInterceptor>());
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddDbContextFactory<PlatformDbContext>((sp, opt) =>
{
    opt.UseSqlite(connStr);
    opt.AddInterceptors(sp.GetRequiredService<PlatformSqliteConnectionInterceptor>());
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

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
// ── Workspace Agent 运行时文件服务（管理运行时工作目录，非配置）──
builder.Services.AddSingleton<WorkspaceAgentFileService>();
builder.Services.AddSingleton<IWorkspaceAgentCatalog>(sp => sp.GetRequiredService<WorkspaceAgentFileService>());
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
builder.Services.AddSingleton<TokenUsageRebuildService>();
builder.Services.AddSingleton<SessionSteeringService>();
builder.Services.AddScoped<CacheDiagnosticsService>();

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
        sp.GetService<IMemoryLlmClient>()));
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
builder.Services.AddSingleton<ChatTranscriptWriter>();
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
builder.Services.AddSingleton<ExecutionJournal>();
builder.Services.AddSingleton<CompletionPolicy>();
builder.Services.AddSingleton<SandboxExecutor>();
builder.Services.AddSingleton<AgentSkillPackageRegistry>();
builder.Services.AddSingleton<AgentSkillFileService>();
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
builder.Services.AddPuddingAgentTool<SmartSearchTool>();
builder.Services.AddPuddingAgentTool<SmartQuerySessionLogsTool>();
builder.Services.AddPuddingAgentTool<LlmResourcePoolTool>();
builder.Services.AddPuddingAgentTool<ReadOfficeDocumentTool>();
builder.Services.AddPuddingAgentTool<TaskManagerTool>();
builder.Services.AddPuddingTool<SubAgentTool>();
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

// ── 潜意识管道触发工具：手动触发 Auto-Dream / 经验提取 / 技能改进 ──────────
builder.Services.AddPuddingTool<SubconsciousTriggerTool>();

// ── 统一 Tool 注册表：Agent 工具统一通过 IPuddingTool/native registry 暴露 ──────────
builder.Services.AddPuddingToolsFromAssembly(typeof(Program).Assembly);
builder.Services.AddPuddingToolsFromAssembly(typeof(PuddingRuntime.RuntimeServiceExtensions).Assembly);
builder.Services.AddPuddingToolRegistry(builder.Configuration);
builder.Services.AddSingleton<IToolInvocationService, ToolInvocationService>();

// ── 会话历史查询服务 (Repository → Service 分层) ────
builder.Services.AddScoped<IChatHistoryService, ChatHistoryService>();
builder.Services.AddScoped<MessageTopicService>();

builder.Services.AddSingleton<SkillRuntime>();
builder.Services.AddSingleton<ITerminalProcessManager, TerminalProcessManager>();
builder.Services.AddSingleton<ITerminalCommandPolicy, DefaultTerminalCommandPolicy>();
builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
builder.Services.AddSingleton<IAgentLoopHook, EmbeddingGenerationHook>();
builder.Services.Configure<SubconsciousOptions>(
    builder.Configuration.GetSection(SubconsciousOptions.SectionName));

// ── 潜意识记忆系统（阶段 2：LLM 抽取与后台整合）────────────────
var subconsciousChannel = Channel.CreateUnbounded<ConsolidationJob>(
    new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
builder.Services.AddSingleton(subconsciousChannel);
builder.Services.AddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();
if (builder.Configuration.GetValue<bool>(
        $"{SubconsciousOptions.SectionName}:{nameof(SubconsciousOptions.EnableLegacyConsolidationHook)}"))
{
    builder.Services.AddSingleton<SubconsciousConsolidationHook>();
    builder.Services.AddSingleton<IAgentLoopHook>(sp => sp.GetRequiredService<SubconsciousConsolidationHook>());
}
// HOSTED-DISABLED: builder.Services.AddHostedService<SubconsciousWorkerService>();

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
builder.Services.AddSingleton<IdleDetector>();
builder.Services.AddSingleton<IIdleDetector>(sp => sp.GetRequiredService<IdleDetector>());
// HOSTED-DISABLED: builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleDetector>());

// ── 主动心跳系统（空闲驱动 + 多 Agent 队列 + 尽力模式 + 哲学引导）────
builder.Services.AddSingleton<AgentWakeQueue>();
builder.Services.AddSingleton<HeartbeatOrchestrator>();
// HOSTED-DISABLED: builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatOrchestrator>());
builder.Services.AddSingleton<IAgentExecutionStateRegistry, AgentExecutionStateRegistry>();
builder.Services.AddSingleton<IAgentExecutionAvailabilityProvider, DefaultAgentExecutionAvailabilityProvider>();
builder.Services.AddSingleton<MessageDeliveryDispatcher>();
// HOSTED-DISABLED: builder.Services.AddHostedService(sp => sp.GetRequiredService<MessageDeliveryDispatcher>());

// 检查点与订阅管理
builder.Services.AddSingleton<AgentCheckpointService>();
builder.Services.AddSingleton<IAgentCheckpointService>(sp => sp.GetRequiredService<AgentCheckpointService>());
builder.Services.AddPuddingAgentTool<EventSubscriptionTool>();
builder.Services.AddSingleton<IEventSubscriptionTool>(sp => sp.GetRequiredService<EventSubscriptionTool>());

// IEventHandler 消费者 — 事件系统的唯一边界
builder.Services.AddSingleton<IEventHandler, AgentEventHandler>();

// 入站桥：IInternalEventBus → Preprocessor → PriorityQueue 管道入口
// HOSTED-DISABLED: builder.Services.AddHostedService<EventIngressBridge>();

// 分发器：PriorityQueue 出队 → IEventHandler.HandleAsync()
// HOSTED-DISABLED: builder.Services.AddHostedService<EventDispatcher>();
// HOSTED-DISABLED: builder.Services.AddHostedService<SessionCompressedMemoryMaintenanceHook>();

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
builder.Services.AddSingleton<IRuntimeAgentDispatcher, RuntimeAgentDispatcher>();

// ── P2P 发现（局域网 UDP 广播 + HTTP 探活）────────────────
builder.Services.AddSingleton<IP2pDiscoveryService, MdnsDiscoveryService>();

// ── Webhook 连接器 ─────────────────────────────────
builder.Services.AddSingleton<WebhookConnector>();
builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<WebhookConnector>());

// ── HTTP 连接器（最小入站协议）──────────────────────
builder.Services.AddSingleton<HttpConnector>();
builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<HttpConnector>());

// ── WebSocket 连接器 ───────────────────────────────
builder.Services.AddSingleton<WebSocketConnector>();
builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<WebSocketConnector>());

// ── MQTT 连接器（最小协议）──────────────────────────
builder.Services.AddSingleton<MqttConnector>();
builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<MqttConnector>());

// ── 网关鉴权（SM2 + 白名单）────────────────────────
builder.Services.AddSingleton<GatewayAuthService>();

// ── ConnectorHost（统一管理所有连接器）────────────
builder.Services.AddSingleton<ConnectorHost>(sp =>
{
    var host = new ConnectorHost(
        onEventReceived: async (envelope, ct) =>
        {
            // 将连接器事件推入 IInternalEventBus → EventIngressBridge → AgentEventHandler
            var bus = sp.GetRequiredService<PuddingCode.Abstractions.IInternalEventBus>();
            var sessionId = envelope.CorrelationId ?? $"connector-session-{Guid.NewGuid():N}"[..26];
            var messageType = string.IsNullOrWhiteSpace(envelope.MessageType) ? "message" : envelope.MessageType;
            var eventType = ConnectorGatewayContracts.BuildEventType(envelope.ChannelType, messageType);
            var payload = new ConnectorInboundPayload
            {
                ChannelId = envelope.ChannelId,
                ChannelType = envelope.ChannelType,
                UserExternalId = envelope.UserExternalId,
                MessageText = envelope.MessageText,
                MessageType = envelope.MessageType,
                CorrelationId = sessionId,
                Metadata = envelope.Metadata,
            };

            var traceId = envelope.Metadata.TryGetValue("trace_id", out var t) && !string.IsNullOrWhiteSpace(t)
                ? t
                : envelope.EnvelopeId;

            var ie = new PuddingCode.Models.InternalEvent
            {
                EventId = envelope.EnvelopeId,
                Type = eventType,
                Source = new PuddingCode.Models.EventSource { SourceType = envelope.ChannelType, SourceId = envelope.ChannelId },
                SessionId = sessionId,
                WorkspaceId = "default",
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload),
                TimestampUtc = DateTime.UtcNow,
                Priority = PuddingCode.Models.EventPriorityLevel.Normal,
                TraceId = traceId,
                CorrelationId = sessionId,
                CausationId = null, // PuddingIngressEnvelope 尚未标准化，暂无 CausationId
            };

            var spLogger = sp.GetRequiredService<ILogger<Program>>();
            spLogger.LogInformation(
                "[Program:ConnectorIngress] eventId={EventId} traceId={TraceId} eventType={EventType} channelType={ChannelType} channelId={ChannelId} sessionId={SessionId} envelopeId={EnvelopeId}",
                ie.EventId,
                traceId,
                eventType,
                envelope.ChannelType,
                envelope.ChannelId,
                sessionId,
                envelope.EnvelopeId);

            await bus.PublishAsync(ie, ct);

            // 仅 websocket 通道启用 SSM→WS 转发；其他协议仅通过会话层观察。
            if (!string.Equals(envelope.ChannelType, "websocket", StringComparison.OrdinalIgnoreCase))
                return;

            // 将 WebSocket 连接 ID 的 session 订阅到 SSM，后续 SSE 帧转发到 WebSocket
            var ssm2 = sp.GetService<PuddingCode.Abstractions.ISessionStateManager>();
            if (ssm2 is not null)
            {
                var wsConnector = sp.GetRequiredService<WebSocketConnector>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 等待 Agent 创建 session 并开始推流
                        await Task.Delay(2000, ct);
                        spLogger.LogWarning("[Program:SSM→WS] Subscribing session={Sid} conn={Conn} eventType={EventType}", sessionId, envelope.ChannelId, eventType);
                        var reader = ssm2.Subscribe(sessionId);
                        if (reader is null) { spLogger.LogWarning("[Program:SSM→WS] Subscribe returned null"); return; }
                        try
                        {
                            spLogger.LogWarning("[Program:SSM→WS] Forward start conn={Conn} session={Sid}", envelope.ChannelId, sessionId);
                            await foreach (var frame in reader.ReadAllAsync(CancellationToken.None))
                            {
                                var wsMsg = new PuddingCode.Platform.ConnectorMessage
                                {
                                    Target = envelope.ChannelId,
                                    Content = System.Text.Json.JsonSerializer.Serialize(new { type = "sse", @event = frame.Event, data = frame.Data }),
                                };
                                try { await wsConnector.SendAsync(wsMsg, CancellationToken.None); }
                                catch { break; }
                            }
                            spLogger.LogWarning("[Program:SSM→WS] Forward end conn={Conn} session={Sid}", envelope.ChannelId, sessionId);
                        }
                        finally
                        {
                            ssm2.Unsubscribe(sessionId, reader);
                        }
                    }
                    catch (Exception fex) { spLogger.LogWarning(fex, "[Program:SSM→WS] Forward error"); }
                });
            }
        },
        sp.GetRequiredService<ILogger<ConnectorHost>>());
    return host;
});

// ── Cron 定时任务调度 ──────────────────────────────
// HOSTED-DISABLED: builder.Services.AddHostedService<CronSchedulerService>();
// HOSTED-DISABLED: builder.Services.AddHostedService<AgentDailySummaryHostedService>();
// HOSTED-DISABLED: builder.Services.AddHostedService<StartupDailySummaryCompensationService>();

// ILlmConfigService 已注册（见上方），同时注册 ILlmResolver 兼容旧接口。
// Provider/model 配置仅从 data/config/llm.providers.json 读取，不再回落到 DB。
builder.Services.AddSingleton<ILlmResolver>(sp =>
{
    return new FileLlmResolver(
        sp.GetRequiredService<ILlmConfigService>(),
        sp.GetRequiredService<ILogger<FileLlmResolver>>());
});

builder.Services.AddHttpClient("DirectLlm", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient("HttpFetchSkill", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("SkillPackageDL", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

// ── TTS/ASR 语音 Provider ──
builder.Services.AddHttpClient("DashScopeTts", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("DashScopeAsr", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<PuddingCode.Abstractions.IVoiceProviderFactory, PuddingRuntime.Services.VoiceProviderFactory>();

// ── Bootstrap 初始化 ─────────────────────────────────
var stateFilePath = Path.Combine(dataPaths.RuntimeRoot, "bootstrap-state.json");

if (!File.Exists(stateFilePath))
{
    var secretBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    var secret = Convert.ToBase64String(secretBytes);
    var initialState = System.Text.Json.JsonSerializer.Serialize(new
    {
        Bootstrap = new { Secret = secret, Initialized = false }
    });
    File.WriteAllText(stateFilePath, initialState);
}

builder.Configuration.AddJsonFile(stateFilePath, optional: true, reloadOnChange: true);
builder.Services.AddSingleton<BootstrapStateService>(sp =>
    new BootstrapStateService(stateFilePath, sp.GetRequiredService<IConfiguration>()));

// ── JSON 配置种子服务 ─────────────────────────────
builder.Services.AddScoped<JsonConfigSeedService>();

// ── Agent 头像服务（ADR-034）────────────────────────
builder.Services.AddSingleton<AgentAvatarSeedService>();
builder.Services.AddSingleton<AgentAvatarCatalog>();

var app = builder.Build();
Console.WriteLine("[Startup] Host built, configuring middleware...");

var p2pDiscoveryService = app.Services.GetRequiredService<IP2pDiscoveryService>();
var jsonlSessionWriter = app.Services.GetRequiredService<PuddingCode.Services.JsonlSessionWriter>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await p2pDiscoveryService.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "[P2P] Discovery 启动失败。");
        }
        Serilog.Log.Warning("[Program] P2P done, about to start ConnectorHost...");
        try
        {
            // 启动 ConnectorHost：注册所有 IPuddingConnector
            var progLogger = app.Services.GetRequiredService<ILogger<Program>>();
            progLogger.LogWarning("[Program] Starting ConnectorHost via DI logger...");
            var connectorHost = app.Services.GetRequiredService<ConnectorHost>();
            progLogger.LogWarning("[Program] ConnectorHost resolved, getting connectors...");
            var connectors = app.Services.GetServices<IPuddingConnector>().ToList();
            progLogger.LogWarning("[Program] Got {Count} connectors, registering...", connectors.Count);
            foreach (var c in connectors)
                connectorHost.Register(c);
            await connectorHost.StartAllAsync();
            progLogger.LogWarning("[Program] ConnectorHost started with {Count} connectors", connectors.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Program] ConnectorHost 启动失败。");
        }
    });
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        jsonlSessionWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "[Jsonl] Flush on ApplicationStopping failed.");
    }

    _ = Task.Run(async () =>
    {
        try
        {
            await p2pDiscoveryService.StopAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "[P2P] Discovery 停止失败。");
        }
    });
});

Console.WriteLine("[Startup] DB migration skipped — using pre-built database");

// ── HTTP 请求日志（最先执行，记录所有请求）───────────
app.UseHttpLogging();

// ── 错误处理 ─────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseMiddleware<PuddingPlatform.Middleware.TraceableExceptionMiddleware>();

// ── CORS（必须在 Routing 前）────────────────────────
app.UseCors("AdminSpa");

// ── Routing ──────────────────────────────────────────
app.UseRouting();

// ── WebSocket ────────────────────────────────────────
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

// ── Auth ─────────────────────────────────────────────
app.UseAuthentication();

// ── Session ──────────────────────────────────────────
app.UseSession();

app.UseAuthorization();

// ── 请求诊断（Auth 之后）─ 记录到达控制器管线的请求 ──
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("HttpPipeline.Diag");
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        logger.LogDebug(
            "[Pipeline] {Method} {Path} auth={Auth} ct={ContentType} len={Len}",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.User?.Identity?.IsAuthenticated ?? false,
            ctx.Request.ContentType ?? "-",
            ctx.Request.ContentLength ?? 0);
    }
    await next();
});

// ── 静态文件（同时从输出目录 wwwroot/ 和项目 wwwroot/ 提供）─
// 输出目录 wwwroot 由脚本复制前端产物，支持热加载
var outputWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(outputWwwRoot))
{
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(outputWwwRoot);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}
app.MapStaticAssets();
app.UseStaticFiles();

// ── API 路由（必须在 Fallback 前）────────────────────
app.MapControllers();

// ── 路由后诊断 ── 确认请求到达了控制器路由层 ──
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var endpoint = ctx.GetEndpoint();
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("HttpPipeline.Diag");
        logger.LogDebug(
            "[Pipeline:AfterRoute] {Method} {Path} endpoint={Endpoint} status={Status}",
            ctx.Request.Method,
            ctx.Request.Path,
            endpoint?.DisplayName ?? "(none)",
            ctx.Response.StatusCode);
    }
    await next();
});

// ── MVC Controller 路由 ──────────────────────────────
app.MapControllerRoute(
    name: "platform",
    pattern: "platform/{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// ── 健康检查（含版本/Hash）─────────────────────────
app.MapGet("/health", () =>
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
    // 散列 Runtime + MemoryEngine 程序集（覆盖最常变更的业务逻辑）
    string? imageHash = null;
    string? buildTime = null;
    try
    {
        var dlls = new[] { "PuddingRuntime.dll", "PuddingMemoryEngine.dll" };
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var dll in dlls)
        {
            var path = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var hash = sha.ComputeHash(stream); // 累积散列
                buildTime = File.GetLastWriteTimeUtc(path).ToString("o");
            }
        }
        imageHash = Convert.ToHexString(sha.Hash!) [^8..];
    }
    catch { imageHash = "unknown"; }

    return Results.Ok(new
    {
        status = "healthy",
        version,
        imageHash,
        buildTime = buildTime ?? "unknown",
        timestamp = DateTimeOffset.UtcNow
    });
});

// ── 配置热重载接口（文件配置为唯一来源；端点保留向后兼容）───────
app.MapMethods("/admin/reload", new[] { "GET", "POST" }, () =>
{
    return Results.Ok(new { status = "file-backed", timestamp = DateTimeOffset.UtcNow });
});

// ── 潜意识 LLM 状态（可观测性）──────────────────────
app.MapGet("/health/subconscious", async (
    IDbContextFactory<PuddingMemoryEngine.Data.MemoryDbContext> dbFactory,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("HealthCheck");
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var recentJobs = await db.SubconsciousJobLogs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .Select(j => new
            {
                j.JobId,
                j.SessionId,
                j.Status,
                j.FactsExtracted,
                j.FactsMerged,
                j.FactsDiscarded,
                j.ElapsedMs,
                j.LlmModelId,
                j.ErrorMessage,
                j.CreatedAt
            })
            .ToListAsync(ct);

        var totalFacts = await db.MemoryFacts.CountAsync(f => f.Status == "active", ct);
        var totalPrefs = await db.MemoryPreferences.CountAsync(ct);

        return Results.Ok(new
        {
            recentJobs,
            summary = new
            {
                totalJobs = recentJobs.Count,
                successCount = recentJobs.Count(j => j.Status == "completed"),
                failCount = recentJobs.Count(j => j.Status == "failed"),
                totalFacts,
                totalPreferences = totalPrefs
            },
            timestamp = DateTimeOffset.UtcNow
        });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[HealthCheck] Subconscious status query failed");
        return Results.Ok(new { status = "unavailable", error = ex.Message });
    }
});

// ── Chat API ─────────────────────────────────────────
app.MapPost("/api/chat", async (
    ChatRequest request,
    AgentExecutionService executor,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Message is required" });

    var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N")[..8];

    var dispatchRequest = new RuntimeDispatchRequest
    {
        SessionId = sessionId,
        WorkspaceId = request.WorkspaceId ?? "default",
        AgentTemplateId = "workspace-service-agent",
        MessageText = request.Message,
        LlmConfig = llmConfigService.GetDefault()
            ?? throw new InvalidOperationException("Global LLM default (profiles.conscious) is not configured in data/config/llm.providers.json. Configure profiles.conscious.providerId and profiles.conscious.modelId to match an enabled provider."),
    };

    var result = await executor.ExecuteAsync(dispatchRequest, ct);

    return Results.Ok(new
    {
        sessionId,
        reply = result.ReplyText ?? result.ErrorMessage ?? "(empty)",
        isSuccess = result.IsSuccess,
    });
});

// ── Admin SPA fallback（/admin 下的前端路由回退）───────────
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

// ── Chat SPA fallback（根路径 → Chat，必须最后！）──────
app.MapFallbackToFile("index.html");

// ── Workspace Catalog 初始化：从 DB 加载或播种 default workspace ──
Console.WriteLine("[Startup] Initializing Workspace Catalog...");
try
{
    var catalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
    await catalog.LoadAsync();
    Console.WriteLine($"[Startup] Workspace Catalog loaded, {catalog.GetAll().Count} workspace(s)");
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] Workspace Catalog init failed: {ex.Message}");
}

// ── jieba 分词回填：存量 Chapter 的 TitleTokens / ContentTokens ──
Console.WriteLine("[Startup] Starting jieba backfill...");
try
{
    var library = app.Services.GetRequiredService<IMemoryLibrary>();
    if (library is MemoryLibrary memLib)
    {
        await memLib.BackfillTokensAsync();
        Console.WriteLine("[startup] jieba tokens backfill completed.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[startup] jieba tokens backfill skipped: {ex.Message}");
}

Console.WriteLine("[Startup] Entering app.Run() — HostedServices will start...");
try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

static void EnsureDefaultData(string dataRoot, string defaultDataRoot)
{
    Directory.CreateDirectory(dataRoot);

    if (!Directory.Exists(defaultDataRoot))
        return;

    CopyMissingFiles(defaultDataRoot, dataRoot, relative =>
        !relative.StartsWith("agent-template-presets", StringComparison.OrdinalIgnoreCase));
}

static void CopyMissingFiles(string sourceRoot, string targetRoot, Func<string, bool>? shouldCopy = null)
{
    foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceRoot, directory);
        if (shouldCopy is not null && !shouldCopy(relative))
            continue;

        Directory.CreateDirectory(Path.Combine(targetRoot, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceRoot, file);
        if (shouldCopy is not null && !shouldCopy(relative))
            continue;

        var target = Path.Combine(targetRoot, relative);
        if (File.Exists(target))
            continue;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void EnsureRuntimeDirectories(PuddingDataPaths paths)
{
    Directory.CreateDirectory(paths.ConfigRoot);
    Directory.CreateDirectory(paths.AgentTemplatesRoot);
    Directory.CreateDirectory(paths.AgentInstancesRoot);
    Directory.CreateDirectory(paths.WorkspacesRoot);
    Directory.CreateDirectory(paths.SystemLogsRoot);
    Directory.CreateDirectory(paths.DiagnosticsLogsRoot);
    Directory.CreateDirectory(paths.SessionLogsRoot);
    Directory.CreateDirectory(paths.RuntimeTracesRoot);
    Directory.CreateDirectory(paths.EventQueueRoot);
    Directory.CreateDirectory(paths.MemoryRoot);
    Directory.CreateDirectory(paths.DatabasesRoot);
    Directory.CreateDirectory(paths.BackupsRoot);
    Directory.CreateDirectory(paths.TempRoot);
}

static string? GetDataRoot(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg["--data-root=".Length..];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (arg.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var value = args[i + 1];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    return null;
}

static bool IsDiagnosticsTimelineEnabled(string aspnetcoreEnvironment)
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

/// <summary>
/// 确保默认 Agent 实例存在（幂等：已存在则跳过）。
/// </summary>
static void EnsureDefaultAgentInstance(PuddingDataPaths paths)
{
    var instanceId = "default.general-assistant-001";
    var manifestPath = Path.Combine(paths.AgentInstanceRoot(instanceId), "manifest.json");
    if (File.Exists(manifestPath))
    {
        EnsureAgentSkillDirectory(paths, instanceId);
        return;
    }

    Log.Information("[Bootstrap] 创建默认 Agent 实例: {InstanceId}", instanceId);

    // manifest.json
    var manifestDir = Path.GetDirectoryName(manifestPath)!;
    Directory.CreateDirectory(manifestDir);
    var manifest = """
    {
      "agentInstanceId": "default.general-assistant-001",
      "templateId": "general-assistant",
      "displayName": "布丁",
      "workspaceId": "default",
      "isEnabled": true
    }
    """;
    File.WriteAllText(manifestPath, manifest);

    // config/llm.json
    var configDir = paths.AgentInstanceConfigRoot(instanceId);
    Directory.CreateDirectory(configDir);
    var llmConfig = """
    {
      "conscious": {
        "profileId": "default-conscious"
      },
      "subconscious": {
        "profileId": "default-subconscious"
      }
    }
    """;
    File.WriteAllText(Path.Combine(configDir, "llm.json"), llmConfig);

    // config/memory.json
    var memoryConfig = """
    {
      "maxFacts": 1000,
      "maxPreferences": 200,
      "recallMode": "auto"
    }
    """;
    File.WriteAllText(Path.Combine(configDir, "memory.json"), memoryConfig);

    EnsureAgentSkillDirectory(paths, instanceId);
}

static void EnsureAgentSkillDirectory(PuddingDataPaths paths, string agentInstanceId)
{
    var skillsRoot = Path.Combine(paths.AgentInstanceRoot(agentInstanceId), "skills");
    Directory.CreateDirectory(skillsRoot);

    var indexPath = Path.Combine(skillsRoot, "index.json");
    if (File.Exists(indexPath))
        return;

    var index = $$"""
    {
      "agentInstanceId": "{{agentInstanceId}}",
      "generatedAt": "{{DateTimeOffset.UtcNow:O}}",
      "skills": []
    }
    """;
    File.WriteAllText(indexPath, index);
}

/// <summary>
/// 确保 SQLite 表包含指定列；列已存在时跳过，避免直接执行 ALTER TABLE 触发 EF Core Error 日志。
/// </summary>
static async Task EnsureSqliteColumnAsync(DbContext db, Microsoft.Extensions.Logging.ILogger logger, string tableName, string columnName, string ddl, string successMessage)
{
    try
    {
        if (await SqliteColumnExistsAsync(db, tableName, columnName))
            return;

        await db.Database.ExecuteSqlRawAsync(ddl);
        logger.LogInformation("{Message}：{Ddl}", successMessage, ddl);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[Schema] SQLite 列补齐失败（将继续启动）：{Table}.{Column}", tableName, columnName);
    }
}

/// <summary>
/// 通过 PRAGMA table_info 查询 SQLite 列是否存在。
/// </summary>
static async Task<bool> SqliteColumnExistsAsync(DbContext db, string tableName, string columnName)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;
    if (shouldClose)
        await connection.OpenAsync();

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqliteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.FieldCount > 1 && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
    finally
    {
        if (shouldClose)
            await connection.CloseAsync();
    }
}

/// <summary>
/// 转义 SQLite 标识符，避免 PRAGMA 查询表名时出现特殊字符问题。
/// </summary>
static string QuoteSqliteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

public sealed record ChatRequest
{
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
}

public partial class Program { }
