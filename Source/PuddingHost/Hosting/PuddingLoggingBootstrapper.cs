using PuddingCode.Configuration;
using PuddingCode.Observability;
using Serilog;
using Serilog.Events;

namespace PuddingHost.Hosting;

/// <summary>
/// Configures Serilog structured logging from Program.cs.
/// Includes: component-specific sinks, error log, system log, console output.
/// </summary>
public static class PuddingLoggingBootstrapper
{
    private const long MaxFileSize = 1_048_576;
    private const int RetainedFiles = 200;

    public static void Configure(
        PuddingDataPaths dataPaths,
        IConfiguration bootstrapConfiguration)
    {
        var logLevel = Environment.GetEnvironmentVariable("PUDDING_LOG_LEVEL") ?? "Information";
        var minLevel = logLevel.Equals("Debug", StringComparison.OrdinalIgnoreCase)
            ? LogEventLevel.Debug
            : LogEventLevel.Information;

        var fileOutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [trace:{TraceId}] [session:{SessionId}] {Message:lj}{NewLine}{Exception}";

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
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [trace:{TraceId}] {Message:lj}{NewLine}{Exception}")
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
    }
}
