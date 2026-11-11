using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingDingTalk;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;
using System.Text.Json;

namespace PuddingHost.Tests;

public sealed class DingTalkPublicAgentTests
{
    [Theory]
    [InlineData("mpcal_public_help")]
    [InlineData("mpcal_record_feedback")]
    [InlineData("shell")]
    public async Task ActualLoopLoadsAgentToolsButOnlyExecutesAuthorizedGuestActions(string tool)
    {
        var dir = Directory.CreateTempSubdirectory("pudding-dingtalk-test-");
        try
        {
            var paths = PuddingDataPaths.FromRoot(dir.FullName);
            const string id = "test-whale";
            var agentRoot = Directory.CreateDirectory(paths.AgentInstanceRoot(id));
            await File.WriteAllTextAsync(Path.Combine(agentRoot.FullName, "manifest.json"),
                JsonSerializer.Serialize(new { agentInstanceId = id, templateId = "global:general-assistant",
                    workspaceId = "test", isEnabled = true, agentsMdFile = "AGENTS.md",
                    preferredProviderId = "demo", preferredModelId = "test-model" }));
            await File.WriteAllTextAsync(Path.Combine(agentRoot.FullName, "AGENTS.md"),
                "<!-- pudding:dingtalk-group:v1:begin -->公开规则<!-- pudding:dingtalk-group:v1:end -->private-canary-736");
            var cfg = new Mock<ILlmConfigService>();
            cfg.Setup(c => c.Resolve("demo","test-model")).Returns(new LlmConfig { ModelId = "test-model",
                MaxContextTokens = 16384, MaxOutputTokens = 800 });
            var model = new Mock<IRuntimeLlmClient>(MockBehavior.Strict);
            var calls = 0;
            var schemas = new List<string[]>();
            var history = new List<ChatMessage>();
            model.Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<IReadOnlyList<LlmToolDefinition>?>(),
                It.IsAny<LlmConfig?>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string session, string template, IReadOnlyList<ChatMessage> messages,
                    IReadOnlyList<LlmToolDefinition>? tools, LlmConfig? config, CancellationToken ct) =>
                {
                    schemas.Add(tools!.Select(t => t.Name).Order().ToArray());
                    history.AddRange(messages);
                    calls++;
                    return Task.FromResult(calls == 1 ? new LlmResponse(null, [new("demo-call", tool, "{}")]) :
                        new LlmResponse("{\"status\":\"DONE\",\"message\":\"单轮合成回复\"}", null));
                });
            using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            var inbox = Path.Combine(dir.FullName, "feedback");
            var catalog = new Mock<PuddingCode.Tools.IPuddingToolRegistry>();
            catalog.Setup(r => r.ContextualToolNames(It.IsAny<PuddingCode.Tools.ToolExecutionContext>())).Returns(["shell"]);
            catalog.Setup(r => r.ContextualPolicyOwners(It.IsAny<PuddingCode.Tools.ToolExecutionContext>()))
                .Returns(new Dictionary<string, string>());
            catalog.Setup(r => r.ListAvailable(It.IsAny<CapabilityPolicy?>(), It.IsAny<PuddingCode.Tools.ToolExecutionContext>()))
                .Returns([new PuddingCode.Tools.ToolDescriptor { ToolId = "shell", Name = "shell", Description = "Agent shell" }]);
            var agent = new DingTalkPublicAgent(new(paths), paths, id, model.Object, cfg.Object, null,
                new(inbox), services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance, new AgentExecutionStateRegistry(), new PuddingRuntime.Services.Tools.PuddingToolSchemaService(catalog.Object));
            var now = DateTimeOffset.UtcNow;
            var msg = new PublicQuestion(new("corp", "group", "employee", now, now),
                "登记一个软件BUG，在小屏幕的显示屏上，软件显示错位。reference-736", "message");
            var reply = (await agent.RunAsync(msg, "ding-group-test", [], [], null, default)).ReplyText;
            Assert.Equal(2, calls);
            Assert.All(schemas, names => Assert.Equal(new[] { "mpcal_public_help","mpcal_record_feedback","shell" }, names));
            Assert.DoesNotContain(history, m => (m.Content ?? "").Contains("private-canary-736"));
            Assert.Contains(history, m => m.Role == ChatRole.Tool);
            if (tool == "mpcal_record_feedback")
            {
                Assert.Contains("已登记BUG反馈", reply);
                Assert.Contains("显示错位", await File.ReadAllTextAsync(Directory.GetFiles(inbox).Single()));
            }
            else
            {
                Assert.False(Directory.Exists(inbox));
                Assert.Equal(tool == "mpcal_public_help" ? "单轮合成回复" : PublicGroupAnswers.Refusal, reply);
            }
            if (tool == "mpcal_public_help")
            {
                var previous = new DingTalkConversationIngress.Sender("app", id, msg.Source, "first");
                // 单一会话：会话 ID 不再由发送者派生。
                const string session = "main";
                ChatMessageRow Row(DingTalkConversationIngress.Sender sender) => new() { Id = 1, Role = "user",
                    SessionId = session, WorkspaceId = "test", MessageId = sender.Input, Content = "reference-736",
                    MetadataJson = JsonSerializer.Serialize(new Dictionary<string,string> {
                        [DingTalkConversationIngress.Provenance] = JsonSerializer.Serialize(sender) }) };
                var current = new ChatMessageRow { Id = 2, SessionId = session, WorkspaceId = "test" };
                var replies = new Dictionary<string,string> { [previous.Input] = """{"reply":"previous-reply"}""" };
                var persisted = DingTalkTurnExecutor.PublicHistory([Row(previous)], current, replies);
                history.Clear();
                await agent.RunAsync(msg with { Text = "接着说", MessageId = "next" }, session, persisted, [], null, default);
                Assert.Contains(history, m => m.Role == ChatRole.User && (m.Content ?? "").Contains("previous-reply"));
                // 单一会话：不再由发送者派生会话 ID，同一群的所有成员共用同一主会话。
                var other = previous with { Source = msg.Source with { ConversationId = "another-room" } };
                Assert.Empty(DingTalkTurnExecutor.PublicHistory([Row(other)], current, replies));
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void OnlyTheVerifiedRoomActorAndAgentCanSubmit()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new DingTalkConversationIngress.Sender("robot", "agent", new("org","room","staff",now,now), "message");
        var command = new SubmitTurnCommand("main", "w", source.Actor, source.Input, source.Input,
            new() { Type = "agent", AgentIds = ["agent"] }, [], new Dictionary<string,string>
            { [DingTalkConversationIngress.Provenance] = JsonSerializer.Serialize(source) }) { IsTrustedGatewayIngress = true };
        DingTalkSubmitGuard.Validate(command);
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command with { IsTrustedGatewayIngress = false }));
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command with { UserId = "admin" }));
        Assert.Throws<UnauthorizedAccessException>(() => DingTalkSubmitGuard.Validate(command with { Metadata = null }));
    }

    [Fact]
    public void OutgoingImageMustBeAnArtifactBoundToTheCurrentMessage()
    {
        var id = "vision-" + new string('a', 32);
        var reply = "截图\n```image\n" + id + "\n```";
        var good = DingTalkConversationIngress.ProjectReply(reply, new Dictionary<string, GroupImage> { [id] = new([1], "image/png") });
        Assert.Equal("截图", good.Text);
        Assert.Single(good.Images!);
        Assert.Null(DingTalkConversationIngress.ProjectReply(reply, new Dictionary<string, GroupImage>()).Images);
        var inline = DingTalkPublicMedia.NormalizeReply($"产品图标\n![MPCAL 产品图标](artifact://{id})");
        var mapped = DingTalkConversationIngress.ProjectReply(inline, new Dictionary<string, GroupImage> { [id] = new([2], "image/png") });
        Assert.Single(mapped.Images!);
        Assert.Equal("产品图标", mapped.Text);
        Assert.Null(DingTalkConversationIngress.ProjectReply(inline, new Dictionary<string, GroupImage>()).Images);
    }
}
