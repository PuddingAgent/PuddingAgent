using System.Text.Json;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// D2 的接入面：<c>code_symbol_search</c> 不得把陈旧索引行当权威结果。
/// <para>
/// 断言两件事：① 显式项目未在注册表登记 ⇒ fail-closed（不返回索引视图里的命中）；
/// ② 命中路径必须通过校验（文件存在 + 落在其登记项目根目录内），失效命中计入 stale_skipped
/// 并从 <c>results</c> 剔除，且"全是陈旧命中"不得读成"符号不存在"。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSymbolSearchStalePathGateTests
{
    private const string WorkspaceId = "workspace-1";

    private static Task<ToolExecutionResult> ExecuteAsync(CodeSymbolSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = WorkspaceId,
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

    [TestMethod]
    public async Task Explicit_Unregistered_Project_Fails_Closed_Without_Returning_Stale_Hits()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-d2-unregistered");
        try
        {
            var file = Path.Combine(temp.FullName, "Widget.cs");
            await File.WriteAllTextAsync(file, "class Widget {}");
            var service = new SearchQueryService([Detail(file, "project-gone")]);
            var tool = new CodeSymbolSearchTool(service, resolver: null, new RegistryStub([]));

            var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-gone"}""");

            Assert.IsFalse(result.Success, "未登记项目必须 fail-closed");
            StringAssert.Contains(result.Error, "project-gone");
            StringAssert.Contains(result.Error, "not registered");
            Assert.AreEqual(0, service.SearchCallCount, "未登记项目不得触达索引查询");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Registered_Project_With_Existing_File_Inside_Root_Is_Returned()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-d2-healthy");
        try
        {
            var file = Path.Combine(temp.FullName, "Widget.cs");
            await File.WriteAllTextAsync(file, "class Widget {}");
            var service = new SearchQueryService([Detail(file, "project-live")]);
            var tool = new CodeSymbolSearchTool(
                service,
                resolver: null,
                new RegistryStub([Project("project-live", temp.FullName)]));

            var result = await ExecuteAsync(tool, """{"query":"Widget","project_id":"project-live"}""");

            Assert.IsTrue(result.Success, result.Error);
            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.AreEqual(1, json.RootElement.GetProperty("count").GetInt32());
            Assert.AreEqual(0, json.RootElement.GetProperty("stale_skipped").GetInt32());
            Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
            Assert.AreEqual(
                file,
                json.RootElement.GetProperty("results")[0].GetProperty("file_path").GetString());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Hit_Whose_File_No_Longer_Exists_Is_Skipped_And_Not_Reported_As_No_Match()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-d2-stale");
        try
        {
            var stalePath = Path.Combine(temp.FullName, "Deleted.cs");
            var service = new SearchQueryService([Detail(stalePath, "project-live")]);
            var tool = new CodeSymbolSearchTool(
                service,
                resolver: null,
                new RegistryStub([Project("project-live", temp.FullName)]));

            var result = await ExecuteAsync(tool, """{"query":"Deleted","project_id":"project-live"}""");

            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.AreEqual(0, json.RootElement.GetProperty("count").GetInt32());
            Assert.AreEqual(1, json.RootElement.GetProperty("stale_skipped").GetInt32());
            Assert.AreEqual(
                "file_not_found",
                json.RootElement.GetProperty("stale_examples")[0].GetProperty("reason").GetString());
            StringAssert.Contains(result.Output!, "were stale");
            Assert.IsFalse(
                result.Output!.TrimStart().StartsWith("(no matches)", StringComparison.Ordinal),
                "陈旧索引不得读成空结果");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Hit_Outside_The_Registered_Root_Is_Skipped()
    {
        var root = Directory.CreateTempSubdirectory("pudding-d2-root");
        var other = Directory.CreateTempSubdirectory("pudding-d2-other");
        try
        {
            var foreignFile = Path.Combine(other.FullName, "Elsewhere.cs");
            await File.WriteAllTextAsync(foreignFile, "class Elsewhere {}");
            var service = new SearchQueryService([Detail(foreignFile, "project-live")]);
            var tool = new CodeSymbolSearchTool(
                service,
                resolver: null,
                new RegistryStub([Project("project-live", root.FullName)]));

            var result = await ExecuteAsync(tool, """{"query":"Elsewhere","project_id":"project-live"}""");

            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.AreEqual(0, json.RootElement.GetProperty("count").GetInt32());
            Assert.AreEqual(1, json.RootElement.GetProperty("stale_skipped").GetInt32());
            Assert.AreEqual(
                "outside_registered_root",
                json.RootElement.GetProperty("stale_examples")[0].GetProperty("reason").GetString());
        }
        finally
        {
            root.Delete(recursive: true);
            other.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task IncludeStale_Returns_The_Raw_Row_Marked_With_Its_Reason()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-d2-include-stale");
        try
        {
            var stalePath = Path.Combine(temp.FullName, "Deleted.cs");
            var service = new SearchQueryService([Detail(stalePath, "project-live")]);
            var tool = new CodeSymbolSearchTool(
                service,
                resolver: null,
                new RegistryStub([Project("project-live", temp.FullName)]));

            var result = await ExecuteAsync(
                tool,
                """{"query":"Deleted","project_id":"project-live","include_stale":true}""");

            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.AreEqual(1, json.RootElement.GetProperty("count").GetInt32());
            Assert.IsTrue(json.RootElement.GetProperty("results_include_stale").GetBoolean());
            Assert.AreEqual(
                "file_not_found",
                json.RootElement.GetProperty("results")[0].GetProperty("stale_reason").GetString());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Without_Registry_Only_Existence_Is_Validated()
    {
        var temp = Directory.CreateTempSubdirectory("pudding-d2-degraded");
        try
        {
            var file = Path.Combine(temp.FullName, "Widget.cs");
            await File.WriteAllTextAsync(file, "class Widget {}");
            var service = new SearchQueryService([Detail(file, "project-live")]);
            var tool = new CodeSymbolSearchTool(service);

            var result = await ExecuteAsync(tool, """{"query":"Widget"}""");

            using var json = JsonDocument.Parse(StripNotes(result.Output!));
            Assert.AreEqual(1, json.RootElement.GetProperty("count").GetInt32());
            Assert.IsFalse(
                json.RootElement.GetProperty("complete").GetBoolean(),
                "注册表不可用时必须显式声明覆盖不完整，不得假装完成");
            Assert.AreEqual(0, json.RootElement.GetProperty("stale_skipped").GetInt32());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    /// <summary>输出 = JSON 主体 + 人类可读提示行；测试只解析 JSON 主体。</summary>
    private static string StripNotes(string output)
    {
        var index = output.IndexOf("\n\n", StringComparison.Ordinal);
        return index < 0 ? output : output[..index];
    }

    private static CodeSymbolDetail Detail(string filePath, string projectId)
        => new(new CodeSymbolRecord(
            WorkspaceId,
            projectId,
            filePath,
            SymbolId: "sym-1",
            Name: "Widget",
            Kind: CodeSymbolKind.Class,
            StartLine: 1,
            EndLine: 1));

    private static CodeProjectRecord Project(string projectId, string rootPath)
        => new(WorkspaceId, projectId, rootPath, CodeProjectStatus.Active);

    private sealed class SearchQueryService(IReadOnlyList<CodeSymbolDetail> hits) : ICodeQueryService
    {
        public int SearchCallCount { get; private set; }

        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(hits);
        }

        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
            string workspaceId, string projectId, string symbolId, int maxDepth = 3,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistryStub(IReadOnlyList<CodeProjectRecord> projects) : ICodeProjectRegistry
    {
        public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
            string workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects);

        public Task<CodeProjectRecord?> GetProjectAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects.FirstOrDefault(p =>
                string.Equals(p.ProjectId, projectId, StringComparison.Ordinal)));

        public Task<CodeIndexResult> AddProjectAsync(
            CodeProjectAddRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CodeIndexResult> RemoveProjectAsync(
            CodeProjectRemoveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
