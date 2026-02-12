using System;
using System.IO;

namespace PuddingCodeDesktop.Services;

/// <summary>
/// Simple file logger for PuddingCode Desktop.
/// Writes to ~/.pudding/logs/desktop-{date}.log
/// </summary>
public static class PuddingLogger
{
    private static readonly string s_logDir;
    private static readonly string s_logFile;
    private static readonly object s_lock = new();

    /// <summary>Raised on every log entry (for UI display). Always on the calling thread.</summary>
    public static event Action<string>? EntryLogged;

    public static string LogFilePath => s_logFile;

    static PuddingLogger()
    {
        s_logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pudding", "logs");
        try { Directory.CreateDirectory(s_logDir); } catch { /* best effort */ }
        s_logFile = Path.Combine(s_logDir, $"desktop-{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Log(string level, string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var entry = $"[{ts}] [{level}] {message}";

        // Write to file (thread-safe)
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

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Error(string message, Exception ex) => Log("ERROR", $"{message}: {ex}");
    public static void Debug(string message) => Log("DEBUG", message);
}
