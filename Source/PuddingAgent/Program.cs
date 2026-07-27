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

// The composition root must fail at startup rather than at the first chat request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateScopes = true;
    o.ValidateOnBuild = true;
});
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

builder.AddPuddingApplicationServices(dataPaths, bootstrapConfiguration, aspnetcoreEnvironment);
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
            var channelConfiguration = app.Services
                .GetRequiredService<ChannelConfigurationFileService>();
            await channelConfiguration.MigrateLegacyAgentFeishuBindingsAsync(
                CancellationToken.None);
            var connectorHost = app.Services.GetRequiredService<ConnectorHost>();
            progLogger.LogWarning("[Program] ConnectorHost resolved, getting connectors...");
            var connectors = app.Services.GetServices<IPuddingConnector>().ToList();
            var feishuConnectors = await app.Services
                .GetRequiredService<FeishuConnectorFactory>()
                .CreateAsync(CancellationToken.None);
            connectors.AddRange(feishuConnectors);
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
        app.Services
            .GetRequiredService<ConnectorHost>()
            .StopAllAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "[ConnectorHost] Graceful stop failed.");
    }

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

app.MapPuddingApplication();
await app.InitializePuddingDataAsync();
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
    Directory.CreateDirectory(Path.GetDirectoryName(paths.ErrorLogFile)!);
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
      "preferredProviderId": "deepseek",
      "preferredModelId": "deepseek-v4-pro",
      "isEnabled": true
    }
    """;
    File.WriteAllText(manifestPath, manifest);

    // 管理兼容镜像；主 Agent 执行模型由 manifest preferred* 字段决定。
    var configDir = paths.AgentInstanceConfigRoot(instanceId);
    Directory.CreateDirectory(configDir);
    var llmConfig = """
    {
      "conscious": {
        "providerId": "deepseek",
        "modelId": "deepseek-v4-pro"
      },
      "subconscious": {
        "providerId": "deepseek",
        "modelId": "deepseek-v4-flash"
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

public partial class Program { }
