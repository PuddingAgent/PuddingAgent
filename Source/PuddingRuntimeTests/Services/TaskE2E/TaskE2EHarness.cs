using System.Net.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services.TaskE2E;

/// <summary>
/// TB-08-C 共享 harness：SQLite 临时库 + 真实 <see cref="TaskAgentCommandService"/> +
/// 真实四工具（经 <see cref="TaskToolInvocationService"/> 分发）+ 真实
/// <see cref="AgentExecutionService"/> + 种子/探测助手。
/// </summary>
public sealed class TaskE2EHarness : IDisposable
{
    private readonly string _testRoot;

    public PlatformDbContextFactory DbFactory { get; }

    public TaskAgentCommandService CommandService { get; }

    public TaskToolInvocationService Tools { get; }

    public ExecutionJournal Journal { get; }

    public TaskDbProbe Probe { get; }

    public ScriptedLlmClient Llm { get; }

    public AgentExecutionService ExecutionService { get; }

    public TaskE2EHarness()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-e2e",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var databasePath = Path.Combine(_testRoot, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={databasePath};Default Timeout=10")
            .Options;
        DbFactory = new PlatformDbContextFactory(options);

        CommandService = new TaskAgentCommandService(DbFactory, new ManualAlwaysAllowFence());
        Tools = new TaskToolInvocationService(CommandService);
        Journal = new ExecutionJournal();
        Probe = new TaskDbProbe(DbFactory);
        Llm = new ScriptedLlmClient();
        ExecutionService = CreateExecutionService(Llm);
    }

    /// <summary>[TestInitialize] 调用：确保 SQLite schema 建表完成。</summary>
    public async Task InitializeAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>
    /// 种子：直接落 <c>workspace_tasks</c> + <c>task_assignment_attempts</c> +
    /// <c>task_events</c> + <c>task_execution_bindings</c> 四表（Assigned 态，Version=1，
    /// Attempt=Assigned，Event=task.assigned，Binding 预留 DeliveryId 供回填）。
    /// 说明：Admin 命令面 <c>TaskCommandService.ApplyCommandAsync(Assign)</c> 产生的是
    /// Reserved 而非 Assigned（状态机 Ready→Reserved），不满足契约要求；故按契约 §4.1/§7
    /// 回退为直插三表 + Binding，并在此记录偏差。
    /// </summary>
    public async Task<ActiveTaskRuntimeContext> SeedAssignedTaskAsync(
        string workspaceId,
        string taskId,
        string assignmentId,
        string agentId,
        int version = 1,
        string priority = "p1",
        string executionWindow = "anytime",
        string? origin = "task.manual",
        string? deliveryId = null,
        string? reservationFencingToken = null,
        string? dispatchIdempotencyKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        var priorityEnum = TaskWireMaps.PriorityFromString(priority);
        var windowEnum = TaskWireMaps.ExecutionWindowFromString(executionWindow);
        var effectiveDeliveryId = deliveryId ?? $"delivery-{taskId}";

        await using var db = await DbFactory.CreateDbContextAsync();

        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Title = $"Task {taskId}",
            Status = WorkspaceTaskStatus.Assigned,
            Priority = priorityEnum,
            ExecutionWindow = windowEnum,
            ActiveAssignmentId = assignmentId,
            SortOrder = 0,
            Version = version,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = assignmentId,
            TaskId = taskId,
            WorkspaceId = workspaceId,
            AgentId = agentId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.Assigned,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActiveAtUtc = now,
            ReleasedAtUtc = null,
        });

        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Sequence = 1,
            EventType = TaskEventType.TaskAssigned,
            AssignmentId = assignmentId,
            AgentId = agentId,
            CreatedAtUtc = now,
        });

        db.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
        {
            TaskId = taskId,
            AssignmentId = assignmentId,
            DeliveryId = effectiveDeliveryId,
            ExecutionId = null,
            SessionId = null,
            BoundAtUtc = now,
        });

        await db.SaveChangesAsync();

        return new ActiveTaskRuntimeContext
        {
            WorkspaceId = workspaceId,
            TaskId = taskId,
            AssignmentId = assignmentId,
            AgentId = agentId,
            Origin = origin ?? "task.manual",
            Priority = priority,
            ExecutionWindow = executionWindow,
            ExpectedVersion = version,
            PolicyVersion = "v1",
            DispatchIdempotencyKey = dispatchIdempotencyKey,
            DeliveryId = effectiveDeliveryId,
            ReservationFencingToken = reservationFencingToken,
        };
    }

    /// <summary>构造 RuntimeDispatchRequest（对齐 B2:264-274）。</summary>
    public RuntimeDispatchRequest CreateDispatchRequest(
        string sessionId,
        ActiveTaskRuntimeContext? activeTask) => new()
    {
        SessionId = sessionId,
        AgentTemplateId = "workspace-task-agent",
        MessageText = "please handle the task",
        WorkspaceId = activeTask?.WorkspaceId ?? "ws-1",
        AgentInstanceId = activeTask?.AgentId ?? "agent-1",
        LlmConfig = CreateLlmConfig(),
        SuppressContextAutoCompaction = true,
        MaxRounds = 8,
        ActiveTask = activeTask,
    };

    /// <summary>构造 DispatchWakeupRequest（对齐 B2:276-286）。</summary>
    public DispatchWakeupRequest CreateWakeupRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        AgentTemplateId = "workspace-task-agent",
        WorkspaceId = "ws-1",
        EventType = "file.ready",
        EventData = "the file is ready",
        LlmConfig = CreateLlmConfig(),
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // best-effort 清理；临时目录由系统兜底
            }
        }
    }

    // ── 组装（复用 B2 CreateService 骨架，toolInvocationService 传真实工具路径）──

    private AgentExecutionService CreateExecutionService(ScriptedLlmClient llm)
    {
        var sessionManager = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var runtimeSessionStore = new InMemoryRuntimeSessionStore();
        var memory = new FakeMemoryEngine();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var guardrails = new AgentExecutionGuardrails();
        var controlRegistry = new ExecutionControlRegistry();
        var completionPolicy = new CompletionPolicy();
        var skillPackageRegistry = new AgentSkillPackageRegistry();
        var skillRuntime = new SkillRuntime(
            Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var promptBuilder = new SystemPromptBuilder(
            memory, skillRuntime, skillPackageRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo());
        var contextPipeline = new ContextPipeline(
            memory,
            skillRuntime,
            skillPackageRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            new ContextAssemblyStore(),
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider());
        var contextManager = new ContextWindowManager(
            sessionManager,
            runtimeSessionStore,
            controlRegistry,
            Journal,
            NullLogger<ContextWindowManager>.Instance);
        var sessionExecutionGate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var skillPackageDownloader = new SkillPackageDownloadService(
            new FakeHttpClientFactory(), NullLogger<SkillPackageDownloadService>.Instance);

        return new AgentExecutionService(
            sessionManager,
            runtimeSessionStore,
            memory,
            sandbox,
            llm,
            skillRuntime,
            guardrails,
            controlRegistry,
            Journal,
            completionPolicy,
            skillPackageRegistry,
            skillPackageDownloader,
            Array.Empty<IAgentLoopHook>(),
            contextPipeline,
            contextManager,
            NullLogger<AgentExecutionService>.Instance,
            sessionExecutionGate,
            toolInvocationService: Tools);
    }

    private static LlmConfig CreateLlmConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 8192,
    };

    // ── 测试替身（沿用 B2:355-405）────────────────────────────────────────

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null)
            => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) { }

        public void ClearSession(string sessionId) { }
    }

    private sealed class FakeExecutionEnvironmentProvider : IExecutionEnvironmentProvider
    {
        public string OsDescription => "TestOS";
        public string OsArchitecture => "X64";
        public string RuntimeVersion => "10.0";
        public string AppBaseDirectory => "E:\\app";
        public string PathSeparator => "\\";
        public bool IsContainer => false;
        public string DefaultShell => "powershell";
        public string EnvironmentFingerprint => "test-env";
        public string? GetWorkspaceRoot(string workspaceId) => $"E:\\workspaces\\{workspaceId}";
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>
/// 脚本化 LLM 客户端（沿用 B2:322-340）。每个元素是一轮完整 JSON 响应，
/// 按轮次依次出队；耗尽即抛异常。
/// </summary>
public sealed class ScriptedLlmClient : IRuntimeLlmClient
{
    private readonly Queue<LlmResponse> _responses = new();

    public ScriptedLlmClient()
    {
    }

    public ScriptedLlmClient(params string[] contents)
        => Enqueue(contents);

    /// <summary>向脚本队列追加若干轮响应（供共享 harness 实例按测试逐个配置）。</summary>
    public void Enqueue(params string[] contents)
    {
        foreach (var content in contents)
        {
            _responses.Enqueue(new LlmResponse(content, null));
        }
    }

    public Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        if (_responses.Count == 0)
            throw new InvalidOperationException(
                "ScriptedLlmClient exhausted: more LLM rounds than scripted responses.");
        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
        => throw new NotSupportedException("Buffered path only in these tests.");
}
