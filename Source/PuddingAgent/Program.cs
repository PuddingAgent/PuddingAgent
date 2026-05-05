using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingController;
using PuddingController.Data;
using PuddingController.Services;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Sandbox;
using PuddingRuntime.Services.Skills;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingAgent.P2P;
using PuddingAgent.Connectors;
using PuddingAgent.Services;
using Serilog;
using Serilog.Events;

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

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(bootstrapConfiguration)
    .MinimumLevel.Information()
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
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
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
}, ServiceLifetime.Singleton);

builder.Services.AddDbContextFactory<PlatformDbContext>(opt =>
{
    opt.UseSqlite(connStr);
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Singleton);

builder.Services.AddSingleton<Sm2JwtSigner>();
builder.Services.AddSingleton<IKeyVaultService, KeyVaultService>();
builder.Services.AddSingleton<AgentTemplateProvider>();
builder.Services.AddSingleton<IAgentTemplateProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());
builder.Services.AddSingleton<IWorkspaceProfileProvider>(sp => sp.GetRequiredService<AgentTemplateProvider>());

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
builder.Services.AddSingleton<SkillRuntime>();
builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
builder.Services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
builder.Services.AddSingleton<AgentExecutionService>();

// ── P2P 发现（局域网 UDP 广播 + HTTP 探活）────────────────
builder.Services.AddSingleton<IP2pDiscoveryService, MdnsDiscoveryService>();

// ── Webhook 连接器 ─────────────────────────────────
builder.Services.AddSingleton<WebhookConnector>();
builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<WebhookConnector>());

// ── Cron 定时任务调度 ──────────────────────────────
builder.Services.AddHostedService<CronSchedulerService>();

// ── LLM 配置 ─────────────────────────────────────────
var llmEndpoint = builder.Configuration["LLM_ENDPOINT"] ?? "https://api.openai.com/v1";
var llmApiKey = builder.Configuration["LLM_API_KEY"] ?? "";
var llmModel = builder.Configuration["LLM_MODEL"] ?? "gpt-4o-mini";
var runtimeEndpoint = builder.Configuration["Pudding:RuntimeEndpoint"] ?? "http://localhost:8080";

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

    var controllerDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
    await using (var controllerDb = await controllerDbFactory.CreateDbContextAsync())
    {
        await controllerDb.Database.EnsureCreatedAsync();
    }

    var memoryDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
    await MemoryDbInitializer.InitializeAsync(memoryDbFactory);

    var workspaceCatalog = scope.ServiceProvider.GetRequiredService<InMemoryWorkspaceCatalog>();
    await workspaceCatalog.LoadAsync();

    var runtimeDispatcher = scope.ServiceProvider.GetRequiredService<RuntimeDispatcher>();
    runtimeDispatcher.SetFallbackEndpoint(runtimeEndpoint);
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

// ── 健康检查 ─────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

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
        LlmConfig = new LlmConfig
        {
            Endpoint = llmEndpoint,
            ApiKey = llmApiKey,
            ModelId = llmModel,
        },
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