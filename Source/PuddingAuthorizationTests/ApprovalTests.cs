using PuddingAuthorization;
using PuddingAuthorization.Contracts;

namespace PuddingAuthorizationTests;

[TestClass]
public sealed class ApprovalTests
{
    private static ApprovalCoordinator Coordinator(FakeApprovalStore store, TimeProvider? clock = null)
        => new(store, clock ?? new FixedClock(AuthorizationFixtures.Now));

    [TestMethod]
    public async Task Request_IsIdempotentByOperation_ButDifferentContentConflicts()
    {
        var store = new FakeApprovalStore();
        var coordinator = Coordinator(store);
        var first = await coordinator.RequestAsync(AuthorizationFixtures.HostBinding, AuthorizationFixtures.Approval());
        var replay = await coordinator.RequestAsync(AuthorizationFixtures.HostBinding, AuthorizationFixtures.Approval());
        Assert.AreEqual(first.ApprovalId, replay.ApprovalId);
        Assert.AreEqual(ApprovalState.Pending, replay.State);

        var conflict = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.RequestAsync(AuthorizationFixtures.HostBinding,
                AuthorizationFixtures.Approval(argumentHash: "sha256-different")));
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalConflict, conflict.Code);
    }

    [TestMethod]
    public async Task Request_ValidatesBoundsAndBinding()
    {
        var coordinator = Coordinator(new FakeApprovalStore());
        var invalidTtl = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.RequestAsync(AuthorizationFixtures.HostBinding,
                AuthorizationFixtures.Approval() with { Ttl = TimeSpan.Zero }));
        Assert.AreEqual(AuthorizationErrorCodes.RequestInvalid, invalidTtl.Code);
        var invalidBinding = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.RequestAsync(AuthorizationFixtures.HostBinding with { RequestedBy = "" },
                AuthorizationFixtures.Approval()));
        Assert.AreEqual(AuthorizationErrorCodes.RequestInvalid, invalidBinding.Code);
    }

    [TestMethod]
    public async Task Plugin_CannotDecideItsOwnApproval()
    {
        var store = new FakeApprovalStore();
        var coordinator = Coordinator(store);
        await coordinator.RequestAsync(AuthorizationFixtures.PluginBinding, AuthorizationFixtures.Approval());
        var forbidden = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.DecideAsync(AuthorizationFixtures.PluginBinding, "approval-1", ApprovalState.Approved, 0));
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalForbidden, forbidden.Code);
    }

    [TestMethod]
    public async Task Decide_RequiresTheCurrentRevision_AndAPendingApproval()
    {
        var store = new FakeApprovalStore();
        var coordinator = Coordinator(store);
        await coordinator.RequestAsync(AuthorizationFixtures.HostBinding, AuthorizationFixtures.Approval());

        var conflict = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.DecideAsync(AuthorizationFixtures.HostBinding, "approval-1", ApprovalState.Approved, 9));
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalConflict, conflict.Code);

        var approved = await coordinator.DecideAsync(AuthorizationFixtures.HostBinding, "approval-1",
            ApprovalState.Approved, 0);
        Assert.AreEqual(ApprovalState.Approved, approved.State);
        Assert.AreEqual("actor-1", approved.DecidedBy);
        Assert.AreEqual(1L, approved.Revision);

        var again = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.DecideAsync(AuthorizationFixtures.HostBinding, "approval-1", ApprovalState.Denied, 1));
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalNotPending, again.Code);
    }

    [TestMethod]
    public async Task Decide_AfterExpiry_IsRejected()
    {
        var clock = new MutableClock(AuthorizationFixtures.Now);
        var store = new FakeApprovalStore();
        var coordinator = Coordinator(store, clock);
        await coordinator.RequestAsync(AuthorizationFixtures.HostBinding,
            AuthorizationFixtures.Approval() with { Ttl = TimeSpan.FromMinutes(1) });
        clock.Advance(TimeSpan.FromMinutes(2));

        var expired = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () =>
            await coordinator.DecideAsync(AuthorizationFixtures.HostBinding, "approval-1", ApprovalState.Approved, 0));
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalExpired, expired.Code);
    }

    [TestMethod]
    public async Task Get_ReturnsMissingNull_AndInvalidIdIsTyped()
    {
        var coordinator = Coordinator(new FakeApprovalStore());
        Assert.IsNull(await coordinator.GetAsync("approval-missing"));
        var invalid = await Assert.ThrowsExactlyAsync<AuthorizationException>(async () => await coordinator.GetAsync(" "));
        Assert.AreEqual(AuthorizationErrorCodes.RequestInvalid, invalid.Code);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
