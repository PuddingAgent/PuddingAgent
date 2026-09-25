using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.Python;
using PuddingCodeIntelligence.TypeScript;

namespace PuddingCodeIntelligenceTests.Services;

/// <summary>
/// C-2, the red line "C# behaviour is unchanged", asserted against the <b>real</b>
/// <see cref="RoslynCSharpIndexer"/> and against the real composition. No Node.js or Python is needed:
/// neither comparison touches the extractor subprocesses.
/// </summary>
[TestClass]
public sealed class CompositeCodeIndexerLanguageDispatchTests
{
    [TestMethod]
    public async Task IndexFileAsync_For_A_CSharp_File_Equals_A_Direct_RoslynCSharpIndexer_Call()
    {
        var root = NewTempRoot();

        try
        {
            var store = new SqliteCodeIndexStore(Path.Combine(root, "code-index.db"));
            var csharp = new RoslynCSharpIndexer(store, NullLogger<RoslynCSharpIndexer>.Instance);
            var typescript = new LanguageIndexerDouble("TypeScript/JavaScript", ".ts");
            var python = new LanguageIndexerDouble("Python", ".py");
            var composite = new CompositeCodeIndexer([csharp, typescript, python]);

            var file = Path.Combine(root, "Probe.cs");
            await File.WriteAllTextAsync(file, "namespace Probe { public sealed class Alpha { } }");

            // The project path does not exist, so both calls stop at the same deterministic checkpoint: the
            // comparison cannot be polluted by timestamps that two separate calls could never share.
            var descriptor = new CodeWorkspaceDescriptor(
                "ws-c2", "scope-c2", Path.Combine(root, "missing-project"), ProjectFilePaths: []);

            var direct = await csharp.IndexFileAsync(descriptor, file);
            var aggregated = await composite.IndexFileAsync(descriptor, file);

            StringAssert.Contains(direct.Message!, "Project path does not exist",
                "positive control: the real C# indexer ran and reached that checkpoint");

            Assert.AreEqual(0, typescript.WorkspaceCalls, "a .cs file must not reach another language");
            Assert.AreEqual(0, python.WorkspaceCalls);

            Assert.AreEqual(direct.Success, aggregated.Success);
            Assert.AreEqual(direct.Status, aggregated.Status);
            Assert.AreEqual(direct.Message, aggregated.Message);
            Assert.AreEqual(direct.StartedAtUtc, aggregated.StartedAtUtc);
            Assert.AreEqual(direct.CompletedAtUtc, aggregated.CompletedAtUtc);
            Assert.AreEqual(direct.WorkspaceId, aggregated.WorkspaceId);
            Assert.AreEqual(direct.ProjectId, aggregated.ProjectId);
            Assert.AreEqual(direct.LanguageOutcomes, aggregated.LanguageOutcomes,
                "a per-file call is passed through, so no per-language detail is attached");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Production_Language_Indexers_Own_Their_Extensions_And_Do_Not_Overlap()
    {
        var root = NewTempRoot();

        try
        {
            var store = new SqliteCodeIndexStore(Path.Combine(root, "code-index.db"));

            ILanguageCodeIndexer[] languages =
            [
                new RoslynCSharpIndexer(store, NullLogger<RoslynCSharpIndexer>.Instance),
                new TypeScriptIndexer(store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver()),
                new PythonIndexer(store, NullLogger<PythonIndexer>.Instance, new ExtractorAssetResolver()),
            ];

            AssertOwns(languages[0], ".cs");
            AssertOwns(languages[1], ".ts");
            AssertOwns(languages[1], ".tsx");
            AssertOwns(languages[1], ".js");
            AssertOwns(languages[1], ".jsx");
            AssertOwns(languages[2], ".py");

            var owners = languages.SelectMany(language => language.SupportedExtensions).ToArray();
            Assert.AreEqual(owners.Length, owners.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "routing needs exactly one owner per extension: the sets must not overlap");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AddPuddingCodeIntelligence_Registers_The_Aggregate_Over_The_Three_Languages()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICodeIndexStore>(fixture.Store);

        services.AddPuddingCodeIntelligence();

        using var provider = services.BuildServiceProvider();

        var aggregate = provider.GetRequiredService<ICodeIndexer>() as CompositeCodeIndexer;
        Assert.IsNotNull(aggregate,
            "ICodeIndexer must be the aggregate; resolving it must not recurse into the languages");

        Assert.AreSame(aggregate, provider.GetRequiredService<ICodeIndexer>(), "the aggregate is a singleton");

        var languages = provider.GetServices<ILanguageCodeIndexer>().ToArray();
        Assert.HasCount(3, languages,
            "exactly the three languages: the aggregate must not be registered under the language port");
        Assert.AreEqual("C#", languages[0].Language);
        Assert.AreEqual("TypeScript/JavaScript", languages[1].Language);
        Assert.AreEqual("Python", languages[2].Language);

        Assert.HasCount(3, aggregate!.LanguageIndexers, "the aggregate must be given all three languages");
    }

    private static void AssertOwns(ILanguageCodeIndexer language, string extension) =>
        Assert.IsTrue(language.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
            $"{language.Language} must own {extension} for per-file routing");

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-c2-dispatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>
/// Language implementation double for the dispatch comparison: it records whether it was asked to do
/// anything, so the test can prove a .cs file never reached another language.
/// </summary>
internal sealed class LanguageIndexerDouble : ILanguageCodeIndexer
{
    public LanguageIndexerDouble(string language, params string[] supportedExtensions)
    {
        Language = language;
        SupportedExtensions = supportedExtensions;
    }

    public string Language { get; }

    public IReadOnlyCollection<string> SupportedExtensions { get; }

    public int WorkspaceCalls { get; private set; }

    public Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        WorkspaceCalls++;
        return Task.FromResult(new CodeIndexResult(
            true, CodeIndexStatus.Completed, "language double",
            WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));
    }

    public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
}
