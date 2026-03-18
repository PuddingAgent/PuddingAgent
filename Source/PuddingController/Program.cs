using PuddingController.Services;
using PuddingGateway;
using PuddingGateway.Adapters;

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

// ── 初始化 ──────────────────────────────────────────
var gatewayHost = app.Services.GetRequiredService<GatewayAdapterHost>();
gatewayHost.Register(new CliGatewayAdapter());
gatewayHost.Register(new EmailGatewayAdapter());
gatewayHost.Register(new WebChatGatewayAdapter());

var workspaceCatalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
workspaceCatalog.SeedDefaults();

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
