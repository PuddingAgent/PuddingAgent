namespace PuddingCodeIntelligence.Services;

internal static class CodePathIdentity
{
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool TryNormalizeDirectoryPath(string path, out string normalizedPath, out string? error)
    {
        try
        {
            normalizedPath = NormalizeDirectoryPath(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    public static string NormalizePathForIdentity(string normalizedPath) =>
        OperatingSystem.IsWindows()
            ? normalizedPath.ToUpperInvariant()
            : normalizedPath;
}
