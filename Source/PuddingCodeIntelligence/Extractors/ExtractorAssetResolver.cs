namespace PuddingCodeIntelligence.Extractors;

/// <summary>
/// Resolves the extractor assets from the directory that holds this component's assembly
/// (<see cref="AppContext.BaseDirectory"/> by default) instead of from the indexed project's
/// <c>ProjectPath</c>.
/// </summary>
/// <remarks>
/// <para>
/// Layout it expects (all three items are shipped next to the assembly, see
/// <c>PuddingCodeIntelligence.csproj</c>):
/// <code>
/// &lt;base&gt;/Scripts/extract-ts-symbols.js
/// &lt;base&gt;/Scripts/extract-py-symbols.py
/// &lt;base&gt;/Scripts/node_modules/typescript/...
/// </code>
/// </para>
/// <para>
/// Deliberately absent: <c>CodeWorkspaceDescriptor.ProjectPath</c>,
/// <c>Directory.GetCurrentDirectory()</c>, and any upward directory walk. The indexed project is not
/// where the component's assets live — a project that happens to ship a <c>Scripts/</c> directory
/// must not be able to change what the component executes.
/// </para>
/// </remarks>
public sealed class ExtractorAssetResolver : IExtractorAssetResolver
{
    private const string ScriptsDirectoryName = "Scripts";
    private const string NodeModulesDirectoryName = "node_modules";

    private readonly string _baseDirectory;

    /// <summary>Creates a resolver rooted at the directory holding this assembly.</summary>
    public ExtractorAssetResolver()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>Creates a resolver rooted at <paramref name="baseDirectory"/> (injected for tests).</summary>
    /// <param name="baseDirectory">Directory that contains the <c>Scripts/</c> asset folder.</param>
    public ExtractorAssetResolver(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    /// <inheritdoc />
    public ExtractorAssetResolution Resolve(ExtractorAssetKind kind)
    {
        var (fileName, requiresNodeModules) = Describe(kind);

        var expectedScriptPath = Path.Combine(_baseDirectory, ScriptsDirectoryName, fileName);
        var expectedNodeModulesPath = Path.Combine(_baseDirectory, ScriptsDirectoryName, NodeModulesDirectoryName);

        if (!File.Exists(expectedScriptPath))
        {
            return ExtractorAssetResolution.Missing(
                _baseDirectory,
                ExtractorAssetFailure.AssetMissing,
                expectedScriptPath,
                expectedNodeModulesPath,
                $"Extractor asset missing: expected '{expectedScriptPath}' next to the component assembly at '{_baseDirectory}'.");
        }

        var nodeModulesPath = Directory.Exists(expectedNodeModulesPath) ? expectedNodeModulesPath : null;

        if (requiresNodeModules && nodeModulesPath is null)
        {
            return ExtractorAssetResolution.Missing(
                _baseDirectory,
                ExtractorAssetFailure.NodeModulesMissing,
                expectedScriptPath,
                expectedNodeModulesPath,
                $"Extractor dependency missing: '{expectedScriptPath}' requires the 'typescript' module, expected at '{expectedNodeModulesPath}'.");
        }

        return ExtractorAssetResolution.Resolved(
            _baseDirectory, expectedScriptPath, nodeModulesPath, expectedNodeModulesPath);
    }

    private static (string FileName, bool RequiresNodeModules) Describe(ExtractorAssetKind kind) => kind switch
    {
        ExtractorAssetKind.TypeScriptScript => ("extract-ts-symbols.js", true),
        ExtractorAssetKind.PythonScript => ("extract-py-symbols.py", false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown extractor asset kind."),
    };
}
