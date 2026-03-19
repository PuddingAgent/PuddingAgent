using Microsoft.EntityFrameworkCore;
using PuddingController.Data;
using PuddingController.Services;
using PuddingGateway;
using PuddingGateway.Adapters;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── 基础设施 DI ──────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                  "http://localhost:9528",
                  "http://localhost:9527",
                  "http://localhost:3000",
                  "http://localhost:5173",
                  "http://localhost:8080")
              .AllowAnyHeader()
              .AllowAnyMethod());
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ── PostgreSQL（持久化数据：Workspace、审计、路由决策）──────────
var pgConnStr = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=pudding;Username=pudding;Password=pudding_dev";
builder.Services.AddDbContextFactory<ControllerDbContext>(opt => opt.UseNpgsql(pgConnStr));

// ── Redis（热数据：Session、审批、Runtime 节点注册）──────────────
var redisConnStr = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnStr));

// ── Controller 业务服务 ──────────────────────────────
builder.Services.AddSingleton<InMemoryWorkspaceCatalog>();
builder.Services.AddSingleton<InMemorySessionRepository>();
builder.Services.AddSingleton<InMemoryAuditEventStore>();
builder.Services.AddSingleton<InMemoryRouteDecisionStore>();
builder.Services.AddSingleton<InMemoryApprovalService>();
builder.Services.AddSingleton<AuthorizationService>();
builder.Services.AddSingleton<GatewayEgressService>();
builder.Services.AddSingleton<RuntimeRegistryService>();
builder.Services.AddSingleton<RuntimeDispatcher>();
builder.Services.AddSingleton<SessionRouter>();
builder.Services.AddSingleton<AgentTemplateRegistry>();
builder.Services.AddSingleton<ControllerLlmProxyService>();

// ── 知识基础设施服务 ─────────────────────────────────
builder.Services.AddSingleton<KnowledgeBaseService>();
builder.Services.AddSingleton<UnifiedStorageService>();
builder.Services.AddSingleton<KnowledgeGraphService>();

// ── Gateway 适配器宿主 ──────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<GatewayAdapterHost>>();
    return new GatewayAdapterHost(
        onEventReceived: (envelope, ct) =>
        {
            logger.LogInformation("[Gateway] Received event from channel={Channel} user={User}",
                envelope.ChannelId, envelope.UserExternalId);
            return Task.CompletedTask;
        },
        log: msg => logger.LogInformation("{Msg}", msg));
});

var app = builder.Build();

// ── 初始化：应用迁移（自动建 ctrl_ 表，多服务共享库安全）────────────
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
await using (var db = dbFactory.CreateDbContext())
    await db.Database.MigrateAsync();

// ── 初始化 ──────────────────────────────────────────
var gatewayHost = app.Services.GetRequiredService<GatewayAdapterHost>();
gatewayHost.Register(new CliGatewayAdapter());
gatewayHost.Register(new EmailGatewayAdapter());
gatewayHost.Register(new WebChatGatewayAdapter());

var workspaceCatalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
await workspaceCatalog.LoadAsync();

var dispatcher = app.Services.GetRequiredService<RuntimeDispatcher>();
var runtimeEndpoint = app.Configuration["Pudding:RuntimeEndpoint"];
if (!string.IsNullOrWhiteSpace(runtimeEndpoint))
    dispatcher.SetFallbackEndpoint(runtimeEndpoint);

await gatewayHost.StartAllAsync();

// ── 中间件管道 ──────────────────────────────────────
app.UseCors();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.Run();

