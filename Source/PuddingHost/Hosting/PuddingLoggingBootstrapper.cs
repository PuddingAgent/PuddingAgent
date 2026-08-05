using System.Text;
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
        // Serilog 自检日志：sink 内部异常（写入失败、文件被删、格式化错误）
        // 默认被静默吞掉，曾导致"日志文件 0KB 滚动"问题完全不可诊断。
        // 落盘到 logs\system\serilog-selflog.log 以便事后取证。
        try
        {
            var selfLogDir = Path.GetDirectoryName(dataPaths.SystemLogFile)!;
            Directory.CreateDirectory(selfLogDir);
            Serilog.Debugging.SelfLog.Enable(
                new SelfLogAppendingWriter(Path.Combine(selfLogDir, "serilog-selflog.log")));
        }
        catch
        {
            // SelfLog 初始化失败不应阻止日志系统启动
        }

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

    /// <summary>
    /// 线程安全的追加写入器，供 Serilog SelfLog 使用。
    /// 带 10MB 体积上限，防止异常风暴下自检日志本身撑爆磁盘。
    /// </summary>
    private sealed class SelfLogAppendingWriter : TextWriter
    {
        private const long MaxSelfLogBytes = 10 * 1024 * 1024;
        private readonly string _path;
        private readonly object _gate = new();

        public SelfLogAppendingWriter(string path) => _path = path;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                try
                {
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxSelfLogBytes)
                        return; // 超限后停止写入（保留现场）

                    File.AppendAllText(_path, $"{DateTime.Now:O} {value}{Environment.NewLine}");
                }
                catch
                {
                    // SelfLog 写入失败必须静默
                }
            }
        }

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                WriteLine(value);
        }

        public override void Write(char value)
        {
            // SelfLog 以行写入为主；逐字符写入忽略以避免碎片 IO
        }
    }
}
