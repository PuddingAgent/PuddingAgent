using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.AgentModelPolicyClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace AgentModelPolicyClient.Tests;

[TestClass]
public sealed class PolicyDemoTests
{
    private static readonly DateTimeOffset ResolvedAt = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly string ExpectedUsage = "Usage: PolicyDemo get <workspaceId> <agentId>";

    [TestMethod]
    public async Task Get_InheritPrimarySnapshot_PreservesInheritanceAndJson()
    {
        var snapshot = InheritPrimarySnapshot();
        var api = new FakeAgentModelPolicyApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls, "GetEffectiveAsync must be called exactly once.");
        Assert.AreEqual(0, api.UpdateCalls, "The read-only sample must never call UpdateAsync.");
        Assert.AreEqual(new AgentRef("workspace-one", "agent-one"), api.LastAgent);
        var json = ParseSingleLine(result.Output);
        AssertPolicyJson(json, snapshot);
        // An explicit "inherit Primary" purpose keeps the inherited model and the flag; nothing is recomputed.
        var review = Entry(json, "Review");
        Assert.IsTrue(review.GetProperty("inheritPrimary").GetBoolean());
        Assert.AreEqual("provider/model-primary", review.GetProperty("modelId").GetString());
    }

    [TestMethod]
    public async Task Get_InstanceOverrideSnapshot_PreservesOverrideSource()
    {
        var snapshot = InstanceOverrideSnapshot();
        var api = new FakeAgentModelPolicyApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        var json = ParseSingleLine(result.Output);
        AssertPolicyJson(json, snapshot);
        var review = Entry(json, "Review");
        Assert.AreEqual("provider/model-review", review.GetProperty("modelId").GetString());
        Assert.AreEqual("rev-review", review.GetProperty("modelRevision").GetString());
        Assert.AreEqual("instance", review.GetProperty("source").GetString());
        Assert.AreEqual(9L, review.GetProperty("sourceRevision").GetInt64());
        Assert.IsFalse(review.GetProperty("inheritPrimary").GetBoolean());
    }

    [TestMethod]
    public async Task Get_UnconfiguredSnapshot_KeepsMissingReasonAndNoModel()
    {
        var snapshot = UnconfiguredSnapshot();
        var api = new FakeAgentModelPolicyApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode, "A missing purpose is still a successful effective-policy query.");
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        var json = ParseSingleLine(result.Output);
        AssertPolicyJson(json, snapshot);
        var review = Entry(json, "Review");
        Assert.AreEqual(JsonValueKind.Null, review.GetProperty("modelId").ValueKind,
            "The sample must never invent a default model.");
        Assert.AreEqual(JsonValueKind.Null, review.GetProperty("source").ValueKind);
        Assert.AreEqual("model_policy_missing", review.GetProperty("missingReason").GetString());
        Assert.IsFalse(review.GetProperty("inheritPrimary").GetBoolean());
    }

    [TestMethod]
    public async Task Get_PersistedButNotAppliedSnapshot_KeepsRevisionsAndPendingReasons()
    {
        var snapshot = PersistedNotAppliedSnapshot();
        var api = new FakeAgentModelPolicyApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        var json = ParseSingleLine(result.Output);
        AssertPolicyJson(json, snapshot);
        Assert.IsLessThan(snapshot.EffectiveRevision, snapshot.AppliedRevision);
        Assert.AreEqual(8L, json.GetProperty("effectiveRevision").GetInt64());
        Assert.AreEqual(7L, json.GetProperty("appliedRevision").GetInt64());
        Assert.AreEqual(4L, json.GetProperty("policyGeneration").GetInt64());
        CollectionAssert.AreEqual(new[] { "next_run" }, Strings(json.GetProperty("pendingReasons")),
            "Pending-application reasons must be preserved verbatim.");
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
            ["get", "\r\n", "agent-one"],
            ["get", "workspace-one", " \t "]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task GetNotFound_WritesFoundFalse_AndReturnsThree()
    {
        var api = new FakeAgentModelPolicyApi { Snapshot = null };

        var result = await Run(api, ["get", "workspace-one", "agent-missing"]);

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("{\"found\":false}" + Environment.NewLine, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
    }

    [TestMethod]
    public async Task Get_PassesArgumentsVerbatim_IncludingChineseAndSurroundingSpaces()
    {
        const string workspaceId = "  工作区-甲  ";
        const string agentId = "\t代理-乙 ";
        var snapshot = Policy(workspaceId, agentId,
            ImmutableArray.Create(PrimaryEntry(), InstanceOverrideEntry()), ImmutableArray<string>.Empty);
        var api = new FakeAgentModelPolicyApi { Snapshot = snapshot };

        var result = await Run(api, ["get", workspaceId, agentId]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        Assert.IsNotNull(api.LastAgent);
        Assert.AreEqual(workspaceId, api.LastAgent!.WorkspaceId, "The workspace id must not be trimmed.");
        Assert.AreEqual(agentId, api.LastAgent.AgentId, "The agent id must not be trimmed.");
        Assert.AreEqual(new AgentRef(workspaceId, agentId), api.LastAgent);
        var json = ParseSingleLine(result.Output);
        Assert.AreEqual(workspaceId, json.GetProperty("workspaceId").GetString());
        Assert.AreEqual(agentId, json.GetProperty("agentId").GetString());
    }

    [TestMethod]
    public async Task AgentModelPolicyException_WritesOnlyCode_AndReturnsOne()
    {
        const string message = "internal storage detail for workspace-one that must never leak";
        var api = new FakeAgentModelPolicyApi
        {
            GetFailure = new AgentModelPolicyException("model_policy.unavailable", message)
        };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual("code=model_policy.unavailable" + Environment.NewLine, result.Error);
        Assert.IsFalse(result.Error.Contains("internal storage detail", StringComparison.Ordinal),
            "Server messages must never reach stderr.");
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
    }

    [TestMethod]
    public async Task CallerCancellation_Returns130_WithoutExtraCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeAgentModelPolicyApi { Snapshot = InstanceOverrideSnapshot() };

        var result = await Run(api, ["get", "workspace-one", "agent-one"], cancellation.Token);

        Assert.AreEqual(130, result.ExitCode);
        Assert.AreEqual(1, api.GetCalls, "Cancellation must not trigger a retry or a second read.");
        Assert.AreEqual(0, api.UpdateCalls, "Cancellation must not trigger a write.");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeAgentModelPolicyApi
        {
            GetFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            PolicyDemo.RunAsync(["get", "workspace-one", "agent-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task UnexpectedException_IsNotCaughtOrConvertedToSuccess()
    {
        var api = new FakeAgentModelPolicyApi { GetFailure = new InvalidOperationException("boom") };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PolicyDemo.RunAsync(["get", "workspace-one", "agent-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(0, api.UpdateCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task Get_Success_WritesExactlyOneCamelCaseJsonLine()
    {
        var api = new FakeAgentModelPolicyApi { Snapshot = InstanceOverrideSnapshot() };

        var result = await Run(api, ["get", "workspace-one", "agent-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, result.Output.Count(character => character == '\n'), "stdout must be exactly one line.");
        var body = result.Output[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must not contain extra line breaks.");
        using var document = JsonDocument.Parse(body);
        CollectionAssert.AreEqual(
            new[]
            {
                "workspaceId", "agentId", "effectiveRevision", "appliedRevision", "policyGeneration", "resolvedAt",
                "purposes", "pendingReasons"
            },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
            "Every effective snapshot field must be present as a camelCase JSON property.");
    }

    [TestMethod]
    public async Task NullReferenceArguments_ThrowArgumentNullException()
    {
        var api = new FakeAgentModelPolicyApi { Snapshot = InstanceOverrideSnapshot() };
        string[] args = ["get", "workspace-one", "agent-one"];

        await AssertNullArgument("args",
            () => PolicyDemo.RunAsync(null!, api, new StringWriter(), new StringWriter()));
        await AssertNullArgument("api",
            () => PolicyDemo.RunAsync(args, null!, new StringWriter(), new StringWriter()));
        await AssertNullArgument("output",
            () => PolicyDemo.RunAsync(args, api, null!, new StringWriter()));
        await AssertNullArgument("error",
            () => PolicyDemo.RunAsync(args, api, new StringWriter(), null!));
    }

    private static async Task AssertNullArgument(string parameterName, Func<Task> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        Assert.AreEqual(parameterName, exception.ParamName);
    }

    private static async Task AssertUsageError(string[] args)
    {
        var api = new FakeAgentModelPolicyApi { Snapshot = InstanceOverrideSnapshot() };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode, $"Expected usage exit for: [{string.Join(' ', args)}]");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.GetCalls, "Argument errors must not call GetEffectiveAsync.");
        Assert.AreEqual(0, api.UpdateCalls, "Argument errors must not call UpdateAsync.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(IAgentModelPolicyApi api, string[] args,
        CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await PolicyDemo.RunAsync(args, api, output, error, ct);
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

    private static string[] Strings(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static JsonElement Entry(JsonElement json, string purpose) =>
        json.GetProperty("purposes").EnumerateArray().Single(entry =>
            entry.GetProperty("purpose").GetString() == purpose);

    private static void AssertPolicyJson(JsonElement json, EffectiveAgentModelPolicy policy)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "workspaceId", "agentId", "effectiveRevision", "appliedRevision", "policyGeneration", "resolvedAt",
                "purposes", "pendingReasons"
            },
            Names(json));
        Assert.AreEqual(policy.WorkspaceId, json.GetProperty("workspaceId").GetString());
        Assert.AreEqual(policy.AgentId, json.GetProperty("agentId").GetString());
        Assert.AreEqual(policy.EffectiveRevision, json.GetProperty("effectiveRevision").GetInt64());
        Assert.AreEqual(policy.AppliedRevision, json.GetProperty("appliedRevision").GetInt64());
        Assert.AreEqual(policy.PolicyGeneration, json.GetProperty("policyGeneration").GetInt64());
        Assert.AreEqual(policy.ResolvedAt, json.GetProperty("resolvedAt").GetDateTimeOffset());
        Assert.AreEqual(policy.Purposes.Length, json.GetProperty("purposes").GetArrayLength());
        for (var index = 0; index < policy.Purposes.Length; index++)
        {
            AssertEntryJson(json.GetProperty("purposes")[index], policy.Purposes[index]);
        }
        CollectionAssert.AreEqual(policy.PendingReasons.ToArray(), Strings(json.GetProperty("pendingReasons")));
    }

    private static void AssertEntryJson(JsonElement json, EffectiveModelEntry entry)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "purpose", "modelId", "modelRevision", "source", "sourceRevision", "missingReason", "inheritPrimary"
            },
            Names(json));
        Assert.AreEqual(entry.Purpose, json.GetProperty("purpose").GetString());
        Assert.AreEqual(entry.ModelId, json.GetProperty("modelId").GetString());
        Assert.AreEqual(entry.ModelRevision, json.GetProperty("modelRevision").GetString());
        Assert.AreEqual(entry.Source, json.GetProperty("source").GetString());
        Assert.AreEqual(entry.SourceRevision, json.GetProperty("sourceRevision").GetInt64());
        Assert.AreEqual(entry.MissingReason, json.GetProperty("missingReason").GetString());
        Assert.AreEqual(entry.InheritPrimary, json.GetProperty("inheritPrimary").GetBoolean());
    }

    private static EffectiveAgentModelPolicy InheritPrimarySnapshot() => Policy(
        ImmutableArray.Create(
            PrimaryEntry(),
            new EffectiveModelEntry("Review", "provider/model-primary", "rev-primary", "instance", 7, null,
                InheritPrimary: true)),
        ImmutableArray<string>.Empty);

    private static EffectiveAgentModelPolicy InstanceOverrideSnapshot() => Policy(
        ImmutableArray.Create(PrimaryEntry(), InstanceOverrideEntry()),
        ImmutableArray<string>.Empty);

    private static EffectiveAgentModelPolicy UnconfiguredSnapshot() => Policy(
        ImmutableArray.Create(
            PrimaryEntry(),
            new EffectiveModelEntry("Review", null, null, null, 0, "model_policy_missing")),
        ImmutableArray<string>.Empty);

    private static EffectiveAgentModelPolicy PersistedNotAppliedSnapshot() => Policy(
        ImmutableArray.Create(PrimaryEntry(), InstanceOverrideEntry()),
        ImmutableArray.Create("next_run"),
        effectiveRevision: 8,
        appliedRevision: 7,
        policyGeneration: 4);

    private static EffectiveModelEntry PrimaryEntry() =>
        new("Primary", "provider/model-primary", "rev-primary", "workspace", 3, null);

    private static EffectiveModelEntry InstanceOverrideEntry() =>
        new("Review", "provider/model-review", "rev-review", "instance", 9, null);

    private static EffectiveAgentModelPolicy Policy(ImmutableArray<EffectiveModelEntry> purposes,
        ImmutableArray<string> pendingReasons, long effectiveRevision = 7, long appliedRevision = 7,
        long policyGeneration = 3) =>
        Policy("workspace-one", "agent-one", purposes, pendingReasons, effectiveRevision, appliedRevision,
            policyGeneration);

    private static EffectiveAgentModelPolicy Policy(string workspaceId, string agentId,
        ImmutableArray<EffectiveModelEntry> purposes, ImmutableArray<string> pendingReasons,
        long effectiveRevision = 7, long appliedRevision = 7, long policyGeneration = 3) =>
        new(workspaceId, agentId, effectiveRevision, appliedRevision, policyGeneration, ResolvedAt, purposes,
            pendingReasons);

    private sealed class FakeAgentModelPolicyApi : IAgentModelPolicyApi
    {
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public AgentRef? LastAgent { get; private set; }
        public EffectiveAgentModelPolicy? Snapshot { get; init; }
        public Exception? GetFailure { get; init; }

        public Task<EffectiveAgentModelPolicy?> GetEffectiveAsync(AgentRef agent, CancellationToken ct = default)
        {
            GetCalls++;
            LastAgent = agent;
            ct.ThrowIfCancellationRequested();
            return GetFailure is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<EffectiveAgentModelPolicy?>(GetFailure);
        }

        public Task<AgentModelPolicyReceipt> UpdateAsync(UpdateAgentModelPolicy request,
            CancellationToken ct = default)
        {
            UpdateCalls++;
            // The offline read-only sample must never write: if it ever calls Update, fail loudly instead of passing.
            throw new InvalidOperationException("The read-only model policy sample must never call UpdateAsync.");
        }
    }
}
