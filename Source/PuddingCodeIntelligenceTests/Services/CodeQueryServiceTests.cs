using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

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
