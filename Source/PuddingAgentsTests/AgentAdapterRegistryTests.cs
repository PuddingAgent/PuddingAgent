using PuddingAgents;
using PuddingAgents.Contracts;

namespace PuddingAgentsTests;

[TestClass]
public sealed class AgentAdapterRegistryTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);
    private static readonly AgentAdapterBinding Codex = new("dev", "code-review", "codex-install");
    private static readonly AgentAdapterBinding OtherWorkspace = Codex with { WorkspaceId = "ops" };
    private static readonly AgentAdapterBinding OtherInstallation = Codex with { InstallationId = "other-install" };
    private readonly AgentAdapterTestClock _clock = new();

    [TestMethod]
    public void RegistryRoutesThreeAdaptersByWorkspaceAndLogicalAgent()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var first = registry.Register(Codex);
        var second = registry.Register(OtherWorkspace);
        var runner = new AgentAdapterBinding("dev", "test-runner", "cli-install");
        var third = registry.Register(runner);

        Assert.AreEqual(first, registry.Resolve("dev", "code-review"));
        Assert.AreEqual(second, registry.Resolve("ops", "code-review"));
        Assert.AreEqual(third, registry.Resolve("dev", "test-runner"));
        Assert.IsNull(registry.Resolve("ops", "test-runner"));
    }

    [TestMethod]
    public async Task ConcurrentRegistrationAllowsOnlyOneConnectionForAnAgent()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                registry.Register(Codex);
                return "registered";
            }
            catch (AgentBackendException ex) { return ex.Code; }
        })));
        Assert.AreEqual(1, results.Count(result => result == "registered"));
        Assert.AreEqual(7, results.Count(result => result == AgentAdapterErrorCodes.Busy));
    }

    [TestMethod]
    public void ReplacementFencesTheOldConnectionAndRequiresTheSameInstallation()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var oldLease = registry.Register(Codex);
        var forbidden = Assert.ThrowsExactly<AgentBackendException>(() =>
            registry.Register(OtherInstallation, oldLease.LeaseId));
        Assert.AreEqual(AgentAdapterErrorCodes.LeaseRejected, forbidden.Code);

        var next = registry.Register(Codex, oldLease.LeaseId);
        Assert.AreNotEqual(oldLease.LeaseId, next.LeaseId);
        Assert.IsNull(registry.Renew(Codex, oldLease.LeaseId));
        Assert.IsFalse(registry.Release(Codex, oldLease.LeaseId));
        var staleReplacement = Assert.ThrowsExactly<AgentBackendException>(() =>
            registry.Register(Codex, oldLease.LeaseId));
        Assert.AreEqual(AgentAdapterErrorCodes.LeaseRejected, staleReplacement.Code);
        Assert.AreEqual(next, registry.Resolve(Codex.WorkspaceId, Codex.AgentId));
    }

    [TestMethod]
    public void LeaseIdAloneCannotRenewOrReleaseAnotherBinding()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var lease = registry.Register(Codex);
        var foreignLease = registry.Register(OtherWorkspace);

        Assert.IsNull(registry.Renew(OtherInstallation, lease.LeaseId));
        Assert.IsFalse(registry.Release(OtherInstallation, lease.LeaseId));
        Assert.IsNull(registry.Renew(OtherWorkspace, lease.LeaseId));
        Assert.IsFalse(registry.Release(OtherWorkspace, lease.LeaseId));
        Assert.AreEqual(foreignLease, registry.Resolve(OtherWorkspace.WorkspaceId, OtherWorkspace.AgentId));
        Assert.IsTrue(registry.Release(Codex, lease.LeaseId));
        Assert.IsNull(registry.Resolve(Codex.WorkspaceId, Codex.AgentId));
    }

    [TestMethod]
    public void RenewExtendsTheSameLeaseAndExpiryCannotBeReversed()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var lease = registry.Register(Codex);
        _clock.Now = lease.ExpiresAtUtc.AddSeconds(-1);
        var renewed = registry.Renew(Codex, lease.LeaseId);
        Assert.IsNotNull(renewed);
        Assert.AreEqual(lease.LeaseId, renewed.LeaseId);
        Assert.AreEqual(_clock.Now + Duration, renewed.ExpiresAtUtc);

        _clock.Now = renewed.ExpiresAtUtc;
        Assert.IsNull(registry.Renew(Codex, lease.LeaseId));
        Assert.IsNull(registry.Resolve(Codex.WorkspaceId, Codex.AgentId));
        Assert.AreNotEqual(lease.LeaseId, registry.Register(Codex).LeaseId);
    }

    [TestMethod]
    public void InvalidHostBindingCannotRegister()
    {
        var registry = new AgentAdapterRegistry(_clock, Duration);
        var invalid = Assert.ThrowsExactly<AgentBackendException>(() =>
            registry.Register(Codex with { InstallationId = "" }));
        Assert.AreEqual(AgentErrorCodes.BindingInvalid, invalid.Code);
        Assert.IsNull(registry.Resolve(Codex.WorkspaceId, Codex.AgentId));
    }

    private sealed class AgentAdapterTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
