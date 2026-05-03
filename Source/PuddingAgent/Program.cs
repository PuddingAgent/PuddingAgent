using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingMemoryEngine;

var builder = WebApplication.CreateBuilder(args);

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
                "http://localhost:3000")
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
    var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Workspace 业务层 ──────────────────────────────────
builder.Services.AddScoped<WorkspaceBusinessService>();

// ── EF Core / 数据库 ──────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/pudding_platform.db";
builder.Services.AddDbContext<PlatformDbContext>(opt =>
{
    opt.UseSqlite(connStr);
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// ── Session（用于 Auth API 的轻量登录态）──────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// ── Runtime 核心服务 ─────────────────────────────────
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSingleton<InMemoryRuntimeSessionStore>();
builder.Services.AddSingleton<SessionMemoryStore>();
builder.Services.AddSingleton<WorkspaceMemoryStore>();
builder.Services.AddSingleton<MemoryBoundaryService>();
builder.Services.AddSingleton<MemoryEngine>();
builder.Services.AddSingleton<AgentExecutionGuardrails>();
builder.Services.AddSingleton<ExecutionControlRegistry>();
builder.Services.AddSingleton<ExecutionJournal>();
builder.Services.AddSingleton<CompletionPolicy>();
builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();
builder.Services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
builder.Services.AddSingleton<AgentExecutionService>();

// ── LLM 配置 ─────────────────────────────────────────
var llmEndpoint = builder.Configuration["LLM_ENDPOINT"] ?? "https://api.openai.com/v1";
var llmApiKey = builder.Configuration["LLM_API_KEY"] ?? "";
var llmModel = builder.Configuration["LLM_MODEL"] ?? "gpt-4o-mini";

builder.Services.AddHttpClient("DirectLlm", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
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

// ── 启动时应用迁移 ───────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();
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

app.Run();

public sealed record ChatRequest
{
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
}