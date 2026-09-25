using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Extractors;

namespace PuddingCodeIntelligenceTests.Extractors;

/// <summary>
/// B4+ A1/A2/A3 (+ the "no upward search" guard): the extractor assets are resolved against an
/// injected base directory, and a miss is reported as a structured, diagnosable failure.
/// </summary>
[TestClass]
public sealed class ExtractorAssetResolverTests : IDisposable
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-extractor-asset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    public void Resolve_TypeScriptAssetsPresent_ResolvesFromInjectedBaseDirectory()
    {
        var baseDirectory = Path.Combine(_root, "component");
        var scriptsDirectory = Path.Combine(baseDirectory, "Scripts");
        Directory.CreateDirectory(Path.Combine(scriptsDirectory, "node_modules", "typescript"));
        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-ts-symbols.js"), "// stub");

        var resolution = new ExtractorAssetResolver(baseDirectory).Resolve(ExtractorAssetKind.TypeScriptScript);

        Assert.IsTrue(resolution.Success, resolution.Message);
        Assert.AreEqual(ExtractorAssetFailure.None, resolution.Failure);
        Assert.AreEqual(Path.GetFullPath(baseDirectory), resolution.BaseDirectory);
        Assert.AreEqual(Path.Combine(scriptsDirectory, "extract-ts-symbols.js"), resolution.ScriptPath);
        Assert.AreEqual(Path.Combine(scriptsDirectory, "node_modules"), resolution.NodeModulesPath);
    }

    [TestMethod]
    public void Resolve_PythonScriptPresent_SucceedsWithoutNodeModules()
    {
        var baseDirectory = Path.Combine(_root, "component");
        var scriptsDirectory = Path.Combine(baseDirectory, "Scripts");
        Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-py-symbols.py"), "# stub");

        var resolution = new ExtractorAssetResolver(baseDirectory).Resolve(ExtractorAssetKind.PythonScript);

        Assert.IsTrue(resolution.Success, resolution.Message);
        Assert.AreEqual(Path.Combine(scriptsDirectory, "extract-py-symbols.py"), resolution.ScriptPath);
        Assert.IsNull(resolution.NodeModulesPath,
            "the Python extractor only uses the standard library, so node_modules must stay optional");
    }

    [TestMethod]
    public void Resolve_ScriptMissing_ReportsAssetMissingWithExpectedPath()
    {
        var baseDirectory = Path.Combine(_root, "component");
        Directory.CreateDirectory(baseDirectory);

        var resolution = new ExtractorAssetResolver(baseDirectory).Resolve(ExtractorAssetKind.TypeScriptScript);

        var expectedScriptPath = Path.Combine(baseDirectory, "Scripts", "extract-ts-symbols.js");

        Assert.IsFalse(resolution.Success);
        Assert.AreEqual(ExtractorAssetFailure.AssetMissing, resolution.Failure);
        Assert.IsNull(resolution.ScriptPath);
        Assert.AreEqual(expectedScriptPath, resolution.ExpectedScriptPath);
        StringAssert.Contains(resolution.Message, expectedScriptPath);
        StringAssert.Contains(resolution.Message, Path.GetFullPath(baseDirectory));
    }

    [TestMethod]
    public void Resolve_NodeModulesMissing_ReportsNodeModulesMissing()
    {
        var baseDirectory = Path.Combine(_root, "component");
        var scriptsDirectory = Path.Combine(baseDirectory, "Scripts");
        Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-ts-symbols.js"), "// stub");

        var resolution = new ExtractorAssetResolver(baseDirectory).Resolve(ExtractorAssetKind.TypeScriptScript);

        var expectedNodeModulesPath = Path.Combine(scriptsDirectory, "node_modules");

        Assert.IsFalse(resolution.Success);
        Assert.AreEqual(ExtractorAssetFailure.NodeModulesMissing, resolution.Failure);
        Assert.IsNull(resolution.NodeModulesPath);
        Assert.AreEqual(expectedNodeModulesPath, resolution.ExpectedNodeModulesPath);
        StringAssert.Contains(resolution.Message, expectedNodeModulesPath);
    }

    [TestMethod]
    public void Resolve_AssetsOnlyInAncestorDirectory_DoesNotWalkUp()
    {
        // A decoy asset tree above the injected base directory: searching parents would find it.
        var ancestorScripts = Path.Combine(_root, "Scripts");
        Directory.CreateDirectory(Path.Combine(ancestorScripts, "node_modules"));
        File.WriteAllText(Path.Combine(ancestorScripts, "extract-ts-symbols.js"), "// ancestor decoy");

        var baseDirectory = Path.Combine(_root, "component");
        Directory.CreateDirectory(baseDirectory);

        var resolution = new ExtractorAssetResolver(baseDirectory).Resolve(ExtractorAssetKind.TypeScriptScript);

        Assert.IsFalse(resolution.Success, "the resolver must not search parent directories");
        Assert.AreEqual(ExtractorAssetFailure.AssetMissing, resolution.Failure);
        Assert.AreEqual(Path.Combine(baseDirectory, "Scripts", "extract-ts-symbols.js"), resolution.ExpectedScriptPath);
    }

    [TestMethod]
    public void Constructor_BlankBaseDirectory_ThrowsArgumentException()
    {
        var threw = false;
        try
        {
            _ = new ExtractorAssetResolver(" ");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "a blank base directory must be rejected");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
