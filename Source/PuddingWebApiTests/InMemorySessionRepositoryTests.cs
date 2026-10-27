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
    public async Task RebindApi_LockedFileReturnsCorrelatedSafeStorageDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            await repo.CreateAsync(CreateSession("next", "default") with { SessionRole = SessionRole.Task });
            using var locked = File.Open(Path.Combine(paths.RuntimeRoot, "sessions", "default", "next.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var controller = new PuddingController.Controllers.SessionController(repo,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PuddingController.Controllers.SessionController>.Instance);
            var response = await controller.RebindMain(new PuddingController.Controllers.RebindMainSessionRequest
            { WorkspaceId = "default", PrincipalKind = "agent", PrincipalId = "agent-alpha", SuccessorSessionId = "next" }, CancellationToken.None);
            var result = (Microsoft.AspNetCore.Mvc.ObjectResult)response.Result!;
            Assert.AreEqual(500, result.StatusCode);
            var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
            Assert.AreEqual("session.persistence_failed", body.GetProperty("errorCode").GetString());
            Assert.AreEqual(7, body.GetProperty("writeAttempts").GetInt32());
            Assert.AreEqual(12, body.GetProperty("errorId").GetString()!.Length);
            Assert.IsFalse(body.GetRawText().Contains(root));
            Assert.AreEqual(SessionRole.Task, (await repo.GetAsync("next"))!.SessionRole);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedCreate_DoesNotPublishUnpersistedSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            Directory.CreateDirectory(Path.Combine(paths.RuntimeRoot, "sessions"));
            await File.WriteAllTextAsync(Path.Combine(paths.RuntimeRoot, "sessions", "default"), "blocked directory");
            await Assert.ThrowsAsync<IOException>(() => repo.CreateAsync(CreateSession("failed", "default")));
            Assert.IsNull(await repo.GetAsync("failed"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedUpdateAndDelete_PreserveMemoryAndDisk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            var original = CreateSession("locked", "default") with { Title = "original" };
            await repo.CreateAsync(original);
            using var locked = File.Open(Path.Combine(paths.RuntimeRoot, "sessions", "default", "locked.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await Assert.ThrowsAsync<IOException>(() => repo.UpdateAsync(original with { Title = "uncommitted" }));
            await Assert.ThrowsAsync<IOException>(() => repo.DeleteAsync("locked"));
            Assert.AreEqual(original, await repo.GetAsync("locked"));
            Assert.AreEqual(original, await new InMemorySessionRepository(paths).GetAsync("locked"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Rebind_PartialDemotionFailureMatchesRestartAndCanResume()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pudding-session-repo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            await repo.CreateAsync(CreateSession("old", "default"));
            await repo.CreateAsync(CreateSession("next", "default") with { SessionRole = SessionRole.Task });
            using var locked = File.Open(Path.Combine(paths.RuntimeRoot, "sessions", "default", "old.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await Assert.ThrowsAsync<IOException>(() => repo.RebindMainAsync("default", "agent", "agent-alpha", "next"));
            var live = await repo.FindMainAsync("default", "agent", "agent-alpha");
            var reloaded = await new InMemorySessionRepository(paths).FindMainAsync("default", "agent", "agent-alpha");
            Assert.AreEqual("next", live!.SessionId);
            Assert.AreEqual(live, reloaded);
            locked.Dispose();
            await repo.RebindMainAsync("default", "agent", "agent-alpha", "next");
            Assert.AreEqual(SessionRole.Task, (await repo.GetAsync("old"))!.SessionRole);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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

    [TestMethod]
    public async Task RebindMainAsync_ShouldPersistSuccessorAsOnlyMainAcrossRestart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-session-repo-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var repo = new InMemorySessionRepository(paths);
            var previous = CreateSession("previous-main", "default");
            var successor = CreateSession("successor-main", "default") with
            {
                SessionRole = SessionRole.Task,
                PrincipalKind = null,
                PrincipalId = null,
                AgentInstanceId = null,
                CreatedAt = previous.CreatedAt.AddMinutes(1),
                LastActiveAt = previous.LastActiveAt.AddMinutes(1),
            };
            await repo.CreateAsync(previous);
            await repo.CreateAsync(successor);

            var rebound = await repo.RebindMainAsync(
                "default",
                "agent",
                "agent-alpha",
                successor.SessionId);

            Assert.AreEqual(successor.SessionId, rebound.SessionId);
            Assert.AreEqual(SessionRole.Main, rebound.SessionRole);
            Assert.AreEqual("agent-alpha", rebound.AgentInstanceId);

            var reloaded = new InMemorySessionRepository(paths);
            var main = await reloaded.FindMainAsync(
                "default",
                "agent",
                "agent-alpha");
            var demoted = await reloaded.GetAsync(previous.SessionId);

            Assert.IsNotNull(main);
            Assert.AreEqual(successor.SessionId, main!.SessionId);
            Assert.AreEqual(SessionRole.Task, demoted!.SessionRole);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
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
