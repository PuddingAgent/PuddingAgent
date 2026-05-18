using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingController;
using PuddingController.Data;
using PuddingController.Services;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Events;
using PuddingRuntime.Services.Observability;
using PuddingRuntime.Services.Sandbox;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.SubAgents;
using PuddingRuntime.Services.Tools;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingAgent.P2P;
using PuddingAgent.Connectors;
using PuddingAgent.Services;
using PuddingAgent.Services.Events;
using Serilog;
using Serilog.Events;
using System.Threading.Channels;

// ── Serilog 结构化日志 ─────────────────────────────
var aspnetcoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
var bootstrapConfiguration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{aspnetcoreEnvironment}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var logDir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
Directory.CreateDirectory(logDir);
Directory.CreateDirectory(Path.Combine(logDir, "error"));

// PUDDING_LOG_LEVEL 环境变量控制日志级别（默认 Information；设为 Debug 可诊断管线细节）
var logLevel = Environment.GetEnvironmentVariable("PUDDING_LOG_LEVEL") ?? "Information";
var minLevel = logLevel.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? LogEventLevel.Debug : LogEventLevel.Information;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(bootstrapConfiguration)
    .MinimumLevel.Is(minLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Map(
        evt => evt.Level,
        (level, wt) =>
        {
            if (level >= LogEventLevel.Error)
            {
                wt.File(
                    Path.Combine(logDir, "error", "pudding-error-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
            }
        })
    .WriteTo.File(
        Path.Combine(logDir, "pudding-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    // ── Session 级日志（按 sessionId 分目录，按天滚动）─
    .WriteTo.Map(
        "SessionId",
        (sessionId, wt) =>
        {
            var sid = sessionId as string;
            if (!string.IsNullOrEmpty(sid))
            {
                var sessionLogDir = Path.Combine(logDir, "sessions", sid);
                Directory.CreateDirectory(sessionLogDir);
                wt.File(
                    Path.Combine(sessionLogDir, "session-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
            }
        })
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// EF Core 10: AddDbContextFactory 的 Singleton factory 消费 Scoped DbContextOptions
// 需要关闭 scope validation。
if (aspnetcoreEnvironment == "Development")
{
    builder.Host.UseDefaultServiceProvider(o => o.ValidateScopes = false);
}
builder.Host.UseSerilog();

// ── 端口 ─────────────────────────────────────────────
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// ── CORS（允许 Admin SPA 跨域访问）───────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminSpa", policy =>
        policy.WithOrigins(
                "http://localhost:8000",
                "http://localhost:8001",
                "http://localhost:8004",
            "http://localhost:3000",
            "http://localhost:8080")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.AddControllersWithViews();

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
    var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:8080";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Workspace 业务层 ──────────────────────────────────
builder.Services.AddScoped<WorkspaceBusinessService>();
builder.Services.AddSingleton<MinioStorageService>();
builder.Services.AddSingleton<SessionEventHub>();
builder.Services.AddSingleton<SessionStateManager>();
builder.Services.AddSingleton<ISessionStateManager>(sp => sp.GetRequiredService<SessionStateManager>());
builder.Services.AddSingleton<SubAgentManager>();
builder.Services.AddSingleton<ISubAgentManager>(sp => sp.GetRequiredService<SubAgentManager>());
builder.Services.AddSingleton<IRuntimeTraceAccessor, AmbientRuntimeTraceAccessor>();
builder.Services.AddSingleton<RuntimeActivitySink>();
builder.Services.AddSingleton<IRuntimeActivitySink>(sp => sp.GetRequiredService<RuntimeActivitySink>());
builder.Services.AddPuddingController();

// ── EF Core / 数据库 ──────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/pudding_platform.db";
var controllerConnStr = builder.Configuration.GetConnectionString("Controller")
    ?? "Data Source=data/pudding_controller.db";
var memoryConnStr = builder.Configuration.GetConnectionString("Memory")
    ?? "Data Source=data/pudding_memory.db";
builder.Services.AddDbContext<PlatformDbContext>(opt =>
{
    opt.UseSqlite(connStr);
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddDbContextFactory<PlatformDbContext>(opt =>
{
    opt.UseSqlite(connStr);
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddSingleton<Sm2JwtSigner>();
builder.Services.AddSingleton<IKeyVaultService, KeyVaultService>();
builder.Services.AddSingleton<AgentTemplateProvider>();
builder.Services.AddSingleton<IAgentTemplateProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());
builder.Services.AddSingleton<IWorkspaceProfileProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());
builder.Services.AddSingleton<AgentLLMConfigResolver>();
builder.Services.AddSingleton<ILLMConfigResolver>(sp => sp.GetRequiredService<AgentLLMConfigResolver>());

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
builder.Services.AddSingleton<MemoryRecallService>();
builder.Services.AddSingleton<IMemoryRecallService>(sp => sp.GetRequiredService<MemoryRecallService>());
builder.Services.AddSingleton<JsonlSessionWriter>();
builder.Services.AddSingleton<JsonlSessionReader>();
builder.Services.AddSingleton<AgentExecutionGuardrails>();
builder.Services.AddSingleton<ExecutionControlRegistry>();
builder.Services.AddSingleton<ExecutionJournal>();
builder.Services.AddSingleton<CompletionPolicy>();
builder.Services.AddSingleton<SandboxExecutor>();
builder.Services.AddSingleton<AgentContainerRegistry>();
builder.Services.AddSingleton<ISandboxProvider, DockerSandboxProvider>();
builder.Services.AddSingleton<AgentSkillPackageRegistry>();
builder.Services.AddSingleton<SkillPackageDownloadService>();
builder.Services.AddSingleton<IAgentSkill, BashSkill>();
builder.Services.AddSingleton<IAgentSkill, ReadFileSkill>();
builder.Services.AddSingleton<IAgentSkill, WriteFileSkill>();
builder.Services.AddSingleton<IAgentSkill, PythonSkill>();
builder.Services.AddSingleton<IAgentSkill, HttpFetchSkill>();
builder.Services.AddSingleton<IAgentSkill, TerminalSkill>();
builder.Services.AddSingleton<SearchFilesTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<SearchFilesTool>());
builder.Services.AddSingleton<SearchCodebaseTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<SearchCodebaseTool>());
builder.Services.AddSingleton<TaskManagerTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<TaskManagerTool>());
builder.Services.AddSingleton<SubAgentTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<SubAgentTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<SubAgentTool>());
builder.Services.AddSingleton<MemoryExplorerSubAgent>();
builder.Services.AddSingleton<MemoryLibraryTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<MemoryLibraryTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<MemoryLibraryTool>());

// ── 记忆增强 Tools（P0：save / manage / grep）──────────
builder.Services.AddSingleton<SaveMemoryTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<SaveMemoryTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<SaveMemoryTool>());

builder.Services.AddSingleton<ManageMemoryTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<ManageMemoryTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<ManageMemoryTool>());

builder.Services.AddSingleton<GrepMemoryTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<GrepMemoryTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<GrepMemoryTool>());

builder.Services.AddSingleton<QuerySessionsTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<QuerySessionsTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<QuerySessionsTool>());

// ── 子代理管理工具（ADR-016 扩展）──────────────────────
builder.Services.AddSingleton<QuerySubAgentsTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<QuerySubAgentsTool>());

// ── 会话历史查询服务 (Repository → Service 分层) ────
builder.Services.AddScoped<IChatHistoryService, ChatHistoryService>();

builder.Services.AddSingleton<SkillRuntime>();
builder.Services.AddSingleton<ITerminalProcessManager, TerminalProcessManager>();
builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
builder.Services.AddSingleton<IAgentLoopHook, EmbeddingGenerationHook>();

// ── 潜意识记忆系统（阶段 2：LLM 抽取与后台整合）────────────────
var subconsciousChannel = Channel.CreateUnbounded<ConsolidationJob>(
    new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
builder.Services.AddSingleton(subconsciousChannel);
builder.Services.AddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();
builder.Services.AddSingleton<SubconsciousConsolidationHook>();
builder.Services.AddSingleton<IAgentLoopHook>(sp => sp.GetRequiredService<SubconsciousConsolidationHook>());
builder.Services.AddHostedService<SubconsciousWorkerService>();

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

// 检查点与订阅管理
builder.Services.AddSingleton<AgentCheckpointService>();
builder.Services.AddSingleton<IAgentCheckpointService>(sp => sp.GetRequiredService<AgentCheckpointService>());
builder.Services.AddSingleton<EventSubscriptionTool>();
builder.Services.AddSingleton<IEventSubscriptionTool>(sp => sp.GetRequiredService<EventSubscriptionTool>());
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<EventSubscriptionTool>());

// IEventHandler 消费者 — 事件系统的唯一边界
builder.Services.AddSingleton<IEventHandler, AgentEventHandler>();

// 入站桥：IInternalEventBus → Preprocessor → PriorityQueue 管道入口
builder.Services.AddHostedService<EventIngressBridge>();

// 分发器：PriorityQueue 出队 → IEventHandler.HandleAsync()
builder.Services.AddHostedService<EventDispatcher>();

builder.Services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
builder.Services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();

// ── 统一 LLM 配置服务（JSON 文件，唯一来源）──────────
var llmConfigPath = Path.Combine(AppContext.BaseDirectory, "data", "llm", "config.json");
var llmConfigService = new JsonLlmConfigService(llmConfigPath);
builder.Services.AddSingleton<ILlmConfigService>(llmConfigService);

// Memory LLM client — 通过统一服务获取配置
builder.Services.AddSingleton<IMemoryLlmClient>(sp =>
{
    var svc = sp.GetRequiredService<ILlmConfigService>();
    var memCfg = svc.GetMemoryConfig();
    return new DirectMemoryLlmClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<DirectMemoryLlmClient>>(),
        sp.GetService<IRuntimeLlmClient>(),
        memCfg);
});

// ── 启动环境信息 ──
builder.Services.AddSingleton(new StartupEnvironmentInfo());
builder.Services.AddSingleton<SystemPromptBuilder>();
builder.Services.AddSingleton<ContextAssemblyStore>();
builder.Services.AddSingleton<ContextPipeline>();
builder.Services.AddSingleton<ContextWindowManager>();
// ── Agent Persona 文件读取器 ──
builder.Services.AddSingleton(sp =>
{
    var dataDir = builder.Configuration["Pudding:AgentPersonaDir"]
        ?? Path.Combine(AppContext.BaseDirectory, "data", "agents");
    return new AgentPersonaFileProvider(dataDir,
        sp.GetRequiredService<ILogger<AgentPersonaFileProvider>>());
});
builder.Services.AddSingleton<SessionArchiver>();
builder.Services.AddSingleton<AgentExecutionService>();

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
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Priority = PuddingCode.Models.EventPriorityLevel.Normal,
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
                    catch (Exception fex) { spLogger.LogWarning(fex, "[Program:SSM→WS] Forward error"); }
                });
            }
        },
        sp.GetRequiredService<ILogger<ConnectorHost>>());
    return host;
});

// ── Cron 定时任务调度 ──────────────────────────────
builder.Services.AddHostedService<CronSchedulerService>();

// ILlmConfigService 已注册（见上方），同时注册 ILlmResolver 兼容旧接口
builder.Services.AddSingleton<ILlmResolver>(sp =>
{
    // DbLlmResolver 的 DB 同步能力保留（启动时将 JSON 同步到 DB）
    return new DbLlmResolver(
        sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<DbLlmResolver>>());
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

// ── Bootstrap 初始化 ─────────────────────────────────
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var stateFilePath = Path.Combine(dataDir, "bootstrap-state.json");

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

var app = builder.Build();

var p2pDiscoveryService = app.Services.GetRequiredService<IP2pDiscoveryService>();
var jsonlSessionWriter = app.Services.GetRequiredService<JsonlSessionWriter>();
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

// ── 启动时应用迁移 ───────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();

    // ── 幂等补列：实体已新增但尚未生成迁移的列，启动时通过 ALTER TABLE 兜底
    //   仿照 MemoryLibraryDbInitializer 的"duplicate column name" 异常吞噬模式。
    //   一旦后续生成正式迁移，这些 ALTER 仍然安全（已存在则忽略）。
    var pendingColumnDdl = new[]
    {
        "ALTER TABLE \"GlobalAgentTemplates\" ADD COLUMN \"MemorySearchMode\" TEXT NOT NULL DEFAULT 'deep';",
        "ALTER TABLE \"GlobalAgentTemplates\" ADD COLUMN \"ReasoningEffort\" TEXT NULL;",
        "ALTER TABLE \"WorkspaceAgentTemplates\" ADD COLUMN \"MemorySearchMode\" TEXT NOT NULL DEFAULT 'deep';",
        "ALTER TABLE \"WorkspaceAgentTemplates\" ADD COLUMN \"ReasoningEffort\" TEXT NULL;",
        "ALTER TABLE \"GlobalAgentTemplates\" ADD COLUMN \"MaxRounds\" INTEGER NOT NULL DEFAULT 200;",
        "ALTER TABLE \"GlobalAgentTemplates\" ADD COLUMN \"MaxElapsedSeconds\" INTEGER NOT NULL DEFAULT 1200;",
        "ALTER TABLE \"GlobalAgentTemplates\" ADD COLUMN \"MaxToolCallsTotal\" INTEGER NOT NULL DEFAULT 100;",
        "ALTER TABLE \"WorkspaceAgentTemplates\" ADD COLUMN \"MaxRounds\" INTEGER NOT NULL DEFAULT 200;",
        "ALTER TABLE \"WorkspaceAgentTemplates\" ADD COLUMN \"MaxElapsedSeconds\" INTEGER NOT NULL DEFAULT 1200;",
        "ALTER TABLE \"WorkspaceAgentTemplates\" ADD COLUMN \"MaxToolCallsTotal\" INTEGER NOT NULL DEFAULT 100;",
    };
    foreach (var ddl in pendingColumnDdl)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(ddl);
            app.Logger.LogInformation("[Schema] 已补列：{Ddl}", ddl);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // 幂等：列已存在，忽略
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Schema] 幂等补列失败（将继续启动）：{Ddl}", ddl);
        }
    }

    // ── ADR-016：幂等建表 — session_event_log / session_sub_agents
    var pendingTableDdl = new[]
    {
        // session_event_log — 会话事件日志（append-only）
        @"CREATE TABLE IF NOT EXISTS session_event_log (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id      TEXT    NOT NULL,
            workspace_id    TEXT    NOT NULL,
            sequence_num    INTEGER NOT NULL,
            event_type      TEXT    NOT NULL,
            data            TEXT    NOT NULL,
            recorded_at     TEXT    NOT NULL,
            UNIQUE(session_id, sequence_num)
        );",
        @"CREATE INDEX IF NOT EXISTS idx_sel_session_seq ON session_event_log(session_id, sequence_num);",
        @"CREATE INDEX IF NOT EXISTS idx_sel_workspace_time ON session_event_log(workspace_id, recorded_at);",

        // session_sub_agents — 子代理状态追踪
        @"CREATE TABLE IF NOT EXISTS session_sub_agents (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            parent_session_id   TEXT    NOT NULL,
            parent_agent_id     TEXT,
            sub_session_id      TEXT    NOT NULL UNIQUE,
            status              TEXT    NOT NULL DEFAULT 'running',
            template_id         TEXT,
            model_id            TEXT,
            task_summary        TEXT    NOT NULL,
            spawned_at          TEXT    NOT NULL,
            completed_at        TEXT,
            success             INTEGER,
            reply_summary       TEXT,
            error_summary       TEXT,
            full_result_json    TEXT
        );",
        @"CREATE INDEX IF NOT EXISTS idx_ssa_parent ON session_sub_agents(parent_session_id, status);",
        @"CREATE INDEX IF NOT EXISTS idx_ssa_sub ON session_sub_agents(sub_session_id);",

        // runtime_activity — 运行时活动诊断日志
        @"CREATE TABLE IF NOT EXISTS runtime_activity (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            activity_id         TEXT    NOT NULL UNIQUE,
            trace_id            TEXT    NOT NULL,
            correlation_id      TEXT    NOT NULL,
            session_id          TEXT,
            workspace_id        TEXT,
            execution_id        TEXT,
            parent_execution_id TEXT,
            sub_agent_id        TEXT,
            event_id            TEXT,
            connector_id        TEXT,
            user_id             TEXT,
            component           TEXT    NOT NULL,
            operation           TEXT    NOT NULL,
            status              TEXT    NOT NULL,
            started_at_utc      TEXT    NOT NULL,
            ended_at_utc        TEXT,
            duration_ms         INTEGER,
            severity            TEXT    NOT NULL DEFAULT 'info',
            summary             TEXT,
            metadata_json       TEXT,
            error_code          TEXT,
            error_message       TEXT
        );",
        @"CREATE INDEX IF NOT EXISTS idx_ra_trace ON runtime_activity(trace_id);",
        @"CREATE INDEX IF NOT EXISTS idx_ra_session ON runtime_activity(session_id);",
        @"CREATE INDEX IF NOT EXISTS idx_ra_execution ON runtime_activity(execution_id);",
        @"CREATE INDEX IF NOT EXISTS idx_ra_component ON runtime_activity(component);",
        @"CREATE INDEX IF NOT EXISTS idx_ra_started ON runtime_activity(started_at_utc);",
    };
    foreach (var ddl in pendingTableDdl)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(ddl);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Schema] ADR-016 建表失败（将继续启动）：{Ddl}", ddl[..Math.Min(ddl.Length, 80)]);
        }
    }

    var pendingSessionEventTraceColumnDdl = new[]
    {
        "ALTER TABLE session_event_log ADD COLUMN trace_id TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN correlation_id TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN execution_id TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN parent_execution_id TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN sub_agent_id TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN component TEXT NULL;",
        "ALTER TABLE session_event_log ADD COLUMN operation TEXT NULL;",
    };
    foreach (var ddl in pendingSessionEventTraceColumnDdl)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(ddl);
            app.Logger.LogInformation("[Schema] 已补会话事件追踪列：{Ddl}", ddl);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // 幂等：列已存在，忽略
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Schema] 会话事件追踪列补齐失败（将继续启动）：{Ddl}", ddl);
        }
    }

    // ── 幂等种子：记忆工具 Capabilities（启动时幂等插入，正式迁移来之前兜底）
    var now = DateTimeOffset.UtcNow;
    var memoryCaps = new (string CapabilityId, string Name, string ToolName, string Description, string ToolDescription, int SortOrder)[]
    {
        ("cap-search-memory", "搜索记忆", "search_memory", "允许 Agent 使用 search_memory 工具搜索记忆图书馆中的事实和偏好", "Search the user's memory library for related facts, preferences, and knowledge.", 60),
        ("cap-save-memory", "保存记忆", "save_memory", "允许 Agent 使用 save_memory 工具主动写入事实、偏好、摘要到记忆图书馆", "Save or update a fact, preference, summary, or chapter into the user's memory library.", 70),
        ("cap-manage-memory", "管理记忆图书馆", "manage_memory", "允许 Agent 使用 manage_memory 工具管理记忆图书馆结构（Book/Chapter/指针CRUD）", "Manage memory library structure: create/list/update/delete books, chapters, and pointers.", 80),
        ("cap-grep-memory", "全文检索记忆", "grep_memory", "允许 Agent 使用 grep_memory 工具执行全文搜索、Book内检索、目录浏览", "Full-text search across the memory library with FTS5, in-book search, and table-of-contents listing.", 90),
    };
    foreach (var (capId, name, toolName, desc, toolDesc, sortOrder) in memoryCaps)
    {
        try
        {
            var exists = await db.Capabilities.AnyAsync(c => c.CapabilityId == capId);
            if (!exists)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Capabilities (CapabilityId, Name, ToolName, Description, ToolDescription, RequiresShellExecution, RequiresFileWrite, RequiresNetworkAccess, IsEnabled, SortOrder, CreatedAt, UpdatedAt) VALUES ({0}, {1}, {2}, {3}, {4}, 0, 0, 0, 1, {5}, {6}, {7})",
                    capId, name, toolName, desc, toolDesc, sortOrder, now, now);
                app.Logger.LogInformation("[Seed] 已插入 Capability: {CapId}", capId);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Seed] 幂等插入 Capability 失败（继续启动）: {CapId}", capId);
        }
    }

    var controllerDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
    await using (var controllerDb = await controllerDbFactory.CreateDbContextAsync())
    {
        await controllerDb.Database.EnsureCreatedAsync();
    }

    var memoryDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
    await MemoryDbInitializer.InitializeAsync(memoryDbFactory);

    var libraryDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryLibraryDbContext>>();
    await MemoryLibraryDbInitializer.InitializeAsync(libraryDbFactory);

    var workspaceCatalog = scope.ServiceProvider.GetRequiredService<InMemoryWorkspaceCatalog>();
    await workspaceCatalog.LoadAsync();

    var runtimeDispatcher = scope.ServiceProvider.GetRequiredService<RuntimeDispatcher>();
    runtimeDispatcher.SetFallbackEndpoint("http://localhost:8080");

    // ── JSON 配置种子（幂等 Upsert）──────────────────
    var configSeed = scope.ServiceProvider.GetRequiredService<JsonConfigSeedService>();
    await configSeed.SeedAsync();
}

// ── 错误处理 ─────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

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

// ── 静态文件 ─────────────────────────────────────────
app.MapStaticAssets();
app.UseStaticFiles();

// ── API 路由（必须在 Fallback 前）────────────────────
app.MapControllers();

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
        LlmConfig = llmConfigService.GetDefault() ?? new LlmConfig(),
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

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

public sealed record ChatRequest
{
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
}

public partial class Program { }
