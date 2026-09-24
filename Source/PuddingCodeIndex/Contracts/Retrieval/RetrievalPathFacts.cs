namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 从路径派生的可解释事实（语言、顶层目录）：分布摘要与过载诊断都依赖它，
/// 因此做成**确定性纯函数**，避免"同一份结果两次统计不同"。
/// </summary>
public static class RetrievalPathFacts
{
    /// <summary>无法识别扩展名时返回的语言标识（诚实：不猜）。</summary>
    public const string UnknownLanguage = "unknown";

    /// <summary>路径不含目录段时使用的顶层目录标识。</summary>
    public const string RootDirectory = ".";

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".py"] = "python",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".json"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".md"] = "markdown",
        [".bicep"] = "bicep",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".h"] = "cpp",
        [".hpp"] = "cpp",
        [".java"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".sql"] = "sql",
    };

    /// <summary>扩展名 → 语言标识（未登记 ⇒ <see cref="UnknownLanguage"/>，不假装支持）。</summary>
    public static string LanguageOf(string filePath)
    {
        var extension = Path.GetExtension(SymbolIdentity.NormalizePath(filePath));
        return LanguageByExtension.TryGetValue(extension, out var language) ? language : UnknownLanguage;
    }

    /// <summary>归一化路径的第一个目录段（无目录段 ⇒ <see cref="RootDirectory"/>）。</summary>
    public static string TopLevelDirectoryOf(string filePath)
    {
        var normalized = SymbolIdentity.NormalizePath(filePath);
        var separator = normalized.IndexOf('/');
        return separator <= 0 ? RootDirectory : normalized[..separator];
    }
}
