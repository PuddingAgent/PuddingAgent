namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Centralized exclude patterns for code indexing.
/// Prevents indexing of build artifacts, dependency packages, and IDE configuration directories.
/// </summary>
public static class IndexExcludePatterns
{
    /// <summary>
    /// Directory names to exclude (case-insensitive exact match on directory name).
    /// </summary>
    public static readonly HashSet<string> NoiseDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode",
        "node_modules", "packages", "dist", "build", ".next", "out",
        "__pycache__", ".venv", "venv", ".tox", ".eggs",
        "coverage", ".nyc_output", ".pytest_cache",
        ".pudding-code", "TestResults",
        "Debug", "Release", "x64", "x86", "ARM", "ARM64",
    };

    /// <summary>
    /// Determines whether a file path contains any noise directory segment.
    /// Checks for directory separator boundaries to ensure exact segment matching.
    /// </summary>
    public static bool IsNoisePath(string filePath)
    {
        foreach (var segment in NoiseDirNames)
        {
            if (filePath.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}") ||
                filePath.EndsWith($"{Path.DirectorySeparatorChar}{segment}") ||
                filePath.StartsWith($"{segment}{Path.DirectorySeparatorChar}"))
                return true;
        }

        return false;
    }
}
