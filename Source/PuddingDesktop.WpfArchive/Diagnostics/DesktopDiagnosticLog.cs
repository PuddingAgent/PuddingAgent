using PuddingDesktop.Configuration;

namespace PuddingDesktop.Diagnostics;

/// <summary>
/// Minimal file diagnostic available before Core and the normal logging stack exist.
/// </summary>
public static class DesktopDiagnosticLog
{
    private static readonly object Sync = new();
    public static DesktopRunJournal? Journal { get; set; }

    public static string FilePath => Path.Combine(
        DesktopBootstrapPathProvider.GetDirectoryPath(),
        "logs",
        "desktop.log");

    public static void Write(string category, Exception exception, bool fatal = false)
    {
        try
        {
            Journal?.Exception(category, exception, fatal);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024)
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O} [pid={Environment.ProcessId}] [run={Journal?.State.RunId ?? "startup"}] [{DesktopRunJournal.SafeText(category, 128)}]{Environment.NewLine}{DesktopRunJournal.SafeText(exception.ToString(), 16 * 1024)}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never replace the original failure.
        }
    }
}
