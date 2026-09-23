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

    /// <summary>
    /// Recursively collects all .gitignore patterns from the project root directory.
    /// Skips directories listed in <see cref="NoiseDirNames"/>.
    /// </summary>
    public static HashSet<string> CollectGitIgnorePatterns(string projectRoot)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
            return patterns;

        CollectGitIgnorePatternsRecursive(projectRoot, patterns);
        return patterns;
    }

    private static void CollectGitIgnorePatternsRecursive(string directory, HashSet<string> patterns)
    {
        try
        {
            // Check for .gitignore in current directory
            var gitignorePath = Path.Combine(directory, ".gitignore");
            if (File.Exists(gitignorePath))
            {
                ParseGitIgnoreFile(gitignorePath, patterns);
            }

            // Recurse into subdirectories, skipping noise directories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (NoiseDirNames.Contains(dirName))
                    continue;

                CollectGitIgnorePatternsRecursive(subDir, patterns);
            }
        }
        catch (IOException)
        {
            // Skip directories we cannot access
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we are not authorized to access
        }
    }

    private static void ParseGitIgnoreFile(string filePath, HashSet<string> patterns)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                // Skip negation rules (simplified handling)
                if (line.StartsWith('!'))
                    continue;

                // Remove trailing slash (directory indicator)
                line = line.TrimEnd('/');

                // Remove leading slash (root-relative indicator)
                line = line.TrimStart('/');

                if (string.IsNullOrEmpty(line))
                    continue;

                // Convert to a **/ prefixed matching pattern
                patterns.Add($"**/{line}");
            }
        }
        catch (IOException)
        {
            // Skip files we cannot read
        }
        catch (UnauthorizedAccessException)
        {
            // Skip files we are not authorized to read
        }
    }

    /// <summary>
    /// Checks whether a relative path matches any of the collected gitignore patterns.
    /// Uses simple EndsWith / Contains matching.
    /// </summary>
    public static bool MatchesGitIgnore(string relativePath, HashSet<string> patterns)
    {
        if (string.IsNullOrEmpty(relativePath) || patterns.Count == 0)
            return false;

        // Normalize separators to forward slash for consistent matching
        var normalized = relativePath.Replace('\\', '/');

        foreach (var pattern in patterns)
        {
            // pattern is in the form **/something
            // Extract the core part after **/
            var core = pattern.StartsWith("**/") ? pattern[3..] : pattern;

            // Match: path ends with the pattern, or contains it as a segment
            if (normalized.EndsWith(core, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains($"/{core}/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains($"/{core}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Facade method that combines <see cref="NoiseDirNames"/> and gitignore pattern checks.
    /// Returns true if the file should be excluded from indexing.
    /// </summary>
    public static bool ShouldExclude(string filePath, string projectRoot, HashSet<string>? gitIgnorePatterns = null)
    {
        // First check noise directory names
        if (IsNoisePath(filePath))
            return true;

        // Then check gitignore patterns if provided
        if (gitIgnorePatterns is { Count: > 0 })
        {
            // Compute relative path from project root
            string relativePath;
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                var fullRoot = Path.GetFullPath(projectRoot);
                relativePath = Path.GetRelativePath(fullRoot, fullPath);
            }
            catch
            {
                relativePath = filePath;
            }

            if (MatchesGitIgnore(relativePath, gitIgnorePatterns))
                return true;
        }

        return false;
    }
}
