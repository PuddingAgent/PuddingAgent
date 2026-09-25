using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.TypeScript;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIntelligenceTests.TypeScript;

/// <summary>
/// B4+ integration guards: the indexer must run the <b>component-owned</b> extraction script (A4) and
/// must keep NODE_PATH inside the child process (A5).
/// </summary>
/// <remarks>
/// The stub extractor below is a real Node program, so these tests need Node.js on PATH — the same
/// prerequisite the production TypeScript indexer already has (it reports "Node.js not available" as a
/// Failed result without it).
/// </remarks>
[TestClass]
public sealed class TypeScriptIndexerAssetTests : IDisposable
{
    private string _root = null!;
    private SqliteCodeIndexStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-ts-indexer-asset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "code-index.db"));
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_ProjectPathShipsDecoyScript_UsesComponentOwnedScript()
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        WriteStubExtractor(
            Path.Combine(componentBaseDirectory, "Scripts"),
            markerPath: null,
            symbolName: "ComponentOwnedSymbol");

        // The indexed project ships a same-named script at ProjectPath/Scripts — the pre-B4+ resolution
        // base. It must NOT be the one that runs.
        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(
            Path.Combine(projectPath, "sample.ts"),
            "export function ComponentOwnedSymbol(): number { return 1; }");
        WriteStubExtractor(
            Path.Combine(projectPath, "Scripts"),
            markerPath: null,
            symbolName: "DecoySymbol");

        var indexer = new TypeScriptIndexer(
            _store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver(componentBaseDirectory));

        var result = await indexer.IndexWorkspaceAsync(
            new CodeWorkspaceDescriptor("ws-asset", "proj-asset", projectPath, ProjectFilePaths: []),
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);

        var componentOwned = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-asset", "ComponentOwnedSymbol"), CancellationToken.None);
        Assert.IsTrue(componentOwned.Count >= 1,
            "B4+ R2/A4 failed: the component-owned extractor script did not produce the symbols.");

        var decoy = await _store.SearchSymbolsAsync(
            new CodeSymbolSearchRequest("ws-asset", "DecoySymbol"), CancellationToken.None);
        Assert.AreEqual(0, decoy.Count,
            "B4+ A4 failed: extraction was influenced by descriptor.ProjectPath (the decoy script under the indexed project ran).");
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_ExtractionRun_KeepsNodePathOutOfTheHostProcess()
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        var scriptsDirectory = Path.Combine(componentBaseDirectory, "Scripts");
        var markerPath = Path.Combine(_root, "child-node-path.txt");
        WriteStubExtractor(scriptsDirectory, markerPath, symbolName: "NodePathProbeSymbol");

        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(
            Path.Combine(projectPath, "sample.ts"),
            "export function NodePathProbeSymbol(): number { return 1; }");

        var nodePathBefore = Environment.GetEnvironmentVariable("NODE_PATH") ?? "<unset>";

        var indexer = new TypeScriptIndexer(
            _store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver(componentBaseDirectory));

        var result = await indexer.IndexWorkspaceAsync(
            new CodeWorkspaceDescriptor("ws-nodepath", "proj-nodepath", projectPath, ProjectFilePaths: []),
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);

        // (1) the child process really received NODE_PATH pointing at the component-owned node_modules ...
        Assert.IsTrue(File.Exists(markerPath), "the extraction script never ran, so NODE_PATH could not be observed");
        Assert.AreEqual(
            Path.Combine(scriptsDirectory, "node_modules"),
            File.ReadAllText(markerPath).Trim(),
            "B4+ R3 failed: the extraction child process did not receive NODE_PATH pointing at the component assets.");

        // (2) ... and the host process environment stayed untouched (A5 / M2).
        Assert.AreEqual(
            nodePathBefore,
            Environment.GetEnvironmentVariable("NODE_PATH") ?? "<unset>",
            "B4+ A5/M2 failed: the extraction run mutated the process-wide NODE_PATH.");
    }

    [TestMethod]
    public async Task IndexWorkspaceAsync_ComponentAssetsMissing_FailsWithExpectedPath()
    {
        var componentBaseDirectory = Path.Combine(_root, "component");
        Directory.CreateDirectory(componentBaseDirectory);

        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(Path.Combine(projectPath, "sample.ts"), "export function Anything(): number { return 1; }");

        var indexer = new TypeScriptIndexer(
            _store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver(componentBaseDirectory));

        var result = await indexer.IndexWorkspaceAsync(
            new CodeWorkspaceDescriptor("ws-missing", "proj-missing", projectPath, ProjectFilePaths: []),
            CancellationToken.None);

        Assert.IsFalse(result.Success, "a missing extractor asset must fail closed, never silently index nothing");
        Assert.AreEqual(CodeIndexStatus.Failed, result.Status);
        StringAssert.Contains(
            result.Message,
            Path.Combine(componentBaseDirectory, "Scripts", "extract-ts-symbols.js"));
    }

    /// <summary>
    /// Writes a stub <c>extract-ts-symbols.js</c> that reports one symbol per file. Project mode is
    /// refused (exit 1) so the indexer takes its existing per-file fallback path, and when
    /// <paramref name="markerPath"/> is given the stub records the NODE_PATH it actually received.
    /// </summary>
    private static void WriteStubExtractor(string scriptsDirectory, string? markerPath, string symbolName)
    {
        Directory.CreateDirectory(Path.Combine(scriptsDirectory, "node_modules"));

        var markerWrite = markerPath is null
            ? string.Empty
            : $"'use strict';\nconst fs = require('fs');\nfs.writeFileSync({JsonSerializer.Serialize(markerPath)}, String(process.env.NODE_PATH || ''));";

        var script = $$"""
            {{markerWrite}}
            if (process.argv[2] === '--project') { process.exit(1); }
            process.stdout.write(JSON.stringify({
              symbols: [{ name: '{{symbolName}}', kind: 'function', startLine: 1, endLine: 1, signature: 'export function {{symbolName}}()' }],
              relations: []
            }) + '\n');
            """;

        File.WriteAllText(Path.Combine(scriptsDirectory, "extract-ts-symbols.js"), script);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
