using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingDesktop.Diagnostics;

public sealed record DesktopProcessIdentity(int ProcessId, DateTime StartedUtc);
public sealed record DesktopScene(string ShellState = "Starting", string CoreState = "Unknown",
    int? CoreProcessId = null, int? CoreExitCode = null, string? CoreError = null, string? CoreLogTail = null,
    long? PrivateBytes = null, long? WorkingSetBytes = null);
public sealed record DesktopRunState
{
    public string Schema { get; init; } = "pudding.desktop-run/1";
    public required string RunId { get; init; }
    public required DesktopProcessIdentity Process { get; init; }
    public string? Build { get; init; }
    public string Os { get; init; } = Environment.OSVersion.VersionString;
    public string Runtime { get; init; } = Environment.Version.ToString();
    public string? ExecutablePath { get; init; } = Environment.ProcessPath;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Phase { get; init; } = "running";
    public string? ExitReason { get; init; }
    public string? FatalSource { get; init; }
    public string? FatalException { get; init; }
    public bool ManagedProcessExitObserved { get; init; }
    public DesktopScene Scene { get; init; } = new();
}

/// <summary>Durable, bounded evidence; never reads configuration, credentials or conversation databases.</summary>
public sealed class DesktopRunJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private DesktopRunState _state;
    public string DirectoryPath { get; }
    public DesktopRunState State { get { lock (_sync) return _state; } }
    public string? LastWriteError { get; private set; }

    public DesktopRunJournal(string root, DesktopProcessIdentity process, string? build)
    {
        var id = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{process.ProcessId}-{Guid.NewGuid():N}";
        DirectoryPath = Path.Combine(root, id);
        _state = new() { RunId = id, Process = process, Build = SafeText(build, 256) };
        Record("process_started");
    }

    public void Capture(DesktopScene scene) => Change(s => s with { Scene = scene with
    {
        ShellState = SafeText(scene.ShellState, 128), CoreState = SafeText(scene.CoreState, 128),
        CoreError = SafeText(scene.CoreError, 2048), CoreLogTail = SafeText(scene.CoreLogTail, 24 * 1024)
    } });
    public void Record(string name, string? detail = null) => Change(s => s, name, detail);
    public void RequestExit(string reason) => Change(s => s with { Phase = "stopping", ExitReason = SafeText(reason, 256) }, "exit_requested", reason);
    public void CancelExit() => Change(s => s with { Phase = "running", ExitReason = null }, "shutdown_failed");
    public void CompleteExit() => Change(s => s with { Phase = "shutdown_completed" }, "shutdown_completed");
    public void ProcessExit() => Change(s => s with { ManagedProcessExitObserved = true }, "managed_process_exit");
    public void Exception(string source, Exception exception, bool fatal) => Change(s => fatal
        ? s with { FatalSource = SafeText(source, 256), FatalException = SafeText(exception.ToString(), 16 * 1024) }
        : s, fatal ? "fatal_exception" : "exception", source + "\n" + exception);

    private void Change(Func<DesktopRunState, DesktopRunState> update, string? name = null, string? detail = null)
    {
        lock (_sync)
        {
            _state = update(_state) with { UpdatedAt = DateTimeOffset.UtcNow };
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                WriteAtomic(Path.Combine(DirectoryPath, "state.json"), _state);
                if (name is not null)
                {
                    var path = Path.Combine(DirectoryPath, "lifecycle.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                        File.Move(path, path + ".previous", overwrite: true);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { at = _state.UpdatedAt,
                        name = SafeText(name, 128), detail = SafeText(detail, 16 * 1024) }) + "\n");
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                LastWriteError = null;
            }
            catch (Exception ex) { LastWriteError = ex.GetType().Name; Debug.WriteLine($"Desktop evidence write failed: {ex.GetType().Name}"); }
        }
    }

    public static DesktopRunState? ReadState(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "state.json");
            if (new FileInfo(path).Length > 256 * 1024) return null;
            var state = JsonSerializer.Deserialize<DesktopRunState>(File.ReadAllText(path), Json);
            return state?.Schema == "pudding.desktop-run/1" ? state : null;
        }
        catch { return null; }
    }

    public static string ClassifyExit(DesktopRunState state, int? exitCode) =>
        state.FatalSource is not null ? "unhandled_exception" :
        exitCode is not null and not 0 ? "abnormal_exit_code" :
        state.Phase == "shutdown_completed" ? "clean_shutdown" :
        state.ManagedProcessExitObserved ? "uncoordinated_process_exit" : "unobserved_termination";

    /// <summary>Called only after the identified process is known to have exited. No inference about who killed it.</summary>
    public static void RecordObservedExit(string directory, int? exitCode, string source)
    {
        var state = ReadState(directory);
        if (state is null) return;
        var cause = ClassifyExit(state, exitCode);
        var evidence = new { schema = "pudding.desktop-exit/1", observedAt = DateTimeOffset.UtcNow,
            source, cause, exitCode, exitCodeHex = exitCode is null ? null : $"0x{unchecked((uint)exitCode.Value):X8}",
            lastSnapshotAt = state.UpdatedAt, terminationInitiator = "unknown", state };
        WriteAtomic(Path.Combine(directory, source == "observer" ? "exit.json" : "recovered-exit.json"), evidence);
        if (cause != "clean_shutdown") WriteAtomic(Path.Combine(directory, "incident.json"), evidence);
    }

    public static bool? IsSameProcessAlive(DesktopProcessIdentity identity)
    {
        try
        {
            using var p = Process.GetProcessById(identity.ProcessId);
            return !p.HasExited && p.StartTime.ToUniversalTime() == identity.StartedUtc;
        }
        catch (ArgumentException) { return false; }
        catch { return null; } // Access denied is not evidence of death.
    }

    public static void RecoverAndPrune(string root, Func<DesktopProcessIdentity, bool?>? alive = null, int retain = 20)
    {
        if (!Directory.Exists(root)) return;
        alive ??= IsSameProcessAlive;
        var finished = new List<string>();
        foreach (var dir in Directory.GetDirectories(root).OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                // Never follow a directory link while collecting or pruning evidence.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var state = ReadState(dir);
                if (state is null || alive(state.Process) != false) continue;
                if (!File.Exists(Path.Combine(dir, "exit.json")) && !File.Exists(Path.Combine(dir, "recovered-exit.json")))
                    RecordObservedExit(dir, null, "next_startup");
                finished.Add(dir);
            }
            catch { /* One damaged record cannot prevent launcher startup. */ }
        }
        foreach (var dir in finished.Skip(Math.Max(1, retain)))
        {
            try
            {
                // Only remove the files we own; never recursively delete unknown content.
                foreach (var name in new[] { "state.json", "state.json.tmp", "lifecycle.jsonl", "lifecycle.jsonl.previous",
                    "exit.json", "exit.json.tmp", "incident.json", "incident.json.tmp", "recovered-exit.json", "recovered-exit.json.tmp", "observer.json", "observer.json.tmp" })
                    File.Delete(Path.Combine(dir, name));
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }

    public static void WriteAtomic(string path, object value)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static string SafeText(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Redact before truncating, including quoted values, bearer schemes and URL query values.
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        try
        {
            value = Regex.Replace(value, @"(authorization\s*[:=]\s*)(?:Bearer\s+|Basic\s+)?[^\r\n,}]+", "$1[REDACTED]", options, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, "(\"?(?:[\\w-]*token|api[_-]?key|app[_-]?secret|password|secret)\"?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)", "$1[REDACTED]", options, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(https?://[^\s?]+)\?[^\s]+", "$1?[REDACTED]", options, TimeSpan.FromMilliseconds(100));
            return value.Length <= limit ? value : value[..limit] + "\n[TRUNCATED]";
        }
        catch (RegexMatchTimeoutException) { return "[REDACTED: input too complex]"; }
    }
}
