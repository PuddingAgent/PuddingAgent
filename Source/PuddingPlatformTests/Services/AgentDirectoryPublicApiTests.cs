using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;
using PuddingCode.Configuration;
using PuddingCode.Security;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Agents;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-02 I0 public directory: listing/reading works from the manifest authority without any Work/root budget; pagination
/// is bounded and ordered; only a management principal is bound as actor.
/// </summary>
[TestClass]
public sealed class AgentDirectoryPublicApiTests
{
    [TestMethod]
    public async Task ManifestDirectory_ReadsWithoutExecutionIdentity_AndPaginates()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-agent-directory", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            foreach (var (id, name) in new[] { ("a-agent", "Alpha"), ("b-agent", "Beta") })
            {
                var instanceDir = paths.AgentInstanceRoot(id);
                Directory.CreateDirectory(instanceDir);
                await File.WriteAllTextAsync(Path.Combine(instanceDir, "manifest.json"),
                    System.Text.Json.JsonSerializer.Serialize(new AgentInstanceManifest
                    {
                        AgentInstanceId = id,
                        WorkspaceId = "default",
                        TemplateId = "general-assistant",
                        DisplayName = name,
                    }, jsonOptions));
                var refDir = Path.Combine(paths.WorkspaceRoot("default"), "agents", id);
                Directory.CreateDirectory(refDir);
                await File.WriteAllTextAsync(Path.Combine(refDir, "ref.json"),
                    System.Text.Json.JsonSerializer.Serialize(new WorkspaceAgentRef
                    {
                        AgentInstanceId = id,
                        WorkspaceId = "default",
                        AgentPath = Path.GetRelativePath(refDir, instanceDir),
                        IsEnabled = true,
                    }, jsonOptions));
            }
            var services = new ServiceCollection().BuildServiceProvider();
            using var avatarFixture = new AvatarCatalogTestFixture();
            var service = new WorkspaceAgentFileService(paths, null!, avatarFixture.Catalog,
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceAgentFileService>.Instance);
            var store = new WorkspaceAgentDirectoryStore(service, paths);

            var first = await store.ListAsync("default", null, 1);
            Assert.AreEqual(1, first.Agents.Length);
            Assert.AreEqual("a-agent", first.Agents[0].AgentId);
            Assert.IsFalse(first.Complete);
            Assert.AreEqual("a-agent", first.Cursor);

            var second = await store.ListAsync("default", first.Cursor, 1);
            Assert.AreEqual("b-agent", second.Agents[0].AgentId);
            Assert.IsTrue(second.Complete);
            Assert.IsNull(second.Cursor);

            var get = await store.GetAsync("default", "b-agent");
            Assert.AreEqual("Beta", get!.DisplayName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task PublicDirectory_UsesManagementActor_AndRejectsExternal()
    {
        var store = new FakeStore();
        var factory = new AgentDirectoryApiFactory(store);
        var api = factory.ForManagementUser(Principal("user-1"));
        var page = await api.ListAsync(new AgentListQuery("ws-1"));
        Assert.AreEqual(1, page.Agents.Length);
        Assert.AreEqual("agent-1", (await api.GetAsync(new AgentRef("ws-1", "agent-1")))!.AgentId);

        var external = Assert.ThrowsExactly<AgentDirectoryException>(() =>
            factory.ForManagementUser(Principal("ext:abc", external: true)));
        Assert.AreEqual(AgentErrorCodes.Forbidden, external.Code);
        var anonymous = Assert.ThrowsExactly<AgentDirectoryException>(() =>
            factory.ForManagementUser(Principal(null)));
        Assert.AreEqual(AgentErrorCodes.Forbidden, anonymous.Code);
    }

    private static ClaimsPrincipal Principal(string? subject, bool external = false)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (external) claims.Add(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: subject is null ? null : "Bearer"));
    }

    private sealed class FakeStore : IAgentDirectoryStore
    {
        public ValueTask<AgentDirectoryQueryResult> ListAsync(string workspaceId, string? cursor, int maxCount,
            CancellationToken ct = default)
        {
            ImmutableArray<AgentDirectoryRecord> agents =
            [
                new(workspaceId, "agent-1", "Agent", "template", 1, 1, false, DateTimeOffset.UtcNow),
            ];
            return ValueTask.FromResult(new AgentDirectoryQueryResult(agents, null, true));
        }

        public ValueTask<AgentDirectoryRecord?> GetAsync(string workspaceId, string agentId,
            CancellationToken ct = default)
            => ValueTask.FromResult<AgentDirectoryRecord?>(new(workspaceId, agentId, "Agent", "template", 1, 1, false,
                DateTimeOffset.UtcNow));
    }
}
