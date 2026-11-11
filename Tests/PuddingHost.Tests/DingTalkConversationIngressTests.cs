using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingDingTalk;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Execution;

namespace PuddingHost.Tests;

public sealed class DingTalkConversationIngressTests
{
    [Fact]
    public async Task TwoMembersShareADurableAgentBranchAndTheImageReachesCanonicalAcceptance()
    {
        var dir = Directory.CreateTempSubdirectory("ding-room-test-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        try
        {
            var paths = PuddingDataPaths.FromRoot(dir.FullName);
            const string agent = "whale";
            Directory.CreateDirectory(paths.AgentInstanceRoot(agent));
            await File.WriteAllTextAsync(Path.Combine(paths.AgentInstanceRoot(agent), "manifest.json"),
                JsonSerializer.Serialize(new { agentInstanceId = agent, templateId = "global:general-assistant",
                    workspaceId = "test", isEnabled = true }), ct);
            var sessions = new InMemorySessionRepository(paths);
            await sessions.CreateAsync(new() { SessionId = "private-main", WorkspaceId = "test",
                ChannelId = "desktop", OwnerUserId = "owner", AgentInstanceId = agent, AgentTemplateId = "global:general-assistant",
                SessionRole = SessionRole.Main, PrincipalKind = "agent", PrincipalId = agent }, ct);
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<PlatformDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir.FullName, "test.db")};Pooling=false"));
            var artifacts = new VisionArtifactStorageService(paths, NullLogger<VisionArtifactStorageService>.Instance);
            services.AddSingleton<IVisualArtifactLocalFileResolver>(artifacts);
            services.AddSingleton<ICommittedEventSignal, CommittedEventSignal>();
            services.AddScoped<IConversationAcceptanceStore, ConversationAcceptanceStore>();
            services.AddScoped<SubmitTurnHandler>();
            services.AddScoped<ISubmitTurnHandler, DingTalkSubmitGuard>();
            services.AddSingleton<IExecutionLeaseStore, SqliteExecutionLeaseStore>();
            services.AddSingleton<IExecutionJournal, SqliteExecutionJournal>();
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            using var http = new HttpClient(new FixtureMedia());
            var now = DateTimeOffset.UtcNow;
            var source = new GroupSource("enterprise", "room", "first-member", now, now);
            // 单一会话：锁钉群消息汇入该 Agent 的唯一主会话，而不是渠道自有会话。
            const string sessionId = "private-main";
            var leaseStore = provider.GetRequiredService<IExecutionLeaseStore>();
            var journal = provider.GetRequiredService<IExecutionJournal>();
            foreach (var message in new[] { new PublicQuestion(source, "截图", "one")
                { ImageDownloadCodes = ["provider-only"], GroupTitle = "示例群" },
                new PublicQuestion(source with { UserId = "second-member" }, "接上条", "two") })
            {
                var ingress = new DingTalkConversationIngress(new(paths), agent, "robot",
                    new InMemorySessionRepository(paths), provider.GetRequiredService<IServiceScopeFactory>(),
                    artifacts, new(http, "robot", "fixture"), null);
                var receiving = ingress.ReplyAsync(message, ct);
                ExecutionLease? lease;
                while ((lease = await leaseStore.TryAcquireAsync("test-worker", TimeSpan.FromMinutes(1), ct)) is null)
                {
                    if (receiving.IsCompleted) Assert.Fail("Turn did not enter the queue: " + (await receiving).Text);
                    await Task.Delay(20, ct);
                }
                Assert.Equal(sessionId, lease.ConversationId);
                var row = await db.ChatMessages.AsNoTracking().SingleAsync(m => m.SessionId == sessionId &&
                    m.MessageId == new DingTalkConversationIngress.Sender("robot", agent, message.Source, message.MessageId).Input, ct);
                Assert.DoesNotContain("provider-only", row.MetadataJson);
                var answer = "同群答复";
                if (message.MessageId == "one")
                {
                    var image = Assert.Single(ContentPartsEnvelope.Decode(row.ContentPartsJson)!.OfType<LlmImagePart>());
                    Assert.NotNull(await artifacts.ResolveLocalFileAsync("test", image.ArtifactId, ct));
                    answer += "\n```image\n" + image.ArtifactId + "\n```";
                }
                await journal.StartRunAsync(lease, "snapshot",
                    new(Guid.NewGuid().ToString("N"), ConversationEventTypes.TurnStarted, 1, "test", lease.TurnId,
                        lease.CommandId, lease.RunId, null, null, null, null, JsonSerializer.SerializeToElement(new { })), ct);
                await journal.CommitTerminalAsync(lease, TurnTerminal.Success(answer, null), [], ct);
                var reply = await receiving;
                Assert.Equal("同群答复", reply.Text);
                Assert.Equal(message.MessageId == "one" ? 1 : 0, reply.Images?.Count ?? 0);
            }
            var reloaded = new InMemorySessionRepository(paths);
            var main = Assert.Single(await reloaded.QueryAsync(channelId: "desktop", workspaceId: "test", ct: ct));
            Assert.Equal("private-main", main.SessionId);
            Assert.Equal("agent", main.PrincipalKind);
            Assert.Equal(2, await db.ChatExecutionCommands.CountAsync(c => c.SessionId == "private-main" && c.Status == "succeeded", ct));
            // 单一会话后不再产生渠道自有会话。
            Assert.Empty(await reloaded.QueryAsync(channelId: "dingtalk", workspaceId: "test", ct: ct));
        }
        finally { dir.Delete(recursive: true); }
    }

    private sealed class FixtureMedia : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.RequestUri!.AbsolutePath switch
            {
                "/v1.0/oauth2/accessToken" => (HttpContent)JsonContent.Create(new { accessToken = "fixture", expireIn = 600 }),
                "/v1.0/robot/messageFiles/download" => JsonContent.Create(new { downloadUrl = "https://media.dingtalk.com/test.png" }),
                "/test.png" => new ByteArrayContent(Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6ioAAAAASUVORK5CYII=")),
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
