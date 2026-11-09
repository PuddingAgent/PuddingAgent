using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.ExecutionBudgetClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace ExecutionBudgetClient.Tests;

[TestClass]
public sealed class BudgetDemoTests
{
    private static readonly string ExpectedUsage = "Usage: BudgetDemo get <accountId>";

    [TestMethod]
    public async Task Get_Success_CallsGetOnce_AndWritesSnapshotJson()
    {
        var snapshot = Snapshot();
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls, "GetAsync must be called exactly once.");
        Assert.AreEqual("account-one", api.LastAccountId);
        AssertSnapshotJson(ParseSingleLine(result.Output), snapshot);
    }

    [TestMethod]
    public async Task Get_ExhaustedBudget_IsStillSuccess()
    {
        var snapshot = Snapshot(
            limits: new BudgetLimitsView(1_000, 5m, 40, 1, 2_000),
            actual: new BudgetActualView(2_000, 1_000, 250, 5m, "usd"),
            reserved: new BudgetReservedView(0, 0m));
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode, "An exhausted budget is still a successful query.");
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        var json = ParseSingleLine(result.Output);
        AssertSnapshotJson(json, snapshot);
        Assert.AreEqual(5m, json.GetProperty("actual").GetProperty("cost").GetDecimal());
        Assert.AreEqual(5m, json.GetProperty("limits").GetProperty("maxCost").GetDecimal());
    }

    [TestMethod]
    public async Task Get_CancelledBudget_KeepsCancellationFields()
    {
        var snapshot = Snapshot(cancelled: true, cancellationRevision: 4, reasonCode: "budget.cancelled");
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode, "A cancelled budget is still a readable snapshot.");
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        var json = ParseSingleLine(result.Output);
        AssertSnapshotJson(json, snapshot);
        Assert.IsTrue(json.GetProperty("cancelled").GetBoolean());
        Assert.AreEqual(4L, json.GetProperty("cancellationRevision").GetInt64());
        Assert.AreEqual("budget.cancelled", json.GetProperty("reasonCode").GetString());
    }

    [TestMethod]
    public async Task Get_NullLimits_ArePreservedAsJsonNull_NeverZero()
    {
        var snapshot = Snapshot(limits: new BudgetLimitsView(null, null, null, 2, null));
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode);
        var json = ParseSingleLine(result.Output);
        AssertSnapshotJson(json, snapshot);
        var limits = json.GetProperty("limits");
        Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("maxOutputTokens").ValueKind,
            "A null limit means unlimited and must not be rewritten as zero.");
        Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("maxCost").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("maxCalls").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, limits.GetProperty("maxInputTokensPerRequest").ValueKind);
        Assert.AreEqual(2, limits.GetProperty("maxConcurrentAttempts").GetInt32());
        Assert.AreEqual(1, api.GetCalls);
    }

    [TestMethod]
    public async Task Get_UnknownAmounts_ArePreservedAndNotZeroed()
    {
        var snapshot = Snapshot(unknown: new BudgetUnknownView(37, 1.25m));
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        var json = ParseSingleLine(result.Output);
        AssertSnapshotJson(json, snapshot);
        var unknown = json.GetProperty("unknown");
        Assert.AreEqual(37L, unknown.GetProperty("outputTokens").GetInt64(),
            "Unknown tokens must be preserved, never replaced by zero.");
        Assert.AreEqual(1.25m, unknown.GetProperty("cost").GetDecimal(),
            "Unknown cost must be preserved, never replaced by zero.");
        Assert.AreNotEqual(0L, unknown.GetProperty("outputTokens").GetInt64());
        Assert.AreNotEqual(0m, unknown.GetProperty("cost").GetDecimal());
    }

    [TestMethod]
    public async Task Get_PassesAccountIdVerbatim_IncludingChineseAndSurroundingSpaces()
    {
        const string accountId = "  账户-甲:预算/一  ";
        var snapshot = Snapshot(accountId: accountId);
        var api = new FakeExecutionBudgetApi { Snapshot = snapshot };

        var result = await Run(api, ["get", accountId]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(accountId, api.LastAccountId, "The account id must not be trimmed or repaired.");
        var json = ParseSingleLine(result.Output);
        Assert.AreEqual(accountId, json.GetProperty("accountId").GetString());
    }

    [TestMethod]
    public async Task NoArguments_ReturnUsageWithoutCallingApi() => await AssertUsageError([]);

    [TestMethod]
    public async Task UnknownCommand_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["remove", "account-one"],
            ["GET", "account-one"],
            ["Get", "account-one"],
            ["getAll"],
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
            ["get", "account-one", "extra"],
            ["get", "account-one", "extra", "more"],
            ["get", "account-one", "extra", "more", "even-more"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task BlankAccountId_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["get", " "],
            ["get", ""],
            ["get", "\t"],
            ["get", "\r\n"],
            ["get", "  "]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task GetNotFound_WritesFoundFalse_AndReturnsThree()
    {
        var api = new FakeExecutionBudgetApi { Snapshot = null };

        var result = await Run(api, ["get", "account-missing"]);

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("{\"found\":false}" + Environment.NewLine, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.GetCalls);
    }

    [TestMethod]
    public async Task ExecutionBudgetException_WritesOnlyCode_AndReturnsOne()
    {
        const string message = "internal ledger detail for account-one that must never leak";
        var api = new FakeExecutionBudgetApi
        {
            GetFailure = new ExecutionBudgetException("budget.account_not_found", message)
        };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual("code=budget.account_not_found" + Environment.NewLine, result.Error);
        Assert.IsFalse(result.Error.Contains("internal ledger detail", StringComparison.Ordinal),
            "Server messages must never reach stderr.");
        Assert.AreEqual(1, api.GetCalls);
    }

    [TestMethod]
    public async Task CallerCancellation_Returns130_WithoutExtraCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeExecutionBudgetApi { Snapshot = Snapshot() };

        var result = await Run(api, ["get", "account-one"], cancellation.Token);

        Assert.AreEqual(130, result.ExitCode);
        Assert.AreEqual(1, api.GetCalls, "Cancellation must not trigger a retry or a second read.");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(string.Empty, result.Error);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeExecutionBudgetApi
        {
            GetFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            BudgetDemo.RunAsync(["get", "account-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task UnexpectedException_IsNotCaughtOrConvertedToSuccess()
    {
        var api = new FakeExecutionBudgetApi { GetFailure = new InvalidOperationException("boom") };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BudgetDemo.RunAsync(["get", "account-one"], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.GetCalls);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task Get_Success_WritesExactlyOneCamelCaseJsonLine()
    {
        var api = new FakeExecutionBudgetApi { Snapshot = Snapshot() };

        var result = await Run(api, ["get", "account-one"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, result.Output.Count(character => character == '\n'), "stdout must be exactly one line.");
        var body = result.Output[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must not contain extra line breaks.");
        using var document = JsonDocument.Parse(body);
        CollectionAssert.AreEqual(
            new[]
            {
                "accountId", "revision", "limits", "actual", "reserved", "unknown", "activeAttempts", "cancelled",
                "cancellationRevision", "reasonCode"
            },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
            "Every execution budget snapshot field must be present as a camelCase JSON property.");
    }

    [TestMethod]
    public async Task NullReferenceArguments_ThrowArgumentNullException()
    {
        var api = new FakeExecutionBudgetApi { Snapshot = Snapshot() };
        string[] args = ["get", "account-one"];

        await AssertNullArgument("args",
            () => BudgetDemo.RunAsync(null!, api, new StringWriter(), new StringWriter()));
        await AssertNullArgument("api",
            () => BudgetDemo.RunAsync(args, null!, new StringWriter(), new StringWriter()));
        await AssertNullArgument("output",
            () => BudgetDemo.RunAsync(args, api, null!, new StringWriter()));
        await AssertNullArgument("error",
            () => BudgetDemo.RunAsync(args, api, new StringWriter(), null!));
    }

    private static async Task AssertNullArgument(string parameterName, Func<Task> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        Assert.AreEqual(parameterName, exception.ParamName);
    }

    private static async Task AssertUsageError(string[] args)
    {
        var api = new FakeExecutionBudgetApi { Snapshot = Snapshot() };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode, $"Expected usage exit for: [{string.Join(' ', args)}]");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.GetCalls, "Argument errors must not call GetAsync.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(IExecutionBudgetApi api, string[] args,
        CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await BudgetDemo.RunAsync(args, api, output, error, ct);
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

    private static void AssertSnapshotJson(JsonElement json, ExecutionBudgetSnapshot snapshot)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "accountId", "revision", "limits", "actual", "reserved", "unknown", "activeAttempts", "cancelled",
                "cancellationRevision", "reasonCode"
            },
            Names(json));
        Assert.AreEqual(snapshot.AccountId, json.GetProperty("accountId").GetString());
        Assert.AreEqual(snapshot.Revision, json.GetProperty("revision").GetInt64());
        AssertLimitsJson(json.GetProperty("limits"), snapshot.Limits);
        AssertActualJson(json.GetProperty("actual"), snapshot.Actual);
        AssertReservedJson(json.GetProperty("reserved"), snapshot.Reserved);
        AssertUnknownJson(json.GetProperty("unknown"), snapshot.Unknown);
        Assert.AreEqual(snapshot.ActiveAttempts, json.GetProperty("activeAttempts").GetInt32());
        Assert.AreEqual(snapshot.Cancelled, json.GetProperty("cancelled").GetBoolean());
        Assert.AreEqual(snapshot.CancellationRevision, json.GetProperty("cancellationRevision").GetInt64());
        Assert.AreEqual(snapshot.ReasonCode, json.GetProperty("reasonCode").GetString());
    }

    private static void AssertLimitsJson(JsonElement json, BudgetLimitsView limits)
    {
        CollectionAssert.AreEqual(
            new[] { "maxOutputTokens", "maxCost", "maxCalls", "maxConcurrentAttempts", "maxInputTokensPerRequest" },
            Names(json));
        AssertNullableLong(json.GetProperty("maxOutputTokens"), limits.MaxOutputTokens);
        AssertNullableDecimal(json.GetProperty("maxCost"), limits.MaxCost);
        AssertNullableInt(json.GetProperty("maxCalls"), limits.MaxCalls);
        Assert.AreEqual(limits.MaxConcurrentAttempts, json.GetProperty("maxConcurrentAttempts").GetInt32());
        AssertNullableLong(json.GetProperty("maxInputTokensPerRequest"), limits.MaxInputTokensPerRequest);
    }

    private static void AssertActualJson(JsonElement json, BudgetActualView actual)
    {
        CollectionAssert.AreEqual(
            new[] { "inputTokens", "outputTokens", "cacheHitTokens", "cost", "costUnit" },
            Names(json));
        Assert.AreEqual(actual.InputTokens, json.GetProperty("inputTokens").GetInt64());
        Assert.AreEqual(actual.OutputTokens, json.GetProperty("outputTokens").GetInt64());
        Assert.AreEqual(actual.CacheHitTokens, json.GetProperty("cacheHitTokens").GetInt64());
        Assert.AreEqual(actual.Cost, json.GetProperty("cost").GetDecimal());
        Assert.AreEqual(actual.CostUnit, json.GetProperty("costUnit").GetString());
    }

    private static void AssertReservedJson(JsonElement json, BudgetReservedView reserved)
    {
        CollectionAssert.AreEqual(new[] { "outputTokens", "cost" }, Names(json));
        Assert.AreEqual(reserved.OutputTokens, json.GetProperty("outputTokens").GetInt64());
        Assert.AreEqual(reserved.Cost, json.GetProperty("cost").GetDecimal());
    }

    private static void AssertUnknownJson(JsonElement json, BudgetUnknownView unknown)
    {
        CollectionAssert.AreEqual(new[] { "outputTokens", "cost" }, Names(json));
        Assert.AreEqual(unknown.OutputTokens, json.GetProperty("outputTokens").GetInt64());
        Assert.AreEqual(unknown.Cost, json.GetProperty("cost").GetDecimal());
    }

    private static void AssertNullableLong(JsonElement json, long? expected)
    {
        if (expected is null)
        {
            Assert.AreEqual(JsonValueKind.Null, json.ValueKind);
            return;
        }
        Assert.AreEqual(expected.Value, json.GetInt64());
    }

    private static void AssertNullableInt(JsonElement json, int? expected)
    {
        if (expected is null)
        {
            Assert.AreEqual(JsonValueKind.Null, json.ValueKind);
            return;
        }
        Assert.AreEqual(expected.Value, json.GetInt32());
    }

    private static void AssertNullableDecimal(JsonElement json, decimal? expected)
    {
        if (expected is null)
        {
            Assert.AreEqual(JsonValueKind.Null, json.ValueKind);
            return;
        }
        Assert.AreEqual(expected.Value, json.GetDecimal());
    }

    private static ExecutionBudgetSnapshot Snapshot(
        string accountId = "account-one",
        long revision = 12,
        BudgetLimitsView? limits = null,
        BudgetActualView? actual = null,
        BudgetReservedView? reserved = null,
        BudgetUnknownView? unknown = null,
        int activeAttempts = 2,
        bool cancelled = false,
        long cancellationRevision = 0,
        string? reasonCode = null) =>
        new(
            accountId,
            revision,
            limits ?? new BudgetLimitsView(100_000, 25.5m, 40, 4, 50_000),
            actual ?? new BudgetActualView(1_200, 800, 300, 1.75m, "usd"),
            reserved ?? new BudgetReservedView(500, 0.5m),
            unknown ?? new BudgetUnknownView(0, 0m),
            activeAttempts,
            cancelled,
            cancellationRevision,
            reasonCode);

    private sealed class FakeExecutionBudgetApi : IExecutionBudgetApi
    {
        public int GetCalls { get; private set; }
        public string? LastAccountId { get; private set; }
        public ExecutionBudgetSnapshot? Snapshot { get; init; }
        public Exception? GetFailure { get; init; }

        public Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
        {
            GetCalls++;
            LastAccountId = accountId;
            ct.ThrowIfCancellationRequested();
            return GetFailure is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<ExecutionBudgetSnapshot?>(GetFailure);
        }
    }
}
