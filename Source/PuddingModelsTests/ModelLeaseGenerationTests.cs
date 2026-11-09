using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

[TestClass]
public sealed class ModelLeaseGenerationTests
{
    private static readonly Dictionary<string, int> OneModel = new() { ["model-1"] = 1 };

    [TestMethod]
    public async Task NewGeneration_RevokesNewDispatch_ButOldLeasesStillRelease()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(OneModel, configRevision: 1);
        var lease = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));

        var generationTwo = registry.CreateGeneration(OneModel, configRevision: 2);
        var revoked = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-1", generationTwo.Generation - 1, ModelFixtures.Now, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ModelErrorCodes.LeaseGenerationRevoked, revoked.Code);

        await registry.ReleaseAsync(lease.LeaseId);
        Assert.AreEqual(0, registry.ActiveCount("model-1"), "The old attempt releases its real slot after completion.");
    }

    [TestMethod]
    public async Task Downgrade_DoesNotPreemptActiveSlots_AndOnlyLimitsNewAdmission()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(new Dictionary<string, int> { ["model-1"] = 2 }, configRevision: 1);
        var first = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));
        var second = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));

        registry.CreateGeneration(OneModel, configRevision: 2);
        Assert.AreEqual(2, registry.ActiveCount("model-1"), "A downgrade must not kill running attempts.");
        var busy = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ModelErrorCodes.LeaseBusy, busy.Code);

        await registry.ReleaseAsync(first.LeaseId);
        var stillBusy = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ModelErrorCodes.LeaseBusy, stillBusy.Code, "One active lease still occupies the single slot.");
        await registry.ReleaseAsync(second.LeaseId);
        var admitted = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));
        Assert.AreEqual(registry.CurrentGeneration, admitted.Generation);
    }

    [TestMethod]
    public async Task UpgradeAndRevoke_ApplyTheNewLimitToNewAdmission()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(OneModel, configRevision: 1);
        registry.CreateGeneration(new Dictionary<string, int> { ["model-1"] = 3 }, configRevision: 2);
        for (var i = 0; i < 3; i++)
            await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));
        var busy = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ModelErrorCodes.LeaseBusy, busy.Code);
        var active = await registry.RevokeGenerationAsync(registry.CurrentGeneration);
        Assert.AreEqual(3, active, "Revoking returns the still-running attempts without freeing them.");
    }

    [TestMethod]
    public async Task Revoke_KeepsActiveSlotsUntilRelease()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(OneModel, configRevision: 1);
        var lease = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));
        var active = await registry.RevokeGenerationAsync(registry.CurrentGeneration);
        Assert.AreEqual(1, active);
        Assert.AreEqual(1, registry.ActiveCount("model-1"));
        await registry.ReleaseAsync(lease.LeaseId);
        Assert.AreEqual(0, registry.ActiveCount("model-1"));
    }

    [TestMethod]
    public async Task ReleaseTwice_IsRejected_AndUnknownModelIsUnavailable()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(OneModel, configRevision: 1);
        var lease = await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1));
        await registry.ReleaseAsync(lease.LeaseId);
        var again = await Assert.ThrowsExactlyAsync<ModelException>(async () => await registry.ReleaseAsync(lease.LeaseId));
        Assert.AreEqual(ModelErrorCodes.LeaseReleaseRejected, again.Code);

        var unavailable = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-unknown", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ModelErrorCodes.Unavailable, unavailable.Code);
    }

    [TestMethod]
    public async Task LeaseLifetime_IsBounded()
    {
        var registry = new ModelLeaseRegistry();
        registry.CreateGeneration(OneModel, configRevision: 1);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await registry.AcquireAsync("model-1", registry.CurrentGeneration, ModelFixtures.Now, TimeSpan.FromHours(2)));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, failure.Code);
    }
}
