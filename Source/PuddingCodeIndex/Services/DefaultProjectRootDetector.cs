using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Upward-only project root detector. Walks parent directories from the start
/// path, stopping at .git or the first directory containing a known project
/// marker file. Never scans child directories.
/// </summary>
public sealed class DefaultProjectRootDetector : ICodeProjectRootDetector
{
    // Directories that stop the walk even without a marker file — they are
    // never project roots and should be treated as boundaries.
    private static readonly HashSet<string> BoundaryDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".pudding-code",
        "bin",
        "obj",
        "node_modules",
    };

    // Files whose presence in a directory marks it as a project root.
    private static readonly IReadOnlyList<RootDetectorRule> Rules = BuildRules();

    public Task<string?> DetectRootAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!CodePathIdentity.TryNormalizeDirectoryPath(startDirectory, out var current, out _))
            return Task.FromResult<string?>(null);

        if (!Directory.Exists(current))
            return Task.FromResult<string?>(null);

        var rootDir = CodePathIdentity.NormalizeDirectoryPath(
            Path.GetPathRoot(current) ?? string.Empty);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProjectRoot(current))
                return Task.FromResult<string?>(current);

            // Stop at filesystem boundary (drive root or '/')
            if (string.Equals(current, rootDir, CodePathIdentity.PathComparison))
                return Task.FromResult<string?>(null);

            var parent = Directory.GetParent(current);
            if (parent is null)
                return Task.FromResult<string?>(null);

            current = parent.FullName;
        }
    }

    private static bool IsProjectRoot(string directory)
    {
        // Boundary directories are never project roots.
        var dirName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(dirName) || BoundaryDirectories.Contains(dirName))
            return false;

        // .git directory or file is always a project root stop
        var gitPath = Path.Combine(directory, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
            return true;

        foreach (var rule in Rules)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                    directory, rule.Pattern, rule.Options))
                {
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible directories */ }
            catch (IOException) { /* skip I/O errors */ }
        }

        return false;
    }

    private static IReadOnlyList<RootDetectorRule> BuildRules()
    {
        var list = new List<RootDetectorRule>();

        // .NET / C / C++
        Add(list, "*.sln");
        Add(list, "*.slnx");
        Add(list, "*.csproj");
        Add(list, "*.fsproj");
        Add(list, "*.vbproj");
        Add(list, "*.vcxproj");
        Add(list, "CMakeLists.txt");
        Add(list, "Makefile");

        // JS / TS / frontend
        Add(list, "package.json");
        Add(list, "pnpm-workspace.yaml");
        Add(list, "nx.json");
        Add(list, "turbo.json");
        Add(list, "vite.config.*");
        Add(list, "next.config.*");
        Add(list, "angular.json");
        Add(list, "vue.config.*");
        Add(list, "svelte.config.*");

        // Python
        Add(list, "pyproject.toml");
        Add(list, "setup.py");
        Add(list, "setup.cfg");
        Add(list, "requirements.txt");
        Add(list, "Pipfile");
        Add(list, "poetry.lock");

        // Go / Java / JVM
        Add(list, "go.mod");
        Add(list, "pom.xml");
        Add(list, "build.gradle");
        Add(list, "build.gradle.kts");
        Add(list, "settings.gradle");
        Add(list, "settings.gradle.kts");

        // Rust / PHP / Ruby / Elixir / Dart / Deno
        Add(list, "Cargo.toml");
        Add(list, "composer.json");
        Add(list, "Gemfile");
        Add(list, "mix.exs");
        Add(list, "pubspec.yaml");
        Add(list, "deno.json");
        Add(list, "deno.jsonc");

        // IDE project files
        Add(list, "*.xcodeproj");
        Add(list, "*.xcworkspace");
        Add(list, "*.iml");

        return list;
    }

    private static void Add(List<RootDetectorRule> list, string pattern) =>
        list.Add(new(pattern));

    private sealed record RootDetectorRule(string Pattern)
    {
        public SearchOption Options { get; } = SearchOption.TopDirectoryOnly;
    }
}
