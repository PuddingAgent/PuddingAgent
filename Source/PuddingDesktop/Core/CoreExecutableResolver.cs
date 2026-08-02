namespace PuddingDesktop.Core;

/// <summary>
/// Resolves the path to PuddingAgent.exe for Core child process startup.
/// Search order:
/// 1. User-configured CoreExecutablePath from bootstrap settings
/// 2. ./core/PuddingAgent.exe relative to Desktop executable
/// 3. ../PuddingAgent/PuddingAgent.exe (dev layout)
/// </summary>
public static class CoreExecutableResolver
{
    public static string Resolve(string? configuredPath)
    {
        // 1. User-configured absolute path
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullConfiguredPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullConfiguredPath))
                throw new FileNotFoundException(
                    $"Configured Core executable does not exist: {fullConfiguredPath}",
                    fullConfiguredPath);
            return fullConfiguredPath;
        }

        // 2. Side-by-side: Desktop.exe / core / PuddingAgent.exe
        var desktopDir = AppContext.BaseDirectory;
        var sideBySide = Path.Combine(desktopDir, "core", "PuddingAgent.exe");
        if (File.Exists(sideBySide))
            return sideBySide;

        // 3. Dev layout: Source/PuddingDesktop / ../PuddingAgent/bin/...
        var devPath = Path.GetFullPath(Path.Combine(desktopDir,
            "..", "..", "..", "..", "PuddingAgent"));
        // Try to find PuddingAgent.exe under the dev output tree
        if (Directory.Exists(devPath))
        {
            var candidate = Directory
                .GetFiles(devPath, "PuddingAgent.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null)
                return candidate;
        }

        throw new FileNotFoundException(
            "Cannot find PuddingAgent.exe. Set CoreExecutablePath in desktop.json " +
            $"or place core/PuddingAgent.exe next to PuddingDesktop.exe. Searched: {desktopDir}");
    }
}
