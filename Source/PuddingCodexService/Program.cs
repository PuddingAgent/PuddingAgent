using System.Net;
using ModelContextProtocol.Protocol;
using PuddingCodexService;
using PuddingCodexService.Services;
using PuddingCodexService.Tools;

var builder = WebApplication.CreateBuilder(args);

var options = CodexServiceOptions.FromConfiguration(builder.Configuration);
options.Validate();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<FileCodexTaskStore>();
builder.Services.AddSingleton<ICodexExecutor, CodexMcpExecutor>();
builder.Services.AddSingleton<CodexTaskCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CodexTaskCoordinator>());
builder.Services.AddSingleton<SupervisorRestartRequestWriter>();

builder.Services.AddMcpServer(serverOptions =>
    {
        serverOptions.ServerInfo = new Implementation
        {
            Name = "PuddingCodexService",
            Title = "Pudding Codex Service",
            Version = "0.1.0",
        };
    })
    // Task identity is owned by CodexTaskCoordinator rather than by an HTTP MCP session.
    // A new Pudding process can therefore reconnect and query an existing task.
    .WithHttpTransport(transportOptions => transportOptions.Stateless = true)
    .WithTools<CodexTaskTools>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("PuddingCodexService only accepts loopback connections.");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PuddingCodexService" }));
app.MapGet("/readyz", () => Results.Ok(new { ready = true }));
app.MapMcp("/mcp");

await app.RunAsync();

public partial class Program;
