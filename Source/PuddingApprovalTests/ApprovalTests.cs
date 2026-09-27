using PuddingApproval;

namespace PuddingApprovalTests;

public class ApprovalTests
{
    [Fact]
    public void ComponentReferencesOnlyFrameworkAssemblies() => Assert.All(typeof(ApprovalService).Assembly.GetReferencedAssemblies(),
        reference => Assert.StartsWith("System.", reference.Name));
    private static readonly ApprovalOperation Operation = new("tool", "{}", "{}", Path.GetTempPath());
    private static readonly ApprovalBinding Binding = new("w", "a", "s", "r", "t", "i", Operation.Fingerprint(), "policy");
    private static readonly DecisionCommand Allow = new("decision", HumanDecision.AllowOnce, "human", null);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UnixEpoch; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IApprovalStore
    {
        private readonly object _gate = new();
        public ApprovalRecord Current = new("approval", Binding, DateTimeOffset.UnixEpoch.AddMinutes(1), Operation);
        public Task<ApprovalRecord?> ReadAsync(string id, CancellationToken ct) { lock (_gate) return Task.FromResult(id == Current.Id ? Current : null); }
        public Task<bool> CompareExchangeAsync(string id, long version, ApprovalRecord next, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); lock (_gate) { if (id != Current.Id || version != Current.Version) return Task.FromResult(false); Current = next; return Task.FromResult(true); } }
    }
    [Fact]
    public async Task CorruptedPersistedSnapshotCannotBeApprovedOrConsumed()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        store.Current = store.Current with { Operation = Operation with { ArgumentsJson = "{\"changed\":true}" } };
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync("approval", 0, Binding, Allow));
        store.Current = store.Current with { State = ApprovalState.Approved };
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConsumeAsync("approval", Binding, true));
        Assert.Equal(0, store.Current.Version);
    }
    [Fact]
    public async Task ConcurrentDecisionsHaveOneWinner()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => service.DecideAsync("approval", 0, Binding,
            new($"d{i}", i % 2 == 0 ? HumanDecision.AllowOnce : HumanDecision.Deny, "human", null)))));
        Assert.Single(results, r => r.Outcome == ApprovalOutcome.Applied); Assert.Equal(1, store.Current.Version);
    }
    [Fact]
    public async Task ReplayRequiresIdenticalDecisionAndDoesNotGrantAgain()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        await service.DecideAsync("approval", 0, Binding, Allow);
        Assert.Equal(ApprovalOutcome.Replayed, (await service.DecideAsync("approval", 0, Binding, Allow)).Outcome);
        Assert.Equal(ApprovalOutcome.Conflict, (await service.DecideAsync("approval", 0, Binding, Allow with { Decision = HumanDecision.Deny })).Outcome);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => service.ConsumeAsync("approval", Binding, true))));
        Assert.Single(results, r => r.Outcome == ApprovalOutcome.Applied);
        Assert.Equal(ApprovalState.Consumed, (await service.DecideAsync("approval", 0, Binding, Allow)).Record!.State);
    }
    [Fact]
    public async Task ChangedOperationAndHardBoundaryCannotBeOverridden()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        Assert.Equal(ApprovalOutcome.BindingMismatch, (await service.DecideAsync("approval", 0, Binding with { AgentId = "other" }, Allow)).Outcome);
        await service.DecideAsync("approval", 0, Binding, Allow);
        Assert.Equal(ApprovalOutcome.BindingMismatch, (await service.ConsumeAsync("approval", Binding with { OperationFingerprint = "changed" }, true)).Outcome);
        Assert.Equal(ApprovalOutcome.BindingMismatch, (await service.ConsumeAsync("approval", Binding with { PolicyRevision = "new" }, true)).Outcome);
        Assert.Equal(ApprovalOutcome.Unavailable, (await service.ConsumeAsync("approval", Binding, false)).Outcome);
        Assert.Equal(ApprovalState.Approved, store.Current.State);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpiryIsEnforcedBeforeDecisionAndConsumption(bool approveFirst)
    {
        var clock = new Clock(); var store = new Store(); var service = new ApprovalService(store, clock);
        if (approveFirst) await service.DecideAsync("approval", 0, Binding, Allow);
        clock.Now = clock.Now.AddMinutes(1);
        var result = approveFirst ? await service.ConsumeAsync("approval", Binding, true) : await service.DecideAsync("approval", 0, Binding, Allow);
        Assert.Equal(ApprovalOutcome.Expired, result.Outcome); Assert.Equal(ApprovalState.Expired, store.Current.State);
    }
    [Fact]
    public async Task DeniedAndUnknownDispatchCannotExecute()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        await service.DecideAsync("approval", 0, Binding, Allow with { Decision = HumanDecision.Deny });
        Assert.Equal(ApprovalOutcome.Unavailable, (await service.ConsumeAsync("approval", Binding, true)).Outcome);
        store = new Store(); service = new(store, new Clock());
        await service.DecideAsync("approval", 0, Binding, Allow); await service.ConsumeAsync("approval", Binding, true);
        Assert.Equal(ApprovalOutcome.Applied, (await service.MarkDispatchUnknownAsync("approval", Binding)).Outcome);
        Assert.Equal(ApprovalOutcome.Unavailable, (await service.ConsumeAsync("approval", Binding, true)).Outcome);
        Assert.Equal(ApprovalState.DispatchUnknown, store.Current.State);
    }
    [Fact]
    public async Task InvalidIdentityAndDecisionAreRejectedBeforeWrite()
    {
        var store = new Store(); var service = new ApprovalService(store, new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync("approval", 0, Binding with { InvocationId = "" }, Allow));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync("approval", 0, Binding, Allow with { Decision = (HumanDecision)99 }));
        Assert.Equal(ApprovalOutcome.NotFound, (await service.DecideAsync("missing", 0, Binding, Allow)).Outcome);
        Assert.Equal(ApprovalOutcome.Conflict, (await service.DecideAsync("approval", 42, Binding, Allow)).Outcome);
        Assert.Equal(0, store.Current.Version);
    }
}
