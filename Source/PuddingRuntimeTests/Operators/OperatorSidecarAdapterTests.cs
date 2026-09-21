using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Operators;
using PuddingRuntime.Operators.Adapters;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S2b 交付物 2（生产实现桥接）用例：T3「裁决先于留痕」、T4「健康观测器抛异常 ⇒ 裁决不受影响」，
/// 以及两个适配器的映射 / 吞掉但可探查 / DI 追加注册。
/// <para>
/// 这些用例锁定的是<b>旁挂被真的接上之后</b>的语义：接缝在生产里此前<b>没有任何实现</b>（定义了端口、
/// 没人实现 ⇒ 永远不会被调用）。接上之后最危险的退化有两个——① 把审计提成同步必经环节（回退
/// 「裁决先于留痕」）；② 把失败静默吞掉（故障既无痕迹也不可见）。两条都必须有测试说话。
/// </para>
/// </summary>
[TestClass]
public sealed class OperatorSidecarAdapterTests
{
    private static readonly string ProbeOperatorId = typeof(ProbeJudge).FullName!;

    // ---------- T3：裁决先于留痕（审计写入抛异常 ⇒ 裁决不变 + 有 Warning，且不上抛） ----------

    [TestMethod]
    public async Task T3_AuditStoreThrows_VerdictStaysIntact_WarningLogged_NothingBubbles()
    {
        var logger = new RecordingLogger();
        var store = new ThrowingAuditStore();
        var audit = new OperatorAuditSinkAdapter(store, logger);
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), audit: audit, logger: logger),
            OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Yes, result.Outcome, "审计写入失败不得改变已定裁决（裁决先于留痕）。");
        Assert.IsNull(result.Envelope.ReasonCode, "降级原因码不得因为审计失败而出现。");
        Assert.AreEqual(1, store.Attempts, "写入确实被尝试过（旁挂失败 ≠ 静默跳过）。");
        Assert.AreEqual(1, audit.SwallowedFailureCount, "吞掉但必须可探查：失败计数必须可见。");
        StringAssert.Contains(audit.LastSwallowedFailure!, "InvalidOperationException");
        Assert.IsTrue(
            logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException),
            "吞掉但必须可探查：必须记 Warning（不得静默）。");
    }

    [TestMethod]
    public async Task T3b_AuditStoreHealthy_RecordIsWritten_WithJudgementIdSceneAndOperatorId()
    {
        var store = new RecordingAuditStore();
        var audit = new OperatorAuditSinkAdapter(store);
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), audit: audit),
            OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.HasCount(1, store.Events, "正常路径必须留痕（否则「留痕」本身失效）。");
        Assert.AreEqual(result.Envelope.JudgementId, store.Events[0].EventId, "EventId 取判定的确定性 id。");
        Assert.AreEqual(ProbeOperatorId, store.Events[0].ClassifierId);
        Assert.AreEqual(result.Envelope.CreatedAtUtc, store.Events[0].CreatedAtUtc);
        StringAssert.Contains(store.Events[0].Reason!, "scene=probe-scene");
        StringAssert.Contains(store.Events[0].Reason!, "reasonCode=none");
        Assert.AreEqual(0, audit.SwallowedFailureCount);
    }

    // ---------- T4：健康观测器抛异常 ⇒ 裁决不受影响；适配器内部失败同样被吞掉且可探查 ----------

    [TestMethod]
    public async Task T4_ThrowingHealthObserver_VerdictUnchanged()
    {
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), health: new ThrowingHealthObserver()),
            OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Yes, result.Outcome, "健康上报失败不得影响裁决。");
        Assert.IsNull(result.Envelope.ReasonCode);
    }

    [TestMethod]
    public async Task T4b_HealthAdapter_RecordsSuccessAndDeferred_PartitionedByScene()
    {
        var reporter = new ClassifierHealthReporter();
        var logger = new RecordingLogger();
        var health = new OperatorHealthObserverAdapter(reporter, logger);

        // 成功路径 ⇒ 分类器维度健康恢复（既有 RecordSuccess 语义）。
        var ok = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), health: health),
            OperatorTestData.Policy());
        Assert.AreEqual(JudgeOutcome.Yes, (await ok.JudgeAsync(new ProbeContext())).Outcome);

        var status = reporter.Snapshot().Single(s => s.ClassifierId == ProbeOperatorId);
        Assert.AreEqual(ClassifierHealth.Healthy, status.Health);
        Assert.AreEqual(0, status.ConsecutiveFailures);
        Assert.AreEqual(ClassifierHealth.Healthy, reporter.HealthForScene(ProbeOperatorId, "probe-scene"));

        // 失败路径（内核异常 ⇒ 降级 ⇒ Succeeded=false）⇒ 落 per-key 计数，且键带场景维度。
        var broken = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), health: health),
            OperatorTestData.Policy(),
            (_, _, _) => throw new InvalidOperationException("core down"));
        var degraded = await broken.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Unknown, degraded.Outcome);

        var counter = reporter.SnapshotDeferredCounters().Single();
        Assert.AreEqual(ProbeOperatorId, counter.ClassifierId, "算子标识即分类器维度的取值（不自造身份）。");
        Assert.AreEqual("probe-scene", counter.SceneKey, "计数按场景键分区（S2b 的核心）。");
        Assert.AreEqual(OperatorHealthObserverAdapter.OperatorSampleToolId, counter.ToolId, "算子采样无工具维度 ⇒ 具名占位而非空串。");
        Assert.AreEqual(1, counter.ConsecutiveDeferred);
        Assert.AreEqual(OperatorReasonCodes.CoreFailure, counter.LastReasonCode);
        Assert.AreEqual(string.Empty, counter.ArgumentsHash, "算子采样无参数维度 ⇒ 参数哈希为空串（既有算法语义）。");
        Assert.AreEqual(0, health.SwallowedFailureCount);
        Assert.AreEqual(0, logger.WarningCount, "正常路径不得产生 Warning（可探查 ≠ 噪声）。");
    }

    [TestMethod]
    public void T4c_HealthAdapter_InternalFailure_IsSwallowed_AndProbeable_WithoutHalfCounters()
    {
        var reporter = new ClassifierHealthReporter();
        var logger = new RecordingLogger();
        var health = new OperatorHealthObserverAdapter(reporter, logger);

        // 畸形采样（算子标识缺失）会让既有健康面在内部抛错——适配器必须吞掉，且不得留下半截计数。
        health.Report(new OperatorHealthSample
        {
            OperatorId = null!,
            SceneKey = "probe-scene",
            Succeeded = false,
            Cached = false,
            ObservedAtUtc = OperatorTestData.Origin,
        });

        Assert.AreEqual(1, health.SwallowedFailureCount, "内部失败必须可探查（不是静默吞掉）。");
        StringAssert.Contains(health.LastSwallowedFailure!, "ArgumentNullException");
        Assert.AreEqual(1, logger.WarningCount, "吞掉必须留 Warning（唯一记日志层在适配器）。");
        Assert.IsEmpty(reporter.SnapshotDeferredCounters(), "失败的上报不得留下半截计数。");
    }

    [TestMethod]
    public void T4d_AuditAdapter_WithoutStore_DropsVisibly_WithoutThrowing()
    {
        var logger = new RecordingLogger();
        var audit = new OperatorAuditSinkAdapter(store: null, logger: logger);

        audit.Write(new OperatorAuditRecord
        {
            OperatorId = ProbeOperatorId,
            SceneKey = "probe-scene",
            JudgementId = "jdg-test",
            Reason = "判定完成。",
            Cached = false,
            CreatedAtUtc = OperatorTestData.Origin,
        });

        audit.Write(new OperatorAuditRecord
        {
            OperatorId = ProbeOperatorId,
            SceneKey = "probe-scene",
            JudgementId = "jdg-test-2",
            Reason = "判定完成。",
            Cached = false,
            CreatedAtUtc = OperatorTestData.Origin,
        });

        Assert.AreEqual(2, audit.SwallowedFailureCount, "丢弃必须可探查。");
        Assert.AreEqual(1, logger.WarningCount, "未接线只记一次 Warning（不刷屏），但绝不静默。");
        StringAssert.Contains(audit.LastSwallowedFailure!, "未接线");
    }

    // ---------- §3.4：DI 追加注册（两个接缝在生产组合里可解析） ----------

    [TestMethod]
    public void T6_Di_ResolvesBothSidecarPorts_ToTheProductionAdapters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalAllowlistStore>(new InMemoryToolApprovalAllowlistStore());
        services.AddSingleton<IToolApprovalAuditStore>(new InMemoryToolApprovalAuditStore());
        services.AddPuddingToolRegistry();
        using var provider = services.BuildServiceProvider();

        var healthPort = provider.GetRequiredService<IOperatorHealthObserver>();
        var auditPort = provider.GetRequiredService<IOperatorAuditSink>();

        Assert.IsInstanceOfType<OperatorHealthObserverAdapter>(healthPort);
        Assert.IsInstanceOfType<OperatorAuditSinkAdapter>(auditPort);
        Assert.AreSame(healthPort, provider.GetRequiredService<OperatorHealthObserverAdapter>(), "端口与具体类型必须是同一单例。");
        Assert.AreSame(auditPort, provider.GetRequiredService<OperatorAuditSinkAdapter>());
    }

    // ---------- helpers ----------

    private static OperatorEnvironment Env(
        IClassifierModel? model = null,
        IOperatorHealthObserver? health = null,
        IOperatorAuditSink? audit = null,
        ILogger? logger = null)
        => new()
        {
            Model = model,
            Health = health,
            Audit = audit,
            Logger = logger,
            Clock = new FixedClock(OperatorTestData.Origin),
            Timeout = TimeSpan.FromSeconds(5),
        };

    /// <summary>记录日志条目（用于断言「吞掉但必须可探查」）。</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>记录写入的既有审计存储假实现（验证映射，不发起任何 IO）。</summary>
    private sealed class RecordingAuditStore : IToolApprovalAuditStore
    {
        public List<ToolApprovalAuditEvent> Events { get; } = [];

        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolApprovalAuditEvent>>(Events.ToArray());
    }

    /// <summary>总是抛异常的既有审计存储假实现（验证「裁决先于留痕」不回退）。</summary>
    private sealed class ThrowingAuditStore : IToolApprovalAuditStore
    {
        public int Attempts { get; private set; }

        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
        {
            Attempts++;
            throw new InvalidOperationException("audit store down");
        }

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolApprovalAuditEvent>>([]);
    }
}
