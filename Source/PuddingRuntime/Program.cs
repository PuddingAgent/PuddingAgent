using PuddingRuntime.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ── Runtime 服务 ──────────────────────────────────────
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSingleton<AgentExecutionService>();

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.Run();
