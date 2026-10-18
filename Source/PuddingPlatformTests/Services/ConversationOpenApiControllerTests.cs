using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C3b：统一打开/历史/过程 HTTP 控制器合同（ETag 304、稳定错误码、身份解析接线）。
/// 直接实例化控制器，使用真实 SQLite 查询服务与 fake 身份解析依赖。
/// </summary>
[TestClass]
public sealed class ConversationOpenApiControllerTests
{
    [TestMethod]
    public async Task OpenAgentConversation_ReturnsSnapshotAndHonorsIfNoneMatch()
    {
        using var env = new ControllerEnv();
        await env.SeedConversationAsync(messageCount: 3, checkpoint: 3, head: 3);

        var controller = env.NewController("single-user");
        var result = await controller.OpenAgentConversation(
            "ws", "agent-1", messageLimit: 20, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "expected 200 snapshot");
        var snapshot = (ConversationOpenSnapshot)ok.Value!;
        Assert.HasCount(3, snapshot.History.Items);
        Assert.AreEqual(3L, snapshot.SnapshotCursor);

        var etag = controller.Response.Headers.ETag.ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(etag));

        var conditional = env.NewController("single-user");
        conditional.ControllerContext.HttpContext.Request.Headers["If-None-Match"] = etag;
        var notModified = await conditional.OpenAgentConversation(
            "ws", "agent-1", messageLimit: 20, CancellationToken.None);

        Assert.IsInstanceOfType<StatusCodeResult>(notModified.Result);
        Assert.AreEqual(StatusCodes.Status304NotModified, ((StatusCodeResult)notModified.Result!).StatusCode);
    }

    [TestMethod]
    public async Task OpenAgentConversation_UnknownAgent_Returns404()
    {
        using var env = new ControllerEnv();
        await env.SeedConversationAsync(messageCount: 1, checkpoint: 1, head: 1);

        var controller = env.NewController("single-user", useKnownAgent: false);
        var result = await controller.OpenAgentConversation(
            "ws", "missing-agent", messageLimit: null, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetHistory_InvalidCursor_ReturnsStableErrorCode()
    {
        using var env = new ControllerEnv();
        await env.SeedConversationAsync(messageCount: 1, checkpoint: 1, head: 1);

        var controller = env.NewController("single-user");
        var result = await controller.GetHistory(
            "conv-1", beforeCreatedAt: 1, beforeMessageId: null, limit: 10, CancellationToken.None);

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        StringAssert.Contains(JsonSerializer.Serialize(badRequest.Value), "invalid_cursor");
    }

    [TestMethod]
    public async Task GetProcess_ThroughAheadOfHead_ReturnsStableErrorCode()
    {
        using var env = new ControllerEnv();
        await env.SeedConversationAsync(messageCount: 1, checkpoint: 1, head: 1);

        var controller = env.NewController("single-user");
        var result = await controller.GetProcess(
            "conv-1", turnId: null, beforeSequence: null, throughSequence: 5, limit: 10, CancellationToken.None);

        var badRequest = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        StringAssert.Contains(JsonSerializer.Serialize(badRequest.Value), "invalid_cursor");
    }

    private sealed class ControllerEnv : IDisposable
    {
        private readonly string _root;
        private readonly SessionRedirectStore _redirects;
        private readonly FakeSessionRepository _sessions;
        private readonly FakeWorkspaceAgentCatalog _catalog;

        public ControllerEnv()
        {
            _root = Path.Combine(Path.GetTempPath(), "pudding-open-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")}")
                .Options;
            DbFactory = new PlatformDbContextFactory(Options);
            _redirects = new SessionRedirectStore(
                PuddingCode.Configuration.PuddingDataPaths.FromRoot(_root));
            QueryService = new ConversationOpenQueryService(
                DbFactory, NullLogger<ConversationOpenQueryService>.Instance);

            _sessions = new FakeSessionRepository();
            _sessions.Add(new SessionRecord
            {
                SessionId = "conv-1",
                WorkspaceId = "ws",
                AgentTemplateId = "tpl",
                ChannelId = "local",
                OwnerUserId = "single-user",
                SessionRole = SessionRole.Main,
                PrincipalKind = "agent",
                PrincipalId = "agent-1",
            });
            _catalog = new FakeWorkspaceAgentCatalog(new WorkspaceAgentDto(
                "agent-1",
                "name",
                null,
                null,
                null,
                null,
                null,
                "conv-1",
                null,
                null,
                null,
                true,
                false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public DbContextOptions<PlatformDbContext> Options { get; }
        public PlatformDbContextFactory DbFactory { get; }
        public ConversationOpenQueryService QueryService { get; }

        public async Task SeedConversationAsync(int messageCount, long checkpoint, long head)
        {
            await using var db = new PlatformDbContext(Options);
            await db.Database.EnsureCreatedAsync();
            db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
            {
                ConversationId = "conv-1",
                ProjectedThrough = checkpoint,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = head });
            db.ConversationReadStates.Add(new ConversationReadStateEntity
            {
                ConversationId = "conv-1",
                InitializationState = (int)ReadModelInitializationState.Ready,
                ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            for (var index = 1; index <= messageCount; index++)
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = $"msg-{index}",
                    SessionId = "conv-1",
                    WorkspaceId = "ws",
                    AgentInstanceId = "agent-1",
                    AgentTemplateId = "tpl",
                    Role = "agent",
                    Content = $"content-{index}",
                    CreatedAt = 1_000 + index,
                });
            }

            await db.SaveChangesAsync();
        }

        public ConversationOpenApiController NewController(
            string owner,
            bool useKnownAgent = true)
        {
            var catalog = useKnownAgent
                ? _catalog
                : new FakeWorkspaceAgentCatalog();
            var resolver = new AgentMainSessionResolver(
                _sessions,
                catalog,
                _redirects,
                NullLogger<AgentMainSessionResolver>.Instance);
            var controller = new ConversationOpenApiController(resolver, QueryService);
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, owner)], "Test"));
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            return controller;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of temp data outside the repo
            }
        }
    }
}
