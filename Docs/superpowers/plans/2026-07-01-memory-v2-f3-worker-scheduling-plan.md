# Memory v2 F3 Worker Scheduling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a scheduling gate in front of durable `SubconsciousJobs` execution so background memory maintenance respects idle windows, workspace limits, budgets, and diagnostics before any subconscious LLM plan execution is introduced.

**Architecture:** Keep `SubconsciousJobs` as the durable queue and insert a runtime-level `SubconsciousJobScheduler` between `SubconsciousWorkerService` and `ISubconsciousJobQueue.LeaseNextAsync`. The scheduler evaluates global idle state, agent availability, queue backoff, workspace/global concurrency, per-window budget, and dry-run mode, then records structured skip/lease decisions through the existing RuntimeActivity and TelemetryMetric paths.

**Tech Stack:** C#/.NET hosted services, existing Pudding runtime DI, SQLite-backed `SubconsciousJobQueue`, MSTest, Python stdlib diagnostics in `Tools/Diagnostics/query_metrics.py`.

---

## Source Design References

- Spec: `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
- Requirements: `memory/memory-system-v2-requirements.md`
- Current worker: `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- Current queue: `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`
- Current options: `Source/PuddingCore/Configuration/SubconsciousOptions.cs`
- Current DTO/interface: `Source/PuddingCore/Platform/SubconsciousDtos.cs`, `Source/PuddingCore/Abstractions/ISubconsciousOrchestrator.cs`
- Current diagnostics: `Tools/Diagnostics/query_metrics.py`

## File Structure

Create:

- `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs`
- `Source/PuddingRuntimeTests/Services/SubconsciousJobSchedulerTests.cs`

Modify:

- `Source/PuddingCore/Configuration/SubconsciousOptions.cs`
- `Source/PuddingCore/Platform/SubconsciousDtos.cs`
- `Source/PuddingCore/Abstractions/ISubconsciousOrchestrator.cs`
- `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`
- `Source/PuddingMemoryEngineTests/SubconsciousJobQueueTests.cs`
- `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- `Source/PuddingRuntime/DependencyInjection.cs`
- `Source/PuddingAgent/Program.cs`
- `Source/PuddingRuntimeTests/Services/RuntimeServiceExtensionsTests.cs`
- `Tools/Diagnostics/query_metrics.py`
- `Tools/Diagnostics/tests/test_query_metrics.py`
- `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
- `memory/memory-system-v2-requirements.md`
- `goal.md`

Do not modify:

- MemoryLibrary write paths.
- `SubconsciousOrchestrator` consolidation logic.
- Hook publisher/handler payload semantics.
- Database schema unless a test proves existing fields cannot support the scheduling gate.

---

## Task 1: Add Scheduling Contracts And Options

**Files:**

- Modify: `Source/PuddingCore/Configuration/SubconsciousOptions.cs`
- Modify: `Source/PuddingCore/Platform/SubconsciousDtos.cs`
- Modify: `Source/PuddingCore/Abstractions/ISubconsciousOrchestrator.cs`

- [ ] **Step 1: Add failing options/contract tests**

Add to `Source/PuddingRuntimeTests/Services/RuntimeServiceExtensionsTests.cs`:

```csharp
[TestMethod]
public void AddPuddingRuntime_BindsSubconsciousSchedulingOptions()
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subconscious:Scheduling:Enabled"] = "true",
            ["Subconscious:Scheduling:DryRun"] = "true",
            ["Subconscious:Scheduling:IdleCooldownSeconds"] = "15",
            ["Subconscious:Scheduling:MaxWorkspaceConcurrentJobs"] = "2",
        })
        .Build();

    using var provider = new ServiceCollection()
        .AddLogging()
        .AddPuddingRuntime(configuration)
        .BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<SubconsciousOptions>>().Value;

    Assert.IsTrue(options.Scheduling.Enabled);
    Assert.IsTrue(options.Scheduling.DryRun);
    Assert.AreEqual(15, options.Scheduling.IdleCooldownSeconds);
    Assert.AreEqual(2, options.Scheduling.MaxWorkspaceConcurrentJobs);
}
```

- [ ] **Step 2: Run test and verify it fails**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~RuntimeServiceExtensionsTests"
```

Expected: compile fails because `SubconsciousOptions.Scheduling` does not exist.

- [ ] **Step 3: Extend `SubconsciousOptions`**

Update `Source/PuddingCore/Configuration/SubconsciousOptions.cs`:

```csharp
namespace PuddingCode.Configuration;

/// <summary>Feature flags and scheduling controls for subconscious memory maintenance.</summary>
public sealed class SubconsciousOptions
{
    public const string SectionName = "Subconscious";

    /// <summary>
    /// Enables the legacy agent-loop hook that writes directly to the in-memory consolidation channel.
    /// Keep disabled when durable Hook v2 jobs are the primary maintenance path.
    /// </summary>
    public bool EnableLegacyConsolidationHook { get; init; }

    /// <summary>
    /// Enables the older AgentExecutionService channel fallback when no legacy hook is registered.
    /// This is off by default to prevent duplicate learning beside durable SubconsciousJobs.
    /// </summary>
    public bool EnableLegacyAgentExecutionFallback { get; init; }

    /// <summary>Scheduling controls for durable subconscious jobs.</summary>
    public SubconsciousSchedulingOptions Scheduling { get; init; } = new();
}

/// <summary>Runtime scheduling controls for durable subconscious background jobs.</summary>
public sealed class SubconsciousSchedulingOptions
{
    public bool Enabled { get; init; } = true;
    public bool DryRun { get; init; }
    public int TickIntervalSeconds { get; init; } = 2;
    public int IdleCooldownSeconds { get; init; } = 60;
    public bool ForegroundGenerationBlocksExecution { get; init; } = true;
    public int MaxGlobalConcurrentJobs { get; init; } = 1;
    public int MaxWorkspaceConcurrentJobs { get; init; } = 1;
    public int MaxSessionConcurrentJobs { get; init; } = 1;
    public int MaxJobsPerTick { get; init; } = 1;
    public int MaxJobsPerWorkspacePerHour { get; init; } = 20;
    public int MaxRetryAttempts { get; init; } = 3;
    public int RetryBackoffSeconds { get; init; } = 60;
    public int BudgetWindowMinutes { get; init; } = 60;
}
```

- [ ] **Step 4: Add scheduling DTOs**

Append to `Source/PuddingCore/Platform/SubconsciousDtos.cs`:

```csharp
public static class SubconsciousSchedulingSkipReasons
{
    public const string Disabled = "skip_disabled";
    public const string DryRun = "would_lease";
    public const string ForegroundBusy = "skip_foreground_busy";
    public const string Cooldown = "skip_cooldown";
    public const string WorkspaceLimit = "skip_workspace_limit";
    public const string GlobalLimit = "skip_global_limit";
    public const string SessionLimit = "skip_session_limit";
    public const string BudgetExhausted = "skip_budget_exhausted";
    public const string BackoffNotElapsed = "skip_backoff_not_elapsed";
    public const string NoEligibleJob = "skip_no_eligible_job";
}

public sealed record SubconsciousJobLeaseQuery
{
    public IReadOnlySet<string> ExcludedWorkspaceIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> ExcludedSessionIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public int? MaxRetryCount { get; init; }
}

public sealed record SubconsciousJobQueueStats
{
    public int Pending { get; init; }
    public int Retrying { get; init; }
    public int Processing { get; init; }
    public int Completed { get; init; }
    public int DeadLetter { get; init; }
    public IReadOnlyDictionary<string, int> ProcessingByWorkspace { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> ProcessingBySession { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed record SubconsciousSchedulingSkipRequest
{
    public required string Reason { get; init; }
    public string? JobId { get; init; }
    public string? JobType { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? SourceHookName { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
```

- [ ] **Step 5: Extend `ISubconsciousJobQueue`**

Update `Source/PuddingCore/Abstractions/ISubconsciousOrchestrator.cs`:

```csharp
Task<SubconsciousJobQueueItem?> LeaseNextAsync(
    string leaseOwner,
    TimeSpan leaseDuration,
    SubconsciousJobLeaseQuery? query = null,
    CancellationToken ct = default);

Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default);

Task RecordSchedulingSkipAsync(
    SubconsciousSchedulingSkipRequest request,
    CancellationToken ct = default);
```

Keep the existing three-parameter `LeaseNextAsync` call compatible by using the optional `query = null` parameter.

- [ ] **Step 6: Run contract tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~RuntimeServiceExtensionsTests"
```

Expected: contract/option compile issues move to queue fake implementations. Fix fakes in later tasks.

---

## Task 2: Add Queue Filtering, Stats, And Scheduling Metrics

**Files:**

- Modify: `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`
- Modify: `Source/PuddingMemoryEngineTests/SubconsciousJobQueueTests.cs`

- [ ] **Step 1: Add failing queue tests**

Add tests to `Source/PuddingMemoryEngineTests/SubconsciousJobQueueTests.cs`:

```csharp
[TestMethod]
public async Task LeaseNextAsync_ShouldSkipExcludedWorkspace()
{
    await using var scope = await TestScope.CreateAsync();
    var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
    await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
    await queue.EnqueueAsync(CreateRequest("session-2", "cmp-2") with
    {
        IdempotencyKey = "memory:workspace-2:session-2:cmp-2",
        Job = new ConsolidationJob
        {
            SessionId = "session-2",
            WorkspaceId = "workspace-2",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
        },
    });

    var leased = await queue.LeaseNextAsync(
        "worker-1",
        TimeSpan.FromMinutes(5),
        new SubconsciousJobLeaseQuery
        {
            ExcludedWorkspaceIds = new HashSet<string>(StringComparer.Ordinal) { "workspace-1" },
        });

    Assert.IsNotNull(leased);
    Assert.AreEqual("workspace-2", leased.Job.WorkspaceId);
}

[TestMethod]
public async Task GetStatsAsync_ShouldCountProcessingByWorkspace()
{
    await using var scope = await TestScope.CreateAsync();
    var queue = new SubconsciousJobQueue(scope.Factory, NullLogger<SubconsciousJobQueue>.Instance);
    await queue.EnqueueAsync(CreateRequest("session-1", "cmp-1"));
    await queue.EnqueueAsync(CreateRequest("session-2", "cmp-2"));
    _ = await queue.LeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

    var stats = await queue.GetStatsAsync();

    Assert.AreEqual(1, stats.Pending);
    Assert.AreEqual(1, stats.Processing);
    Assert.AreEqual(1, stats.ProcessingByWorkspace["workspace-1"]);
}

[TestMethod]
public async Task RecordSchedulingSkipAsync_ShouldRecordTelemetryMetric()
{
    await using var scope = await TestScope.CreateAsync();
    var telemetry = new RecordingTelemetryMetricSink();
    var queue = new SubconsciousJobQueue(
        scope.Factory,
        NullLogger<SubconsciousJobQueue>.Instance,
        telemetrySink: telemetry);

    await queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
    {
        Reason = SubconsciousSchedulingSkipReasons.Cooldown,
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentId = "agent-1",
        AgentTemplateId = "template-1",
        JobType = SubconsciousJobTypes.MemoryConsolidateSession,
    });

    var metric = telemetry.Metrics.Single(m => m.Name == "subconscious_job.schedule_skip");
    Assert.AreEqual(TelemetryMetricCategories.Memory, metric.Category);
    Assert.AreEqual(TelemetryMetricStatuses.Deferred, metric.Status);
    Assert.AreEqual("skip_cooldown", metric.Dimensions!["skip_reason"]);
    Assert.AreEqual("workspace-1", metric.Dimensions!["workspace_id"]);
}
```

- [ ] **Step 2: Run queue tests and verify failure**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --filter "FullyQualifiedName~SubconsciousJobQueueTests"
```

Expected: compile fails because new queue methods and overload signature are missing.

- [ ] **Step 3: Update `LeaseNextAsync` signature and query filter**

In `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs`, change both public and private lease methods to accept `SubconsciousJobLeaseQuery? query`.

Use this query fragment in `LeaseNextCoreAsync` before ordering:

```csharp
var candidates = db.SubconsciousJobs
    .Where(j => (j.Status == "pending" || j.Status == "retrying" || j.Status == "processing")
        && j.AvailableAt <= now
        && (j.LeaseUntil == null || j.LeaseUntil <= now));

if (query?.MaxRetryCount is not null)
    candidates = candidates.Where(j => j.RetryCount <= query.MaxRetryCount.Value);

if (query?.ExcludedWorkspaceIds.Count > 0)
    candidates = candidates.Where(j => !query.ExcludedWorkspaceIds.Contains(j.WorkspaceId));

if (query?.ExcludedSessionIds.Count > 0)
    candidates = candidates.Where(j => !query.ExcludedSessionIds.Contains(j.SessionId));

var entity = await candidates
    .OrderBy(j => j.CreatedAt)
    .FirstOrDefaultAsync(ct);
```

- [ ] **Step 4: Add queue stats implementation**

Add to `SubconsciousJobQueue`:

```csharp
public async Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
{
    await using var db = await _dbFactory.CreateDbContextAsync(ct);
    var rows = await db.SubconsciousJobs
        .Select(j => new { j.Status, j.WorkspaceId, j.SessionId })
        .ToListAsync(ct);

    return new SubconsciousJobQueueStats
    {
        Pending = rows.Count(j => j.Status == "pending"),
        Retrying = rows.Count(j => j.Status == "retrying"),
        Processing = rows.Count(j => j.Status == "processing"),
        Completed = rows.Count(j => j.Status == "completed"),
        DeadLetter = rows.Count(j => j.Status == "dead_letter"),
        ProcessingByWorkspace = rows
            .Where(j => j.Status == "processing")
            .GroupBy(j => j.WorkspaceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        ProcessingBySession = rows
            .Where(j => j.Status == "processing")
            .GroupBy(j => j.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
    };
}
```

- [ ] **Step 5: Add scheduling skip telemetry/activity**

Add to `SubconsciousJobQueue`:

```csharp
public async Task RecordSchedulingSkipAsync(
    SubconsciousSchedulingSkipRequest request,
    CancellationToken ct = default)
{
    await RecordSchedulingSkipActivityAsync(request, ct);
    await RecordSchedulingSkipMetricAsync(request, ct);
}
```

Implement helper methods using `RuntimeActivityComponents.Memory`, `RuntimeActivityStatuses.Deferred`, `TelemetryMetricCategories.Memory`, `TelemetryMetricStatuses.Deferred`, and metric name `subconscious_job.schedule_skip`. Dimensions must include `skip_reason`, `workspace_id`, `session_id`, `agent_id`, `agent_template_id`, `job_type`, and optional source fields when present.

- [ ] **Step 6: Run queue tests**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --filter "FullyQualifiedName~SubconsciousJobQueueTests"
```

Expected: all `SubconsciousJobQueueTests` pass.

---

## Task 3: Create Runtime Scheduler

**Files:**

- Create: `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs`
- Create: `Source/PuddingRuntimeTests/Services/SubconsciousJobSchedulerTests.cs`

- [ ] **Step 1: Add failing scheduler tests**

Create `Source/PuddingRuntimeTests/Services/SubconsciousJobSchedulerTests.cs` with tests for disabled, cooldown, dry-run, workspace limit, and successful lease.

Core test shape:

```csharp
[TestClass]
public sealed class SubconsciousJobSchedulerTests
{
    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldSkip_WhenCooldownHasNotElapsed()
    {
        var idle = new FakeIdleDetector(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        var queue = new FakeQueue();
        var scheduler = CreateScheduler(queue, idle, options: new SubconsciousSchedulingOptions
        {
            Enabled = true,
            IdleCooldownSeconds = 60,
        });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.Cooldown, queue.Skips.Single().Reason);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldReturnNullAndRecordWouldLease_WhenDryRun()
    {
        var queue = new FakeQueue { Next = SampleJob("job-1", "workspace-1", "session-1") };
        var scheduler = CreateScheduler(queue, new FakeIdleDetector(DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromMinutes(5)),
            options: new SubconsciousSchedulingOptions { Enabled = true, DryRun = true, IdleCooldownSeconds = 1 });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNull(result);
        Assert.AreEqual(SubconsciousSchedulingSkipReasons.DryRun, queue.Skips.Single().Reason);
        Assert.AreEqual(0, queue.LeaseCalls);
    }

    [TestMethod]
    public async Task TryLeaseNextAsync_ShouldLease_WhenIdleAndWithinLimits()
    {
        var queue = new FakeQueue { Next = SampleJob("job-1", "workspace-1", "session-1") };
        var scheduler = CreateScheduler(queue, new FakeIdleDetector(DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromMinutes(5)),
            options: new SubconsciousSchedulingOptions { Enabled = true, IdleCooldownSeconds = 1 });

        var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

        Assert.IsNotNull(result);
        Assert.AreEqual("job-1", result.JobId);
        Assert.AreEqual(1, queue.LeaseCalls);
    }
}
```

- [ ] **Step 2: Run scheduler tests and verify failure**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SubconsciousJobSchedulerTests"
```

Expected: compile fails because scheduler does not exist.

- [ ] **Step 3: Create scheduler**

Create `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// Decides whether durable subconscious jobs may be leased in the current runtime window.
/// </summary>
public sealed class SubconsciousJobScheduler
{
    private readonly ISubconsciousJobQueue _queue;
    private readonly IIdleDetector? _idleDetector;
    private readonly IOptions<SubconsciousOptions> _options;
    private readonly ILogger<SubconsciousJobScheduler> _logger;

    public SubconsciousJobScheduler(
        ISubconsciousJobQueue queue,
        IOptions<SubconsciousOptions> options,
        ILogger<SubconsciousJobScheduler> logger,
        IIdleDetector? idleDetector = null)
    {
        _queue = queue;
        _options = options;
        _logger = logger;
        _idleDetector = idleDetector;
    }

    public async Task<SubconsciousJobQueueItem?> TryLeaseNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var scheduling = _options.Value.Scheduling;
        if (!scheduling.Enabled)
        {
            await _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
            {
                Reason = SubconsciousSchedulingSkipReasons.Disabled,
            }, ct);
            return null;
        }

        if (_idleDetector is not null && _idleDetector.IdleDuration < TimeSpan.FromSeconds(scheduling.IdleCooldownSeconds))
        {
            await _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
            {
                Reason = SubconsciousSchedulingSkipReasons.Cooldown,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["idle_seconds"] = ((int)_idleDetector.IdleDuration.TotalSeconds).ToString(),
                    ["required_idle_seconds"] = scheduling.IdleCooldownSeconds.ToString(),
                },
            }, ct);
            return null;
        }

        var stats = await _queue.GetStatsAsync(ct);
        if (stats.Processing >= scheduling.MaxGlobalConcurrentJobs)
        {
            await _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
            {
                Reason = SubconsciousSchedulingSkipReasons.GlobalLimit,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["processing"] = stats.Processing.ToString(),
                    ["limit"] = scheduling.MaxGlobalConcurrentJobs.ToString(),
                },
            }, ct);
            return null;
        }

        var excludedWorkspaces = stats.ProcessingByWorkspace
            .Where(pair => pair.Value >= scheduling.MaxWorkspaceConcurrentJobs)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (scheduling.DryRun)
        {
            await _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
            {
                Reason = SubconsciousSchedulingSkipReasons.DryRun,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = "dry_run",
                },
            }, ct);
            return null;
        }

        var leased = await _queue.LeaseNextAsync(
            leaseOwner,
            leaseDuration,
            new SubconsciousJobLeaseQuery
            {
                ExcludedWorkspaceIds = excludedWorkspaces,
                MaxRetryCount = scheduling.MaxRetryAttempts,
            },
            ct);

        if (leased is null)
        {
            await _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
            {
                Reason = excludedWorkspaces.Count > 0
                    ? SubconsciousSchedulingSkipReasons.WorkspaceLimit
                    : SubconsciousSchedulingSkipReasons.NoEligibleJob,
            }, ct);
        }

        return leased;
    }
}
```

- [ ] **Step 4: Run scheduler tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SubconsciousJobSchedulerTests"
```

Expected: scheduler tests pass after adding local fakes for `IIdleDetector` and `ISubconsciousJobQueue`.

---

## Task 4: Route Worker Leasing Through Scheduler

**Files:**

- Modify: `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- Modify: `Source/PuddingRuntimeTests/Services/SubconsciousJobSchedulerTests.cs`

- [ ] **Step 1: Add worker-level test or extend scheduler test**

Add a focused test proving the scheduler can return null without forcing legacy channel processing:

```csharp
[TestMethod]
public async Task TryLeaseNextAsync_ShouldNotLease_WhenSchedulingDisabled()
{
    var queue = new FakeQueue { Next = SampleJob("job-1", "workspace-1", "session-1") };
    var scheduler = CreateScheduler(queue, new FakeIdleDetector(DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromMinutes(5)),
        options: new SubconsciousSchedulingOptions { Enabled = false });

    var result = await scheduler.TryLeaseNextAsync("worker-1", TimeSpan.FromMinutes(5));

    Assert.IsNull(result);
    Assert.AreEqual(0, queue.LeaseCalls);
    Assert.AreEqual(SubconsciousSchedulingSkipReasons.Disabled, queue.Skips.Single().Reason);
}
```

- [ ] **Step 2: Modify worker constructor**

In `SubconsciousWorkerService`, add optional scheduler:

```csharp
private readonly SubconsciousJobScheduler? _scheduler;

public SubconsciousWorkerService(
    Channel<ConsolidationJob> channel,
    ISubconsciousOrchestrator orchestrator,
    ILogger<SubconsciousWorkerService> logger,
    ILLMConfigResolver? llmConfigResolver = null,
    ISubconsciousJobQueue? jobQueue = null,
    SubconsciousJobScheduler? scheduler = null)
{
    _channel = channel;
    _orchestrator = orchestrator;
    _llmConfigResolver = llmConfigResolver;
    _jobQueue = jobQueue;
    _scheduler = scheduler;
    _logger = logger;
}
```

- [ ] **Step 3: Replace direct durable lease**

Replace:

```csharp
var durableJob = await _jobQueue.LeaseNextAsync(
    _leaseOwner,
    DurableLeaseDuration,
    stoppingToken);
```

with:

```csharp
var durableJob = _scheduler is not null
    ? await _scheduler.TryLeaseNextAsync(_leaseOwner, DurableLeaseDuration, stoppingToken)
    : await _jobQueue.LeaseNextAsync(_leaseOwner, DurableLeaseDuration, ct: stoppingToken);
```

Keep legacy channel fallback unchanged and still behind existing legacy producer flags.

- [ ] **Step 4: Run runtime tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~Subconscious"
```

Expected: existing hook tests and scheduler tests pass.

---

## Task 5: Register Scheduler In DI

**Files:**

- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Modify: `Source/PuddingRuntimeTests/Services/RuntimeServiceExtensionsTests.cs`

- [ ] **Step 1: Add DI test**

Add to `RuntimeServiceExtensionsTests`:

```csharp
[TestMethod]
public void AddPuddingRuntime_RegistersSubconsciousJobScheduler()
{
    using var provider = new ServiceCollection()
        .AddLogging()
        .AddPuddingRuntime()
        .BuildServiceProvider();

    var scheduler = provider.GetService<SubconsciousJobScheduler>();

    Assert.IsNotNull(scheduler);
}
```

- [ ] **Step 2: Register scheduler**

In both DI locations after `ISubconsciousJobQueue` registration:

```csharp
services.AddSingleton<SubconsciousJobScheduler>();
```

and in `Program.cs`:

```csharp
builder.Services.AddSingleton<SubconsciousJobScheduler>();
```

- [ ] **Step 3: Run DI tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~RuntimeServiceExtensionsTests"
```

Expected: DI tests pass.

---

## Task 6: Extend Diagnostics For Scheduling Skips

**Files:**

- Modify: `Tools/Diagnostics/query_metrics.py`
- Modify: `Tools/Diagnostics/tests/test_query_metrics.py`

- [ ] **Step 1: Add failing Python test**

Extend `test_subconscious_jobs_summarizes_memory_metrics` to insert one skip row and assert `scheduleSkips` and `skipReasons`.

Expected assertions:

```python
self.assertEqual(1, row["scheduleSkips"])
self.assertEqual({"skip_cooldown": 1}, row["skipReasons"])
```

- [ ] **Step 2: Update fixture rows**

In `_insert_subconscious_job_rows`, add telemetry row:

```python
(
    "memory-7", "s2", "subconscious_job.schedule_skip", "deferred",
    "2026-05-31T00:00:07+00:00", "info", "Subconscious job scheduling skipped",
    '{"job_id":"job-2","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","skip_reason":"skip_cooldown","workspace_id":"default","session_id":"s2"}',
    None, None,
),
```

- [ ] **Step 3: Update summarizer**

In `query_subconscious_jobs`, add:

```python
schedule_skips = sum(1 for name in names if name == "subconscious_job.schedule_skip")
skip_reasons: dict[str, int] = defaultdict(int)
for row in items:
    dimensions = row.get("dimensions") or {}
    reason = dimensions.get("skip_reason")
    if reason:
        skip_reasons[str(reason)] += 1
```

Include in result:

```python
"scheduleSkips": schedule_skips,
"skipReasons": dict(sorted(skip_reasons.items())),
```

- [ ] **Step 4: Run diagnostics tests**

Run:

```powershell
.\.venv\Scripts\python.exe -m pytest Tools\Diagnostics\tests\test_query_metrics.py
```

Expected: all query metrics tests pass.

---

## Task 7: Update Documentation And Close F3 Gate

**Files:**

- Modify: `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
- Modify: `memory/memory-system-v2-requirements.md`
- Modify: `goal.md`

- [ ] **Step 1: Update foundation spec status**

In the foundation spec, update F3 status from `pending` to `implementation-ready` before implementation starts, then to `partial` or `done` only after tests pass.

- [ ] **Step 2: Update requirements implementation record**

Add a row to `memory/memory-system-v2-requirements.md` after implementation:

```markdown
| 2026-07-01 | F3 Worker Scheduling & Resource Control 第一实现 | partial | 新增 `SubconsciousJobScheduler`，在 durable job lease 前执行 idle cooldown、global/workspace limit、dry-run 和 skip reason metrics；不调用潜意识 LLM、不生成 plan、不写 MemoryLibrary |
```

- [ ] **Step 3: Update goal log**

Append:

```markdown
- 2026-07-01: F3 Worker Scheduling & Resource Control 第一实现完成 — durable `SubconsciousJobs` 现在经由 `SubconsciousJobScheduler` 进行 idle、并发、预算和 dry-run 门禁后再 lease；该阶段仍不调用潜意识 LLM、不生成 plan、不写 MemoryLibrary。下一步评审 F4 Subconscious Plan Protocol。
```

- [ ] **Step 4: Run doc checks**

Run:

```powershell
rg -n "F3 Worker Scheduling|SubconsciousJobScheduler|skip_cooldown|dry-run|不调用潜意识 LLM" Docs\superpowers\specs\2026-07-01-memory-v2-foundation-prerequisites.md memory\memory-system-v2-requirements.md goal.md
rg -n "[ \t]+$" Docs\superpowers\specs\2026-07-01-memory-v2-foundation-prerequisites.md memory\memory-system-v2-requirements.md goal.md
```

Expected: first command finds the new records; second command prints nothing.

---

## Final Verification

Run these commands before claiming F3 implementation is complete:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --filter "FullyQualifiedName~SubconsciousJobQueueTests"
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~Subconscious"
.\.venv\Scripts\python.exe -m pytest Tools\Diagnostics\tests\test_query_metrics.py
git diff --check -- Source/PuddingCore/Configuration/SubconsciousOptions.cs Source/PuddingCore/Platform/SubconsciousDtos.cs Source/PuddingCore/Abstractions/ISubconsciousOrchestrator.cs Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs Tools/Diagnostics/query_metrics.py
```

Expected:

- Queue tests pass.
- Runtime subconscious tests pass.
- Diagnostics query metrics tests pass.
- `git diff --check` reports no whitespace errors.

Do not run full solution tests until the focused tests are green.

---

## Implementation Notes

- Keep F3 serial-safe first. The current worker loop is serial; the first implementation should enforce limits and record decisions without introducing parallel execution.
- `MaxGlobalConcurrentJobs` and `MaxWorkspaceConcurrentJobs` still matter because existing `processing` rows may be leased by another process or stale lease window.
- `DryRun=true` must not call `LeaseNextAsync`; it should only emit `would_lease` scheduling metrics.
- Skip reasons must be stable enum-like strings because diagnostics aggregates them.
- Do not use content hash anywhere in this implementation.
- Do not add LLM calls or memory write plan execution in F3.
