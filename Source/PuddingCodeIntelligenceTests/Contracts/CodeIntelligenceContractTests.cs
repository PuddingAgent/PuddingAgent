using System;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligenceTests.Contracts;

[TestClass]
public sealed class CodeIntelligenceContractTests
{
    [TestMethod]
    public void CodeProjectRecord_And_Add_Request_Store_Expected_Values()
    {
        var record = new CodeProjectRecord(
            WorkspaceId: "ws_1",
            ProjectId: "proj_1",
            ProjectPath: "C:\\code\\proj_1",
            Status: CodeProjectStatus.Active,
            DisplayName: "Project One",
            AddedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        var addRequest = new CodeProjectAddRequest(
            WorkspaceId: record.WorkspaceId,
            ProjectId: record.ProjectId,
            ProjectPath: record.ProjectPath,
            DisplayName: "Project One");

        var removeRequest = new CodeProjectRemoveRequest(
            WorkspaceId: record.WorkspaceId,
            ProjectId: record.ProjectId,
            RemoveIndexData: true);

        Assert.AreEqual(record.WorkspaceId, addRequest.WorkspaceId);
        Assert.AreEqual(record.ProjectId, removeRequest.ProjectId);
        Assert.AreEqual("C:\\code\\proj_1", addRequest.ProjectPath);
        Assert.IsTrue(removeRequest.RemoveIndexData);
        Assert.AreEqual(CodeProjectStatus.Active, record.Status);
        Assert.AreEqual("Project One", record.DisplayName);
    }

    [TestMethod]
    public void Symbol_Relation_And_Reference_Contracts_Compile_And_Contain_Expected()
    {
        var file = new CodeFileRecord("ws_1", "proj_1", "src/Foo.cs", "CSharp");
        var symbol = new CodeSymbolRecord(
            WorkspaceId: file.WorkspaceId,
            ProjectId: file.ProjectId,
            FilePath: file.FilePath,
            SymbolId: "symbol-1",
            Name: "Foo",
            Kind: CodeSymbolKind.Class,
            StartLine: 10,
            EndLine: 20);

        var relation = new CodeRelationRecord(
            WorkspaceId: symbol.WorkspaceId,
            ProjectId: symbol.ProjectId,
            SourceSymbolId: symbol.SymbolId,
            TargetSymbolId: "symbol-2",
            Kind: CodeRelationKind.Calls,
            SourceLine: 42);

        var reference = new CodeReferenceRecord(
            WorkspaceId: symbol.WorkspaceId,
            ProjectId: symbol.ProjectId,
            SourceSymbolId: symbol.SymbolId,
            TargetSymbolId: "symbol-2",
            SourceFilePath: file.FilePath,
            SourceLine: 43);

        var searchRequest = new CodeSymbolSearchRequest(
            WorkspaceId: file.WorkspaceId,
            Query: "Foo",
            ProjectId: file.ProjectId,
            Kind: CodeSymbolKind.Class,
            Limit: 10,
            Skip: 0);

        var detail = new CodeSymbolDetail(symbol, file, "Foo class detail");

        Assert.AreEqual("src/Foo.cs", file.FilePath);
        Assert.AreEqual("Foo", symbol.Name);
        Assert.AreEqual(CodeRelationKind.Calls, relation.Kind);
        Assert.AreEqual(42, relation.SourceLine);
        Assert.AreEqual(43, reference.SourceLine);
        Assert.AreEqual("Foo", detail.Symbol.Name);
        Assert.AreEqual(file.WorkspaceId, detail.File!.WorkspaceId);
        Assert.AreEqual("Foo", searchRequest.Query);
    }

    [TestMethod]
    public void Index_And_Lsp_Contracts_Expose_Expected_Results()
    {
        var indexResult = new CodeIndexResult(
            Success: true,
            Status: CodeIndexStatus.Completed,
            Message: "ok",
            WorkspaceId: "ws_1",
            ProjectId: "proj_1",
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAtUtc: DateTimeOffset.UtcNow);

        var request = new LanguageServerRequest(
            WorkspaceId: "ws_1",
            Method: LanguageServerMethod.Hover,
            DocumentPath: "src/Foo.cs",
            Line: 10,
            Character: 3,
            ProjectId: "proj_1");

        var response = LanguageServerResponse.Unsupported(request.Method, request.CorrelationId);
        var correlated = LanguageServerResponse.Unsupported(
            LanguageServerMethod.Definition,
            correlationId: "corr-1");
        var success = LanguageServerResponse.Success(
            LanguageServerMethod.Hover,
            resultJson: """{"contents":"Foo"}""",
            correlationId: "corr-2");

        Assert.IsTrue(indexResult.Success);
        Assert.AreEqual(CodeIndexStatus.Completed, indexResult.Status);
        Assert.AreEqual(LanguageServerMethod.Hover, request.Method);
        Assert.IsFalse(response.IsSupported);
        Assert.AreEqual("Language server is not implemented.", response.Error);
        Assert.AreEqual("corr-1", correlated.CorrelationId);
        Assert.IsTrue(success.IsSupported);
        Assert.AreEqual("corr-2", success.CorrelationId);
        Assert.AreEqual("""{"contents":"Foo"}""", success.ResultJson);
    }

    [TestMethod]
    public void Query_Container_Descriptors_Exist()
    {
        var workspace = new CodeWorkspaceDescriptor("ws_1", "proj_1", "C:\\code\\proj_1");
        var removeStatus = CodeProjectStatus.Removed;
        var relation = new CodeRelationRecord(workspace.WorkspaceId, workspace.ProjectId, "a", "b", CodeRelationKind.Contains);

        Assert.AreEqual("ws_1", workspace.WorkspaceId);
        Assert.AreEqual("proj_1", workspace.ProjectId);
        Assert.IsFalse(workspace.IsLoaded);
        Assert.AreEqual(CodeProjectStatus.Removed, removeStatus);
        Assert.AreEqual(CodeRelationKind.Contains, relation.Kind);
    }
}
