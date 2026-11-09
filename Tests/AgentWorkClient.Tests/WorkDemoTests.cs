using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.AgentWorkClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace AgentWorkClient.Tests;

[TestClass]
public sealed class WorkDemoTests
{
    private static readonly AgentRef Agent = new("workspace-one", "agent-one");
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly string ExpectedUsage = string.Join(Environment.NewLine,
        "Usage: WorkDemo submit <workspaceId> <agentId> <operationId> <text>",
        "       WorkDemo get <workspaceId> <agentId> <operationId>");

    [TestMethod]
    public async Task Submit_Success_CallsSubmitOnce_AndWritesReceiptJson()
    {
        var api = new FakeAgentWorkApi { Receipt = Receipt("op-1") };

        var result = await Run(api, ["submit", "workspace-one", "agent-one", "op-1", "hello world"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.SubmitCalls, "SubmitAsync must be called exactly once.");
        Assert.AreEqual(0, api.GetCalls, "A successful submit must not query status automatically.");
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEqual(
            new[]
            {
                "agent", "operationId", "workId", "conversationId", "inputMessageId", "turnId", "commandId",
                "acceptedSequence", "acceptedAt"
            },
            Names(json));
        Assert.AreEqual("op-1", json.GetProperty("operationId").GetString());
        Assert.AreEqual("work-op-1", json.GetProperty("workId").GetString());
        Assert.AreEqual("workspace-one", json.GetProperty("agent").GetProperty("workspaceId").GetString());
        Assert.AreEqual("agent-one", json.GetProperty("agent").GetProperty("agentId").GetString());
        Assert.AreEqual(1L, json.GetProperty("acceptedSequence").GetInt64());
        Assert.AreEqual(AcceptedAt, json.GetProperty("acceptedAt").GetDateTimeOffset());
    }

    [TestMethod]
    public async Task Get_Success_CallsGetOnce_AndWritesSnapshotJson()
    {
        var api = new FakeAgentWorkApi
        {
            Snapshot = new AgentWorkSnapshot(Receipt("op-1"), AgentWorkStatus.Accepted, null, null)
        };

        var result = await Run(api, ["get", "workspace-one", "agent-one", "op-1"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls, "GetAsync must be called exactly once.");
        Assert.AreEqual(0, api.SubmitCalls);
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEqual(
            new[] { "receipt", "status", "terminal", "diagnosticCode", "verification", "isTerminal" },
            Names(json));
        Assert.AreEqual("Accepted", json.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("terminal").ValueKind);
        Assert.AreEqual("not_requested", json.GetProperty("verification").GetString());
        Assert.IsFalse(json.GetProperty("isTerminal").GetBoolean());
    }

    [TestMethod]
    public async Task Submit_PassesArgumentsVerbatim_IncludingChineseAndSurroundingSpaces()
    {
        const string text = "  请检查当前工作区，并报告发现。  ";
        var api = new FakeAgentWorkApi { Receipt = Receipt("job-42.v1") };

        var result = await Run(api, ["submit", "workspace-1", "agent_1", "job-42.v1", text]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(new AgentRef("workspace-1", "agent_1"), api.LastAgent);
        Assert.AreEqual(new AgentWorkRequest("job-42.v1", text), api.LastRequest);
        Assert.AreEqual(text, api.LastRequest!.Text, "The text must not be trimmed or joined.");
        Assert.AreEqual(1, api.SubmitCalls);
        Assert.AreEqual(0, api.GetCalls);
    }

    [TestMethod]
    public async Task Get_PassesArgumentsVerbatim()
    {
        var snapshot = new AgentWorkSnapshot(Receipt("job-42.v1"), AgentWorkStatus.Completed,
            new AgentWorkTerminal("event-1", "run-1", 2, "done", null, null), null);
        var api = new FakeAgentWorkApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "workspace-1", "agent_1", "job-42.v1"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(new AgentRef("workspace-1", "agent_1"), api.LastAgent);
        Assert.AreEqual("job-42.v1", api.LastGetOperationId);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.SubmitCalls);
    }

    [TestMethod]
    public async Task NoArguments_ReturnUsageWithoutCallingApi() => await AssertUsageError([]);

    [TestMethod]
    public async Task UnknownCommand_ReturnsUsageWithoutCallingApi() =>
        await AssertUsageError(["remove", "workspace-one", "agent-one", "op-1"]);

    [TestMethod]
    public async Task WrongArgumentCount_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["submit", "workspace-one", "agent-one", "op-1"],
            ["submit", "workspace-one", "agent-one", "op-1", "hello", "extra"],
            ["get", "workspace-one", "agent-one"],
            ["get", "workspace-one", "agent-one", "op-1", "extra"],
            ["submit"],
            ["get"]
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
            ["submit", " ", "agent-one", "op-1", "hello"],
            ["submit", "workspace-one", "\t", "op-1", "hello"],
            ["submit", "workspace-one", "agent-one", "  ", "hello"],
            ["submit", "workspace-one", "agent-one", "op-1", "   "],
            ["submit", "workspace-one", "agent-one", "op-1", ""],
            ["get", " ", "agent-one", "op-1"],
            ["get", "workspace-one", "", "op-1"],
            ["get", "workspace-one", "agent-one", " "]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task UnknownEffect_ReturnsFour_WithoutRetryOrSensitiveText()
    {
        var submitApi = new FakeAgentWorkApi
        {
            SubmitFailure = new AgentWorkException("submission.unknown", "internal server detail 12345", "unknown")
        };
        var submitted = await Run(submitApi, ["submit", "workspace-one", "agent-one", "op-1", "hello"]);
        Assert.AreEqual(4, submitted.ExitCode);
        Assert.AreEqual(1, submitApi.SubmitCalls, "An unknown outcome must not be retried.");
        Assert.AreEqual(0, submitApi.GetCalls, "The sample must not query status automatically.");
        Assert.AreEqual(string.Empty, submitted.Output);
        Assert.AreEqual("code=submission.unknown effectOutcome=unknown operationId=op-1" + Environment.NewLine,
            submitted.Error);
        Assert.IsFalse(submitted.Error.Contains("internal server detail", StringComparison.Ordinal),
            "Server messages must never reach stderr.");

        var getApi = new FakeAgentWorkApi
        {
            GetFailure = new AgentWorkException("transport.invalid_response", "raw response body", "unknown")
        };
        var queried = await Run(getApi, ["get", "workspace-one", "agent-one", "op-1"]);
        Assert.AreEqual(4, queried.ExitCode);
        Assert.AreEqual(1, getApi.GetCalls);
        Assert.AreEqual(0, getApi.SubmitCalls);
        Assert.AreEqual("code=transport.invalid_response effectOutcome=unknown operationId=op-1" + Environment.NewLine,
            queried.Error);
        Assert.IsFalse(queried.Error.Contains("raw response body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NotAppliedFailure_ReturnsOne_WithoutSensitiveText()
    {
        var api = new FakeAgentWorkApi
        {
            SubmitFailure = new AgentWorkException("operation.conflict", "different text was already accepted",
                "not_applied")
        };

        var result = await Run(api, ["submit", "workspace-one", "agent-one", "op-1", "hello"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(1, api.SubmitCalls, "A definite failure must not be retried either.");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual("code=operation.conflict effectOutcome=not_applied operationId=op-1" + Environment.NewLine,
            result.Error);
        Assert.IsFalse(result.Error.Contains("different text", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetNotFound_WritesFoundFalse_AndReturnsThree()
    {
        var api = new FakeAgentWorkApi { Snapshot = null };

        var result = await Run(api, ["get", "workspace-one", "agent-one", "op-1"]);

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("{\"found\":false}" + Environment.NewLine, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
    }

    [TestMethod]
    public async Task FailedAndCancelledQueries_ReturnZero_WithoutRewritingStatus()
    {
        foreach (var status in new[] { AgentWorkStatus.Failed, AgentWorkStatus.Cancelled })
        {
            var terminal = new AgentWorkTerminal("event-1", "run-1", 2, null, "failure.code", "failure message");
            var api = new FakeAgentWorkApi
            {
                Snapshot = new AgentWorkSnapshot(Receipt("op-1"), status, terminal, null)
            };

            var result = await Run(api, ["get", "workspace-one", "agent-one", "op-1"]);

            Assert.AreEqual(0, result.ExitCode, $"{status} is still a successful query.");
            Assert.AreEqual(string.Empty, result.Error);
            var json = ParseSingleLine(result.Output);
            Assert.AreEqual(status.ToString(), json.GetProperty("status").GetString());
            Assert.IsTrue(json.GetProperty("isTerminal").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, json.GetProperty("terminal").GetProperty("reply").ValueKind);
        }
    }

    [TestMethod]
    public async Task EmptyReply_KeepsCompletedStatus()
    {
        var terminal = new AgentWorkTerminal("event-1", "run-1", 2, string.Empty, null, null);
        var api = new FakeAgentWorkApi
        {
            Snapshot = new AgentWorkSnapshot(Receipt("op-1"), AgentWorkStatus.Completed, terminal, null)
        };

        var result = await Run(api, ["get", "workspace-one", "agent-one", "op-1"]);

        Assert.AreEqual(0, result.ExitCode);
        var json = ParseSingleLine(result.Output);
        Assert.AreEqual("Completed", json.GetProperty("status").GetString());
        Assert.AreEqual(string.Empty, json.GetProperty("terminal").GetProperty("reply").GetString());
        Assert.IsTrue(json.GetProperty("isTerminal").GetBoolean());
    }

    [TestMethod]
    public async Task CanceledToken_Returns130_WithoutExtraCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var submitApi = new FakeAgentWorkApi { Receipt = Receipt("op-1") };
        var submitted = await Run(submitApi, ["submit", "workspace-one", "agent-one", "op-1", "hello"],
            cancellation.Token);
        Assert.AreEqual(130, submitted.ExitCode);
        Assert.AreEqual(1, submitApi.SubmitCalls, "Cancellation must not trigger a retry.");
        Assert.AreEqual(0, submitApi.GetCalls, "Cancellation must not trigger a status query or cancel command.");
        Assert.AreEqual(string.Empty, submitted.Output);
        Assert.AreEqual(string.Empty, submitted.Error);

        var getApi = new FakeAgentWorkApi
        {
            Snapshot = new AgentWorkSnapshot(Receipt("op-1"), AgentWorkStatus.Accepted, null, null)
        };
        var queried = await Run(getApi, ["get", "workspace-one", "agent-one", "op-1"], cancellation.Token);
        Assert.AreEqual(130, queried.ExitCode);
        Assert.AreEqual(1, getApi.GetCalls);
        Assert.AreEqual(0, getApi.SubmitCalls);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeAgentWorkApi
        {
            SubmitFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            WorkDemo.RunAsync(["submit", "workspace-one", "agent-one", "op-1", "hello"], api, output, error,
                CancellationToken.None));
        Assert.AreEqual(1, api.SubmitCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    private static async Task AssertUsageError(string[] args)
    {
        var api = new FakeAgentWorkApi { Receipt = Receipt("op-1") };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode, $"Expected usage exit for: {string.Join(' ', args)}");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.SubmitCalls, "Argument errors must not call SubmitAsync.");
        Assert.AreEqual(0, api.GetCalls, "Argument errors must not call GetAsync.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(IAgentWorkApi api, string[] args,
        CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await WorkDemo.RunAsync(args, api, output, error, ct);
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

    private static AgentWorkReceipt Receipt(string operationId) => new(Agent, operationId, "work-" + operationId,
        "conversation-1", "input-1", "turn-1", "command-1", 1, AcceptedAt);

    private sealed class FakeAgentWorkApi : IAgentWorkApi
    {
        public int SubmitCalls { get; private set; }
        public int GetCalls { get; private set; }
        public AgentRef? LastAgent { get; private set; }
        public AgentWorkRequest? LastRequest { get; private set; }
        public string? LastGetOperationId { get; private set; }
        public AgentWorkReceipt Receipt { get; init; } = null!;
        public AgentWorkSnapshot? Snapshot { get; init; }
        public Exception? SubmitFailure { get; init; }
        public Exception? GetFailure { get; init; }

        public Task<AgentWorkReceipt> SubmitAsync(AgentRef agent, AgentWorkRequest request,
            CancellationToken ct = default)
        {
            SubmitCalls++;
            LastAgent = agent;
            LastRequest = request;
            ct.ThrowIfCancellationRequested();
            return SubmitFailure is null
                ? Task.FromResult(Receipt)
                : Task.FromException<AgentWorkReceipt>(SubmitFailure);
        }

        public Task<AgentWorkSnapshot?> GetAsync(AgentRef agent, string operationId, CancellationToken ct = default)
        {
            GetCalls++;
            LastAgent = agent;
            LastGetOperationId = operationId;
            ct.ThrowIfCancellationRequested();
            return GetFailure is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<AgentWorkSnapshot?>(GetFailure);
        }
    }
}
