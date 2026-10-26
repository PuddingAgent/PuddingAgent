using System.Text;

namespace PuddingDesktop.Guardian;

/// <summary>
/// Append-only deployment log. <see cref="GuardianState"/> deliberately keeps only the failure type
/// name (no command output, no credentials), which left a rolled-back deployment undiagnosable after
/// the fact: the status document recorded "TimeoutException" and nothing else. Every phase transition
/// is appended here so the sequence that led to a rollback survives the process.
/// </summary>
/// <param name="directory">Guardian state directory; the log sits next to state.json.</param>
/// <param name="maxBytes">Rotation threshold; one previous file is kept as <c>guardian.log.1</c>.</param>
public sealed class GuardianLog(string directory, long maxBytes = 1 << 20)
{
    private readonly object _gate = new();

    public string Path => System.IO.Path.Combine(directory, "guardian.log");

    public void Append(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(Path) && new FileInfo(Path).Length >= maxBytes)
                File.Move(Path, Path + ".1", overwrite: true);
            File.AppendAllText(
                Path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }
}
