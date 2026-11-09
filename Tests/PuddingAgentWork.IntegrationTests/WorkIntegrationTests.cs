using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingCode.Configuration;
using PuddingCode.Security;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Execution;

[assembly: DoNotParallelize]
namespace PuddingAgentWork.IntegrationTests;

[TestClass]
public sealed class WorkIntegrationTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");
    private static ClaimsPrincipal User(string id = "manager") => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    [TestMethod]
    public async Task ConcurrentReplay_CommitsOneCanonicalTurn_AndRejectsDifferentText()
    {
        await using var f = await Fixture.Create();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Api().SubmitAsync(Agent, new("op", "你好"))));
        Assert.IsTrue(receipts.All(x => x == receipts[0]));
        Assert.IsTrue(receipts[0].InputMessageId.Length <= 64);
        await using var db = f.Db();
        Assert.AreEqual(1, await db.AcceptanceBatches.CountAsync()); Assert.AreEqual(1, await db.ChatMessages.CountAsync());
        Assert.AreEqual(1, await db.ChatExecutionCommands.CountAsync()); Assert.AreEqual(1, await db.ConversationTurns.CountAsync());
        Assert.AreEqual(1, await db.ConversationEvents.CountAsync());
        Assert.AreEqual("operation.conflict", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => f.Api().SubmitAsync(Agent, new("op", "different")))).Code);
        using var recreated = new CanonicalAgentWorkBackend(f.Services.GetRequiredService<IServiceScopeFactory>());
        var api = new AgentWorkApiFactory(recreated).ForManagementUser(User(), Agent);
        Assert.AreEqual(receipts[0], (await api.GetAsync(Agent, "op"))!.Receipt);
    }

    [TestMethod]
    public async Task ExactTerminal_FromRealJournal_DoesNotUseOtherTurnsLatestReply()
    {
        await using var f = await Fixture.Create();
        var a = await f.Api().SubmitAsync(Agent, new("a", "first"));
        var b = await f.Api().SubmitAsync(Agent, new("b", "second"));
        await f.Finish(a, TurnTerminal.Success("first reply", null));
        await f.Finish(b, TurnTerminal.Success("second reply", null));
        var snapshot = (await f.Api().GetAsync(Agent, "a"))!;
        Assert.AreEqual(AgentWorkStatus.Completed, snapshot.Status); Assert.AreEqual("first reply", snapshot.Terminal!.Reply);
        Assert.AreEqual("not_requested", snapshot.Verification);
        Assert.AreEqual(a.TurnId, snapshot.Receipt.TurnId);
    }

    [TestMethod]
    [DataRow("Failed", AgentWorkStatus.Failed)]
    [DataRow("Cancelled", AgentWorkStatus.Cancelled)]
    public async Task FailureAndCancellation_AreNotReportedAsSuccess(string kind, AgentWorkStatus expected)
    {
        await using var f = await Fixture.Create(); var receipt = await f.Api().SubmitAsync(Agent, new("op", "hello"));
        await f.Finish(receipt, kind == "Failed" ? TurnTerminal.Failure("test.failure", "diagnostic") : TurnTerminal.Cancelled);
        var snapshot = (await f.Api().GetAsync(Agent, "op"))!;
        Assert.AreEqual(expected, snapshot.Status); Assert.IsTrue(snapshot.IsTerminal); Assert.IsNotNull(snapshot.Terminal!.ErrorCode);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("foreign-run")]
    [DataRow("foreign-command")]
    [DataRow("wrong-kind")]
    [DataRow("invalid-json")]
    public async Task BrokenTerminalEvidence_ReturnsUnknown(string corruption)
    {
        await using var f = await Fixture.Create(); var receipt = await f.Api().SubmitAsync(Agent, new("op", "hello"));
        await f.Finish(receipt, TurnTerminal.Success("reply", null));
        await using (var db = f.Db())
        {
            var terminal = await db.ConversationEvents.SingleAsync(x => x.Type == ConversationEventTypes.TurnCompleted);
            switch (corruption)
            {
                case "missing": db.ConversationEvents.Remove(terminal); break;
                case "foreign-run": terminal.RunId = "other-run"; break;
                case "foreign-command": terminal.CommandId = "other-command"; break;
                case "wrong-kind": terminal.Payload = "{\"kind\":\"Failed\",\"reply\":\"looks good\"}"; break;
                default: terminal.Payload = "["; break;
            }
            await db.SaveChangesAsync();
        }
        var snapshot = (await f.Api().GetAsync(Agent, "op"))!;
        Assert.AreEqual(AgentWorkStatus.Unknown, snapshot.Status); Assert.IsNull(snapshot.Terminal); Assert.IsFalse(snapshot.IsTerminal);
    }

    [TestMethod]
    public async Task SignalFailureAfterCommit_IsRecoveredWithoutDuplicateSubmission()
    {
        await using var f = await Fixture.Create(); f.Signal.Throw = true;
        var receipt = await f.Api().SubmitAsync(Agent, new("op", "hello"));
        Assert.AreEqual(receipt, await f.Api().SubmitAsync(Agent, new("op", "hello")));
        await using var db = f.Db(); Assert.AreEqual(1, await db.ChatExecutionCommands.CountAsync());
    }

    [TestMethod]
    public async Task ReplayPrecedesMutableAvailability_ButNewWorkIsRejected()
    {
        await using var f = await Fixture.Create(); var receipt = await f.Api().SubmitAsync(Agent, new("op", "hello"));
        f.Target.Unavailable = true;
        Assert.AreEqual(receipt, await f.Api().SubmitAsync(Agent, new("op", "hello")));
        Assert.AreEqual("agent.unavailable", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => f.Api().SubmitAsync(Agent, new("new", "hello")))).Code);
        await using var db = f.Db(); Assert.AreEqual(1, await db.ChatExecutionCommands.CountAsync());
    }

    [TestMethod]
    public async Task PrincipalAndAgentScopes_AreCheckedBeforeReadingOrReplaying()
    {
        await using var f = await Fixture.Create(); await f.Api().SubmitAsync(Agent, new("op", "hello"));
        Assert.IsNull(await f.Api("another").GetAsync(Agent, "op"));
        Assert.AreEqual("permission.denied", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => f.Api().GetAsync(new("other", "agent"), "op"))).Code);
        var factory = f.Services.GetRequiredService<AgentWorkApiFactory>();
        Assert.ThrowsExactly<AgentWorkException>(() => factory.ForManagementUser(new(), Agent));
        var external = User(); ((ClaimsIdentity)external.Identity!).AddClaim(new(ExternalAccessTokenClaimNames.TokenId, "token"));
        Assert.ThrowsExactly<AgentWorkException>(() => factory.ForManagementUser(external, Agent));
    }

    [TestMethod]
    public async Task HttpController_RequiresLogin_RejectsControlFields_AndSdkRoundTrips()
    {
        await using var f = await Fixture.Create(); var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(f.Services.GetRequiredService<AgentWorkApiFactory>());
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new()
            { ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key });
            services.AddAuthorization(); services.AddControllers().AddApplicationPart(typeof(AgentWorkApiController).Assembly);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(x => x.MapControllers()); }));
        using var http = server.CreateClient(); const string route = "/api/workspaces/workspace/agents/agent/work";
        using var denied = await http.PostAsJsonAsync(route, new AgentWorkRequest("op", "hello")); Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        var token = new JwtSecurityToken(claims: [new Claim(ClaimTypes.NameIdentifier, "manager")], expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var work = new AgentWorkClient(new HttpAgentWorkApi(http)).For(Agent);
        Assert.IsNull(await work.GetAsync("op"));
        var receipt = await work.SubmitAsync(new("op", "你好"));
        Assert.AreEqual(receipt, (await work.GetAsync("op"))!.Receipt);
        using var forged = await http.PostAsJsonAsync(route, new { operationId = "forged", text = "hello", goalContinuation = new { trusted = true } });
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
        await f.Finish(receipt, TurnTerminal.Success("真实终态", null));
        Assert.AreEqual("真实终态", (await work.GetAsync("op"))!.Terminal!.Reply);
    }

    [TestMethod]
    public async Task RealTargetResolver_RequiresOwnedActiveMainSession_AndEnabledAgent()
    {
        var catalog = new Mock<IWorkspaceAgentCatalog>(); var sessions = new Mock<ISessionRepository>();
        var entry = new WorkspaceAgentDto("agent", "Agent", null, null, null, null, "template", "session", null, null, null,
            true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        catalog.Setup(x => x.ListAgentsAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(() => new[] { entry });
        SessionRecord? session = new() { SessionId = "session", WorkspaceId = "workspace", AgentTemplateId = "template", ChannelId = "web",
            OwnerUserId = "manager", AgentInstanceId = "agent", SessionRole = SessionRole.Main };
        sessions.Setup(x => x.GetAsync("session", It.IsAny<CancellationToken>())).ReturnsAsync(() => session);
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "agent-work-read-only-" + Guid.NewGuid().ToString("N")));
        var main = new AgentMainSessionResolver(sessions.Object, catalog.Object, new SessionRedirectStore(paths), NullLogger<AgentMainSessionResolver>.Instance);
        var resolver = new AgentWorkTargetResolver(catalog.Object, main);
        var key = new WorkKey(new("manager"), Agent, "op", "work");
        Assert.AreEqual("session", await resolver.ResolveConversationAsync(key, default));
        foreach (var invalid in new[] { session with { AgentInstanceId = "another" }, session with { OwnerUserId = "another" },
                     session with { SessionRole = SessionRole.Task }, session with { Status = SessionStatus.Frozen } })
        {
            session = invalid;
            Assert.AreEqual("agent.not_ready", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => resolver.ResolveConversationAsync(key, default))).Code);
        }
        entry = entry with { IsFrozen = true };
        Assert.AreEqual("agent.unavailable", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => resolver.ResolveConversationAsync(key, default))).Code);
        catalog.Setup(x => x.ListAgentsAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkspaceAgentDto>());
        Assert.AreEqual("resource.not_found", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => resolver.ResolveConversationAsync(key, default))).Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public ServiceProvider Services { get; private set; } = null!;
        public FixedTarget Target { get; } = new(); public RecordingSignal Signal { get; } = new();
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f._connection.OpenAsync(); var services = new ServiceCollection();
            services.AddLogging(); services.AddDbContextFactory<PlatformDbContext>(options => options.UseSqlite(f._connection));
            services.AddSingleton<IAgentWorkTargetResolver>(f.Target); services.AddSingleton<ICommittedEventSignal>(f.Signal);
            services.AddSingleton(Mock.Of<IVisualArtifactLocalFileResolver>());
            services.AddScoped<IConversationAcceptanceStore, ConversationAcceptanceStore>();
            services.AddScoped<ISubmitTurnHandler, SubmitTurnHandler>(); services.AddAgentWorkApi();
            f.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var db = f.Db(); await db.Database.EnsureCreatedAsync(); return f;
        }
        public PlatformDbContext Db() => Services.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext();
        public IAgentWorkApi Api(string actor = "manager") => Services.GetRequiredService<AgentWorkApiFactory>().ForManagementUser(User(actor), Agent);
        public async Task Finish(AgentWorkReceipt receipt, TurnTerminal terminal)
        {
            await using var db = Db(); var command = await db.ChatExecutionCommands.SingleAsync(x => x.CommandId == receipt.CommandId);
            var runId = Guid.NewGuid().ToString("N"); command.Status = "running"; command.RunId = runId;
            var turn = await db.ConversationTurns.SingleAsync(x => x.TurnId == receipt.TurnId); turn.Status = "running";
            var run = new ExecutionRunEntity { RunId = runId, CommandId = command.CommandId, ConversationId = receipt.ConversationId,
                TurnId = receipt.TurnId, Attempt = 1, WorkerId = "test-worker", Status = "running", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() };
            db.ExecutionRuns.Add(run); await db.SaveChangesAsync();
            var lease = new ExecutionLease(command.CommandId, "test-worker", Agent.WorkspaceId, receipt.ConversationId, receipt.TurnId, runId,
                run.FencingToken, DateTimeOffset.UtcNow.AddMinutes(1)) { TraceId = null };
            var journal = new SqliteExecutionJournal(Services.GetRequiredService<IServiceScopeFactory>(), Signal, NullLogger<SqliteExecutionJournal>.Instance);
            await journal.CommitTerminalAsync(lease, terminal, [], default);
        }
        public async ValueTask DisposeAsync() { await Services.DisposeAsync(); await _connection.DisposeAsync(); }
    }
    private sealed class FixedTarget : IAgentWorkTargetResolver
    {
        public bool Unavailable;
        public Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct) => Unavailable
            ? throw new AgentWorkException("agent.unavailable", "disabled") : Task.FromResult("conversation");
    }
    private sealed class RecordingSignal : ICommittedEventSignal
    {
        public bool Throw;
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct) => ValueTask.CompletedTask;
        public void Signal(string conversationId, long committedThroughSequence) { if (Throw) throw new IOException("after commit"); }
    }
}
