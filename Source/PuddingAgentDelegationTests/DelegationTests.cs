using Pudding.Api.Contracts;
using PuddingAgentConfiguration.Contracts;
using PuddingAgentDelegation;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace PuddingAgentDelegationTests;

[TestClass]
public sealed class DelegationTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");
    private static AgentConfigurationGrant Grant(bool update = true, int limit = 16) => new("plugin:identity", Agent, DateTimeOffset.UtcNow.AddMinutes(1), true, update, true, limit);

    [TestMethod]
    public async Task ScopedReadOnlyGrant_RejectsOtherAgentsAndUpdatesBeforeStore()
    {
        var store = new Store(); await using var api = new DelegatedAgentConfiguration(store, Grant(false));
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(new("other", "agent")))).Code);
        await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.UpdateAsync(Agent, new() { DisplayName = "new" }, new("initial", "op")));
        Assert.AreEqual(0, store.Accesses);
        Assert.AreEqual("Old", (await api.GetAsync(Agent)).DisplayName);
    }
    [TestMethod]
    public async Task SharedConfigurationRules_PreserveCasDedupAndActorAudit()
    {
        var store = new Store(); var options = new ConfigurationUpdateOptions("initial", "op"); var patch = new AgentConfigurationPatch { DisplayName = "New" };
        await using var api = new DelegatedAgentConfiguration(store, Grant());
        var receipt = await api.UpdateAsync(Agent, patch, options);
        Assert.AreEqual(receipt, await api.UpdateAsync(Agent, patch, options));
        Assert.AreEqual("plugin:identity", store.Record.Operations.Single().ActorId);
        Assert.AreEqual(1, (await api.ReadChangesAsync(Agent)).Items.Length);
        Assert.AreEqual("revision.conflict", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.UpdateAsync(Agent, new() { DisplayName = "other" }, new("initial", "next")))).Code);
    }
    [TestMethod]
    public async Task QuotaCannotBeResetByRepeatedCallsOnSameHandle()
    {
        var store = new Store(); await using var api = new DelegatedAgentConfiguration(store, Grant(limit: 2));
        await Task.WhenAll(api.GetAsync(Agent), api.GetAsync(Agent));
        Assert.AreEqual("delegation.limit", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(Agent))).Code);
        Assert.AreEqual(2, store.Accesses);
    }
    [TestMethod]
    public async Task Revocation_DrainsAdmittedWrite_AndNeverClaimsRollback()
    {
        var store = new Store { BlockWrite = true }; await using var api = new DelegatedAgentConfiguration(store, Grant());
        var write = api.UpdateAsync(Agent, new() { DisplayName = "Committed" }, new("initial", "op"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var revoke = api.RevokeAsync(); Assert.IsFalse(revoke.IsCompleted);
        store.Release.TrySetResult();
        var error = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => write);
        Assert.AreEqual("unknown", error.EffectOutcome); await revoke;
        Assert.AreEqual("Committed", store.Record.Snapshot.DisplayName);
        Assert.AreEqual("delegation.revoked", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(Agent))).Code);
    }
    [TestMethod]
    public async Task ExpirationAndDisposal_DenyStoredHandles()
    {
        var clock = new Clock(); var store = new Store();
        var api = new DelegatedAgentConfiguration(store, Grant() with { Deadline = clock.GetUtcNow().AddMinutes(1) }, clock);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.AreEqual("delegation.revoked", (await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(Agent))).Code);
        await api.DisposeAsync(); await api.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.GetAsync(Agent));
        Assert.AreEqual(0, store.Accesses);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAgentConfigurationStore
    {
        public AgentConfigurationRecord Record = new(new(Agent, "initial", "Old", "Description", "template", DateTimeOffset.UtcNow), [], []);
        public int Accesses; public bool BlockWrite;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentConfigurationRecord> ReadAsync(AgentRef agent, CancellationToken ct) { Accesses++; return Task.FromResult(Record); }
        public async Task<T> TransactAsync<T>(AgentRef agent, Func<AgentConfigurationRecord, ConfigurationTransaction<T>> transaction, CancellationToken ct)
        {
            Accesses++; var next = transaction(Record);
            if (BlockWrite) { Entered.TrySetResult(); await Release.Task; }
            if (next.Write) Record = next.State;
            if (BlockWrite) ct.ThrowIfCancellationRequested(); // Deliberate commit followed by a lost response.
            return next.Result;
        }
    }
}
