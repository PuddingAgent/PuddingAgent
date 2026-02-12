using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;

namespace PuddingCodeIntelligenceTests.Storage;

[TestClass]
public sealed class SqliteCodeIndexStoreTests
{
    [TestMethod]
    public async Task Project_Can_Be_Added_Updated_Listed_And_Removed_Without_Touching_Source()
    {
        using var fixture = StoreFixture.Create();
        var sourceFile = Path.Combine(fixture.ProjectPath, "Program.cs");
        await File.WriteAllTextAsync(sourceFile, "class Program { }\r\n");

        var project = Project(fixture.ProjectPath);
        await fixture.Store.UpsertProjectAsync(project);
        await fixture.Store.UpdateProjectStatusAsync(project.WorkspaceId, project.ProjectId, CodeProjectStatus.Active, "indexed");

        var listed = await fixture.Store.ListProjectsAsync(project.WorkspaceId);
        var stored = await fixture.Store.GetProjectAsync(project.WorkspaceId, project.ProjectId);

        Assert.HasCount(1, listed);
        Assert.IsNotNull(stored);
        Assert.AreEqual(CodeProjectStatus.Active, stored!.Status);
        Assert.IsTrue(File.Exists(sourceFile));

        await fixture.Store.RemoveProjectAsync(project.WorkspaceId, project.ProjectId);

        Assert.IsEmpty(await fixture.Store.ListProjectsAsync(project.WorkspaceId));
        Assert.IsNull(await fixture.Store.GetProjectAsync(project.WorkspaceId, project.ProjectId));
        Assert.IsTrue(File.Exists(sourceFile), "Removing a code project must not delete source files.");
    }

    [TestMethod]
    public async Task RemoveProjectAsync_Can_Mark_Removed_While_Preserving_Index_Data()
    {
        using var fixture = StoreFixture.Create();
        var project = Project(fixture.ProjectPath);
        await fixture.Store.UpsertProjectAsync(project);
        var file = new CodeFileRecord(project.WorkspaceId, project.ProjectId, "src/Foo.cs", "CSharp");
        await fixture.Store.UpsertFilesAsync(project.WorkspaceId, project.ProjectId, [file]);
        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [
            Symbol(project, file.FilePath, "symbol-foo", "Foo", CodeSymbolKind.Class, 1, 2)
        ]);

        await fixture.Store.RemoveProjectAsync(project.WorkspaceId, project.ProjectId, removeIndexedArtifacts: false);

        Assert.IsEmpty(await fixture.Store.ListProjectsAsync(project.WorkspaceId));
        var stored = await fixture.Store.GetProjectAsync(project.WorkspaceId, project.ProjectId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(CodeProjectStatus.Removed, stored!.Status);
        Assert.HasCount(1, await fixture.Store.ListFilesAsync(project.WorkspaceId, project.ProjectId));
        Assert.HasCount(1, await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(project.WorkspaceId, "Foo", project.ProjectId)));
    }

    [TestMethod]
    public async Task Files_Symbols_Relations_And_References_Roundtrip_With_Project_Scope()
    {
        using var fixture = StoreFixture.Create();
        var project = Project(fixture.ProjectPath);
        await fixture.Store.UpsertProjectAsync(project);

        var file = new CodeFileRecord(project.WorkspaceId, project.ProjectId, "src/Foo.cs", "CSharp", DateTimeOffset.UtcNow);
        await fixture.Store.UpsertFilesAsync(project.WorkspaceId, project.ProjectId, [file]);

        var foo = Symbol(project, file.FilePath, "symbol-foo", "Foo", CodeSymbolKind.Class, 1, 10);
        var run = Symbol(project, file.FilePath, "symbol-run", "Run", CodeSymbolKind.Method, 4, 8);
        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [foo, run]);

        var relation = new CodeRelationRecord(
            project.WorkspaceId,
            project.ProjectId,
            run.SymbolId,
            foo.SymbolId,
            CodeRelationKind.Calls,
            5,
            file.FilePath,
            DateTimeOffset.UtcNow);
        await fixture.Store.UpsertRelationsAsync(project.WorkspaceId, project.ProjectId, [relation]);

        var reference = new CodeReferenceRecord(
            project.WorkspaceId,
            project.ProjectId,
            run.SymbolId,
            foo.SymbolId,
            file.FilePath,
            5,
            "new Foo()",
            DateTimeOffset.UtcNow);
        await fixture.Store.UpsertReferencesAsync(project.WorkspaceId, project.ProjectId, [reference]);

        var files = await fixture.Store.ListFilesAsync(project.WorkspaceId, project.ProjectId);
        var searchAllProjects = await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(project.WorkspaceId, "Foo"));
        var searchThisProject = await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(project.WorkspaceId, "Run", project.ProjectId));
        var outgoing = await fixture.Store.ListRelationsAsync(project.WorkspaceId, project.ProjectId, run.SymbolId, CodeRelationKind.Calls);
        var incoming = await fixture.Store.ListIncomingRelationsAsync(project.WorkspaceId, project.ProjectId, foo.SymbolId, CodeRelationKind.Calls);
        var references = await fixture.Store.ListReferencesAsync(project.WorkspaceId, project.ProjectId, foo.SymbolId);

        Assert.HasCount(1, files);
        Assert.AreEqual(file.FilePath, files[0].FilePath);
        Assert.HasCount(1, searchAllProjects);
        Assert.AreEqual(foo.SymbolId, searchAllProjects[0].SymbolId);
        Assert.HasCount(1, searchThisProject);
        Assert.AreEqual(run.SymbolId, searchThisProject[0].SymbolId);
        Assert.HasCount(1, outgoing);
        Assert.AreEqual(relation.TargetSymbolId, outgoing[0].TargetSymbolId);
        Assert.HasCount(1, incoming);
        Assert.AreEqual(relation.SourceSymbolId, incoming[0].SourceSymbolId);
        Assert.HasCount(1, references);
        Assert.AreEqual(reference.SourceText, references[0].SourceText);
    }

    [TestMethod]
    public async Task UpsertSymbols_Replaces_Stale_Symbols_For_A_File()
    {
        using var fixture = StoreFixture.Create();
        var project = Project(fixture.ProjectPath);
        await fixture.Store.UpsertProjectAsync(project);
        var file = new CodeFileRecord(project.WorkspaceId, project.ProjectId, "src/Foo.cs", "CSharp");
        await fixture.Store.UpsertFilesAsync(project.WorkspaceId, project.ProjectId, [file]);

        var foo = Symbol(project, file.FilePath, "symbol-foo", "Foo", CodeSymbolKind.Class, 1, 10);
        var removed = Symbol(project, file.FilePath, "symbol-removed", "Removed", CodeSymbolKind.Method, 6, 8);
        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [foo, removed]);
        await fixture.Store.UpsertRelationsAsync(project.WorkspaceId, project.ProjectId, [
            new CodeRelationRecord(project.WorkspaceId, project.ProjectId, foo.SymbolId, removed.SymbolId, CodeRelationKind.Calls)
        ]);

        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [foo]);

        var removedSearch = await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(project.WorkspaceId, "Removed", project.ProjectId));
        var relations = await fixture.Store.ListRelationsAsync(project.WorkspaceId, project.ProjectId, foo.SymbolId);

        Assert.IsEmpty(removedSearch);
        Assert.IsEmpty(relations);
    }

    [TestMethod]
    public async Task Explicit_Clear_Methods_Remove_Stale_Empty_Index_Results()
    {
        using var fixture = StoreFixture.Create();
        var project = Project(fixture.ProjectPath);
        await fixture.Store.UpsertProjectAsync(project);
        var file = new CodeFileRecord(project.WorkspaceId, project.ProjectId, "src/Foo.cs", "CSharp");
        var foo = Symbol(project, file.FilePath, "symbol-foo", "Foo", CodeSymbolKind.Class, 1, 10);
        var run = Symbol(project, file.FilePath, "symbol-run", "Run", CodeSymbolKind.Method, 4, 8);
        await fixture.Store.UpsertFilesAsync(project.WorkspaceId, project.ProjectId, [file]);
        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [foo, run]);
        await fixture.Store.UpsertRelationsAsync(project.WorkspaceId, project.ProjectId, [
            new CodeRelationRecord(project.WorkspaceId, project.ProjectId, run.SymbolId, foo.SymbolId, CodeRelationKind.Calls)
        ]);
        await fixture.Store.UpsertReferencesAsync(project.WorkspaceId, project.ProjectId, [
            new CodeReferenceRecord(project.WorkspaceId, project.ProjectId, run.SymbolId, foo.SymbolId, file.FilePath, 5)
        ]);

        await fixture.Store.ClearRelationsAsync(project.WorkspaceId, project.ProjectId, run.SymbolId);
        await fixture.Store.ClearReferencesAsync(project.WorkspaceId, project.ProjectId, run.SymbolId);

        Assert.IsEmpty(await fixture.Store.ListRelationsAsync(project.WorkspaceId, project.ProjectId, run.SymbolId));
        Assert.IsEmpty(await fixture.Store.ListReferencesAsync(project.WorkspaceId, project.ProjectId, run.SymbolId));

        await fixture.Store.ClearSymbolsForFileAsync(project.WorkspaceId, project.ProjectId, file.FilePath);

        Assert.IsEmpty(await fixture.Store.GetSymbolsByFileAsync(project.WorkspaceId, project.ProjectId, file.FilePath));
        Assert.IsEmpty(await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(project.WorkspaceId, "Foo", project.ProjectId)));
    }

    [TestMethod]
    public async Task Database_Path_Is_Treated_As_DataSource_When_It_Contains_Connection_String_Characters()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-code-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var fixture = StoreFixture.Create(root, Path.Combine(root, "db;Mode=ReadOnly", "code-index.db"));
        var project = Project(fixture.ProjectPath);

        await fixture.Store.UpsertProjectAsync(project);

        Assert.HasCount(1, await fixture.Store.ListProjectsAsync(project.WorkspaceId));
    }

    [TestMethod]
    public async Task Symbol_Search_Can_Be_Narrowed_To_Project()
    {
        using var fixture = StoreFixture.Create();
        var projectOne = Project(fixture.ProjectPath, "project-one");
        var projectTwo = Project(Path.Combine(fixture.Root, "external-project"), "project-two");
        Directory.CreateDirectory(projectTwo.ProjectPath);
        await fixture.Store.UpsertProjectAsync(projectOne);
        await fixture.Store.UpsertProjectAsync(projectTwo);

        var one = Symbol(projectOne, "src/Foo.cs", "symbol-one", "SharedName", CodeSymbolKind.Class, 1, 2);
        var two = Symbol(projectTwo, "src/Foo.cs", "symbol-two", "SharedName", CodeSymbolKind.Class, 1, 2);
        await fixture.Store.UpsertSymbolsAsync(projectOne.WorkspaceId, projectOne.ProjectId, [one]);
        await fixture.Store.UpsertSymbolsAsync(projectTwo.WorkspaceId, projectTwo.ProjectId, [two]);

        var all = await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(projectOne.WorkspaceId, "SharedName"));
        var narrowed = await fixture.Store.SearchSymbolsAsync(new CodeSymbolSearchRequest(projectOne.WorkspaceId, "SharedName", projectTwo.ProjectId));

        Assert.HasCount(2, all);
        Assert.HasCount(1, narrowed);
        Assert.AreEqual(projectTwo.ProjectId, narrowed[0].ProjectId);
    }

    private static CodeProjectRecord Project(string path, string projectId = "project-one") =>
        new(
            "workspace-one",
            projectId,
            path,
            CodeProjectStatus.Active,
            DisplayName: projectId,
            AddedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static CodeSymbolRecord Symbol(
        CodeProjectRecord project,
        string filePath,
        string symbolId,
        string name,
        CodeSymbolKind kind,
        int startLine,
        int endLine) =>
        new(
            project.WorkspaceId,
            project.ProjectId,
            filePath,
            symbolId,
            name,
            kind,
            startLine,
            endLine,
            Signature: $"{kind} {name}",
            Container: "Demo");

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, string databasePath)
        {
            Root = root;
            ProjectPath = Path.Combine(root, "registered-project-outside-workspace");
            Directory.CreateDirectory(ProjectPath);
            Store = new SqliteCodeIndexStore(databasePath);
        }

        public string Root { get; }
        public string ProjectPath { get; }
        public SqliteCodeIndexStore Store { get; }

        public static StoreFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "pudding-code-store-tests", Guid.NewGuid().ToString("N"));
            return Create(root, Path.Combine(root, "db", "code-index.db"));
        }

        public static StoreFixture Create(string root, string databasePath) =>
            new(root, databasePath);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
