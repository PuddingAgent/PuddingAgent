using PuddingRuntime.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ── Runtime 服务 ──────────────────────────────────────
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSingleton<InMemoryRuntimeSessionStore>();
builder.Services.AddSingleton<AgentExecutionService>();

// ── 后台心跳清理服务 ─────────────────────────────────
builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.Run();
