using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionAdmissionCoordinatorTests
{
    [TestMethod]
    public void AcquireForeground_PreemptsBackgroundAndBlocksReplacementUntilRelease()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        using var backgroundCts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterBackground("default", "agent-1", backgroundCts);

        Assert.IsNotNull(background);
        Assert.IsTrue(coordinator.CanStartBackground("default", "agent-2"));

        using (coordinator.AcquireForeground("default", "agent-1"))
        {
            Assert.IsTrue(backgroundCts.IsCancellationRequested);
            Assert.IsTrue(background!.WasPreempted);
            Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));
            Assert.IsFalse(coordinator.CanStartBackground("default", "agent-1"));
        }

        background.Dispose();
        Assert.IsFalse(coordinator.HasForegroundDemand("default", "agent-1"));
        Assert.IsTrue(coordinator.CanStartBackground("default", "agent-1"));
    }

    [TestMethod]
    public void ReserveForeground_BlocksBackgroundUntilCanonicalTurnConsumesReservation()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();

        coordinator.ReserveForeground("default", "agent-1", TimeSpan.FromMinutes(1));

        Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));
        using var backgroundCts = new CancellationTokenSource();
        Assert.IsNull(coordinator.TryRegisterBackground("default", "agent-1", backgroundCts));

        using (coordinator.AcquireForeground("default", "agent-1"))
            Assert.IsTrue(coordinator.HasForegroundDemand("default", "agent-1"));

        Assert.IsFalse(coordinator.HasForegroundDemand("default", "agent-1"));
    }
}
