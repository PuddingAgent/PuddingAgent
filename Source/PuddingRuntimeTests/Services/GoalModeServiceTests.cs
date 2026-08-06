using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingRuntime.Services.GoalMode;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// Goal 模式服务测试：默认关闭、消费式注入、熔断跳过、持久化、失败不推进。
/// </summary>
[TestClass]
public sealed class GoalModeServiceTests
{
    private sealed class FakeMessageSystem : IMessageSystem
    {
        public List<MessageEnvelope> Sent { get; } = new();
        public bool FailNextSend { get; set; }

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            if (FailNextSend)
                throw new InvalidOperationException("simulated send failure");

            Sent.Add(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = $"msg-{Sent.Count}",
                RoomId = envelope.RoomId,
                DeliveryIds = new[] { $"dlv-{Sent.Count}" },
            });
        }
    }

    private static readonly GoalModeOptions EnabledOptions = new() { Enabled = true, MaxInjectionsPerGoal = 3 };

    private string _tempRoot = "";

    [TestInitialize]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"goal-mode-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 测试目录清理失败不影响结果 */ }
    }

    private GoalModeService CreateService(FakeMessageSystem messageSystem, GoalModeOptions? options = null) =>
        new(
            Options.Create(options ?? EnabledOptions),
            PuddingDataPaths.FromRoot(_tempRoot),
            messageSystem,
            NullLogger<GoalModeService>.Instance);

    private static async Task SeedQueueAsync(GoalModeService service, string agentId, params string[] titles)
    {
        foreach (var title in titles)
            Assert.IsTrue(await service.EnqueueGoalAsync(agentId, title, detail: null, CancellationToken.None));
    }

    // ── 开关与空队列 ──

    [TestMethod]
    public async Task Disabled_NoInjection_EvenWithQueuedGoals()
    {
        var fake = new FakeMessageSystem();
        var enabledService = CreateService(fake);
        await SeedQueueAsync(enabledService, "agent-1", "goal-a");

        var disabledService = CreateService(fake, new GoalModeOptions { Enabled = false });
        var injected = await disabledService.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None);

        Assert.IsFalse(injected);
        Assert.AreEqual(0, fake.Sent.Count);
    }

    [TestMethod]
    public async Task Enabled_EmptyQueue_NoInjection()
    {
        var fake = new FakeMessageSystem();
        var service = CreateService(fake);

        var injected = await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None);

        Assert.IsFalse(injected);
        Assert.AreEqual(0, fake.Sent.Count);
    }

    // ── 消费式注入 ──

    [TestMethod]
    public async Task Enabled_InjectsGoalsInOrder_ThenDrains()
    {
        var fake = new FakeMessageSystem();
        var service = CreateService(fake);
        await SeedQueueAsync(service, "agent-1", "goal-a", "goal-b");

        Assert.IsTrue(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));
        Assert.IsTrue(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));
        Assert.IsFalse(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));

        Assert.AreEqual(2, fake.Sent.Count);
        StringAssert.Contains(fake.Sent[0].Content, "goal-a");
        StringAssert.Contains(fake.Sent[1].Content, "goal-b");

        var first = fake.Sent[0];
        Assert.AreEqual("system", first.From.Kind);
        Assert.AreEqual("goal", first.From.Id);
        Assert.AreEqual("agent-1", first.To.Single().Id);
        Assert.AreEqual("goal-mode", first.Metadata["source"]);
    }

    // ── 熔断：超注入上限的目标被跳过 ──

    [TestMethod]
    public async Task GoalExceedingInjectionCap_IsSkipped()
    {
        var fake = new FakeMessageSystem();
        var service = CreateService(fake);
        await SeedQueueAsync(service, "agent-1", "stuck-goal", "next-goal");

        // 模拟重试历史：游标仍在目标 0，但其注入次数已达上限
        // （例如持久化失败后重放、或外部重置游标）
        var queuePath = Path.Combine(
            PuddingDataPaths.FromRoot(_tempRoot).AgentInstanceRoot("agent-1"),
            "goal_queue.json");
        var json = await File.ReadAllTextAsync(queuePath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["goals"]!.AsArray()[0]!.AsObject()["injection_count"] = 3;
        await File.WriteAllTextAsync(queuePath, root.ToJsonString());

        // 注入时 stuck-goal 达上限被跳过，直接注入 next-goal
        Assert.IsTrue(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));
        Assert.AreEqual(1, fake.Sent.Count);
        StringAssert.Contains(fake.Sent[0].Content, "next-goal");

        // 队列排空
        Assert.IsFalse(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));

        // 状态文件中 stuck-goal 被标记 skipped
        var finalJson = await File.ReadAllTextAsync(queuePath);
        StringAssert.Contains(finalJson, "\"status\": \"skipped\"");
    }

    // ── 持久化：重启（新实例）后游标保留 ──

    [TestMethod]
    public async Task CursorPersisted_NewServiceInstanceContinuesFromDisk()
    {
        var fake = new FakeMessageSystem();
        var firstService = CreateService(fake);
        await SeedQueueAsync(firstService, "agent-1", "goal-a", "goal-b");
        Assert.IsTrue(await firstService.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));

        // 模拟重启：全新实例读取同一份 goal_queue.json
        var secondService = CreateService(fake);
        Assert.IsTrue(await secondService.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));
        Assert.IsFalse(await secondService.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));

        Assert.AreEqual(2, fake.Sent.Count);
        StringAssert.Contains(fake.Sent[1].Content, "goal-b");
    }

    // ── 失败安全：发送失败不推进游标 ──

    [TestMethod]
    public async Task SendFailure_DoesNotAdvanceCursor_RetriesSameGoal()
    {
        var fake = new FakeMessageSystem();
        var service = CreateService(fake);
        await SeedQueueAsync(service, "agent-1", "goal-a", "goal-b");

        fake.FailNextSend = true;
        Assert.IsFalse(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));

        fake.FailNextSend = false;
        Assert.IsTrue(await service.TryInjectNextGoalAsync("ws-1", "agent-1", CancellationToken.None));
        Assert.AreEqual(1, fake.Sent.Count);
        StringAssert.Contains(fake.Sent[0].Content, "goal-a");
    }

    // ── 队列长度上限 ──

    [TestMethod]
    public async Task EnqueueGoal_RespectsMaxLength()
    {
        var fake = new FakeMessageSystem();
        var service = CreateService(fake, new GoalModeOptions
        {
            Enabled = true,
            MaxQueueLength = 2,
        });

        Assert.IsTrue(await service.EnqueueGoalAsync("agent-1", "g1", null, CancellationToken.None));
        Assert.IsTrue(await service.EnqueueGoalAsync("agent-1", "g2", null, CancellationToken.None));
        Assert.IsFalse(await service.EnqueueGoalAsync("agent-1", "g3", null, CancellationToken.None));
    }
}