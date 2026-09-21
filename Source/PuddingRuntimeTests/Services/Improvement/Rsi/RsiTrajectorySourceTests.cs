using PuddingCode.Platform;
using PuddingRuntime.Services.Improvement.Rsi;

namespace PuddingRuntimeTests.Services.Improvement.Rsi;

/// <summary>
/// RSI S3 增量 B3 主用例（任务书 §5.2）：用 fake <see cref="IRsiTrajectoryDataAccess"/> 记录调用，
/// 钉住编排层的冻结契约 —— 盖章不推导（S15）、分组顺序、零访问边界、事件类型三类、null scope 抛出。
/// <para>每条用例都对应一个真实的实现错误；变异方式写在各用例注释里（变异红证据见交付报告）。</para>
/// </summary>
[TestClass]
public sealed class RsiTrajectorySourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- 构造辅助

    private static RsiScope Scope(
        string conversationId = "conv-1",
        string workspaceId = "ws-in",
        string agentInstanceId = "agent-in",
        string sessionId = "sess-in") => new(workspaceId, agentInstanceId, sessionId, conversationId);

    private static RsiEventRowWithTurn EventRow(
        string turnId, long sequence, string type, string? payload = null) => new()
    {
        TurnId = turnId,
        Type = type,
        Sequence = sequence,
        Payload = payload ?? "{}",
        OccurredAtUtc = T0.AddMinutes(sequence),
    };

    /// <summary>记录调用的 fake：两条接缝的调用计数与实参都可断言。</summary>
    private sealed class FakeDataAccess : IRsiTrajectoryDataAccess
    {
        public int RecentTurnIdsCalls { get; private set; }

        public int GetEventsCalls { get; private set; }

        public string? LastConversationId { get; private set; }

        public int LastLimit { get; private set; }

        public string[]? LastTurnIds { get; private set; }

        public string[]? LastEventTypes { get; private set; }

        public IReadOnlyList<string> TurnIdsToReturn { get; set; } = [];

        public IReadOnlyList<RsiEventRowWithTurn> EventsToReturn { get; set; } = [];

        public Task<IReadOnlyList<string>> GetRecentTurnIdsAsync(
            string conversationId, int limit, CancellationToken ct)
        {
            RecentTurnIdsCalls++;
            LastConversationId = conversationId;
            LastLimit = limit;
            return Task.FromResult(TurnIdsToReturn);
        }

        public Task<IReadOnlyList<RsiEventRowWithTurn>> GetEventsByTurnIdsAsync(
            string[] turnIds, string[] eventTypes, CancellationToken ct)
        {
            GetEventsCalls++;
            LastTurnIds = turnIds;
            LastEventTypes = eventTypes;
            return Task.FromResult(EventsToReturn);
        }
    }

    // ---------------------------------------------------------------- S15

    /// <summary>
    /// S15（规格 §2.11，必须能红）：入参 SessionId / AgentInstanceId 取与任何数据都不同的值，
    /// 断言输出轨迹的三个身份字段逐字等于入参。
    /// 变异：把盖章改成从数据推导（例如拿 ConversationId 或 turnId 充当 SessionId）⇒ 本用例必红。
    /// </summary>
    [TestMethod]
    public async Task S15_StampingVerbatimFromScope_NeverDerivesFromData()
    {
        var fake = new FakeDataAccess
        {
            TurnIdsToReturn = ["turn-1"],
            EventsToReturn =
            [
                EventRow("turn-1", 1, ConversationEventTypes.ToolCallRequested, """{"name":"shell"}"""),
                EventRow("turn-1", 2, ConversationEventTypes.ToolCallCompleted, """{"exitCode":0}"""),
            ],
        };
        var source = new RsiTrajectorySource(fake);
        var scope = new RsiScope(
            WorkspaceId: "scope-workspace-marker",
            AgentInstanceId: "scope-agent-marker",
            SessionId: "scope-session-marker",
            ConversationId: "conv-1");

        var trajectories = await source.GetRecentAnnotatedAsync(scope, 5, CancellationToken.None);

        Assert.AreEqual(1, trajectories.Count);
        Assert.AreEqual("scope-workspace-marker", trajectories[0].WorkspaceId, "WorkspaceId 必须逐字等于入参。");
        Assert.AreEqual("scope-agent-marker", trajectories[0].AgentInstanceId, "AgentInstanceId 必须逐字等于入参。");
        Assert.AreEqual("scope-session-marker", trajectories[0].SessionId, "SessionId 必须逐字等于入参。");
    }

    // ---------------------------------------------------------------- 分组与顺序

    /// <summary>
    /// 两个 turn 的事件交错返回 ⇒ 输出按 turnIds（时间升序）顺序，组内按 Sequence 升序。
    /// 变异：分组顺序改成按字典序 / 按 rows 返回顺序 ⇒ 输出轨迹顺序必红。
    /// </summary>
    [TestMethod]
    public async Task MultiTurn_GroupsFollowTurnIdOrder_AndSequenceWithinGroup()
    {
        var fake = new FakeDataAccess
        {
            TurnIdsToReturn = ["turn-a", "turn-b"],
            // 交错返回（模拟 DB 侧乱序到达）：b#20, a#10, b#21, a#11, b#22；
            // b#22 是 requested（不产 step，验证 requested 不参与 steps）。
            EventsToReturn =
            [
                EventRow("turn-b", 20, ConversationEventTypes.ToolCallCompleted, """{"exitCode":0}"""),
                EventRow("turn-a", 10, ConversationEventTypes.ToolCallCompleted, """{"exitCode":0}"""),
                EventRow("turn-b", 21, ConversationEventTypes.ToolCallFailed, """{"exitCode":1,"error":"boom"}"""),
                EventRow("turn-a", 11, ConversationEventTypes.ToolCallCompleted, """{"exitCode":0}"""),
                EventRow("turn-b", 22, ConversationEventTypes.ToolCallRequested, """{"name":"fs.read"}"""),
            ],
        };
        var source = new RsiTrajectorySource(fake);

        var trajectories = await source.GetRecentAnnotatedAsync(
            Scope(), 5, CancellationToken.None);

        Assert.AreEqual(2, trajectories.Count);
        Assert.AreEqual("turn-a", trajectories[0].TurnId, "输出顺序必须跟随 turnIds（时间升序），不是字典序也不是返回序。");
        Assert.AreEqual("turn-b", trajectories[1].TurnId);
        CollectionAssert.AreEqual(
            new long[] { 10, 11 },
            trajectories[0].Steps.Select(s => s.Sequence).ToArray(),
            "组内 steps 必须按 Sequence 升序。");
        CollectionAssert.AreEqual(
            new long[] { 20, 21 },
            trajectories[1].Steps.Select(s => s.Sequence).ToArray(),
            "组内 steps 必须按 Sequence 升序。");
    }

    // ---------------------------------------------------------------- 零访问边界

    /// <summary>
    /// limit &lt;= 0 与空 / 空白 ConversationId ⇒ 返回 [] 且 fake 记录到 0 次调用（不访问数据库）。
    /// 变异：把边界早退删掉（直接放行到数据访问）⇒ 调用数断言必红。
    /// </summary>
    [TestMethod]
    public async Task Guard_ZeroOrNegativeLimit_OrBlankConversationId_ReturnsEmptyWithoutDataAccess()
    {
        var fake = new FakeDataAccess { TurnIdsToReturn = ["turn-1"] };
        var source = new RsiTrajectorySource(fake);

        Assert.AreEqual(0, (await source.GetRecentAnnotatedAsync(Scope(), 0, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await source.GetRecentAnnotatedAsync(Scope(), -1, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await source.GetRecentAnnotatedAsync(Scope(conversationId: ""), 5, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await source.GetRecentAnnotatedAsync(Scope(conversationId: "   "), 5, CancellationToken.None)).Count);

        Assert.AreEqual(0, fake.RecentTurnIdsCalls, "边界入参不得触发 GetRecentTurnIdsAsync。");
        Assert.AreEqual(0, fake.GetEventsCalls, "边界入参不得触发 GetEventsByTurnIdsAsync。");
    }

    /// <summary>无工具事件（只有 MessageContentAppended）⇒ []（装配器对零工具步的 turn 不产出轨迹）。</summary>
    [TestMethod]
    public async Task NoToolEvents_ReturnsEmpty()
    {
        var fake = new FakeDataAccess
        {
            TurnIdsToReturn = ["turn-1"],
            EventsToReturn =
            [
                EventRow("turn-1", 1, ConversationEventTypes.MessageContentAppended, """{"text":"noise"}"""),
            ],
        };
        var source = new RsiTrajectorySource(fake);

        var trajectories = await source.GetRecentAnnotatedAsync(Scope(), 5, CancellationToken.None);

        Assert.AreEqual(0, trajectories.Count);
    }

    // ---------------------------------------------------------------- 事件类型三类

    /// <summary>传给数据访问的 eventTypes 必须恰为三类工具事件（requested / completed / failed）。</summary>
    [TestMethod]
    public async Task EventTypesPassed_AreExactlyTheThreeToolKinds()
    {
        var fake = new FakeDataAccess
        {
            TurnIdsToReturn = ["turn-1"],
            EventsToReturn =
            [
                EventRow("turn-1", 1, ConversationEventTypes.ToolCallCompleted, """{"exitCode":0}"""),
            ],
        };
        var source = new RsiTrajectorySource(fake);

        await source.GetRecentAnnotatedAsync(Scope(), 5, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[]
            {
                ConversationEventTypes.ToolCallRequested,
                ConversationEventTypes.ToolCallCompleted,
                ConversationEventTypes.ToolCallFailed,
            },
            fake.LastEventTypes,
            "事件类型参数必须恰为 requested / completed / failed 三类（§2.4）。");
        CollectionAssert.AreEqual(
            new[] { "turn-1" },
            fake.LastTurnIds,
            "turnIds 必须按时间升序原样传给取事件接缝。");
    }

    // ---------------------------------------------------------------- null scope

    /// <summary>scope 为 null ⇒ ArgumentNullException（边界层允许抛，不得静默成空结果）。</summary>
    [TestMethod]
    public async Task NullScope_ThrowsArgumentNullException()
    {
        var source = new RsiTrajectorySource(new FakeDataAccess());

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await source.GetRecentAnnotatedAsync(null!, 5, CancellationToken.None));
    }
}
