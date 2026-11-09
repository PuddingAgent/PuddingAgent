using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingOperatorAccess;
using PuddingOperatorAccess.Contracts;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace PuddingOperatorAccessTests;

[TestClass]
public sealed class OperatorAccessTests
{
    private static OperatorGrant Grant(int calls = 16) => new("module:test", new("workspace", "agent", "session"), DateTimeOffset.UtcNow.AddMinutes(1), true, ["skill.value"], calls);
    private static OperatorScoreRequest Request(string scene = "skill.value") => new(scene, "schema", 1, JsonSerializer.SerializeToElement(new { x = 1 }), new("call"));
    [TestMethod]
    public async Task Catalog_FiltersUnauthorizedScenes_AndDoesNotExecute()
    {
        var a = new Backend(); var b = new Backend("hidden");
        await using var lease = new OperatorAccessService([a, b]).Open(Grant());
        var catalog = await lease.ListAsync(); Assert.HasCount(1, catalog.Items); Assert.AreEqual("skill.value", catalog.Items[0].SceneKey);
        Assert.AreEqual(0, a.Calls + b.Calls);
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request("hidden")))).Code);
        Assert.AreEqual(0, b.Calls);
    }
    [TestMethod]
    public void Composition_RejectsDuplicateAndModelBackends()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new OperatorAccessService([new Backend(), new Backend()]));
        Assert.ThrowsExactly<ArgumentException>(() => new OperatorAccessService([new Backend { UsesModel = true }]));
    }
    [TestMethod]
    public async Task SchemaAndPayloadRejectedBeforeBackend()
    {
        var backend = new Backend(); await using var lease = new OperatorAccessService([backend]).Open(Grant());
        Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request() with { SceneKey = null! }))).Code);
        Assert.AreEqual("schema.unsupported", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request() with { InputVersion = 2 }))).Code);
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request() with { Input = JsonSerializer.SerializeToElement(new { x = new string('x', 1200) }) }));
        Assert.AreEqual(0, backend.Calls);
    }
    [TestMethod]
    public async Task BudgetCannotBeExpanded_AndDisposedLeaseCannotBeReused()
    {
        var backend = new Backend(); var lease = new OperatorAccessService([backend]).Open(Grant(1));
        await lease.ScoreAsync(Request());
        Assert.AreEqual("delegation.limit", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request()))).Code);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.AreEqual("delegation.revoked", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ListAsync())).Code);
        Assert.AreEqual(1, backend.Calls);
    }
    [TestMethod]
    public async Task ScopeSeparatesFingerprints_AndCallerOriginRemainsExplicit()
    {
        var service = new OperatorAccessService([new Backend()]);
        await using var one = service.Open(Grant()); await using var two = service.Open(Grant() with { Scope = new("other", "agent", "session") });
        var a = await one.ScoreAsync(Request()); var replay = await one.ScoreAsync(Request()); var b = await two.ScoreAsync(Request());
        Assert.AreEqual(a.EvaluationId, replay.EvaluationId); Assert.AreNotEqual(a.InputFingerprint, b.InputFingerprint);
        await using var third = service.Open(Grant() with { CallerId = "another-caller" });
        Assert.AreNotEqual(a.InputFingerprint, (await third.ScoreAsync(Request())).InputFingerprint);
        Assert.AreEqual("caller-supplied", a.InputOrigin); Assert.AreEqual("raw-digest", a.Value.Provenance.InputDigest);
    }
    [TestMethod]
    public async Task PlaceholderZeroOrForeignResult_IsRejected()
    {
        var backend = new Backend { Override = Value() with { Availability = ScoreAvailability.InsufficientData } };
        await using var lease = new OperatorAccessService([backend]).Open(Grant());
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request()));
        backend.Override = Value() with { Availability = ScoreAvailability.InsufficientData, Score = null };
        Assert.IsNull((await lease.ScoreAsync(Request())).Value.Score);
        backend.Override = Value() with { Provenance = Value().Provenance with { SceneKey = "wrong" } };
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request()));
    }
    [TestMethod]
    public async Task CallerCancellationAndDeadline_AreDifferent_AndNoLateSuccess()
    {
        var backend = new Backend { Delay = 40 }; await using var lease = new OperatorAccessService([backend]).Open(Grant());
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => lease.ScoreAsync(Request(), ct.Token)); Assert.AreEqual(0, backend.Calls);
        var error = await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request() with { Options = new("short", 5) }));
        Assert.AreEqual("operation.timeout", error.Code); Assert.AreEqual(1, backend.Calls);
    }
    [TestMethod]
    public async Task BackendFailure_IsTypedAndDoesNotLeakInternalTextOrRetry()
    {
        var backend = new Backend { Wait = () => throw new InvalidOperationException("private backend detail") };
        await using var lease = new OperatorAccessService([backend]).Open(Grant());
        var error = await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ScoreAsync(Request()));
        Assert.AreEqual("operator.failed", error.Code); Assert.DoesNotContain(error.Message, "private");
        Assert.AreEqual(1, backend.Calls);
    }
    [TestMethod]
    public async Task Revoke_DrainsAlreadyAdmittedCallback_AndDeniesNewCalls()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { Wait = async () => { entered.TrySetResult(); await release.Task; } };
        var lease = new OperatorAccessService([backend]).Open(Grant());
        var call = lease.ScoreAsync(Request()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dispose = lease.DisposeAsync().AsTask(); Assert.IsFalse(dispose.IsCompleted);
        await Assert.ThrowsExactlyAsync<OperatorApiException>(() => lease.ListAsync());
        release.TrySetResult(); Assert.AreEqual("delegation.revoked", (await Assert.ThrowsExactlyAsync<OperatorApiException>(() => call)).Code);
        await dispose;
    }
    private static OperatorScoreValue Value() => new(ScoreAvailability.Valid, .8, "0..1", new("judgement", "skill.value", "rule", null, null, 1, 1,
        "raw-digest", null, null, "Unknown", "reason", null, ImmutableArray<OperatorEvidence>.Empty, false, 0, DateTimeOffset.UtcNow));
    private sealed class Backend(string scene = "skill.value") : IOperatorBackend
    {
        public bool UsesModel; public int Calls, Delay; public OperatorScoreValue? Override; public Func<Task>? Wait;
        public OperatorDescriptor Descriptor => new(scene, "score", "schema", 1, "v1", UsesModel, 1024);
        public async Task<OperatorScoreValue> ScoreAsync(JsonElement input, CancellationToken ct)
        { Calls++; if (Wait is not null) await Wait(); if (Delay > 0) await Task.Delay(Delay); return Override ?? Value(); }
    }
}
