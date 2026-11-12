using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.AgentDirectoryClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace AgentDirectoryClient.Tests;

[TestClass]
public sealed class DirectoryDemoTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly string ExpectedUsage = string.Join(Environment.NewLine,
        "Usage: DirectoryDemo get <workspaceId> <agentId>",
        "       DirectoryDemo list <workspaceId> [cursor]");

    [TestMethod]
    public async Task Get_Success_CallsGetOnce_AndWritesEntryJson()
    {
        var entry = Entry();
        var api = new FakeAgentDirectoryApi { Entry = entry };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls, "GetAsync must be called exactly once.");
        Assert.AreEqual(0, api.ListCalls, "A successful get must not list.");
        Assert.AreEqual(new AgentRef("workspace-one", "agent-one"), api.LastAgent);
        AssertEntryJson(ParseSingleLine(result.Output), entry);
    }

    [TestMethod]
    public async Task List_Success_CallsListOnce_AndWritesPageJson()
    {
        var page = new AgentDirectoryPage(ImmutableArray.Create(Entry()), "cursor-2", false);
        var api = new FakeAgentDirectoryApi { Page = page };

        var result = await Run(api, ["list", "workspace-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls, "ListAsync must be called exactly once.");
        Assert.AreEqual(0, api.GetCalls, "A successful list must not get.");
        Assert.AreEqual(new AgentListQuery("workspace-one", 50, null), api.LastQuery);
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEqual(new[] { "agents", "cursor", "complete" }, Names(json));
        Assert.AreEqual(1, json.GetProperty("agents").GetArrayLength());
        AssertEntryJson(json.GetProperty("agents")[0], page.Agents[0]);
        Assert.AreEqual("cursor-2", json.GetProperty("cursor").GetString());
        Assert.IsFalse(json.GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    public async Task List_EmptyPage_KeepsAgentsCursorCompleteFields()
    {
        var api = new FakeAgentDirectoryApi
        {
            Page = new AgentDirectoryPage(ImmutableArray<AgentDirectoryEntry>.Empty, null, true)
        };

        var result = await Run(api, ["list", "workspace-one"]);

        Assert.AreEqual(0, result.ExitCode, "A legitimate empty page is a successful query.");
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls);
        Assert.AreEqual(0, api.GetCalls);
        Assert.AreEqual("{\"agents\":[],\"cursor\":null,\"complete\":true}" + Environment.NewLine,
            result.Output);
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEqual(new[] { "agents", "cursor", "complete" }, Names(json));
        Assert.AreEqual(0, json.GetProperty("agents").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("cursor").ValueKind);
        Assert.IsTrue(json.GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    public async Task Get_PassesArgumentsVerbatim_IncludingChineseAndSurroundingSpaces()
    {
        const string workspaceId = "  工作区-甲  ";
        const string agentId = "\t代理-乙 ";
        var api = new FakeAgentDirectoryApi { Entry = Entry(workspaceId, agentId) };

        var result = await Run(api, ["get", workspaceId, agentId]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.IsNotNull(api.LastAgent);
        Assert.AreEqual(workspaceId, api.LastAgent!.WorkspaceId, "The workspace id must not be trimmed.");
        Assert.AreEqual(agentId, api.LastAgent.AgentId, "The agent id must not be trimmed.");
        Assert.AreEqual(new AgentRef(workspaceId, agentId), api.LastAgent);
    }

    [TestMethod]
    public async Task List_PassesWorkspaceAndCursorVerbatim()
    {
        const string workspaceId = " 工作区-乙 ";
        const string cursor = "  下一页/二  ";
        var api = new FakeAgentDirectoryApi
        {
            Page = new AgentDirectoryPage(ImmutableArray.Create(Entry()), cursor, true)
        };

        var result = await Run(api, ["list", workspaceId, cursor]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ListCalls);
        Assert.AreEqual(0, api.GetCalls);
        Assert.IsNotNull(api.LastQuery);
        Assert.AreEqual(workspaceId, api.LastQuery!.WorkspaceId, "The workspace id must not be trimmed.");
        Assert.AreEqual(cursor, api.LastQuery.Cursor, "The cursor must not be trimmed.");
        Assert.AreEqual(new AgentListQuery(workspaceId, 50, cursor), api.LastQuery);
    }

    [TestMethod]
    public async Task List_WithoutCursor_PassesNullCursorAndFrozenMaxCount()
    {
        var api = new FakeAgentDirectoryApi { Page = new AgentDirectoryPage(ImmutableArray<AgentDirectoryEntry>.Empty, null, true) };

        var result = await Run(api, ["list", "workspace-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(api.LastQuery);
        Assert.AreEqual("workspace-one", api.LastQuery!.WorkspaceId);
        Assert.AreEqual(50, api.LastQuery.MaxCount);
        Assert.IsNull(api.LastQuery.Cursor, "An absent cursor must stay null; it must not be invented or turned into an empty string.");
    }

    [TestMethod]
    public async Task CrossWorkspace_ArgumentsArePassedVerbatimWithoutMixing()
    {
        var api = new FakeAgentDirectoryApi { Entry = Entry("workspace-甲", "agent-x") };

        await Run(api, ["get", "workspace-甲", "agent-x"]);
        var first = api.LastAgent;
        await Run(api, ["get", "workspace-乙", "agent-x"]);

        Assert.AreEqual(2, api.GetCalls);
        Assert.AreEqual(0, api.ListCalls);
        Assert.IsNotNull(first);
        Assert.AreEqual(new AgentRef("workspace-甲", "agent-x"), first);
        Assert.AreEqual(new AgentRef("workspace-乙", "agent-x"), api.LastAgent);
    }

    [TestMethod]
    public async Task NoArguments_ReturnUsageWithoutCallingApi() => await AssertUsageError([]);

    [TestMethod]
    public async Task UnknownCommand_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["remove", "workspace-one", "agent-one"],
            ["GET", "workspace-one", "agent-one"],
            ["Get", "workspace-one", "agent-one"],
            ["getAll", "workspace-one"],
            [""]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task WrongArgumentCount_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["get"],
            ["get", "workspace-one"],
            ["get", "workspace-one", "agent-one", "extra"],
            ["list"],
            ["list", "workspace-one", "cursor-1", "extra"],
            ["get", "workspace-one", "agent-one", "extra", "more"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task BlankFields_ReturnUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["get", " ", "agent-one"],
            ["get", "workspace-one", ""],
            ["get", "workspace-one", "\t"],
            ["get", "  ", "  "],
            ["list", " "],
            ["list", ""],
            ["list", "workspace-one", ""],
            ["list", "workspace-one", "   "],
            ["list", "", "cursor-1"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task GetNotFound_WritesFoundFalse_AndReturnsThree()
    {
        var api = new FakeAgentDirectoryApi { Entry = null };

        var result = await Run(api, ["get", "workspace-one", "agent-missing"]);

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("{\"found\":false}" + Environment.NewLine, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.ListCalls);
    }

    [TestMethod]
    public async Task AgentDirectoryException_WritesOnlyCode_AndReturnsOne()
    {
        const string message = "internal storage detail for workspace-one that must never leak";

        var getApi = new FakeAgentDirectoryApi
        {
            GetFailure = new AgentDirectoryException("directory.unavailable", message)
        };
        var queried = await Run(getApi, ["get", "workspace-one", "agent-one"]);
        Assert.AreEqual(1, queried.ExitCode);
        Assert.AreEqual(string.Empty, queried.Output);
        Assert.AreEqual("code=directory.unavailable" + Environment.NewLine, queried.Error);
        Assert.IsFalse(queried.Error.Contains("internal storage detail", StringComparison.Ordinal),
            "Server messages must never reach stderr.");
        Assert.AreEqual(1, getApi.GetCalls);
        Assert.AreEqual(0, getApi.ListCalls);

        var listApi = new FakeAgentDirectoryApi
        {
            ListFailure = new AgentDirectoryException("cursor.expired", message)
        };
        var listed = await Run(listApi, ["list", "workspace-one"]);
        Assert.AreEqual(1, listed.ExitCode);
        Assert.AreEqual(string.Empty, listed.Output);
        Assert.AreEqual("code=cursor.expired" + Environment.NewLine, listed.Error);
        Assert.IsFalse(listed.Error.Contains("internal storage detail", StringComparison.Ordinal));
        Assert.AreEqual(1, listApi.ListCalls);
        Assert.AreEqual(0, listApi.GetCalls);
    }

    [TestMethod]
    public async Task CallerCancellation_Returns130_WithoutExtraCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var getApi = new FakeAgentDirectoryApi { Entry = Entry() };
        var queried = await Run(getApi, ["get", "workspace-one", "agent-one"], cancellation.Token);
        Assert.AreEqual(130, queried.ExitCode);
        Assert.AreEqual(1, getApi.GetCalls, "Cancellation must not trigger a retry.");
        Assert.AreEqual(0, getApi.ListCalls, "Cancellation must not trigger a second query.");
        Assert.AreEqual(string.Empty, queried.Output);
        Assert.AreEqual(string.Empty, queried.Error);

        var listApi = new FakeAgentDirectoryApi
        {
            Page = new AgentDirectoryPage(ImmutableArray.Create(Entry()), null, true)
        };
        var listed = await Run(listApi, ["list", "workspace-one"], cancellation.Token);
        Assert.AreEqual(130, listed.ExitCode);
        Assert.AreEqual(1, listApi.ListCalls, "Cancellation must not trigger a retry.");
        Assert.AreEqual(0, listApi.GetCalls);
        Assert.AreEqual(string.Empty, listed.Output);
        Assert.AreEqual(string.Empty, listed.Error);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeAgentDirectoryApi
        {
            GetFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            DirectoryDemo.RunAsync(["get", "workspace-one", "agent-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.ListCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task UnexpectedException_IsNotCaughtOrConvertedToSuccess()
    {
        var api = new FakeAgentDirectoryApi { ListFailure = new InvalidOperationException("boom") };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DirectoryDemo.RunAsync(["list", "workspace-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.ListCalls);
        Assert.AreEqual(0, api.GetCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task NullReferenceArguments_ThrowArgumentNullException()
    {
        var api = new FakeAgentDirectoryApi { Page = new AgentDirectoryPage(ImmutableArray<AgentDirectoryEntry>.Empty, null, true) };
        string[] args = ["get", "workspace-one", "agent-one"];

        await AssertNullArgument("args",
            () => DirectoryDemo.RunAsync(null!, api, new StringWriter(), new StringWriter()));
        await AssertNullArgument("api",
            () => DirectoryDemo.RunAsync(args, null!, new StringWriter(), new StringWriter()));
        await AssertNullArgument("output",
            () => DirectoryDemo.RunAsync(args, api, null!, new StringWriter()));
        await AssertNullArgument("error",
            () => DirectoryDemo.RunAsync(args, api, new StringWriter(), null!));
    }

    private static async Task AssertNullArgument(string parameterName, Func<Task> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        Assert.AreEqual(parameterName, exception.ParamName);
    }

    private static async Task AssertUsageError(string[] args)
    {
        var api = new FakeAgentDirectoryApi
        {
            Page = new AgentDirectoryPage(ImmutableArray<AgentDirectoryEntry>.Empty, null, true)
        };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode, $"Expected usage exit for: [{string.Join(' ', args)}]");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.GetCalls, "Argument errors must not call GetAsync.");
        Assert.AreEqual(0, api.ListCalls, "Argument errors must not call ListAsync.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(IAgentDirectoryApi api, string[] args,
        CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await DirectoryDemo.RunAsync(args, api, output, error, ct);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static JsonElement ParseSingleLine(string text)
    {
        Assert.IsTrue(text.EndsWith(Environment.NewLine, StringComparison.Ordinal),
            "stdout must end with exactly one line break.");
        var body = text[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must be exactly one line.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static void AssertEntryJson(JsonElement json, AgentDirectoryEntry entry)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "workspaceId", "agentId", "displayName", "templateId", "templateVersion", "revision", "state",
                "updatedAt"
            },
            Names(json));
        Assert.AreEqual(entry.WorkspaceId, json.GetProperty("workspaceId").GetString());
        Assert.AreEqual(entry.AgentId, json.GetProperty("agentId").GetString());
        Assert.AreEqual(entry.DisplayName, json.GetProperty("displayName").GetString());
        Assert.AreEqual(entry.TemplateId, json.GetProperty("templateId").GetString());
        Assert.AreEqual(entry.TemplateVersion, json.GetProperty("templateVersion").GetInt32());
        Assert.AreEqual(entry.Revision, json.GetProperty("revision").GetInt64());
        Assert.AreEqual(entry.State, json.GetProperty("state").GetString());
        Assert.AreEqual(entry.UpdatedAt, json.GetProperty("updatedAt").GetDateTimeOffset());
    }

    private static AgentDirectoryEntry Entry(string workspaceId = "workspace-one",
        string agentId = "agent-one") =>
        new(workspaceId, agentId, "Agent One", "template-1", 3, 7, "Active", UpdatedAt);

    private sealed class FakeAgentDirectoryApi : IAgentDirectoryApi
    {
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public AgentListQuery? LastQuery { get; private set; }
        public AgentRef? LastAgent { get; private set; }
        public AgentDirectoryPage Page { get; init; } = null!;
        public AgentDirectoryEntry? Entry { get; init; }
        public Exception? ListFailure { get; init; }
        public Exception? GetFailure { get; init; }

        public Task<AgentDirectoryPage> ListAsync(AgentListQuery query, CancellationToken ct = default)
        {
            ListCalls++;
            LastQuery = query;
            ct.ThrowIfCancellationRequested();
            return ListFailure is null
                ? Task.FromResult(Page)
                : Task.FromException<AgentDirectoryPage>(ListFailure);
        }

        public Task<AgentDirectoryEntry?> GetAsync(AgentRef agent, CancellationToken ct = default)
        {
            GetCalls++;
            LastAgent = agent;
            ct.ThrowIfCancellationRequested();
            return GetFailure is null
                ? Task.FromResult(Entry)
                : Task.FromException<AgentDirectoryEntry?>(GetFailure);
        }
    }
}
