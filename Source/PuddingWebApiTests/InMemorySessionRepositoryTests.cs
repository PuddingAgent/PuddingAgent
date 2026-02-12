using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingWebApiTests;

/// <summary>
/// Session 仓储持久化测试。
/// </summary>
[TestClass]
public sealed class InMemorySessionRepositoryTests
{
    [TestMethod]
    public async Task CreateAsync_ShouldPersistSessionToDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            var session = CreateSession("persist-session", "default");

            await repo.CreateAsync(session);

            var sessionFile = Path.Combine(paths.RuntimeRoot, "sessions", "default", "persist-session.json");
            Assert.IsTrue(File.Exists(sessionFile), "Session record should be persisted as a data-root file.");

            var reloaded = new InMemorySessionRepository(paths);
            var loaded = await reloaded.GetAsync(session.SessionId);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(session.SessionId, loaded!.SessionId);
            Assert.AreEqual(session.WorkspaceId, loaded.WorkspaceId);
            Assert.AreEqual(session.PrincipalId, loaded.PrincipalId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RepositoriesUsingDifferentDataRoots_ShouldNotShareSessions()
    {
        var rootA = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repoA = new InMemorySessionRepository(PuddingDataPaths.FromRoot(rootA));
            var repoB = new InMemorySessionRepository(PuddingDataPaths.FromRoot(rootB));

            await repoA.CreateAsync(CreateSession("isolated-session", "default"));

            var leaked = await repoB.GetAsync("isolated-session");

            Assert.IsNull(leaked);
        }
        finally
        {
            if (Directory.Exists(rootA))
                Directory.Delete(rootA, recursive: true);
            if (Directory.Exists(rootB))
                Directory.Delete(rootB, recursive: true);
        }
    }

    private static SessionRecord CreateSession(string sessionId, string workspaceId) => new()
    {
        SessionId = sessionId,
        WorkspaceId = workspaceId,
        AgentTemplateId = "global:general-assistant",
        ChannelId = "web",
        OwnerUserId = "user",
        PrincipalKind = "agent",
        PrincipalId = "agent-alpha",
        AgentInstanceId = "agent-alpha",
        SessionRole = SessionRole.Main,
        CreatedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
        LastActiveAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
    };
}
