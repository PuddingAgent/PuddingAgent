using System;
using System.IO;
using System.Threading;

namespace PuddingAssistantDesktop.Services;

/// <summary>Log severity levels, ordered from most to least verbose.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info  = 2,
    Warn  = 3,
    Error = 4,
    Off   = 5
}

/// <summary>
/// Simple file logger for PuddingAssistant Desktop.
/// Writes to ~/.pudding/logs/desktop-{date}.log
///
/// <para><b>Debug switch:</b> Set <see cref="MinLevel"/> to control verbosity.
/// Default is <see cref="LogLevel.Trace"/> (all on). Set to <see cref="LogLevel.Info"/>
/// or higher for release builds.</para>
/// </summary>
public static class PuddingLogger
{
    private static readonly string s_logDir;
    private static readonly string s_logFile;
    private static readonly object s_lock = new();

    /// <summary>
    /// Global minimum log level. Messages below this level are discarded.
    /// Default: <see cref="LogLevel.Trace"/> (全开).
    /// </summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Trace;

    /// <summary>
    /// Master switch. Set to <c>false</c> to disable all logging (file + event).
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Raised on every accepted log entry (for UI display). Always on the calling thread.</summary>
    public static event Action<string>? EntryLogged;

    public static string LogFilePath => s_logFile;

    // ──── TraceID (async-local, flows through Task/await) ────

    private static readonly AsyncLocal<string?> s_traceId = new();

    /// <summary>Current trace ID (flows through async calls). Null when no trace is active.</summary>
    public static string? TraceId
    {
        get => s_traceId.Value;
        set => s_traceId.Value = value;
    }

    /// <summary>
    /// Begin a new trace scope. Returns an <see cref="IDisposable"/> that clears the TraceID on dispose.
    /// Usage: <c>using var _ = PuddingLogger.BeginTrace("myTraceId");</c>
    /// </summary>
    public static IDisposable BeginTrace(string traceId)
    {
        var prev = s_traceId.Value;
        s_traceId.Value = traceId;
        return new TraceScope(prev);
    }

    /// <summary>Generate a short random trace ID (6 hex chars).</summary>
    public static string NewTraceId() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    static PuddingLogger()
    {
        s_logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pudding", "logs");
        try { Directory.CreateDirectory(s_logDir); } catch { /* best effort */ }
        s_logFile = Path.Combine(s_logDir, $"desktop-{DateTime.Now:yyyyMMdd}.log");
    }

    // ──── Core ────

    public static void Log(LogLevel level, string category, string message)
    {
        if (!Enabled || level < MinLevel) return;

        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var tag = string.IsNullOrEmpty(category) ? "" : $"[{category}] ";
        var trace = s_traceId.Value is { } tid ? $"[T:{tid}] " : "";
        var entry = $"[{ts}] [{level.ToString().ToUpperInvariant(),-5}] {tag}{trace}{message}";

        try
        {
            lock (s_lock)
            {
                File.AppendAllText(s_logFile, entry + Environment.NewLine);
            }
        }
        catch { /* best effort */ }

        EntryLogged?.Invoke(entry);
    }

    /// <summary>Legacy overload (no category).</summary>
    public static void Log(string level, string message) =>
        Log(ParseLevel(level), "", message);

    // ──── Convenience (no category) ────

    public static void Trace(string message) => Log(LogLevel.Trace, "", message);
    public static void Debug(string message) => Log(LogLevel.Debug, "", message);
    public static void Info(string message)  => Log(LogLevel.Info,  "", message);
    public static void Warn(string message)  => Log(LogLevel.Warn,  "", message);
    public static void Error(string message) => Log(LogLevel.Error, "", message);
    public static void Error(string message, Exception ex) => Log(LogLevel.Error, "", $"{message}: {ex}");

    // ──── Category-aware convenience ────

    public static void Trace(string category, string message) => Log(LogLevel.Trace, category, message);
    public static void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    public static void Info(string category, string message)  => Log(LogLevel.Info,  category, message);
    public static void Warn(string category, string message)  => Log(LogLevel.Warn,  category, message);
    public static void Error(string category, string message) => Log(LogLevel.Error, category, message);

    // ──── Swarm-specific shortcuts ────

    /// <summary>Swarm-category trace (topology, selection, rebuild views).</summary>
    public static void SwarmTrace(string message) => Log(LogLevel.Trace, "SWARM", message);

    /// <summary>Swarm-category debug (agent state changes, bubbles, commands).</summary>
    public static void SwarmDebug(string message) => Log(LogLevel.Debug, "SWARM", message);

    /// <summary>Swarm-category info (high-level operations: spawn, kill, simulation phases).</summary>
    public static void SwarmInfo(string message)  => Log(LogLevel.Info,  "SWARM", message);

    /// <summary>Swarm-category warn (agent errors, unexpected states).</summary>
    public static void SwarmWarn(string message)  => Log(LogLevel.Warn,  "SWARM", message);

    /// <summary>Swarm-category error.</summary>
    public static void SwarmError(string message) => Log(LogLevel.Error, "SWARM", message);

    // ──── Helpers ────

    private static LogLevel ParseLevel(string level) => level.ToUpperInvariant() switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO"  => LogLevel.Info,
        "WARN"  => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Info
    };

    private sealed class TraceScope(string? previousTraceId) : IDisposable
    {
        public void Dispose() => s_traceId.Value = previousTraceId;
    }
}
