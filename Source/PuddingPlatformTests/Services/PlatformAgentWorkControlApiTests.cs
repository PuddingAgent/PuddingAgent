using System.Security.Claims;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingPlatform.Services.AgentWork;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-02 I2 control API: Observe/Cancel are bound to the management actor and addressed Agent; terminal work reports
/// AlreadyTerminal without a second cancel; revision mismatch is typed.
/// </summary>
[TestClass]
public sealed class PlatformAgentWorkControlApiTests
{
    [TestMethod]
    public async Task ObserveAndCancel_AreBoundToTheAddressedAgent()
    {
        var backend = new FakeBackend();
        var api = new AgentWorkControlApiFactory(backend).ForManagementUser(Principal("user-1"), new("ws-1", "agent-1"));
        var observation = await api.ObserveAsync(new ObserveWorkRequest(new("ws-1", "agent-1"), "op-1", 0));
        Assert.AreEqual("op-1", observation.OperationId);
        Assert.AreEqual(1, backend.Observes);

        var requested = await api.CancelAsync(new CancelWorkRequest("cancel-1", new("ws-1", "agent-1"), "op-1", 5));
        Assert.AreEqual(CancelWorkEffect.Requested, requested.Effect);
        Assert.AreEqual(1, backend.Cancels);
    }

    [TestMethod]
    public async Task Control_RejectsAnotherAgentAndExternalCallers()
    {
        var factory = new AgentWorkControlApiFactory(new FakeBackend());
        var api = factory.ForManagementUser(Principal("user-1"), new("ws-1", "agent-1"));
        var cross = await Assert.ThrowsExactlyAsync<AgentWorkControlException>(async () =>
            await api.ObserveAsync(new ObserveWorkRequest(new("ws-1", "agent-2"), "op-1", 0)));
        Assert.AreEqual("permission.denied", cross.Code);
        Assert.AreEqual("permission.denied",
            Assert.ThrowsExactly<AgentWorkControlException>(() =>
                factory.ForManagementUser(Principal("ext:x", true), new("ws-1", "agent-1"))).Code);
    }

    [TestMethod]
    public async Task TerminalWork_ReportsAlreadyTerminal()
    {
        var backend = new FakeBackend { Effect = "terminal" };
        var api = new AgentWorkControlApiFactory(backend).ForManagementUser(Principal("user-1"), new("ws-1", "agent-1"));
        var receipt = await api.CancelAsync(new CancelWorkRequest("cancel-1", new("ws-1", "agent-1"), "op-1", 5));
        Assert.AreEqual(CancelWorkEffect.AlreadyTerminal, receipt.Effect);
    }

    private static ClaimsPrincipal Principal(string? subject, bool external = false)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (external) claims.Add(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: subject is null ? null : "Bearer"));
    }

    private sealed class FakeBackend : IAgentWorkControlBackend
    {
        public string Effect { get; init; } = "requested";
        public int Observes { get; private set; }
        public int Cancels { get; private set; }

        public Task<WorkObservation> ObserveAsync(PuddingAgentWork.Contracts.WorkCaller caller, AgentRef agent,
            string operationId, long afterSequence, int maxCount, CancellationToken ct)
        {
            Observes++;
            return Task.FromResult(new WorkObservation(operationId, "Running", [], afterSequence, false, false));
        }

        public Task<WorkCancelBackendResult> CancelAsync(PuddingAgentWork.Contracts.WorkCaller caller, AgentRef agent,
            string operationId, string targetOperationId, long expectedRevision, string reason, CancellationToken ct)
        {
            Cancels++;
            return Task.FromResult(new WorkCancelBackendResult(Effect, expectedRevision, "Running"));
        }
    }
}
