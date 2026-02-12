namespace PuddingFullTextIndex;

/// <summary>
/// 全文索引配置 — 控制哪些文件被索引、大小限制等。
/// 第一版使用硬编码默认值；后续可改为从 JSON 配置读取。
/// </summary>
public sealed record FullTextIndexOptions
{
    /// <summary>直接索引的纯文本文件扩展名（含点，忽略大小写）。</summary>
    public IReadOnlySet<string> PlainTextExtensions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── 编程语言 ──
            ".cs",        // C#
            ".java",      // Java
            ".kt", ".kts", // Kotlin
            ".scala",     // Scala
            ".groovy",    // Groovy
            ".ts", ".tsx", // TypeScript / TSX
            ".js", ".jsx", // JavaScript / JSX
            ".vue",       // Vue SFC
            ".svelte",    // Svelte
            ".py",        // Python
            ".rb",        // Ruby
            ".php",       // PHP
            ".go",        // Go
            ".rs",        // Rust
            ".swift",     // Swift
            ".c", ".h",   // C
            ".cpp", ".hpp", ".cc", ".cxx", // C++
            ".m", ".mm",  // Objective-C
            ".lua",       // Lua
            ".r",         // R
            ".pl", ".pm", // Perl
            ".dart",      // Dart

            // ── 标记/配置/数据 ──
            ".json",
            ".md", ".mdx",
            ".yaml", ".yml",
            ".sql",
            ".csproj", ".vbproj", ".fsproj",
            ".sln", ".slnx",
            ".props", ".targets",
            ".html", ".htm",
            ".css", ".scss", ".less", ".sass",
            ".xml", ".config",
            ".txt",
            ".toml", ".ini", ".cfg",
            ".csv",
            ".graphql", ".gql",
            ".proto",
            ".env", ".env.local",
            ".editorconfig",
            ".gitignore", ".gitattributes",

            // ── Shell / Script ──
            ".sh", ".bash", ".zsh",
            ".ps1", ".psm1", ".psd1",
            ".bat", ".cmd",
            ".dockerfile", ".dockerignore",
            ".makefile", ".mk",
        };

    /// <summary>需解析后索引的扩展名（如 .pdf → PdfPigExtractor）。</summary>
    public IReadOnlySet<string> ParsedExtensions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>忽略的目录名（不含路径，仅匹配目录名本身）。忽略大小写。</summary>
    public IReadOnlySet<string> ExcludedDirectoryNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules",
            ".git",
            ".svn",
            ".hg",
            ".vs",
            ".idea",
            ".vscode",
            "bin",
            "obj",
            "dist",
            "build",
            "out",
            "target",           // Rust / Maven
            "__pycache__",
            ".pytest_cache",
            ".mypy_cache",
            ".tox",
            ".eggs",
            "venv",
            ".venv",
            "vendor",           // PHP / Go
            "bower_components",
            ".next",
            ".nuxt",
            ".output",
            "coverage",
            "packages",         // NuGet
            "TestResults",
            ".angular",
            ".cache",
            ".turbo",
            "tmp",
            "temp",
        };

    /// <summary>忽略的文件名（精确匹配，忽略大小写）。</summary>
    public IReadOnlySet<string> ExcludedFileNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml",
            ".DS_Store",
            "Thumbs.db",
            "desktop.ini",
        };

    /// <summary>单个文件最大索引大小（默认 10MB）。超过的不索引。</summary>
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>索引存储根目录。</summary>
    public string IndexRootDirectory { get; init; } =
        Path.Combine(
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT") ?? Path.GetTempPath(),
            "fulltext-index");

    /// <summary>判断文件扩展名是否在索引白名单中。</summary>
    public bool IsIndexableExtension(string extension) =>
        PlainTextExtensions.Contains(extension) || ParsedExtensions.Contains(extension);

    /// <summary>
    /// 判断文件路径是否应被排除（目录名黑名单命中任一父目录、或文件名在排除列表中）。
    /// </summary>
    /// <param name="filePath">文件绝对路径。</param>
    /// <param name="rootDirectory">索引根目录（用于提取相对路径段）。</param>
    public bool IsExcludedPath(string filePath, string rootDirectory)
    {
        // 文件名匹配
        var fileName = Path.GetFileName(filePath);
        if (ExcludedFileNames.Contains(fileName))
            return true;

        // 目录名匹配：检查路径中每个父目录
        var relative = Path.GetRelativePath(rootDirectory, filePath);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ExcludedDirectoryNames.Contains(part))
                return true;
        }

        return false;
    }
}
