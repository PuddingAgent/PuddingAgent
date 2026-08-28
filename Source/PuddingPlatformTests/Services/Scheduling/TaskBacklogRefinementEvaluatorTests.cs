using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskBacklogRefinementEvaluatorTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-28T02:00:00Z");

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "backlog-refinement-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Evaluate_OnlyOptedInBacklog_AndRequiresCompleteStructuredRoute()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.WorkspaceTasks.AddRange(
                Task("ready", acceptanceCriteria: "tests pass", autoDispatch: true),
                Task("missing-criteria", acceptanceCriteria: null, autoDispatch: true),
                Task("not-opted-in", acceptanceCriteria: "tests pass", autoDispatch: false));
            await db.SaveChangesAsync();
        }
        var evaluator = new TaskBacklogRefinementEvaluator(
            _factory,
            new FixedAgentCatalog([Agent()]),
            Options.Create(new TaskAutoDispatchOptions
            {
                TaskTypeRoutes = new Dictionary<string, TaskTypeRouteOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["implementation"] = new()
                    {
                        AllowedRoles = ["Service"],
                        RequiredCapabilityIds = ["cap-shell"],
                    },
                },
            }));

        var decisions = await evaluator.EvaluateAsync("ws", 20);

        Assert.HasCount(2, decisions);
        Assert.AreEqual(TaskBacklogRefinementVerdict.ReadyCandidate,
            decisions.Single(item => item.TaskId == "ready").Verdict);
        Assert.AreEqual("agent-1", decisions.Single(item => item.TaskId == "ready").CompatibleAgentId);
        Assert.AreEqual("acceptance_criteria_required",
            decisions.Single(item => item.TaskId == "missing-criteria").Code);
        Assert.IsFalse(decisions.Any(item => item.TaskId == "not-opted-in"));
    }

    [TestMethod]
    public async Task Promote_RevalidatesFingerprint_ThenCommitsReadyAndCanonicalEvent()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.WorkspaceTasks.Add(Task("ready", acceptanceCriteria: "tests pass", autoDispatch: true));
            await db.SaveChangesAsync();
        }
        var catalog = new FixedAgentCatalog([Agent()]);
        var configured = Options.Create(new TaskAutoDispatchOptions
        {
            TaskTypeRoutes = new Dictionary<string, TaskTypeRouteOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["implementation"] = new()
                {
                    AllowedRoles = ["Service"],
                    RequiredCapabilityIds = ["cap-shell"],
                },
            },
        });
        var evaluator = new TaskBacklogRefinementEvaluator(_factory, catalog, configured);
        var decision = (await evaluator.EvaluateAsync("ws", 20)).Single();
        var store = new TaskBacklogRefinementStore(
            _factory, catalog, configured, new FixedTimeProvider(_now));

        var stale = await store.TryPromoteAsync(new PromoteBacklogTaskCommand
        {
            WorkspaceId = "ws",
            TaskId = "ready",
            ExpectedTaskVersion = decision.TaskVersion,
            CompatibleAgentId = decision.CompatibleAgentId!,
            ExpectedAgentRoutingFingerprint = new string('b', 64),
        });
        var promoted = await store.TryPromoteAsync(new PromoteBacklogTaskCommand
        {
            WorkspaceId = "ws",
            TaskId = "ready",
            ExpectedTaskVersion = decision.TaskVersion,
            CompatibleAgentId = decision.CompatibleAgentId!,
            ExpectedAgentRoutingFingerprint = decision.AgentRoutingFingerprint!,
        });

        Assert.IsFalse(stale.Promoted);
        Assert.AreEqual(TaskBacklogPromotionCodes.RouteChanged, stale.Code);
        Assert.IsTrue(promoted.Promoted);
        await using var verify = await _factory.CreateDbContextAsync();
        var task = await verify.WorkspaceTasks.SingleAsync();
        var evt = await verify.TaskEvents.SingleAsync();
        Assert.AreEqual(WorkspaceTaskStatus.Ready, task.Status);
        Assert.AreEqual(2, task.Version);
        Assert.AreEqual(TaskEventType.TaskReady, evt.EventType);
        Assert.AreEqual("backlog_refined", evt.DecisionCode);
    }

    private WorkspaceTaskEntity Task(string id, string? acceptanceCriteria, bool autoDispatch) => new()
    {
        TaskId = id,
        WorkspaceId = "ws",
        Title = id,
        Description = "Implement the bounded change.",
        AcceptanceCriteria = acceptanceCriteria,
        Status = WorkspaceTaskStatus.Backlog,
        Priority = TaskPriority.P1,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        PreferredAgentId = "agent-1",
        TaskType = "implementation",
        RequiredCapabilitiesJson = "[]",
        AllowAgentFallback = true,
        AutoDispatchEnabled = autoDispatch,
        Version = 1,
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private WorkspaceAgentDto Agent() => new(
        AgentId: "agent-1",
        Name: "agent-1",
        Description: null,
        DisplayName: "Agent 1",
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "service",
        MainSessionId: "conversation-1",
        SystemPromptOverride: null,
        PreferredProviderId: "bigmodel",
        PreferredModelId: "glm-5.3-flash",
        IsEnabled: true,
        IsFrozen: false,
        CreatedAt: _now.AddDays(-1),
        UpdatedAt: _now.AddDays(-1),
        Role: "Service",
        AllowFileWrite: true,
        AllowShellExecution: true,
        AllowNetworkAccess: true,
        SelectedCapabilityIds: ["cap-shell", "cap-file-write"]);

    private sealed class FixedAgentCatalog(IReadOnlyList<WorkspaceAgentDto> agents)
        : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(agents);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
