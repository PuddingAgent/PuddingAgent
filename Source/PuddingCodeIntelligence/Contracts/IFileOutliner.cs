namespace PuddingCodeIntelligence.Contracts;

/// <summary>
/// Language-agnostic file outliner.
/// Extracts a structured symbol tree from source files for code_outline functionality.
/// </summary>
public interface IFileOutliner
{
    /// <summary>
    /// Returns the set of file extensions this outliner supports (e.g. ".ts", ".tsx").
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Extracts the structural outline of a source file.
    /// </summary>
    Task<OutlineResult> OutlineAsync(
        string filePath,
        string sourceCode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a file outline operation.
/// </summary>
public sealed record OutlineResult(
    bool Success,
    string FilePath,
    IReadOnlyList<OutlineNode> Nodes,
    string? Error = null);

/// <summary>
/// A single node in the outline tree.
/// </summary>
public sealed record OutlineNode(
    string Name,
    CodeSymbolKind Kind,
    int StartLine,     // 1-based
    int EndLine,       // 1-based
    string? Signature = null,
    string? Modifiers = null,
    string? Container = null,
    IReadOnlyList<OutlineNode>? Children = null);

/// <summary>
/// Registry that dispatches to the correct IFileOutliner for a given file.
/// </summary>
public interface IFileOutlinerRegistry
{
    /// <summary>
    /// Returns the outliner for the given file path, or null if unsupported.
    /// </summary>
    IFileOutliner? GetOutliner(string filePath);

    /// <summary>
    /// Returns true if the file has a registered outliner.
    /// </summary>
    bool IsSupported(string filePath);
}
