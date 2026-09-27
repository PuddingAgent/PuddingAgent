namespace PuddingDesktop.Configuration;

/// <summary>
/// Provides the path to %LOCALAPPDATA%\Pudding\desktop.json.
/// </summary>
public static class DesktopBootstrapPathProvider
{
    public const string OverrideDirectoryEnvironmentVariable = "PUDDING_DESKTOP_HOME";

    public static string GetFilePath()
    {
        return Path.Combine(GetDirectoryPath(), "desktop.json");
    }

    public static string GetDirectoryPath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(OverrideDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Pudding");
    }
}
