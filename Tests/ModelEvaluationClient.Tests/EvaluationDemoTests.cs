using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.ModelEvaluationClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace ModelEvaluationClient.Tests;

[TestClass]
public sealed class EvaluationDemoTests
{
    private static readonly string ExpectedUsage = string.Join(Environment.NewLine,
        "Usage: EvaluationDemo classify <requestJson>",
        "       EvaluationDemo review <requestJson>");

    private static readonly OperatorScopeRef Scope = new("workspace-one", "agent-one", "session-one");

    private const string ClassifyJson =
        """{"input":{"text":"  请检查当前工作区，并报告发现。  ","categories":[{"id":"cat-1","description":"问题分类"}]},"options":{"callId":"job-42.v1","maxOutputTokens":2048}}""";

    private const string ReviewJson =
        """{"input":{"task":"  请验收交付物：文件已生成。  ","criteria":[{"id":"criterion-1","text":" 必须包含概述。 "}],"evidence":[{"id":"evidence-1","text":"  输出文件：report.md  "}]},"options":{"callId":"review-job-7.v2","timeoutMilliseconds":20000}}""";

    [TestMethod]
    public async Task Classify_Success_CallsClassifyOnce_AndWritesReceiptJson()
    {
        var api = new FakeModelEvaluationApi
        {
            Classification = ClassificationReceiptFor(confidence: 0.87, reason: "模型给出单一分类。")
        };

        var result = await Run(api, ["classify", ClassifyJson]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ClassifyCalls, "ClassifyAsync must be called exactly once.");
        Assert.AreEqual(0, api.ReviewCalls, "classify must not call ReviewAsync.");
        Assert.AreEqual(0, api.ListCalls, "The sample has no List command.");
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEquivalent(
            new[] { "availability", "label", "confidence", "reason", "reasonCode", "provenance" }, Names(json));
        Assert.AreEqual("Valid", json.GetProperty("availability").GetString());
        Assert.AreEqual("问题分类", json.GetProperty("label").GetString());
        Assert.AreEqual(0.87, json.GetProperty("confidence").GetDouble());
        Assert.AreEqual("模型给出单一分类。", json.GetProperty("reason").GetString());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("reasonCode").ValueKind);
        Assert.AreEqual("call-classify", json.GetProperty("provenance").GetProperty("callId").GetString());
    }

    [TestMethod]
    public async Task Review_Success_CallsReviewOnce_AndWritesReceiptJson()
    {
        var api = new FakeModelEvaluationApi { Acceptance = AcceptanceReceiptFor() };

        var result = await Run(api, ["review", ReviewJson]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Error);
        Assert.AreEqual(1, api.ReviewCalls, "ReviewAsync must be called exactly once.");
        Assert.AreEqual(0, api.ClassifyCalls, "review must not call ClassifyAsync.");
        Assert.AreEqual(0, api.ListCalls, "The sample has no List command.");
        var json = ParseSingleLine(result.Output);
        CollectionAssert.AreEquivalent(
            new[] { "availability", "decision", "checks", "confidence", "reason", "reasonCode", "provenance" },
            Names(json));
        Assert.AreEqual("Valid", json.GetProperty("availability").GetString());
        Assert.AreEqual("Accept", json.GetProperty("decision").GetString());
        Assert.AreEqual(0.9, json.GetProperty("confidence").GetDouble());
        var check = json.GetProperty("checks")[0];
        Assert.AreEqual("criterion-1", check.GetProperty("criterionId").GetString());
        Assert.AreEqual("Supported", check.GetProperty("outcome").GetString());
        CollectionAssert.AreEqual(new[] { "evidence-1" },
            check.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.AreEqual("call-review", json.GetProperty("provenance").GetProperty("callId").GetString());
    }

    [TestMethod]
    public async Task Classify_PassesChineseTextAndOptionsVerbatim()
    {
        var api = new FakeModelEvaluationApi
        {
            Classification = ClassificationReceiptFor(EvaluationAvailability.Abstain, null, null, "证据不足。", "abstain.no_label")
        };

        var result = await Run(api, ["classify", ClassifyJson]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, api.ClassifyCalls);
        Assert.AreEqual("  请检查当前工作区，并报告发现。  ", api.LastClassificationInput!.Text,
            "The supplied text must not be trimmed or rewritten.");
        Assert.HasCount(1, api.LastClassificationInput.Categories);
        Assert.AreEqual("cat-1", api.LastClassificationInput.Categories[0].Id);
        Assert.AreEqual("问题分类", api.LastClassificationInput.Categories[0].Description);
        Assert.AreEqual("job-42.v1", api.LastClassificationOptions!.CallId);
        Assert.AreEqual(2048, api.LastClassificationOptions.MaxOutputTokens);
        Assert.AreEqual(15000, api.LastClassificationOptions.TimeoutMilliseconds,
            "Unspecified option members keep the contract default.");
    }

    [TestMethod]
    public async Task Review_PassesChineseTextAndOptionsVerbatim()
    {
        var api = new FakeModelEvaluationApi { Acceptance = AcceptanceReceiptFor() };

        var result = await Run(api, ["review", ReviewJson]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, api.ReviewCalls);
        Assert.AreEqual("  请验收交付物：文件已生成。  ", api.LastAcceptanceInput!.Task,
            "The supplied task must not be trimmed or rewritten.");
        Assert.AreEqual(" 必须包含概述。 ", api.LastAcceptanceInput.Criteria[0].Text);
        Assert.AreEqual("  输出文件：report.md  ", api.LastAcceptanceInput.Evidence[0].Text);
        Assert.AreEqual("review-job-7.v2", api.LastAcceptanceOptions!.CallId);
        Assert.AreEqual(1024, api.LastAcceptanceOptions.MaxOutputTokens,
            "Unspecified option members keep the contract default.");
        Assert.AreEqual(20000, api.LastAcceptanceOptions.TimeoutMilliseconds);
    }

    [TestMethod]
    public async Task NoArguments_ReturnUsageWithoutCallingApi() => await AssertUsageError([]);

    [TestMethod]
    public async Task UnknownCommand_ReturnsUsageWithoutCallingApi()
    {
        await AssertUsageError(["remove", ClassifyJson]);
        await AssertUsageError([" classify", ClassifyJson], "The command name must not be trimmed.");
        await AssertUsageError(["classify ", ClassifyJson], "The command name must not be trimmed.");
        await AssertUsageError(["CLASSIFY", ClassifyJson]);
    }

    [TestMethod]
    public async Task WrongArgumentCount_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["classify"],
            ["classify", ClassifyJson, "extra"],
            ["review"],
            ["review", ReviewJson, "extra"],
            [ClassifyJson],
            [ReviewJson, ClassifyJson]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task BlankArguments_ReturnUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["classify", ""],
            ["classify", "   "],
            ["classify", "\t"],
            ["review", ""],
            ["", ClassifyJson],
            ["  ", ClassifyJson]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task InvalidJson_ReturnsUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["classify", "not json"],
            ["classify", "{"],
            ["classify", "[1,2]"],
            ["classify", "\"text\""],
            ["review", "not json"],
            ["review", "{\"input\": }"]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task NullRequestOrMissingInputAndOptions_ReturnUsageWithoutCallingApi()
    {
        string[][] cases =
        [
            ["classify", "null"],
            ["classify", "{}"],
            ["classify", """{"options":{"callId":"call-1"}}"""],
            ["classify", """{"input":{"text":"hello","categories":[]}}"""],
            ["classify", """{"input":null,"options":{"callId":"call-1"}}"""],
            ["classify", """{"input":{"text":"hello","categories":[]},"options":null}"""],
            ["review", "null"],
            ["review", "{}"],
            ["review", """{"options":{"callId":"call-1"}}"""],
            ["review", """{"input":{"task":"t","criteria":[],"evidence":[]}}"""],
            ["review", """{"input":null,"options":{"callId":"call-1"}}"""],
            ["review", """{"input":{"task":"t","criteria":[],"evidence":[]},"options":null}"""]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task UnknownJsonMembers_AreRejectedByTheFrozenContract_WithoutCallingApi()
    {
        string[][] cases =
        [
            ["classify",
                """{"input":{"text":"hello","categories":[],"unknown":true},"options":{"callId":"call-1"}}"""],
            ["classify",
                """{"input":{"text":"hello","categories":[]},"options":{"callId":"call-1"},"unknown":true}"""],
            ["classify",
                """{"input":{"text":"hello","categories":[{"id":"a","description":"b","unknown":true}]},"options":{"callId":"call-1"}}"""],
            ["review",
                """{"input":{"task":"t","criteria":[],"evidence":[]},"options":{"callId":"call-1","unknown":true}}"""],
            ["review",
                """{"input":{"task":"t","criteria":[{"id":"c1","text":"x","unknown":1}],"evidence":[]},"options":{"callId":"call-1"}}"""]
        ];
        foreach (var args in cases)
        {
            await AssertUsageError(args);
        }
    }

    [TestMethod]
    public async Task AbstainedDegradedAndRejectedReceipts_ArePreserved_AndExitZero()
    {
        var abstainedApi = new FakeModelEvaluationApi
        {
            Classification = new ClassificationReceipt(EvaluationAvailability.Abstain, null, null, "无法可靠分类。",
                "abstain.no_label", Provenance("call-abstain"))
        };

        var abstained = await Run(abstainedApi, ["classify", ClassifyJson]);

        Assert.AreEqual(0, abstained.ExitCode, "Abstention is a normal receipt, not a transport failure.");
        Assert.AreEqual(string.Empty, abstained.Error);
        var abstainedJson = ParseSingleLine(abstained.Output);
        Assert.AreEqual("Abstain", abstainedJson.GetProperty("availability").GetString());
        Assert.AreEqual(JsonValueKind.Null, abstainedJson.GetProperty("label").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, abstainedJson.GetProperty("confidence").ValueKind);
        Assert.AreEqual("abstain.no_label", abstainedJson.GetProperty("reasonCode").GetString());

        var rejectedApi = new FakeModelEvaluationApi
        {
            Acceptance = new AcceptanceReceipt(EvaluationAvailability.Degraded, AcceptanceDecision.Reject,
                [new AcceptanceCheck("criterion-1", CriterionOutcome.NotSupported, ["evidence-1", "evidence-2"])],
                0.42, "证据不足以支持验收。", "review.evidence_insufficient", Provenance("call-reject"))
        };

        var rejected = await Run(rejectedApi, ["review", ReviewJson]);

        Assert.AreEqual(0, rejected.ExitCode, "A rejecting review is still a delivered receipt.");
        Assert.AreEqual(string.Empty, rejected.Error);
        var rejectedJson = ParseSingleLine(rejected.Output);
        Assert.AreEqual("Degraded", rejectedJson.GetProperty("availability").GetString());
        Assert.AreEqual("Reject", rejectedJson.GetProperty("decision").GetString());
        Assert.AreEqual("review.evidence_insufficient", rejectedJson.GetProperty("reasonCode").GetString());
        Assert.AreEqual(0.42, rejectedJson.GetProperty("confidence").GetDouble());
        var check = rejectedJson.GetProperty("checks")[0];
        Assert.AreEqual("criterion-1", check.GetProperty("criterionId").GetString());
        Assert.AreEqual("NotSupported", check.GetProperty("outcome").GetString());
        CollectionAssert.AreEqual(new[] { "evidence-1", "evidence-2" },
            check.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.IsFalse(rejected.Output.Contains("verified", StringComparison.OrdinalIgnoreCase),
            "The sample must not invent a verified field.");
    }

    [TestMethod]
    public async Task ProvenanceAndAvailabilityFields_AreSerializedCamelCase_WithoutProjection()
    {
        var api = new FakeModelEvaluationApi { Classification = ClassificationReceiptFor() };

        var result = await Run(api, ["classify", ClassifyJson]);

        Assert.AreEqual(0, result.ExitCode);
        var json = ParseSingleLine(result.Output);
        var provenance = json.GetProperty("provenance");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "callId", "leaseId", "scope", "parentRunId", "sceneKey", "instructionVersion", "modelId",
                "modelRevision", "inputFingerprint", "responseFingerprint", "usage", "inputOrigin",
                "confidenceKind"
            },
            Names(provenance));
        Assert.AreEqual("call-classify", provenance.GetProperty("callId").GetString());
        Assert.AreEqual("lease-1", provenance.GetProperty("leaseId").GetString());
        Assert.AreEqual("workspace-one", provenance.GetProperty("scope").GetProperty("workspaceId").GetString());
        Assert.AreEqual("model.evaluation.classify", provenance.GetProperty("sceneKey").GetString());
        Assert.AreEqual(3, provenance.GetProperty("instructionVersion").GetInt32());
        Assert.AreEqual("model-id", provenance.GetProperty("modelId").GetString());
        Assert.AreEqual(11L, provenance.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.AreEqual(22L, provenance.GetProperty("usage").GetProperty("outputTokens").GetInt64());
        Assert.AreEqual(0.0035m, provenance.GetProperty("usage").GetProperty("cost").GetDecimal());
        Assert.AreEqual("caller-supplied", provenance.GetProperty("inputOrigin").GetString());
        Assert.AreEqual("ModelSelfReported", provenance.GetProperty("confidenceKind").GetString());
    }

    [TestMethod]
    public async Task ModelEvaluationException_WritesOnlyCode_AndReturnsOne_WithoutRetry()
    {
        var classifyApi = new FakeModelEvaluationApi
        {
            ClassificationFailure = new ModelEvaluationException("model.unavailable", "provider internal detail 12345")
        };

        var classified = await Run(classifyApi, ["classify", ClassifyJson]);

        Assert.AreEqual(1, classified.ExitCode);
        Assert.AreEqual(1, classifyApi.ClassifyCalls, "A definite failure must not be retried.");
        Assert.AreEqual(0, classifyApi.ReviewCalls);
        Assert.AreEqual(string.Empty, classified.Output);
        Assert.AreEqual("code=model.unavailable" + Environment.NewLine, classified.Error);
        Assert.IsFalse(classified.Error.Contains("provider internal detail", StringComparison.Ordinal),
            "The exception message must never reach stderr.");

        var reviewApi = new FakeModelEvaluationApi
        {
            ReviewFailure = new ModelEvaluationException("review.denied", "host message that echoes request content")
        };

        var reviewed = await Run(reviewApi, ["review", ReviewJson]);

        Assert.AreEqual(1, reviewed.ExitCode);
        Assert.AreEqual(1, reviewApi.ReviewCalls, "A definite failure must not be retried.");
        Assert.AreEqual(0, reviewApi.ClassifyCalls);
        Assert.AreEqual(string.Empty, reviewed.Output);
        Assert.AreEqual("code=review.denied" + Environment.NewLine, reviewed.Error);
        Assert.IsFalse(reviewed.Error.Contains("request content", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CanceledToken_Returns130_WithoutAdditionalCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var classifyApi = new FakeModelEvaluationApi { Classification = ClassificationReceiptFor() };
        var classified = await Run(classifyApi, ["classify", ClassifyJson], cancellation.Token);
        Assert.AreEqual(130, classified.ExitCode);
        Assert.AreEqual(1, classifyApi.ClassifyCalls, "Cancellation must not trigger a retry.");
        Assert.AreEqual(0, classifyApi.ReviewCalls, "Cancellation must not fall back to another command.");
        Assert.AreEqual(0, classifyApi.ListCalls);
        Assert.AreEqual(string.Empty, classified.Output);
        Assert.AreEqual(string.Empty, classified.Error);

        var reviewApi = new FakeModelEvaluationApi { Acceptance = AcceptanceReceiptFor() };
        var reviewed = await Run(reviewApi, ["review", ReviewJson], cancellation.Token);
        Assert.AreEqual(130, reviewed.ExitCode);
        Assert.AreEqual(1, reviewApi.ReviewCalls);
        Assert.AreEqual(0, reviewApi.ClassifyCalls);
        Assert.AreEqual(0, reviewApi.ListCalls);
        Assert.AreEqual(string.Empty, reviewed.Output);
        Assert.AreEqual(string.Empty, reviewed.Error);
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotSwallowed()
    {
        var api = new FakeModelEvaluationApi
        {
            ClassificationFailure = new OperationCanceledException("timeout-like cancellation the caller did not request")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            EvaluationDemo.RunAsync(["classify", ClassifyJson], api, output, error, CancellationToken.None));
        Assert.AreEqual(1, api.ClassifyCalls);
        Assert.AreEqual(0, api.ReviewCalls);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(string.Empty, error.ToString());
    }

    [TestMethod]
    public async Task Stdout_IsExactlyOneJsonLine_ForBothCommands()
    {
        var api = new FakeModelEvaluationApi
        {
            Classification = ClassificationReceiptFor(),
            Acceptance = AcceptanceReceiptFor()
        };

        foreach (var args in new[] { new[] { "classify", ClassifyJson }, new[] { "review", ReviewJson } })
        {
            var result = await Run(api, args);
            Assert.AreEqual(0, result.ExitCode);
            Assert.IsTrue(result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal),
                "stdout must end with exactly one line break.");
            var body = result.Output[..^Environment.NewLine.Length];
            Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "stdout must be exactly one line.");
            using var document = JsonDocument.Parse(body);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        Assert.AreEqual(1, api.ClassifyCalls);
        Assert.AreEqual(1, api.ReviewCalls);
    }

    [TestMethod]
    public async Task NullReferenceArguments_ThrowArgumentNullException()
    {
        var api = new FakeModelEvaluationApi();
        using var output = new StringWriter();
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            EvaluationDemo.RunAsync(null!, api, output, error));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            EvaluationDemo.RunAsync([], null!, output, error));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            EvaluationDemo.RunAsync([], api, null!, error));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            EvaluationDemo.RunAsync([], api, output, null!));
        Assert.AreEqual(0, api.ListCalls);
        Assert.AreEqual(0, api.ClassifyCalls);
        Assert.AreEqual(0, api.ReviewCalls);
    }

    private static async Task AssertUsageError(string[] args, string? because = null)
    {
        var api = new FakeModelEvaluationApi
        {
            Classification = ClassificationReceiptFor(),
            Acceptance = AcceptanceReceiptFor()
        };
        var result = await Run(api, args);
        Assert.AreEqual(2, result.ExitCode,
            because ?? $"Expected usage exit for: {string.Join(' ', args)}");
        Assert.AreEqual(string.Empty, result.Output);
        Assert.AreEqual(ExpectedUsage + Environment.NewLine, result.Error);
        Assert.AreEqual(0, api.ClassifyCalls, "Argument or JSON errors must not call ClassifyAsync.");
        Assert.AreEqual(0, api.ReviewCalls, "Argument or JSON errors must not call ReviewAsync.");
        Assert.AreEqual(0, api.ListCalls);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(IModelEvaluationApi api,
        string[] args, CancellationToken ct = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await EvaluationDemo.RunAsync(args, api, output, error, ct);
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

    private static ModelEvaluationProvenance Provenance(string callId) => new(
        callId, "lease-1", Scope, "parent-run-1", "model.evaluation.classify", 3,
        "model-id", "model-revision-1", "input-fingerprint", "response-fingerprint",
        new EvaluationModelUsage(11, 22, 3, 0.0035m, "usd"));

    private static ClassificationReceipt ClassificationReceiptFor(
        EvaluationAvailability availability = EvaluationAvailability.Valid, string? label = "问题分类",
        double? confidence = 0.87, string reason = "模型给出分类。", string? reasonCode = null) =>
        new(availability, label, confidence, reason, reasonCode, Provenance("call-classify"));

    private static AcceptanceReceipt AcceptanceReceiptFor(
        EvaluationAvailability availability = EvaluationAvailability.Valid,
        AcceptanceDecision decision = AcceptanceDecision.Accept) =>
        new(availability, decision, [new AcceptanceCheck("criterion-1", CriterionOutcome.Supported, ["evidence-1"])],
            0.9, "模型给出验收建议。", null, Provenance("call-review"));

    private sealed class FakeModelEvaluationApi : IModelEvaluationApi
    {
        public int ListCalls { get; private set; }
        public int ClassifyCalls { get; private set; }
        public int ReviewCalls { get; private set; }
        public ClassificationInput? LastClassificationInput { get; private set; }
        public ModelEvaluationOptions? LastClassificationOptions { get; private set; }
        public AcceptanceInput? LastAcceptanceInput { get; private set; }
        public ModelEvaluationOptions? LastAcceptanceOptions { get; private set; }
        public ClassificationReceipt? Classification { get; init; }
        public AcceptanceReceipt? Acceptance { get; init; }
        public Exception? ClassificationFailure { get; init; }
        public Exception? ReviewFailure { get; init; }

        public Task<OperatorCatalog> ListAsync(CancellationToken ct = default)
        {
            ListCalls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new OperatorCatalog([]));
        }

        public Task<ClassificationReceipt> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
        {
            ClassifyCalls++;
            LastClassificationInput = request.Input;
            LastClassificationOptions = request.Options;
            ct.ThrowIfCancellationRequested();
            return ClassificationFailure is null
                ? Task.FromResult(Classification ??
                    throw new InvalidOperationException("No classification receipt configured for this fake."))
                : Task.FromException<ClassificationReceipt>(ClassificationFailure);
        }

        public Task<AcceptanceReceipt> ReviewAsync(AcceptanceRequest request, CancellationToken ct = default)
        {
            ReviewCalls++;
            LastAcceptanceInput = request.Input;
            LastAcceptanceOptions = request.Options;
            ct.ThrowIfCancellationRequested();
            return ReviewFailure is null
                ? Task.FromResult(Acceptance ??
                    throw new InvalidOperationException("No acceptance receipt configured for this fake."))
                : Task.FromException<AcceptanceReceipt>(ReviewFailure);
        }
    }
}
