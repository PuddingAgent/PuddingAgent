using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C3b：Agent 主会话身份解析（redirect → Agent MainSessionId → FindMainAsync），
/// 只做有界点查、校验 workspace/owner、失败返回 null；不再加载整个工作区会话列表。
/// </summary>
[TestClass]
public sealed class AgentMainSessionResolverTests
{
    [TestMethod]
    public async Task Resolve_PrefersRedirectOverAgentMainSession()
    {
        using var temp = new TempResolverEnv();
        var repo = new FakeSessionRepository();
        repo.Add(Session("s-redirect", "ws", "single-user"));
        repo.Add(Session("s-agent", "ws", "single-user"));
        var catalog = new FakeWorkspaceAgentCatalog(Agent("agent-1", "s-agent"));
        temp.Redirects.Register("ws", "agent-1", "old-main", "s-redirect");

        var resolver = Resolver(repo, catalog, temp);
        var session = await resolver.ResolveAsync("ws", "single-user", "agent-1");

        Assert.IsNotNull(session);
        Assert.AreEqual("s-redirect", session.SessionId);
    }

    [TestMethod]
    public async Task Resolve_UsesAgentManifestMainSessionWhenRedirectMissing()
    {
        using var temp = new TempResolverEnv();
        var repo = new FakeSessionRepository();
        repo.Add(Session("s-agent", "ws", "single-user"));
        var catalog = new FakeWorkspaceAgentCatalog(Agent("agent-1", "s-agent"));

        var resolver = Resolver(repo, catalog, temp);
        var session = await resolver.ResolveAsync("ws", "single-user", "agent-1");

        Assert.IsNotNull(session);
        Assert.AreEqual("s-agent", session.SessionId);
    }

    [TestMethod]
    public async Task Resolve_FallsBackToCanonicalFindMain()
    {
        using var temp = new TempResolverEnv();
        var repo = new FakeSessionRepository
        {
            MainSession = Session("s-main", "ws", "single-user"),
        };
        var catalog = new FakeWorkspaceAgentCatalog(Agent("agent-1", mainSessionId: null));

        var resolver = Resolver(repo, catalog, temp);
        var session = await resolver.ResolveAsync("ws", "single-user", "agent-1");

        Assert.IsNotNull(session);
        Assert.AreEqual("s-main", session.SessionId);
        Assert.AreEqual(("ws", "agent", "agent-1"), repo.LastMainLookup);
    }

    [TestMethod]
    public async Task Resolve_RejectsWorkspaceOrOwnerMismatch()
    {
        using var temp = new TempResolverEnv();
        var repo = new FakeSessionRepository();
        repo.Add(Session("s-other-ws", "other-ws", "single-user"));
        repo.Add(Session("s-other-owner", "ws", "other-user"));
        repo.MainSession = Session("s-main-other-owner", "ws", "other-user");
        var catalog = new FakeWorkspaceAgentCatalog(Agent("agent-1", "s-other-ws"));

        var resolver = Resolver(repo, catalog, temp);
        var session = await resolver.ResolveAsync("ws", "single-user", "agent-1");

        Assert.IsNull(session);
    }

    [TestMethod]
    public async Task Resolve_NormalizesAdminOwnerToSingleUser()
    {
        using var temp = new TempResolverEnv();
        var repo = new FakeSessionRepository();
        repo.Add(Session("s-agent", "ws", "single-user"));
        var catalog = new FakeWorkspaceAgentCatalog(Agent("agent-1", "s-agent"));

        var resolver = Resolver(repo, catalog, temp);
        var session = await resolver.ResolveAsync("ws", "admin", "agent-1");

        Assert.IsNotNull(session);
        Assert.AreEqual("s-agent", session.SessionId);
    }

    private static AgentMainSessionResolver Resolver(
        FakeSessionRepository repo,
        FakeWorkspaceAgentCatalog catalog,
        TempResolverEnv env)
        => new(
            repo,
            catalog,
            env.Redirects,
            NullLogger<AgentMainSessionResolver>.Instance);

    private static SessionRecord Session(string sessionId, string workspaceId, string owner) => new()
    {
        SessionId = sessionId,
        WorkspaceId = workspaceId,
        AgentTemplateId = "tpl",
        ChannelId = "local",
        OwnerUserId = owner,
        SessionRole = SessionRole.Main,
        PrincipalKind = "agent",
        PrincipalId = "agent-1",
    };

    private static WorkspaceAgentDto Agent(string agentId, string? mainSessionId) => new(
        agentId,
        "name",
        null,
        null,
        null,
        null,
        null,
        mainSessionId,
        null,
        null,
        null,
        true,
        false,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class TempResolverEnv : IDisposable
    {
        public TempResolverEnv()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-resolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Redirects = new SessionRedirectStore(PuddingDataPaths.FromRoot(Root));
        }

        public string Root { get; }
        public SessionRedirectStore Redirects { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of temp data outside the repo
            }
        }
    }
}
