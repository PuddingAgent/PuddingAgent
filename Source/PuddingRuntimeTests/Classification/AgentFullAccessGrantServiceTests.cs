using System.Globalization;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// <see cref="AgentFullAccessGrantService"/> 的离线单测（方案 v2 §14.5–§14.8，切片 S5a）。
/// <para>
/// 覆盖映射：正常授予与审计（01）、TTL 到期失效 + Expired 审计（02）、到期审计幂等只落一次（03）、
/// 301 秒拒绝且绝不截断（04）、作用域隔离（05）、非放行裁决 fail-closed（06）、
/// 撤销与重复撤销幂等（07）、未到期有效 / 恰好到期失效边界（08）、OperationCanceledException 传播（09）、
/// 零/负时长按默认 300 处理（10）、审计故障 fail-closed 回滚（11）、服务端重算绝对时刻（12）。
/// 全部零网络：注入假时钟与内存审计 store，不接触任何真实模型。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentFullAccessGrantServiceTests
{
    private const string WorkspaceA = "ws-a";
    private const string WorkspaceB = "ws-b";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // —— 01 正常授予：GetActiveAsync 可见，TTL / 过期时刻由服务端计时，Requested + Granted 审计必落 ——

    [TestMethod]
    public async Task GrantAsync_AllowOnceWithinTtl_GrantsAndAudits()
    {
        var (service, audit, _) = CreateService();

        var granted = await service.GrantAsync(GrantRequest());

        Assert.IsNotNull(granted, "放行类结论 + 合法 TTL 必须授予成功。");
        Assert.AreEqual(T0, granted.GrantedAtUtc, "GrantedAtUtc 必须由服务端时钟重算。");
        Assert.AreEqual(T0.AddSeconds(300), granted.ExpiresAtUtc, "默认 TTL 必须=服务端 now+300s。");

        var active = await service.GetActiveAsync(WorkspaceA, AgentA);
        Assert.IsNotNull(active, "授予后必须可查询到。");
        Assert.AreEqual(granted.GrantId, active.GrantId);
        Assert.AreEqual(T0.AddSeconds(300), active.ExpiresAtUtc, "查询到的过期时刻必须与授予一致（读取不刷新时间）。");

        var events = await EventsAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessRequested), "每次请求必落 Requested。");
        var grantedEvent = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted);
        Assert.IsTrue(grantedEvent.Reason!.Contains("ttl_seconds=300", StringComparison.Ordinal), $"Granted 审计必须写明 ttl 秒数，实际：{grantedEvent.Reason}");
        Assert.IsTrue(
            grantedEvent.Reason.Contains($"expires_at_utc={granted.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture)}", StringComparison.Ordinal),
            $"Granted 审计必须写明过期时刻（UTC），实际：{grantedEvent.Reason}");
        Assert.IsTrue(grantedEvent.Reason.Contains("classifier_id=llm-safety-v1", StringComparison.Ordinal), "Granted 审计必须写明分类器 id。");
        Assert.IsTrue(grantedEvent.Reason.Contains("outcome=AllowOnce", StringComparison.Ordinal), "Granted 审计必须写明裁决结论。");
        Assert.AreEqual(WorkspaceA, grantedEvent.WorkspaceId);
        Assert.AreEqual(AgentA, grantedEvent.AgentInstanceId);
    }

    // —— 02 时钟推进超过 TTL ⇒ 失效 ⇒ GetActiveAsync 返回空 + 落 FullAccessExpired ——

    [TestMethod]
    public async Task GetActiveAsync_AfterTtlElapsed_ReturnsNullAndAuditsExpired()
    {
        var (service, audit, clock) = CreateService();
        await service.GrantAsync(GrantRequest());

        clock.Advance(TimeSpan.FromSeconds(301));

        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "TTL 由服务端计时，超时后必须失效。");
        var events = await EventsAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired), "被观测到失效时必须落 Expired 审计。");
        var expiredEvent = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired);
        Assert.AreEqual(WorkspaceA, expiredEvent.WorkspaceId);
        Assert.AreEqual(AgentA, expiredEvent.AgentInstanceId);
    }

    // —— 03 到期审计只落一次：重复查询不再重复落（幂等标记） ——

    [TestMethod]
    public async Task GetActiveAsync_RepeatedQueriesAfterExpiry_AuditExpiredOnlyOnce()
    {
        var (service, audit, clock) = CreateService();
        await service.GrantAsync(GrantRequest());
        clock.Advance(TimeSpan.FromSeconds(300));

        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "第一次查询触发到期判定。");
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "第二次查询仍为空。");
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "第三次查询仍为空。");

        var events = await EventsAsync(audit);
        Assert.AreEqual(
            1,
            events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired),
            "同一份授予的 Expired 审计只允许落一次（重复读取不得重复落）。");
    }

    // —— 04 请求 301 秒 ⇒ 拒绝（typed 异常 + Denied 审计），绝无截断到 300 的授予 ——

    [TestMethod]
    public async Task GrantAsync_Duration301Seconds_RejectsWithoutTruncation()
    {
        var (service, audit, _) = CreateService();

        var ex = await Assert.ThrowsAsync<AgentFullAccessGrantRejectedException>(
            () => service.GrantAsync(GrantRequest(durationSeconds: 301)));

        Assert.AreEqual(AgentFullAccessGrantService.ReasonDurationExceeded, ex.ReasonCode, "超限必须给出 typed 原因码。");
        Assert.IsTrue(ex.Message.Contains("301", StringComparison.Ordinal), $"拒绝理由必须写明请求时长，实际：{ex.Message}");

        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "拒绝 ⇒ 绝不发生截断到 300 的授予。");
        var events = await EventsAsync(audit);
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted), "拒绝 ⇒ 不得有 Granted 审计。");
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessRequested), "被拒请求同样先落 Requested。");
        var denied = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessDenied);
        Assert.IsTrue(
            denied.Reason!.StartsWith(AgentFullAccessGrantService.ReasonDurationExceeded, StringComparison.Ordinal),
            $"Denied 审计必须写明超限原因码，实际：{denied.Reason}");
        Assert.AreEqual(ToolApprovalDecision.Denied, denied.Decision);
    }

    // —— 05 作用域隔离：agent A 的授予不影响 agent B；不同 workspace 同理 ——

    [TestMethod]
    public async Task GrantScope_IsolatedAcrossAgentsAndWorkspaces()
    {
        var (service, audit, _) = CreateService();
        var grant = await service.GrantAsync(GrantRequest());

        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentB), "同 workspace 不同 agent 必须隔离。");
        Assert.IsNull(await service.GetActiveAsync(WorkspaceB, AgentA), "同 agent 不同 workspace 必须隔离。");
        var stillActive = await service.GetActiveAsync(WorkspaceA, AgentA);
        Assert.IsNotNull(stillActive, "本体作用域不受隔离查询影响。");
        Assert.AreEqual(grant.GrantId, stillActive.GrantId);
        Assert.AreEqual(
            0,
            (await EventsAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired),
            "隔离查询不得误触发到期审计。");

        // 撤销 A 只影响 A：B 侧本来就无授予，A 侧立即失效。
        Assert.IsTrue(await service.RevokeAsync(grant.GrantId));
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA));
        Assert.IsNull(await service.GetActiveAsync(WorkspaceB, AgentA), "跨 workspace 查询同样必须为空。");
    }

    // —— 06 裁决为 DenyOnce / Unknown / DenyPermanent ⇒ 一律拒绝（fail-closed） ——

    [TestMethod]
    public async Task GrantAsync_NonAllowOutcomes_FailClosed()
    {
        var (service, audit, _) = CreateService();

        foreach (var outcome in new[]
                 {
                     ClassificationOutcome.DenyOnce,
                     ClassificationOutcome.Unknown,
                     ClassificationOutcome.DenyPermanent,
                 })
        {
            var ex = await Assert.ThrowsAsync<AgentFullAccessGrantRejectedException>(
                () => service.GrantAsync(GrantRequest(outcome: outcome)));
            Assert.AreEqual(AgentFullAccessGrantService.ReasonOutcomeNotAllowed, ex.ReasonCode, $"裁决 {outcome} 必须拒绝且原因码正确。");
        }

        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "任何非放行裁决都不得留下授予。");
        var deniedEvents = (await EventsAsync(audit))
            .Where(e => e.EventType == ToolApprovalAuditEventType.FullAccessDenied)
            .ToList();
        Assert.HasCount(3, deniedEvents, "三种非放行裁决各落一条 Denied。");
        foreach (var denied in deniedEvents)
        {
            Assert.IsTrue(
                denied.Reason!.StartsWith(AgentFullAccessGrantService.ReasonOutcomeNotAllowed, StringComparison.Ordinal),
                $"Denied 原因码必须一致（裁决不允许），实际：{denied.Reason}");
        }

        Assert.AreEqual(0, (await EventsAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted), "不得有 Granted。");
    }

    // —— 07 撤销后立即失效 + FullAccessRevoked；重复撤销幂等（审计不重复、不报错） ——

    [TestMethod]
    public async Task RevokeAsync_RevokesOnceAndIsIdempotent()
    {
        var (service, audit, _) = CreateService();
        var grant = await service.GrantAsync(GrantRequest());

        Assert.IsTrue(await service.RevokeAsync(grant.GrantId), "首次撤销必须成功。");
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "撤销后必须立即失效。");

        Assert.IsFalse(await service.RevokeAsync(grant.GrantId), "重复撤销必须幂等返回 false。");
        Assert.IsFalse(await service.RevokeAsync(grant.GrantId), "第三次撤销同样幂等。");
        Assert.IsFalse(await service.RevokeAsync("fa-unknown-id"), "未知授予 id 幂等返回 false、不抛错。");

        var events = await EventsAsync(audit);
        Assert.AreEqual(
            1,
            events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessRevoked),
            "Revoked 审计不得重复落。");
        var revokedEvent = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessRevoked);
        Assert.IsTrue(revokedEvent.Reason!.Contains("revoked_at_utc=", StringComparison.Ordinal), "Revoked 审计必须写明撤销时刻。");
    }

    // —— 08 时钟推进但未到期 ⇒ 仍有效；恰好 TTL 时刻 ⇒ 失效（now >= ExpiresAtUtc 即失效，安全侧优先） ——

    [TestMethod]
    public async Task GetActiveAsync_BoundaryAtExactTtl_IsInvalidated()
    {
        var (service, _, clock) = CreateService();
        var grant = await service.GrantAsync(GrantRequest());

        clock.Advance(TimeSpan.FromSeconds(299.5));
        var active = await service.GetActiveAsync(WorkspaceA, AgentA);
        Assert.IsNotNull(active, "未到期（299.5s < 300s）必须仍有效。");
        Assert.AreEqual(grant.ExpiresAtUtc, active.ExpiresAtUtc, "过期时刻以授予时服务端计算为准，不随读取刷新。");

        // 边界语义（实现注释固定）：now == ExpiresAtUtc 即视为失效（>= 判定，安全边界优先，宁严勿松）。
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "恰好到达 TTL 时刻必须判定失效。");
    }

    // —— 09 取消令牌触发 ⇒ OperationCanceledException 照常传播（不翻译成拒绝、不吞掉） ——

    [TestMethod]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        var (service, _, _) = CreateService();

        // ① 已取消令牌：方法入口的取消检查直接抛出。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GrantAsync(GrantRequest(), new CancellationToken(canceled: true)));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetActiveAsync(WorkspaceA, AgentA, new CancellationToken(canceled: true)));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RevokeAsync("grant-1", new CancellationToken(canceled: true)));

        // ② 审计 store 自身抛 OCE（如宿主关停）：必须原样传播，不得被 fail-closed 分支折叠成拒绝异常。
        var oceService = new AgentFullAccessGrantService(new OperationCanceledAuditStore(), new FakeClock());
        await Assert.ThrowsAsync<OperationCanceledException>(() => oceService.GrantAsync(GrantRequest()));

        // ③ 到期观测路径的 Expired 审计抛 OCE：同样传播（Requested + Granted 已成功写入，授予本身生效）。
        var clock = new FakeClock();
        var flakyService = new AgentFullAccessGrantService(new FlakyAuditStore(succeedFirst: 2), clock);
        var flakyGrant = await flakyService.GrantAsync(GrantRequest());
        Assert.IsNotNull(flakyGrant);
        clock.Advance(TimeSpan.FromSeconds(301));
        await Assert.ThrowsAsync<OperationCanceledException>(() => flakyService.GetActiveAsync(WorkspaceA, AgentA));
    }

    // —— 10 请求时长 ≤ 0 ⇒ 按默认 300 处理（实现注释固定该语义，而非拒绝） ——

    [TestMethod]
    public async Task GrantAsync_ZeroOrNegativeDuration_FallsBackToDefaultTtl()
    {
        var (service, _, _) = CreateService();

        var zero = await service.GrantAsync(GrantRequest(durationSeconds: 0));
        Assert.AreEqual(T0.AddSeconds(300), zero.ExpiresAtUtc, "0 秒视为未指定 ⇒ 按默认 300 授予。");

        var negative = await service.GrantAsync(GrantRequest(durationSeconds: -5, grantId: "grant-neg"));
        Assert.AreEqual(T0.AddSeconds(300), negative.ExpiresAtUtc, "负值同样按默认 300 授予。");

        Assert.IsNotNull(await service.GetActiveAsync(WorkspaceA, AgentA), "两次授予后作用域内必有有效授予（新覆盖旧）。");
    }

    // —— 11 审计 store 故障 ⇒ fail-closed：授予被回滚、返回未授予（typed audit_store_error） ——

    [TestMethod]
    public async Task GrantAsync_AuditStoreFails_FailClosedRollback()
    {
        var clock = new FakeClock();
        var service = new AgentFullAccessGrantService(new ThrowingAuditStore(), clock);

        var ex = await Assert.ThrowsAsync<AgentFullAccessGrantRejectedException>(
            () => service.GrantAsync(GrantRequest()));

        Assert.AreEqual(AgentFullAccessGrantService.ReasonAuditStoreError, ex.ReasonCode, "审计故障必须给出 typed 原因码。");
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "fail-closed：审计失败 ⇒ 无任何生效授予。");
    }

    // —— 12 服务端重算绝对时刻：客户端给的 GrantedAtUtc / ExpiresAtUtc 只用于计算时长，绝不被信任 ——

    [TestMethod]
    public async Task GrantAsync_ClientTimestampsNeverTrusted_ServerRecalculates()
    {
        var (service, _, clock) = CreateService();

        // 客户端声称「一年前授予、一年前+300s 到期」（时长=300s 合法）。
        var request = GrantRequest() with
        {
            GrantedAtUtc = T0.AddYears(-1),
            ExpiresAtUtc = T0.AddYears(-1).AddSeconds(300),
        };

        var granted = await service.GrantAsync(request);

        Assert.AreEqual(T0, granted.GrantedAtUtc, "授予时刻必须被服务端时钟覆盖。");
        Assert.AreEqual(T0.AddSeconds(300), granted.ExpiresAtUtc, "过期时刻必须被服务端时钟重算，绝不能沿用客户端绝对值。");

        clock.Advance(TimeSpan.FromSeconds(301));
        Assert.IsNull(await service.GetActiveAsync(WorkspaceA, AgentA), "失效判定只依赖服务端时钟推进。");
    }

    // —— 测试辅助 ——

    private static (AgentFullAccessGrantService Service, InMemoryToolApprovalAuditStore Audit, FakeClock Clock) CreateService()
    {
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        return (new AgentFullAccessGrantService(audit, clock), audit, clock);
    }

    private static AgentFullAccessGrant GrantRequest(
        ClassificationOutcome outcome = ClassificationOutcome.AllowOnce,
        int durationSeconds = 300,
        string workspaceId = WorkspaceA,
        string agentInstanceId = AgentA,
        string grantId = "grant-1")
        => new()
        {
            GrantId = grantId,
            WorkspaceId = workspaceId,
            AgentInstanceId = agentInstanceId,
            SessionId = "sess-1",
            GrantedAtUtc = T0,
            ExpiresAtUtc = T0.AddSeconds(durationSeconds),
            GrantedByClassifierId = "llm-safety-v1",
            Outcome = outcome,
            Reason = "依据安全分类器 AllowOnce 裁决申请临时完全访问",
        };

    private static async Task<List<ToolApprovalAuditEvent>> EventsAsync(InMemoryToolApprovalAuditStore store)
        => [.. await store.ListAsync()];

    /// <summary>假时钟：时间只经注入的 TimeProvider 获取（服务端计时语义的可测性）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = T0;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>读/写均抛 OperationCanceledException 的审计 store 存根（验证 OCE 不被 fail-closed 分支吞掉）。</summary>
    private sealed class OperationCanceledAuditStore : IToolApprovalAuditStore
    {
        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
            => throw new OperationCanceledException("audit-save-canceled");

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
            => throw new OperationCanceledException("audit-list-canceled");
    }

    /// <summary>前 N 次写入成功、之后抛 OCE 的审计 store（验证到期观测路径的 OCE 传播）。</summary>
    private sealed class FlakyAuditStore : IToolApprovalAuditStore
    {
        private readonly int _succeedFirst;
        private int _saveCalls;

        public FlakyAuditStore(int succeedFirst) => _succeedFirst = succeedFirst;

        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _saveCalls) > _succeedFirst)
            {
                throw new OperationCanceledException("audit-save-canceled");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolApprovalAuditEvent>>([]);
    }

    /// <summary>写入始终抛普通异常的审计 store（验证审计故障 ⇒ fail-closed 拒绝且不冒泡原始异常）。</summary>
    private sealed class ThrowingAuditStore : IToolApprovalAuditStore
    {
        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
            => throw new InvalidOperationException("audit-boom");

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolApprovalAuditEvent>>([]);
    }
}
