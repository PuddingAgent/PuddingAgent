using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingPlatform.Services.Agents;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-02 I2 factory: creation materializes only an Agent receipt (no inference), archive is revision-fenced, and only a
/// management principal may obtain the factory.
/// </summary>
[TestClass]
public sealed class PlatformAgentFactoryApiTests
{
    [TestMethod]
    public async Task Factory_CreatesAndArchivesThroughTheAuthoringBackend()
    {
        var backend = new FakeBackend();
        var api = new AgentFactoryApiFactory(backend).ForManagementUser(Principal("user-1"));
        var created = await api.CreateAsync(new CreateAgentRequest("op-1", "ws-1", "template-1", 1, "Agent"));
        Assert.AreEqual("agent-1", created.AgentId);
        Assert.AreEqual("user-1", backend.LastActor);
        Assert.AreEqual(1, backend.Creates);

        var archived = await api.ArchiveAsync(new ArchiveAgentRequest("op-2", new("ws-1", "agent-1"), 10));
        Assert.AreEqual("agent-1", archived.AgentId);
        Assert.AreEqual(1, backend.Archives);
    }

    [TestMethod]
    public async Task Factory_RejectsExternalOrAnonymousCallers()
    {
        var factory = new AgentFactoryApiFactory(new FakeBackend());
        Assert.AreEqual("agent.forbidden",
            Assert.ThrowsExactly<AgentDirectoryException>(() => factory.ForManagementUser(Principal("ext:x", true))).Code);
        Assert.AreEqual("agent.forbidden",
            Assert.ThrowsExactly<AgentDirectoryException>(() => factory.ForManagementUser(Principal(null))).Code);
    }

    [TestMethod]
    public async Task Factory_RejectsUnboundedRequests()
    {
        var api = new AgentFactoryApiFactory(new FakeBackend()).ForManagementUser(Principal("user-1"));
        Assert.AreEqual("agent.request_invalid",
            (await Assert.ThrowsExactlyAsync<AgentDirectoryException>(async () =>
                await api.CreateAsync(new CreateAgentRequest("", "ws-1", "t", 1, "A")))).Code);
        Assert.AreEqual("agent.request_invalid",
            (await Assert.ThrowsExactlyAsync<AgentDirectoryException>(async () =>
                await api.ArchiveAsync(new ArchiveAgentRequest("op", new("ws-1", ""), 0)))).Code);
    }

    private static ClaimsPrincipal Principal(string? subject, bool external = false)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (external) claims.Add(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: subject is null ? null : "Bearer"));
    }

    private sealed class FakeBackend : IAgentAuthoringBackend
    {
        public int Creates { get; private set; }
        public int Archives { get; private set; }
        public string? LastActor { get; private set; }

        public Task<AgentAuthoringReceipt> CreateAsync(string workspaceId, CreateAgentRequest request,
            CancellationToken ct = default)
        {
            Creates++;
            LastActor = "user-1";
            return Task.FromResult(new AgentAuthoringReceipt("agent-1", 1, DateTimeOffset.UtcNow));
        }

        public Task<AgentAuthoringReceipt> ArchiveAsync(string workspaceId, string agentId, long expectedRevision,
            CancellationToken ct = default)
        {
            Archives++;
            return Task.FromResult(new AgentAuthoringReceipt(agentId, expectedRevision + 1, DateTimeOffset.UtcNow));
        }
    }
}
