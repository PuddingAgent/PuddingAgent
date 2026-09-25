using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public sealed class CodeQueryServiceTests
{
    [TestMethod]
    public async Task Query_Service_Delegates_Read_Only_Symbol_And_Graph_Queries_To_Store()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var project = new CodeProjectRecord(
            "workspace-one",
            "project-one",
            fixture.Root,
            CodeProjectStatus.Active,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var file = new CodeFileRecord(project.WorkspaceId, project.ProjectId, "src/App.cs", "CSharp");
        var target = Symbol(project, file.FilePath, "target", "TargetService", CodeSymbolKind.Class);
        var caller = Symbol(project, file.FilePath, "caller", "CallerService", CodeSymbolKind.Method);
        var secondOrder = Symbol(project, file.FilePath, "second", "SecondOrderService", CodeSymbolKind.Method);
        await fixture.Store.UpsertProjectAsync(project);
        await fixture.Store.UpsertFilesAsync(project.WorkspaceId, project.ProjectId, [file]);
        await fixture.Store.UpsertSymbolsAsync(project.WorkspaceId, project.ProjectId, [target, caller, secondOrder]);
        await fixture.Store.UpsertRelationsAsync(project.WorkspaceId, project.ProjectId, [
            new CodeRelationRecord(project.WorkspaceId, project.ProjectId, caller.SymbolId, target.SymbolId, CodeRelationKind.Calls, 12, file.FilePath),
            new CodeRelationRecord(project.WorkspaceId, project.ProjectId, secondOrder.SymbolId, caller.SymbolId, CodeRelationKind.Uses, 20, file.FilePath)
        ]);

        var service = new CodeQueryService(fixture.Store);
        var status = await service.GetProjectIndexStatusAsync(project.WorkspaceId, project.ProjectId);
        var search = await service.SearchSymbolsAsync(new CodeSymbolSearchRequest(
            project.WorkspaceId,
            "TargetService",
            project.ProjectId));
        var callers = await service.GetCallersAsync(project.WorkspaceId, project.ProjectId, target.SymbolId);
        var callees = await service.GetCalleesAsync(project.WorkspaceId, project.ProjectId, caller.SymbolId);
        var exploration = await service.ExploreAsync(project.WorkspaceId, project.ProjectId, target.SymbolId);
        var impact = await service.GetImpactAsync(project.WorkspaceId, project.ProjectId, target.SymbolId, maxDepth: 2);

        Assert.IsTrue(status.Success);
        Assert.AreEqual(CodeIndexStatus.Completed, status.Status);
        Assert.HasCount(1, search);
        Assert.AreEqual(file.FilePath, search[0].File!.FilePath);
        Assert.AreEqual(target.SymbolId, search[0].Symbol.SymbolId);
        Assert.HasCount(1, callers);
        Assert.AreEqual(caller.SymbolId, callers[0].SourceSymbolId);
        Assert.HasCount(1, callees);
        Assert.AreEqual(target.SymbolId, callees[0].TargetSymbolId);
        Assert.IsTrue(exploration.Any(symbol => symbol.SymbolId == target.SymbolId));
        Assert.IsTrue(exploration.Any(symbol => symbol.SymbolId == caller.SymbolId));
        Assert.IsTrue(impact.Any(symbol => symbol.SymbolId == caller.SymbolId));
        Assert.IsTrue(impact.Any(symbol => symbol.SymbolId == secondOrder.SymbolId));
    }

    [TestMethod]
    public async Task Query_Service_Returns_Unknown_Status_For_Unregistered_Project()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var service = new CodeQueryService(fixture.Store);

        var status = await service.GetProjectIndexStatusAsync("workspace-one", "missing-project");

        Assert.IsFalse(status.Success);
        Assert.AreEqual(CodeIndexStatus.Unknown, status.Status);
    }

    [TestMethod]
    public async Task SearchSymbols_Deduplicates_The_Same_Symbol_Indexed_Under_Multiple_Projects()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var root = Project(fixture.Root, "project-root");
        var nested = Project(fixture.Root, "project-nested");
        var rootFile = new CodeFileRecord(root.WorkspaceId, root.ProjectId, "src/Conf.cs", "CSharp");
        var nestedFile = new CodeFileRecord(nested.WorkspaceId, nested.ProjectId, "src/Conf.cs", "CSharp");

        // 同一个符号（SymbolId 是全限定名，跨项目唯一）被两个互相嵌套的 project 各索引了一份
        // —— 这正是本仓实测到的形态（仓库根 b375fee0… 与 PuddingRuntime scope-6526fb… 各一份）。
        var inRoot = Symbol(root, rootFile.FilePath, "T:Demo.ConfLoader", "ConfLoader", CodeSymbolKind.Class);
        var inNested = Symbol(nested, nestedFile.FilePath, "T:Demo.ConfLoader", "ConfLoader", CodeSymbolKind.Class);

        await fixture.Store.UpsertProjectAsync(root);
        await fixture.Store.UpsertProjectAsync(nested);
        await fixture.Store.UpsertFilesAsync(root.WorkspaceId, root.ProjectId, [rootFile]);
        await fixture.Store.UpsertFilesAsync(nested.WorkspaceId, nested.ProjectId, [nestedFile]);
        await fixture.Store.UpsertSymbolsAsync(root.WorkspaceId, root.ProjectId, [inRoot]);
        await fixture.Store.UpsertSymbolsAsync(nested.WorkspaceId, nested.ProjectId, [inNested]);

        var service = new CodeQueryService(fixture.Store);

        // 不传 project ⇒ 跨全部已登记项目检索（默认形态，实测会返回重复）
        var across = await service.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(root.WorkspaceId, "ConfLoader"));
        // 传 project ⇒ 单项目，原有行为不变
        var scoped = await service.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(root.WorkspaceId, "ConfLoader", root.ProjectId));

        Assert.HasCount(1, across);
        Assert.AreEqual("T:Demo.ConfLoader", across[0].Symbol.SymbolId);
        Assert.HasCount(1, scoped);
    }

    [TestMethod]
    public async Task SearchSymbols_Keeps_Same_Named_Symbols_That_Have_Distinct_SymbolIds()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var alpha = Project(fixture.Root, "project-alpha");
        var beta = Project(fixture.Root, "project-beta");
        var alphaFile = new CodeFileRecord(alpha.WorkspaceId, alpha.ProjectId, "src/Alpha/Helper.cs", "CSharp");
        var betaFile = new CodeFileRecord(beta.WorkspaceId, beta.ProjectId, "src/Beta/Helper.cs", "CSharp");

        await fixture.Store.UpsertProjectAsync(alpha);
        await fixture.Store.UpsertProjectAsync(beta);
        await fixture.Store.UpsertFilesAsync(alpha.WorkspaceId, alpha.ProjectId, [alphaFile]);
        await fixture.Store.UpsertFilesAsync(beta.WorkspaceId, beta.ProjectId, [betaFile]);
        await fixture.Store.UpsertSymbolsAsync(alpha.WorkspaceId, alpha.ProjectId,
            [Symbol(alpha, alphaFile.FilePath, "T:Alpha.Helper", "Helper", CodeSymbolKind.Class)]);
        await fixture.Store.UpsertSymbolsAsync(beta.WorkspaceId, beta.ProjectId,
            [Symbol(beta, betaFile.FilePath, "T:Beta.Helper", "Helper", CodeSymbolKind.Class)]);

        var service = new CodeQueryService(fixture.Store);
        var results = await service.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(alpha.WorkspaceId, "Helper"));

        // 两个不同项目里各自有名为 Helper 的类 —— 这是**不同的符号**（SymbolId 不同），
        // 绝不能因为名字相同而被去重掉：去重键必须是 SymbolId，不是 Name。
        Assert.HasCount(2, results);
        CollectionAssert.AreEquivalent(
            new[] { "T:Alpha.Helper", "T:Beta.Helper" },
            results.Select(r => r.Symbol.SymbolId).ToArray());
    }

    private static CodeProjectRecord Project(string rootPath, string projectId) =>
        new("workspace-one", projectId, rootPath, CodeProjectStatus.Active, UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static CodeSymbolRecord Symbol(
        CodeProjectRecord project,
        string filePath,
        string symbolId,
        string name,
        CodeSymbolKind kind) =>
        new(
            project.WorkspaceId,
            project.ProjectId,
            filePath,
            symbolId,
            name,
            kind,
            StartLine: 1,
            EndLine: 2,
            Signature: $"{kind} {name}",
            Container: "Demo");
}
