using PuddingDesktop.Configuration;

namespace PuddingDesktop.Diagnostics;

/// <summary>
/// Minimal file diagnostic available before Core and the normal logging stack exist.
/// </summary>
public static class DesktopDiagnosticLog
{
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(
        DesktopBootstrapPathProvider.GetDirectoryPath(),
        "logs",
        "desktop.log");

    public static void Write(string category, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O} [{category}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never replace the original failure.
        }
    }
}
