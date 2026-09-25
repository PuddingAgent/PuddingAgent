namespace PuddingCodeIntelligence.Extractors;

/// <summary>
/// External extractor assets this component owns and ships next to its own assembly
/// (<c>Scripts/extract-ts-symbols.js</c>, <c>Scripts/extract-py-symbols.py</c>, <c>Scripts/node_modules</c>).
/// </summary>
public enum ExtractorAssetKind
{
    /// <summary>Node.js based TypeScript/JavaScript extractor; also needs the <c>typescript</c> module.</summary>
    TypeScriptScript = 0,

    /// <summary>Python (standard library only) extractor.</summary>
    PythonScript = 1,
}

/// <summary>
/// Reason why an extractor asset could not be resolved. A miss is always reported through
/// <see cref="ExtractorAssetResolution"/> so callers can fail closed with a diagnosable message
/// instead of guessing or throwing an unclassified exception.
/// </summary>
public enum ExtractorAssetFailure
{
    /// <summary>Every asset the requested kind needs is present.</summary>
    None = 0,

    /// <summary>The extraction script itself is missing.</summary>
    AssetMissing = 1,

    /// <summary>The script is present but the <c>node_modules</c> directory providing <c>typescript</c> is not.</summary>
    NodeModulesMissing = 2,
}

/// <summary>
/// Outcome of <see cref="IExtractorAssetResolver.Resolve(ExtractorAssetKind)"/>.
/// Never a bare <c>null</c> and never an unclassified exception: the base directory and the expected
/// paths are always carried, so the caller can report *what* was missing *where*.
/// </summary>
public sealed class ExtractorAssetResolution
{
    private ExtractorAssetResolution(
        bool success,
        ExtractorAssetFailure failure,
        string baseDirectory,
        string? scriptPath,
        string? nodeModulesPath,
        string expectedScriptPath,
        string expectedNodeModulesPath,
        string message)
    {
        Success = success;
        Failure = failure;
        BaseDirectory = baseDirectory;
        ScriptPath = scriptPath;
        NodeModulesPath = nodeModulesPath;
        ExpectedScriptPath = expectedScriptPath;
        ExpectedNodeModulesPath = expectedNodeModulesPath;
        Message = message;
    }

    /// <summary>True when every asset the requested kind needs is present.</summary>
    public bool Success { get; }

    /// <summary><see cref="ExtractorAssetFailure.None"/> when <see cref="Success"/>, the miss reason otherwise.</summary>
    public ExtractorAssetFailure Failure { get; }

    /// <summary>Absolute directory the assets were resolved against (the component assembly directory by default).</summary>
    public string BaseDirectory { get; }

    /// <summary>Absolute path of the extraction script, or <c>null</c> when it is missing.</summary>
    public string? ScriptPath { get; }

    /// <summary>Absolute path of the directory holding the extractor's node dependencies, or <c>null</c> when absent or not required.</summary>
    public string? NodeModulesPath { get; }

    /// <summary>Absolute path the script was expected at (also set when resolution failed).</summary>
    public string ExpectedScriptPath { get; }

    /// <summary>Absolute path <c>node_modules</c> was expected at (also set when resolution failed).</summary>
    public string ExpectedNodeModulesPath { get; }

    /// <summary>Human readable diagnosis naming the base directory and the expected path.</summary>
    public string Message { get; }

    internal static ExtractorAssetResolution Resolved(
        string baseDirectory,
        string scriptPath,
        string? nodeModulesPath,
        string expectedNodeModulesPath) =>
        new(true, ExtractorAssetFailure.None, baseDirectory, scriptPath, nodeModulesPath, scriptPath, expectedNodeModulesPath,
            nodeModulesPath is null
                ? $"Extractor asset resolved from '{baseDirectory}': script '{scriptPath}'."
                : $"Extractor assets resolved from '{baseDirectory}': script '{scriptPath}', node_modules '{nodeModulesPath}'.");

    internal static ExtractorAssetResolution Missing(
        string baseDirectory,
        ExtractorAssetFailure failure,
        string expectedScriptPath,
        string expectedNodeModulesPath,
        string message) =>
        new(false, failure, baseDirectory, null, null, expectedScriptPath, expectedNodeModulesPath, message);
}

/// <summary>
/// Resolves the extractor assets <b>owned by this component</b>.
/// </summary>
/// <remarks>
/// The resolution base directory is the directory holding this assembly — never
/// <c>CodeWorkspaceDescriptor.ProjectPath</c>, never the current working directory, and never the
/// result of an upward directory search. The assets ship with the component, so they must be found
/// next to it; otherwise the caller fails closed.
/// </remarks>
public interface IExtractorAssetResolver
{
    /// <summary>Resolves the extraction script (and node modules where the kind needs them) for <paramref name="kind"/>.</summary>
    ExtractorAssetResolution Resolve(ExtractorAssetKind kind);
}
