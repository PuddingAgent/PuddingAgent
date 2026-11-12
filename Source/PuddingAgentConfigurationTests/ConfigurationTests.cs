using System.Text.Json;
using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingAgentConfiguration.Contracts;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace PuddingAgentConfigurationTests;

[TestClass]
public sealed class ConfigurationTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");
    private static AgentConfigurationRecord Seed => AgentConfigurationRecord.Create(new(Agent, "v1", "Old", "Description", "template", DateTimeOffset.UnixEpoch));

    [TestMethod]
    public async Task Patch_PreservesOmittedFields_AndExplicitNullClears()
    {
        var (api, store, _) = Setup();
        var first = await api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "one"));
        Assert.AreEqual("Description", (await api.GetAsync(Agent)).Description);
        var second = await api.UpdateAsync(Agent, new() { Description = ConfigurationField<string>.Set(null) }, new(first.PersistedRevision, "two"));
        Assert.IsNull((await api.GetAsync(Agent)).Description);
        Assert.AreEqual("New", store.State.Snapshot.DisplayName);
        Assert.AreEqual("configuration-source", second.AppliedTarget);
        Assert.AreEqual(second.PersistedRevision, second.AppliedRevision);
    }

    [TestMethod]
    public async Task ConcurrentUpdates_OnlyOneCommits_AndLoserGetsConflict()
    {
        var (api, store, _) = Setup();
        async Task<string> Try(string name)
        {
            try { await api.UpdateAsync(Agent, new() { DisplayName = name }, new("v1", name)); return "ok"; }
            catch (ConfigurationApiException ex) { return ex.Code; }
        }
        var results = await Task.WhenAll(Task.Run(() => Try("one")), Task.Run(() => Try("two")));
        CollectionAssert.AreEquivalent(new[] { "ok", "revision.conflict" }, results);
        Assert.AreEqual(1, store.Writes);
        Assert.HasCount(1, store.State.Changes);
    }

    [TestMethod]
    public async Task ExactRetry_AfterAnotherUpdate_ReturnsOriginalReceiptWithoutReapplying()
    {
        var (api, store, _) = Setup();
        var patch = new AgentConfigurationPatch { DisplayName = "One" };
        var original = await api.UpdateAsync(Agent, patch, new("v1", "one"));
        await api.UpdateAsync(Agent, new() { DisplayName = "Two" }, new(original.PersistedRevision, "two"));
        Assert.AreEqual(original, await api.UpdateAsync(Agent, patch, new("v1", "one")));
        Assert.AreEqual("Two", store.State.Snapshot.DisplayName);
        Assert.AreEqual(2, store.Writes);
        await AssertCode("operation.conflict", () => api.UpdateAsync(Agent, new() { DisplayName = "Different" }, new("v1", "one")));
    }

    [TestMethod]
    public async Task SameIdAcrossCallers_IsNotAnotherCallersReceipt()
    {
        var (api, store, grants) = Setup();
        await api.UpdateAsync(Agent, new() { DisplayName = "One" }, new("v1", "op"));
        var other = new PuddingAgentConfiguration.AgentConfigurationService(store, grants).ForCaller(new("other"));
        await AssertCode("revision.conflict", () => other.UpdateAsync(Agent, new() { DisplayName = "One" }, new("v1", "op")));
    }

    [TestMethod]
    public async Task Revocation_DeniesEvenIdempotentRetry_WithoutReadingStore()
    {
        var (api, store, grants) = Setup();
        await api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "op"));
        grants.Allowed = false;
        var reads = store.Reads;
        await AssertCode("permission.denied", () => api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "op")));
        await AssertCode("permission.denied", () => api.GetAsync(Agent));
        await AssertCode("permission.denied", () => api.ReadChangesAsync(Agent));
        Assert.AreEqual(reads, store.Reads);
        Assert.AreEqual(1, store.Writes);
    }

    [TestMethod]
    public async Task NoOp_HasReceiptButNoChangeEvent()
    {
        var (api, store, _) = Setup();
        var receipt = await api.UpdateAsync(Agent, new() { DisplayName = "Old" }, new("v1", "op"));
        Assert.IsFalse(receipt.Changed);
        Assert.IsNull(receipt.ChangeSequence);
        Assert.AreEqual("v1", receipt.PersistedRevision);
        Assert.IsTrue((await api.ReadChangesAsync(Agent)).Items.IsEmpty);
        Assert.HasCount(1, store.State.Operations);
    }

    [TestMethod]
    public async Task ChangePages_ReportPartialAndRejectImpossibleCursor()
    {
        var (api, _, _) = Setup();
        var first = await api.UpdateAsync(Agent, new() { DisplayName = "One" }, new("v1", "one"));
        await api.UpdateAsync(Agent, new() { DisplayName = "Two" }, new(first.PersistedRevision, "two"));
        var page = await api.ReadChangesAsync(Agent, 0, 1);
        Assert.IsFalse(page.Complete);
        Assert.AreEqual(1L, page.NextCursor);
        var next = await api.ReadChangesAsync(Agent, page.NextCursor, 1);
        Assert.IsTrue(next.Complete);
        Assert.AreEqual(2L, next.NextCursor);
        Assert.AreEqual("displayName", next.Items.Single().Fields.Single());
        await AssertCode("cursor.invalid", () => api.ReadChangesAsync(Agent, 3));
        await AssertCode("request.invalid", () => api.ReadChangesAsync(Agent, -1));
    }

    [TestMethod]
    public async Task CancelledBeforeCommit_DoesNotPersist()
    {
        var (api, store, _) = Setup();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "one"), cancel.Token));
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task FailedStoreCommit_DoesNotReturnSuccess_AndCanRetry()
    {
        var (api, store, _) = Setup(); store.FailCommit = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "one")));
        Assert.AreEqual("v1", store.State.Snapshot.Revision);
        store.FailCommit = false;
        Assert.IsTrue((await api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "one"))).Changed);
    }

    [TestMethod]
    public async Task UntrustedReferenceAndWrongStoreIdentity_AreRejected()
    {
        var (api, store, _) = Setup();
        await AssertCode("request.invalid", () => api.GetAsync(new("../other", "agent")));
        await AssertCode("store.invalid", () => api.GetAsync(new("workspace", "other")));
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public async Task InvalidAndEmptyPatch_DoNotWrite()
    {
        var (api, store, _) = Setup();
        await AssertCode("request.invalid", () => api.UpdateAsync(Agent, new(), new("v1", "one")));
        await AssertCode("request.invalid", () => api.UpdateAsync(Agent, new() { DisplayName = "   " }, new("v1", "one")));
        await AssertCode("request.invalid", () => api.UpdateAsync(Agent, new() { Description = new(false, "hidden") }, new("v1", "one")));
        await AssertCode("request.invalid", () => api.UpdateAsync(Agent, new() { Description = new string('x', 16_001) }, new("v1", "one")));
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public void WireContract_RejectsUnsupportedProperties_AndRetainsClearMarker()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<AgentConfigurationPatch>("{\"status\":\"Completed\"}", json));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<ConfigurationUpdateRequest>("{\"actorId\":\"admin\"}", json));
        var patch = JsonSerializer.Deserialize<AgentConfigurationPatch>("{\"description\":{\"isSpecified\":true,\"value\":null}}", json)!;
        Assert.IsFalse(patch.DisplayName.IsSpecified);
        Assert.IsTrue(patch.Description.IsSpecified); Assert.IsNull(patch.Description.Value);
    }

    [TestMethod]
    public async Task FullJournal_FailsClosed_WithoutDroppingDedupHistory()
    {
        var (api, store, _) = Setup();
        await api.UpdateAsync(Agent, new() { DisplayName = "Old" }, new("v1", "first"));
        var op = store.State.Operations.Single();
        store.State = store.State with { Operations = Enumerable.Range(0, PuddingAgentConfiguration.AgentConfigurationService.MaxRecordedOperations)
            .Select(i => op with { OperationId = $"filled-{i}" }).ToImmutableArray() };
        await AssertCode("resource.limit", () => api.UpdateAsync(Agent, new() { DisplayName = "New" }, new("v1", "new")));
        Assert.AreEqual("Old", store.State.Snapshot.DisplayName);
    }

    private static (IAgentConfigurationApi Api, Store Store, Grants Grants) Setup()
    {
        var store = new Store(Seed); var grants = new Grants();
        return (new PuddingAgentConfiguration.AgentConfigurationService(store, grants).ForCaller(new("tester")), store, grants);
    }

    private static async Task AssertCode(string code, Func<Task> action) =>
        Assert.AreEqual(code, (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(action)).Code);

    private sealed class Grants : IAgentConfigurationAuthorizer
    {
        public bool Allowed = true;
        public ValueTask<bool> IsAllowedAsync(ConfigurationCaller caller, AgentRef agent, ConfigurationAccess access, CancellationToken ct) => ValueTask.FromResult(Allowed);
    }
    private sealed class Store(AgentConfigurationRecord initial) : IAgentConfigurationStore
    {
        private readonly SemaphoreSlim _gate = new(1);
        public AgentConfigurationRecord State = initial;
        public int Writes, Reads;
        public bool FailCommit;
        public Task<AgentConfigurationRecord> ReadAsync(AgentRef agent, CancellationToken ct) { Reads++; return Task.FromResult(State); }
        public async Task<T> TransactAsync<T>(AgentRef agent, Func<AgentConfigurationRecord, ConfigurationTransaction<T>> transaction, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Reads++;
                var outcome = transaction(State);
                if (outcome.Write)
                {
                    if (FailCommit) throw new IOException("Injected before commit");
                    State = outcome.State; Writes++;
                }
                return outcome.Result;
            }
            finally { _gate.Release(); }
        }
    }
}
