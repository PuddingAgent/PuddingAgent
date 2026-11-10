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
    [InlineData("unregistered_demo_operation")]
    public async Task ActualLoopBindsOnlyPublicToolsAndConsumesTheirResults(string tool)
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
            using var agent = new DingTalkPublicAgent(new(paths), paths, id, model.Object, cfg.Object, null,
                new(inbox), services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance, new AgentExecutionStateRegistry());
            var now = DateTimeOffset.UtcNow;
            var msg = new PublicQuestion(new("corp", "group", "employee", now, now),
                "登记一个软件BUG，在小屏幕的显示屏上，软件显示错位。reference-736", "message");
            var reply = await agent.ReplyAsync(msg, default);
            Assert.Equal(2, calls);
            Assert.All(schemas, names => Assert.Equal(new[] { "mpcal_public_help","mpcal_record_feedback" }, names));
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
                var followUp = msg with { Text = "还能继续追问吗？", MessageId = "follow-up" };
                history.Clear();
                await agent.ReplyAsync(followUp, default);
                Assert.Contains(history, m => m.Role == ChatRole.User && (m.Content ?? "").Contains("reference-736"));
                foreach (var other in new[] { msg.Source with { UserId = "other" },
                    msg.Source with { ConversationId = "other-room" } })
                {
                    history.Clear();
                    await agent.ReplyAsync(followUp with { Source = other }, default);
                    Assert.DoesNotContain(history, m => (m.Content ?? "").Contains("reference-736"));
                }
            }
        }
        finally { dir.Delete(recursive: true); }
    }
}
