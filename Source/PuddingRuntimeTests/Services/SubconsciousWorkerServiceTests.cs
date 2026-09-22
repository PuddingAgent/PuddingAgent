using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Skills.Curation;
using PuddingCode.Skills.Family;
using PuddingCode.Skills.Portfolio;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Background;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousWorkerServiceTests
{
    [TestMethod]
    public async Task DurableWorker_WithMemoryNotes_ShouldExecuteWikiPageUpdateAndCompleteJob()
    {
        await using var memory = await CreateMemoryScopeAsync();
        var queue = new RecordingSubconsciousJobQueue
        {
            Job = new ConsolidationJob
            {
                SessionId = "session-1",
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                MemoryNotes = ["用户偏好简单 V1。"],
            },
        };
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            wikiPageUpdateService: new MemoryWikiPageUpdateService(new StaticMemoryLlmClient(PageUpdateJson)),
            wikiPageWriteEntry: new WikiPageWriteEntry(memory.Library));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.ResultRecorded.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultKinds.MemoryWikiPageUpdate, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, queue.RecordedResult.Status);
        Assert.AreEqual("1", queue.RecordedResult.Metadata["written_page_count"]);

        var libraries = await memory.Library.ListLibrariesAsync("workspace-1");
        var books = await memory.Library.ListBooksAsync(libraries[0].LibraryId);
        var chapters = await memory.Library.ListChaptersAsync(books[0].BookId);
        Assert.AreEqual(1, books.Count);
        Assert.AreEqual("用户偏好", books[0].Title);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual("/设计", chapters[0].Title);
        Assert.AreEqual("# 设计\n\n- 用户偏好简单 V1。", chapters[0].Content);
    }

    [TestMethod]
    public async Task DurableWorker_ShouldRecordF5DryRunResultEnvelopeAndCompleteJob()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var planService = new SubconsciousPlanGenerationService(
            new StaticMemoryLlmClient(ValidPlanJson),
            new MemoryMaintenancePlanValidator());
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            planGenerationService: planService,
            memoryWriteCoordinator: coordinator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.ResultRecorded.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, queue.RecordedResult!.Status);
        Assert.AreEqual(1, queue.RecordedResult.MemoryWriteResults.Count);
        Assert.AreEqual("plan-1:op-1", queue.RecordedResult.MemoryWriteResults[0].CommandId);
        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, queue.RecordedResult.MemoryWriteResults[0].Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, queue.RecordedResult.MemoryWriteResults[0].Intent);
    }

    [TestMethod]
    public async Task PausedWorker_ShouldNotLeaseDurableJobs()
    {
        var queue = new RecordingSubconsciousJobQueue();
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var runtimeControl = new PausedSubconsciousRuntimeControl();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            runtimeControl: runtimeControl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, queue.LeaseCount);
        Assert.AreEqual(0, orchestrator.CallCount);
    }

    [TestMethod]
    public async Task PeriodicLoops_ShouldEnqueueFourDurableScopedJobs()
    {
        var queue = new RecordingSubconsciousJobQueue { DisableLeasing = true };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions
            {
                Scheduling = new SubconsciousSchedulingOptions
                {
                    PeriodicJobsEnabled = true,
                    DefaultWorkspaceId = "workspace-evolution",
                    DefaultAgentInstanceId = "agent-evolution",
                    AutoDreamInitialDelaySeconds = 0,
                    PatternExtractionInitialDelaySeconds = 0,
                    SkillImprovementInitialDelaySeconds = 0,
                    SkillCurationInitialDelaySeconds = 0,
                    AutoDreamIntervalSeconds = 3600,
                    PatternExtractionIntervalSeconds = 3600,
                    SkillImprovementIntervalSeconds = 3600,
                    SkillCurationIntervalSeconds = 3600,
                },
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.AllPeriodicJobsEnqueued.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        var requests = queue.EnqueuedRequests;
        CollectionAssert.AreEquivalent(
            new[]
            {
                SubconsciousJobTypes.AutoDream,
                SubconsciousJobTypes.ExtractPatterns,
                SubconsciousJobTypes.ImproveSkills,
                SubconsciousJobTypes.SkillCurate,
            },
            requests.Select(request => request.JobType).ToArray());
        Assert.IsTrue(requests.All(request => request.Job.WorkspaceId == "workspace-evolution"));
        Assert.IsTrue(requests.All(request => request.Job.AgentId == "agent-evolution"));
        Assert.IsTrue(requests.All(request => request.IdempotencyKey.StartsWith("periodic:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PeriodicLoops_ShouldNotReopenExistingTimeBucketJobs()
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            DisableLeasing = true,
            ExistingLookupItem = new SubconsciousJobQueueItem
            {
                JobId = "existing-periodic-job",
                JobType = SubconsciousJobTypes.AutoDream,
                IdempotencyKey = "existing-periodic-key",
                Status = "completed",
                Job = new ConsolidationJob
                {
                    SessionId = "periodic:existing",
                    WorkspaceId = "default",
                    AgentId = "default.general-assistant-001",
                    AgentTemplateId = "default.general-assistant-001",
                },
            },
        };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions
            {
                Scheduling = new SubconsciousSchedulingOptions
                {
                    PeriodicJobsEnabled = true,
                    AutoDreamInitialDelaySeconds = 0,
                    PatternExtractionInitialDelaySeconds = 0,
                    SkillImprovementInitialDelaySeconds = 0,
                    AutoDreamIntervalSeconds = 3600,
                    PatternExtractionIntervalSeconds = 3600,
                    SkillImprovementIntervalSeconds = 3600,
                },
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(queue.LookupCount >= 3);
        Assert.AreEqual(0, queue.EnqueuedRequests.Count);
    }

    [TestMethod]
    public async Task PeriodicLoops_SkillCuration_ShouldNotReopenSameTimeBucketAcrossRestarts()
    {
        var queue = new KeyTrackingJobQueue();
        SubconsciousSchedulingOptions NewScheduling() => new()
        {
            PeriodicJobsEnabled = true,
            DefaultWorkspaceId = "workspace-evolution",
            DefaultAgentInstanceId = "agent-evolution",
            AutoDreamInitialDelaySeconds = 0,
            PatternExtractionInitialDelaySeconds = 0,
            SkillImprovementInitialDelaySeconds = 0,
            SkillCurationInitialDelaySeconds = 0,
            AutoDreamIntervalSeconds = 100_000,
            PatternExtractionIntervalSeconds = 100_000,
            SkillImprovementIntervalSeconds = 100_000,
            SkillCurationIntervalSeconds = 100_000,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker1 = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions { Scheduling = NewScheduling() }));
        await worker1.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker1.StopAsync(CancellationToken.None);

        var worker2 = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            new RecordingSubconsciousOrchestrator(),
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue,
            options: Options.Create(new SubconsciousOptions { Scheduling = NewScheduling() }));
        await worker2.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
        await worker2.StopAsync(CancellationToken.None);

        var skillCurateEnqueues = queue.Requests.Count(request =>
            request.JobType == SubconsciousJobTypes.SkillCurate);
        Assert.AreEqual(1, skillCurateEnqueues);
    }

    [TestMethod]
    public async Task DurableWorker_SkillCurate_ShouldReportWithoutWritingSkills()
    {
        await using var memory = await CreateMemoryScopeAsync();
        var probe = new ProbeSkillStore();
        var llmClient = new StaticMemoryLlmClient("unused: curation must not call the llm");
        var orchestrator = new SubconsciousOrchestrator(
            memory.Library,
            new ThrowingMemoryEngine(),
            llmClient,
            new ThrowingMemoryLibrarian(),
            NullLogger<SubconsciousOrchestrator>.Instance,
            new ThrowingMemoryDbContextFactory(),
            new ThrowingSkillTrajectorySource(),
            probe,
            new SkillEvolutionDeduplicationService(
                probe,
                llmClient,
                NullLogger<SkillEvolutionDeduplicationService>.Instance));

        var queue = new RecordingSubconsciousJobQueue
        {
            JobType = SubconsciousJobTypes.SkillCurate,
            Job = new ConsolidationJob
            {
                SessionId = "debug:evolution:skill.curate:request-1",
                WorkspaceId = "workspace-evolution",
                AgentId = "agent-evolution",
                AgentTemplateId = "agent-evolution",
            },
        };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.JobCompleted.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultKinds.SkillCuration, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Completed, queue.RecordedResult.Status);
        Assert.AreEqual(0, queue.RecordedResult.OperationCount);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_before"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_after"]);
        Assert.IsTrue(!string.IsNullOrWhiteSpace(queue.RecordedResult.Metadata["not_reduced_reason"]));
        Assert.AreEqual("3", queue.RecordedResult.Metadata["candidate_count"]);
        Assert.AreEqual("1", queue.RecordedResult.Metadata["retire_suggestion_count"]);
        Assert.AreEqual("v1", queue.RecordedResult.Metadata["report_version"]);
        Assert.AreEqual(0, probe.WriteCallCount);
    }

    // ───────────── G7 交付物 8：接线级 I7（零写盘）/ I8（零回归）探针 ─────────────
    // 与门禁用例的分工：门禁用例（PuddingMemoryEngineTests）证明**判据本身**可红；
    // 这里证明**接线**：产物进来后被裁决、裁决被记录，且整轮真的一次技能写盘都没有。

    [TestMethod]
    public async Task SkillCurate_WithPolicyAndEmptyProducer_ShouldKeepCurationCountersAtZero()
    {
        var probe = new CurationProbeSkillStore();
        var source = new FakeSkillDistillationSource([]);

        var queue = await RunSkillCurateJobAsync(probe, source, CurationPolicy());

        Assert.AreEqual(1, source.CallCount, "接线必须真的向产物来源取数，否则'空生产者'证明不了任何东西");
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(SubconsciousJobResultKinds.SkillCuration, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Completed, queue.RecordedResult.Status);
        Assert.AreEqual(0, queue.RecordedResult.OperationCount, "I7：本片零写盘，作业结果不得报告任何技能操作");
        Assert.AreEqual("0", queue.RecordedResult.Metadata["curated_products"]);
        Assert.AreEqual("0", queue.RecordedResult.Metadata["curated_shadow"]);
        Assert.AreEqual("0", queue.RecordedResult.Metadata["curated_rejected"]);

        // I8 零回归：既有字段逐字段不变（store 形状与 G6 用例同构 ⇒ 报告应逐字段相同）。
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_before"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_after"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["candidate_count"]);
        Assert.AreEqual("1", queue.RecordedResult.Metadata["retire_suggestion_count"]);
        Assert.AreEqual("v1", queue.RecordedResult.Metadata["report_version"]);

        var reason = queue.RecordedResult.Metadata["not_reduced_reason"];
        Assert.IsTrue(!string.IsNullOrWhiteSpace(reason));
        Assert.IsFalse(
            reason.Contains("until G7 gates land", StringComparison.Ordinal),
            "G6 的过期文案必须已被替换：门禁已就位，未落点是因为写盘职权在 L3-b");
        StringAssert.Contains(reason, "L3-b");
        Assert.AreEqual(0, probe.WriteCallCount, "I7：空生产者路径同样不得有任何技能写调用");
    }

    [TestMethod]
    public async Task SkillCurate_WithViolatingProduct_ShouldRecordRejectionAndNeverWrite()
    {
        var probe = new CurationProbeSkillStore();
        // 产物关键词与**第三方启用技能**（skill-c）相撞 ⇒ C3 必须拒绝。
        var source = new FakeSkillDistillationSource([CurationProduct(keywords: ["登录流程"])]);

        var queue = await RunSkillCurateJobAsync(probe, source, CurationPolicy());

        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual("1", queue.RecordedResult!.Metadata["curated_products"]);
        Assert.AreEqual("0", queue.RecordedResult.Metadata["curated_shadow"]);
        Assert.AreEqual(
            "1",
            queue.RecordedResult.Metadata["curated_rejected"],
            "违规产物必须被记为拒绝，而不是被静默放过（fail-closed）");
        Assert.AreEqual(0, queue.RecordedResult.OperationCount);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_before"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_after"], "拒绝路径同样一次技能写盘都不能有");
        Assert.AreEqual(0, probe.WriteCallCount);
    }

    [TestMethod]
    public async Task SkillCurate_WithCleanProduct_ShouldRecordShadowAndNeverWrite()
    {
        var probe = new CurationProbeSkillStore();
        var source = new FakeSkillDistillationSource([CurationProduct(keywords: ["会话治理"])]);

        var queue = await RunSkillCurateJobAsync(probe, source, CurationPolicy());

        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual("1", queue.RecordedResult!.Metadata["curated_products"]);
        Assert.AreEqual(
            "1",
            queue.RecordedResult.Metadata["curated_shadow"],
            "干净产物 ⇒ 唯一允许的落点是 shadow（本片无写盘职权）");
        Assert.AreEqual("0", queue.RecordedResult.Metadata["curated_rejected"]);
        Assert.AreEqual(0, queue.RecordedResult.OperationCount);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_before"]);
        Assert.AreEqual("3", queue.RecordedResult.Metadata["n_after"]);
        Assert.AreEqual(0, probe.WriteCallCount);
    }

    // ───────────── G4 交付物 D7：家族评审计数必须能从作业结果观测 ─────────────
    // 与 G7 同一教训：只写日志不算「被记录」——「裁决被记录」必须能在作业结果层断言。

    [TestMethod]
    public async Task SkillCurate_WithoutFamilyPolicies_ShouldReportZeroFamilyReviews()
    {
        var probe = new CurationProbeSkillStore();
        await using var memory = await CreateMemoryScopeAsync();
        var orchestrator = CreateCurationOrchestrator(
            memory,
            probe,
            new FakeSkillDistillationSource([]),
            CurationPolicy());

        // 不传家族策略 = 作业侧现状（本片只有接缝，尚无策略来源）⇒ 逐字段零回归。
        var report = await orchestrator.SkillCurateAsync(
            "workspace-evolution",
            "agent-evolution");

        Assert.AreEqual(0, report.FamilyReviewCount, "I8：未传家族策略 ⇒ 家族评审计数恒为 0");
        Assert.AreEqual(report.NBefore, report.NAfter, "I7：家族评审只裁决，n_after 必须等于 n_before");
        Assert.AreEqual(0, probe.WriteCallCount, "I7：家族评审路径一次技能写盘都不能有");
    }

    [TestMethod]
    public async Task SkillCurate_WithOverCapFamily_ShouldReportReviewRequestAndNeverWrite()
    {
        var probe = new CurationProbeSkillStore();
        await using var memory = await CreateMemoryScopeAsync();
        var orchestrator = CreateCurationOrchestrator(
            memory,
            probe,
            new FakeSkillDistillationSource([]),
            CurationPolicy());

        // 探针仓里的 3 个启用技能（Same Name / Same Name / Other Name）在 0.25 阈值下同族（token 重叠）
        // ⇒ 上限 1 时恰好 1 条评审请求（只产出待裁决记录，不禁用任何技能）。
        var report = await orchestrator.SkillCurateAsync(
            "workspace-evolution",
            "agent-evolution",
            memoryLlmConfig: null,
            familyPolicy: FamilyPolicy(),
            portfolioPolicy: PortfolioPolicy(perFamilyCap: 1));

        Assert.AreEqual(1, report.FamilyReviewCount, "超限家族必须被计数，否则接线层看不到任何超限事实");
        StringAssert.Contains(
            report.NotReducedReason,
            "family merge review request",
            "报告文本必须交代多出来的待裁决记录（否则运维只能看到 n_before==n_after）");
        Assert.AreEqual(report.NBefore, report.NAfter, "I7：超限只产出评审请求，不得降低启用技能数");
        Assert.AreEqual(0, probe.WriteCallCount, "I7：超限路径同样不得有任何技能写调用");
    }

    [TestMethod]
    public async Task SkillCurate_JobResult_ShouldExposeFamilyReviewCount()
    {
        var probe = new CurationProbeSkillStore();
        var queue = await RunSkillCurateJobAsync(probe, new FakeSkillDistillationSource([]), CurationPolicy());

        Assert.IsNotNull(queue.RecordedResult);
        Assert.IsTrue(
            queue.RecordedResult!.Metadata.TryGetValue("family_review_count", out var count),
            "D7：家族评审计数必须能从作业结果观测到（key 缺失会让运维无法区分「没接线」与「没超限」）");
        Assert.AreEqual(
            "0",
            count,
            "作业侧尚无家族策略来源（本片只铺接缝）⇒ 计数为 0；非零传播由编排器级用例证明");
        Assert.AreEqual(0, queue.RecordedResult.OperationCount, "I7：本作业零写盘");
        Assert.AreEqual(0, probe.WriteCallCount);
    }

    /// <summary>
    /// 构造一个**真实编排器**（门禁/家族判据全走生产代码），供接线级用例直接调用 <c>SkillCurateAsync</c>。
    /// 与 <see cref="RunSkillCurateJobAsync"/> 同源：构造参数只维护一份，否则参数一多必然漂移。
    /// </summary>
    private static SubconsciousOrchestrator CreateCurationOrchestrator(
        MemoryScope memory,
        IAgentSkillEvolutionStore store,
        ISkillDistillationSource source,
        SkillCurationPolicy policy)
    {
        var llmClient = new StaticMemoryLlmClient("unused: curation must not call the llm");
        return new SubconsciousOrchestrator(
            memory.Library,
            new ThrowingMemoryEngine(),
            llmClient,
            new ThrowingMemoryLibrarian(),
            NullLogger<SubconsciousOrchestrator>.Instance,
            new ThrowingMemoryDbContextFactory(),
            new ThrowingSkillTrajectorySource(),
            store,
            new SkillEvolutionDeduplicationService(
                store,
                llmClient,
                NullLogger<SkillEvolutionDeduplicationService>.Instance),
            skillDistillationSource: source,
            skillCurationPolicy: policy);
    }

    /// <summary>G4 家族划分策略（阈值取只读报告的第一档 0.25，仅为让用例可复现）。</summary>
    private static SkillFamilyPolicy FamilyPolicy() => SkillFamilyPolicy.Create(
        policyId: "skill-family/probe",
        version: 1,
        nameTokenJaccardThreshold: 0.25,
        minTokenLength: 1,
        nameSeparators: SkillNameTokenization.StandardSeparators);

    /// <summary>G4 组合策略（<c>PerFamilyCap</c> 是家族内上限的唯一来源）。</summary>
    private static SkillPortfolioPolicy PortfolioPolicy(int? perFamilyCap)
        => SkillPortfolioPolicy.Create("skill-portfolio/probe", 1, 100, 50, perFamilyCap, 0.05, 90);

    /// <summary>
    /// 跑一次真实的 skill.curate 作业（走 Worker → 编排器 → 门禁），返回录到结果的队列。
    /// </summary>
    private static async Task<RecordingSubconsciousJobQueue> RunSkillCurateJobAsync(
        IAgentSkillEvolutionStore store,
        ISkillDistillationSource source,
        SkillCurationPolicy policy)
    {
        await using var memory = await CreateMemoryScopeAsync();
        var orchestrator = CreateCurationOrchestrator(memory, store, source, policy);

        var queue = new RecordingSubconsciousJobQueue
        {
            JobType = SubconsciousJobTypes.SkillCurate,
            Job = new ConsolidationJob
            {
                SessionId = "debug:evolution:skill.curate:request-1",
                WorkspaceId = "workspace-evolution",
                AgentId = "agent-evolution",
                AgentTemplateId = "agent-evolution",
            },
        };
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.JobCompleted.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);
        return queue;
    }

    /// <summary>门禁策略（阈值必须显式：产品内不得有默认策略工厂）。</summary>
    private static SkillCurationPolicy CurationPolicy() => SkillCurationPolicy.Create(
        policyId: "skill-curation/probe",
        version: 1,
        minRetainedValueRatio: 0.5,
        applicabilityHeadings: ["何时适用"],
        pitfallHeadings: ["陷阱与反例"],
        minMarkdownLength: 60);

    private static DistilledSkillProduct CurationProduct(
        string[]? keywords = null,
        string[]? turns = null,
        string[]? sessions = null)
        => new()
        {
            Name = "把同族经验收敛为可迁移程序",
            Description = "G7 接线探针产物",
            Markdown = CurationCleanMarkdown(),
            Keywords = keywords ?? ["会话治理"],
            EvidenceTags = CurationTags(turns ?? ["turn-1"], sessions ?? ["session-1"]),
            ReplacedSkillIds = ["skill-a"],
        };

    /// <summary>正文**不得**出现 provenance 里的 turn/session id（P3）；标题命中靠去掉 # 后的前缀匹配。</summary>
    private static string CurationCleanMarkdown()
        => "# 何时适用\n\n"
            + "当需要把多条同族经验收敛为一条可迁移程序时使用；单条具体操作不要使用本技能。\n\n"
            + "## 陷阱与反例\n\n"
            + "把会话标识或工具名写进正文会让产物退化成笔记，因此一律只放审计 tags。";

    private static List<string> CurationTags(string[] turns, string[] sessions)
    {
        var tags = new List<string> { "auto-generated" };
        foreach (var turn in turns)
        {
            tags.Add("source-turn:" + turn);
        }

        foreach (var session in sessions)
        {
            tags.Add("source-session:" + session);
        }

        return tags;
    }

    /// <summary>可注入产物的来源替身（默认实现是空生产者，这里用于模拟"有真实提炼器"）。</summary>
    private sealed class FakeSkillDistillationSource : ISkillDistillationSource
    {
        private readonly IReadOnlyList<DistilledSkillProduct> _products;

        public FakeSkillDistillationSource(IReadOnlyList<DistilledSkillProduct> products)
            => _products = products;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DistilledSkillProduct>> GetProductsAsync(
            string workspaceId,
            string agentInstanceId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_products);
        }
    }

    /// <summary>
    /// 带 tags / keywords 的技能仓探针：三个写方法一律**计数并抛异常** —— 这样"零写盘"是被
    /// 证明的（写盘尝试 = 异常 + 计数非零），而不是靠读代码看着没有。
    /// </summary>
    private sealed class CurationProbeSkillStore : IAgentSkillEvolutionStore
    {
        private int _writeCallCount;
        public int WriteCallCount => _writeCallCount;

        private static AgentSkillEvolutionDocument Skill(
            string id,
            string name,
            string version,
            IReadOnlyList<string>? tags = null,
            IReadOnlyList<string>? keywords = null) => new()
            {
                SkillId = id,
                Name = name,
                Version = version,
                Enabled = true,
                Tags = tags ?? [],
                Keywords = keywords ?? [],
                Markdown = $"---\nname: {name}\nversion: {version}\n---\nbody",
            };

        public Task<IReadOnlyList<AgentSkillEvolutionDocument>> ListAutoGeneratedAsync(
            string agentInstanceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkillEvolutionDocument>>(
            [
                // 同名冗余（保留 retire 建议语义）：skill-a / skill-b
                Skill("skill-a", "Same Name", "1.0.0", tags: ["source-turn:turn-1", "source-session:session-1"]),
                Skill("skill-b", "Same Name", "1.0.1"),
                // 第三方启用技能：其关键词是 C3 的判定域（产物不得与之相交）
                Skill("skill-c", "Other Name", "1.0.0", keywords: ["登录流程"]),
            ]);

        public Task<AgentSkillEvolutionDocument?> GetAsync(
            string agentInstanceId, string skillId, CancellationToken ct = default)
            => Task.FromResult<AgentSkillEvolutionDocument?>(null);

        public Task<AgentSkillEvolutionDocument> CreateAsync(
            string agentInstanceId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G7 curation must not write skills (CreateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> UpdateAsync(
            string agentInstanceId, string skillId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G7 curation must not write skills (UpdateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> SetEnabledAsync(
            string agentInstanceId, string skillId, bool enabled, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G7 curation must not write skills (SetEnabledAsync).");
        }
    }

    private sealed class ProbeSkillStore : IAgentSkillEvolutionStore
    {
        private int _writeCallCount;
        public int WriteCallCount => _writeCallCount;

        private static AgentSkillEvolutionDocument Skill(string id, string name, string version) => new()
        {
            SkillId = id,
            Name = name,
            Version = version,
            Enabled = true,
            Markdown = $"---\nname: {name}\nversion: {version}\n---\nbody",
        };

        public Task<IReadOnlyList<AgentSkillEvolutionDocument>> ListAutoGeneratedAsync(
            string agentInstanceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkillEvolutionDocument>>(
            [
                Skill("skill-a", "Same Name", "1.0.0"),
                Skill("skill-b", "Same Name", "1.0.1"),
                Skill("skill-c", "Other Name", "1.0.0"),
            ]);

        public Task<AgentSkillEvolutionDocument?> GetAsync(
            string agentInstanceId, string skillId, CancellationToken ct = default)
            => Task.FromResult<AgentSkillEvolutionDocument?>(null);

        public Task<AgentSkillEvolutionDocument> CreateAsync(
            string agentInstanceId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (CreateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> UpdateAsync(
            string agentInstanceId, string skillId, AgentSkillEvolutionWriteRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (UpdateAsync).");
        }

        public Task<AgentSkillEvolutionDocument> SetEnabledAsync(
            string agentInstanceId, string skillId, bool enabled, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writeCallCount);
            throw new InvalidOperationException("G6 curation must not write skills (SetEnabledAsync).");
        }
    }

    private sealed class ThrowingMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId,
            string? parentSessionId = null) => throw new NotSupportedException();
        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) => throw new NotSupportedException();
        public void ClearSession(string sessionId) => throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryLibrarian : IMemoryLibrarian
    {
        public Task<ExperienceWriteResult> IngestExperienceAsync(
            MemoryIngestionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<MemoryTreeOperation>> PlanTreeMaintenanceAsync(
            string workspaceId, string libraryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ApplyTreeOperationAsync(MemoryTreeOperation operation, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => throw new NotSupportedException();
        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingSkillTrajectorySource : ISkillEvolutionTrajectorySource
    {
        public Task<IReadOnlyList<SkillEvolutionTrajectory>> GetRecentSuccessfulAsync(
            string workspaceId, string agentInstanceId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class KeyTrackingJobQueue : ISubconsciousJobQueue
    {
        private readonly Dictionary<string, SubconsciousJobQueueItem> _items = new(StringComparer.Ordinal);
        public List<SubconsciousJobEnqueueRequest> Requests { get; } = [];

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var item = new SubconsciousJobQueueItem
            {
                JobId = $"job-{Requests.Count}",
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                Job = request.Job,
            };
            _items[request.IdempotencyKey] = item;
            return Task.FromResult(item);
        }

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query, CancellationToken ct = default)
            => Task.FromResult(
                query.IdempotencyKey is not null
                && _items.TryGetValue(query.IdempotencyKey, out var item)
                    ? item
                    : null);

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner, TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null, CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobQueueItem?>(null);
        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());
        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RecordResultAsync(
            string jobId, string leaseOwner, SubconsciousJobResultEnvelope result, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId, CancellationToken ct = default)
            => Task.FromResult<SubconsciousJobResultEnvelope?>(null);
        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string> RetryAsync(
            string jobId, string leaseOwner, string error, TimeSpan? retryDelay = null, CancellationToken ct = default)
            => Task.FromResult("retrying");
        public Task DeadLetterAsync(
            string jobId, string leaseOwner, string error, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [TestMethod]
    [DataRow(SubconsciousJobTypes.AutoDream, SubconsciousJobResultKinds.MemoryAutoDream, 2)]
    [DataRow(SubconsciousJobTypes.ExtractPatterns, SubconsciousJobResultKinds.SkillPatternExtraction, 3)]
    [DataRow(SubconsciousJobTypes.ImproveSkills, SubconsciousJobResultKinds.SkillImprovement, 2)]
    [DataRow(SubconsciousJobTypes.SkillCurate, SubconsciousJobResultKinds.SkillCuration, 0)]
    public async Task DurableWorker_PeriodicEvolutionJob_ShouldPersistReportBeforeCompleting(
        string jobType,
        string expectedResultKind,
        int expectedOperationCount)
    {
        var queue = new RecordingSubconsciousJobQueue
        {
            JobType = jobType,
            Job = new ConsolidationJob
            {
                SessionId = $"debug:evolution:{jobType}:request-1",
                WorkspaceId = "workspace-evolution",
                AgentId = "agent-evolution",
                AgentTemplateId = "agent-evolution",
            },
        };
        var orchestrator = new RecordingSubconsciousOrchestrator();
        var worker = new SubconsciousWorkerService(
            Channel.CreateUnbounded<ConsolidationJob>(),
            orchestrator,
            NullLogger<SubconsciousWorkerService>.Instance,
            jobQueue: queue);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await queue.JobCompleted.Task.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(queue.ResultWasRecordedBeforeComplete);
        Assert.AreEqual(1, orchestrator.CallCount);
        Assert.AreEqual(1, queue.CompleteCount);
        Assert.IsNotNull(queue.RecordedResult);
        Assert.AreEqual(expectedResultKind, queue.RecordedResult!.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Completed, queue.RecordedResult.Status);
        Assert.AreEqual(SubconsciousJobResultDecisions.ExecutionCompleted, queue.RecordedResult.Decision);
        Assert.AreEqual(SubconsciousJobResultNextActions.CompleteJob, queue.RecordedResult.NextAction);
        Assert.IsTrue(queue.RecordedResult.Valid);
        Assert.AreEqual(expectedOperationCount, queue.RecordedResult.OperationCount);
        Assert.AreEqual("workspace-evolution", queue.RecordedResult.Metadata["workspace_id"]);
        Assert.AreEqual("agent-evolution", queue.RecordedResult.Metadata["agent_instance_id"]);
        Assert.AreEqual("job-1", queue.RecordedResult.Metadata["subconscious_job_id"]);
        Assert.AreEqual(jobType, queue.RecordedResult.Metadata["job_type"]);

        switch (jobType)
        {
            case SubconsciousJobTypes.AutoDream:
                Assert.AreEqual("2", queue.RecordedResult.Metadata["executed_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["merged_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["archived_count"]);
                break;
            case SubconsciousJobTypes.ExtractPatterns:
                Assert.AreEqual("3", queue.RecordedResult.Metadata["candidates_found_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["promoted_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["merged_count"]);
                Assert.AreEqual("0", queue.RecordedResult.Metadata["deferred_count"]);
                Assert.AreEqual("skill-create-pr", queue.RecordedResult.Metadata["created_skill_ids"]);
                Assert.AreEqual("skill-health-check", queue.RecordedResult.Metadata["updated_skill_ids"]);
                break;
            case SubconsciousJobTypes.ImproveSkills:
                Assert.AreEqual("2", queue.RecordedResult.Metadata["evaluated_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["patched_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["consolidated_count"]);
                Assert.AreEqual("skill-create-pr", queue.RecordedResult.Metadata["improved_skill_ids"]);
                Assert.AreEqual("skill-create-pr-old", queue.RecordedResult.Metadata["disabled_duplicate_skill_ids"]);
                break;
            case SubconsciousJobTypes.SkillCurate:
                Assert.AreEqual("6", queue.RecordedResult.Metadata["n_before"]);
                Assert.AreEqual("6", queue.RecordedResult.Metadata["n_after"]);
                Assert.IsTrue(!string.IsNullOrWhiteSpace(queue.RecordedResult.Metadata["not_reduced_reason"]));
                Assert.AreEqual("2", queue.RecordedResult.Metadata["candidate_count"]);
                Assert.AreEqual("1", queue.RecordedResult.Metadata["retire_suggestion_count"]);
                Assert.AreEqual("v1", queue.RecordedResult.Metadata["report_version"]);
                break;
        }
    }

    private const string ValidPlanJson = """
        {
          "planId": "plan-1",
          "workspaceId": "workspace-1",
          "source": {
            "workspaceId": "workspace-1",
            "sessionId": "session-1",
            "subconsciousJobId": "job-1",
            "agentId": "agent-1",
            "agentTemplateId": "template-1"
          },
          "operations": [
            {
              "operationId": "op-1",
              "action": "append_new",
              "proposedContent": "User prefers concise engineering summaries.",
              "confidence": 0.84,
              "rationale": "Stable preference from session evidence."
            }
          ],
          "confidence": 0.84,
          "rationale": "Dry-run plan only."
        }
        """;

    private const string PageUpdateJson = """
        {
          "schema": "pudding.memory_wiki_page_update.v1",
          "updates": [
            {
              "book": "用户偏好",
              "page": "/设计",
              "content": "# 设计\n\n- 用户偏好简单 V1。"
            }
          ]
        }
        """;

    private sealed class RecordingSubconsciousJobQueue : ISubconsciousJobQueue
    {
        private int _leaseCount;
        private int _lookupCount;

        public TaskCompletionSource ResultRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllPeriodicJobsEnqueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JobCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubconsciousJobResultEnvelope? RecordedResult { get; private set; }
        public bool ResultWasRecordedBeforeComplete { get; private set; }
        public int CompleteCount { get; private set; }
        public int LeaseCount => _leaseCount;
        public int LookupCount => _lookupCount;
        public ConsolidationJob? Job { get; init; }
        public string JobType { get; init; } = SubconsciousJobTypes.MemoryConsolidateSession;
        public bool DisableLeasing { get; init; }
        public SubconsciousJobQueueItem? ExistingLookupItem { get; init; }
        private readonly List<SubconsciousJobEnqueueRequest> _enqueuedRequests = [];
        public IReadOnlyList<SubconsciousJobEnqueueRequest> EnqueuedRequests
        {
            get
            {
                lock (_enqueuedRequests)
                    return _enqueuedRequests.ToArray();
            }
        }

        public Task<SubconsciousJobQueueItem> EnqueueAsync(
            SubconsciousJobEnqueueRequest request,
            CancellationToken ct = default)
        {
            lock (_enqueuedRequests)
            {
                _enqueuedRequests.Add(request);
                if (_enqueuedRequests.Count >= 4)
                    AllPeriodicJobsEnqueued.TrySetResult();
            }

            return Task.FromResult(new SubconsciousJobQueueItem
            {
                JobId = Guid.NewGuid().ToString("N"),
                JobType = request.JobType,
                IdempotencyKey = request.IdempotencyKey,
                Status = "pending",
                Job = request.Job,
            });
        }

        public Task<SubconsciousJobQueueItem?> LeaseNextAsync(
            string leaseOwner,
            TimeSpan leaseDuration,
            SubconsciousJobLeaseQuery? query = null,
            CancellationToken ct = default)
        {
            if (DisableLeasing)
                return Task.FromResult<SubconsciousJobQueueItem?>(null);

            if (Interlocked.Increment(ref _leaseCount) > 1)
                return Task.FromResult<SubconsciousJobQueueItem?>(null);

            return Task.FromResult<SubconsciousJobQueueItem?>(new SubconsciousJobQueueItem
            {
                JobId = "job-1",
                JobType = JobType,
                IdempotencyKey = "memory:workspace-1:session-1:cmp-1",
                Status = "processing",
                Job = Job ?? new ConsolidationJob
                {
                    SessionId = "session-1",
                    WorkspaceId = "workspace-1",
                    AgentId = "agent-1",
                    AgentTemplateId = "template-1",
                    LastUserMessage = "Please keep summaries concise.",
                    LastAssistantReply = "I will keep the engineering summary concise.",
                },
            });
        }

        public Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new SubconsciousJobQueueStats());

        public Task<SubconsciousJobQueueItem?> FindLatestAsync(
            SubconsciousJobLookupQuery query,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _lookupCount);
            return Task.FromResult(ExistingLookupItem);
        }

        public Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
            DateTimeOffset since,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(
                new Dictionary<string, int>(StringComparer.Ordinal));

        public Task RecordSchedulingSkipAsync(
            SubconsciousSchedulingSkipRequest request,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecordResultAsync(
            string jobId,
            string leaseOwner,
            SubconsciousJobResultEnvelope result,
            CancellationToken ct = default)
        {
            RecordedResult = result;
            ResultRecorded.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<SubconsciousJobResultEnvelope?> GetResultAsync(
            string jobId,
            CancellationToken ct = default)
            => Task.FromResult(RecordedResult);

        public Task CompleteAsync(string jobId, string leaseOwner, CancellationToken ct = default)
        {
            ResultWasRecordedBeforeComplete = RecordedResult is not null;
            CompleteCount++;
            JobCompleted.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<string> RetryAsync(
            string jobId,
            string leaseOwner,
            string error,
            TimeSpan? retryDelay = null,
            CancellationToken ct = default)
            => Task.FromResult("retrying");

        public Task DeadLetterAsync(
            string jobId,
            string leaseOwner,
            string error,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingSubconsciousOrchestrator : ISubconsciousOrchestrator
    {
        public int CallCount { get; private set; }

        public Task ConsolidateAsync(
            ConsolidationJob job,
            string mode,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public Task<SessionSummary> SummarizeSessionAsync(
            string sessionId,
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> RecallAugmentedAsync(
            string userMessage,
            string workspaceId,
            string agentId,
            string? sessionId = null,
            int maxTokens = 2000,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryDashboard> GetMemoryDashboardAsync(
            string workspaceId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySearchResult> SearchMemoriesAsync(
            MemorySearchRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AutoDreamReport> AutoDreamAsync(
            string workspaceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new AutoDreamReport
            {
                DurationMs = 12,
                Merged = 1,
                Archived = 1,
                Deleted = 0,
                Suggested = 3,
                Executed = 2,
                Summary = "merged 1, archived 1, deleted 0",
                Timestamp = new DateTime(2026, 7, 30, 4, 0, 0, DateTimeKind.Utc),
            });
        }

        public Task<PatternExtractionReport> ExtractPatternsAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new PatternExtractionReport
            {
                DurationMs = 23,
                CandidatesFound = 3,
                Promoted = 1,
                Merged = 1,
                Deferred = 0,
                DemotedToMemory = 1,
                Skipped = 1,
                CreatedSkillIds = ["skill-create-pr"],
                UpdatedSkillIds = ["skill-health-check"],
                Summary = "found 3, promoted 1, demoted 1, skipped 1",
                Timestamp = new DateTime(2026, 7, 30, 4, 1, 0, DateTimeKind.Utc),
            });
        }

        public Task<SkillImprovementReport> ImproveSkillsAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new SkillImprovementReport
            {
                DurationMs = 34,
                Evaluated = 2,
                Patched = 1,
                Consolidated = 1,
                Skipped = 1,
                ImprovedSkillIds = ["skill-create-pr"],
                DisabledDuplicateSkillIds = ["skill-create-pr-old"],
                Summary = "Improved 1 skill",
                Timestamp = new DateTime(2026, 7, 30, 4, 2, 0, DateTimeKind.Utc),
            });
        }

        public Task<SkillCurationReport> SkillCurateAsync(
            string workspaceId,
            string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null,
            SkillFamilyPolicy? familyPolicy = null,
            SkillPortfolioPolicy? portfolioPolicy = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new SkillCurationReport
            {
                DurationMs = 45,
                NBefore = 6,
                NAfter = 6,
                NotReducedReason = "G6 is report-only: identified 1 duplicate-name retire suggestion(s), but curation has no disable authority until G7 gates land.",
                CandidateCount = 2,
                RetireSuggestionCount = 1,
                Summary = "n_before=6, n_after=6, refine candidates=2, retire suggestions=1; report-only, no skills modified",
                Timestamp = new DateTime(2026, 7, 30, 4, 3, 0, DateTimeKind.Utc),
            });
        }
    }

    private sealed class StaticMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);

        public Task<string> ChatWithConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);
    }

    private sealed class PausedSubconsciousRuntimeControl : ISubconsciousRuntimeControl
    {
        public bool IsPaused => true;

        public Task<SubconsciousRuntimeControlSnapshot> StartAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousRuntimeControlSnapshot> StopAsync(
            SubconsciousRuntimeControlRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SubconsciousRuntimeControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static async Task<MemoryScope> CreateMemoryScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new MemoryScope(connection, new MemoryLibrary(factory));
    }

    private sealed class MemoryScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public MemoryScope(SqliteConnection connection, IMemoryLibrary library)
        {
            _connection = connection;
            Library = library;
        }

        public IMemoryLibrary Library { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }
}
