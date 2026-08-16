using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// TB-05: TaskDispatcher 端到端测试（真实 MessageSystem + 真实 Store）。
/// 覆盖 §7.2：发送→绑定→Task Assigned、幂等（重复扫描不重复发）、发送后崩溃按 idempotency key
/// 找回同一 Delivery（不变量 #8）。
/// </summary>
[TestClass]
public sealed class TaskDispatcherTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private PlatformDbContext _fabricDb = null!;
    private ServiceProvider _provider = null!;
    private SqliteWorkspaceTaskStore _store = null!;
    private TaskCommandService _commands = null!;
    private TaskDispatchOutboxStore _outbox = null!;
    private IMessageSystem _messages = null!;
    private TaskDispatcher _dispatcher = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "task-dispatcher-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _fabricDb = new PlatformDbContext(options);
        var bus = new RecordingInternalEventBus();
        var participants = new WorkspaceRoomParticipantProvider(new RecordingWorkspaceAgentCatalog(Agent(AgentId)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<PlatformDbContext>>(_dbFactory);
        services.AddSingleton<TaskDispatchOutboxStore>();
        services.AddSingleton<SqliteWorkspaceTaskStore>();
        services.AddSingleton<ITaskStore>(sp => sp.GetRequiredService<SqliteWorkspaceTaskStore>());
        services.AddSingleton<TaskCommandService>();
        services.AddSingleton<IMessageSystem>(new MessageSystem(
            new MessageRouter(),
            new MessageFabricStore(_fabricDb),
            bus,
            participants));
        services.AddSingleton<IWorkAdmissionFence, ManualAlwaysAllowFence>();
        services.AddSingleton(TimeProvider.System);
        services.Configure<TaskDispatcherOptions>(_ => { });
        services.AddSingleton<TaskDispatcher>();
        _provider = services.BuildServiceProvider();

        _store = _provider.GetRequiredService<SqliteWorkspaceTaskStore>();
        _commands = _provider.GetRequiredService<TaskCommandService>();
        _outbox = _provider.GetRequiredService<TaskDispatchOutboxStore>();
        _messages = _provider.GetRequiredService<IMessageSystem>();
        _dispatcher = _provider.GetRequiredService<TaskDispatcher>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _provider?.Dispose();
        _fabricDb?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    // ── 1. 完整闭环：发送 → 绑定 → Task Reserved→Assigned ──

    [TestMethod]
    public async Task ProcessOnce_SendsBindsAndAssigns()
    {
        var task = await CreateReadyTaskAsync();
        await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);

        var processed = await _dispatcher.ProcessOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, processed);

        // outbox → sent
        var outbox = (await _outbox.PeekPendingOutboxAsync(DateTimeOffset.UtcNow));
        Assert.AreEqual(0, outbox.Count);
        var sent = await _outbox.GetOutboxAsync(outboxId: (await GetSingleOutboxIdAsync()));
        Assert.AreEqual(TaskDispatchOutboxStatuses.Sent, sent!.Status);
        Assert.IsNotNull(sent.SentAtUtc);

        // Task → Assigned（version 2→3）
        var after = await _store.GetTaskAsync(WorkspaceId, task.TaskId);
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, after!.Status);
        Assert.AreEqual(3, after.Version);

        // Delivery + Binding 可查询
        Assert.AreEqual(1, await CountDeliveriesAsync());
        var deliveryId = await GetSingleDeliveryIdAsync();
        var binding = await _outbox.GetBindingByDeliveryIdAsync(deliveryId);
        Assert.IsNotNull(binding);
        Assert.AreEqual(task.TaskId, binding!.TaskId);
        Assert.AreEqual(after.ActiveAssignmentId, binding.AssignmentId);
        Assert.AreEqual(deliveryId, binding.DeliveryId);

        // task.assigned 事件
        Assert.AreEqual(TaskEventType.TaskAssigned, await GetLastEventTypeAsync(task.TaskId));
    }

    // ── 2. 幂等：重复扫描不重复发送 / 不重复绑定 ──

    [TestMethod]
    public async Task ProcessOnce_Rerun_DoesNotDuplicateDeliveryOrBinding()
    {
        var task = await CreateReadyTaskAsync();
        await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);

        Assert.AreEqual(1, await _dispatcher.ProcessOnceAsync(CancellationToken.None));
        Assert.AreEqual(0, await _dispatcher.ProcessOnceAsync(CancellationToken.None));

        Assert.AreEqual(1, await CountDeliveriesAsync());
        Assert.AreEqual(1, await CountBindingsAsync());
    }

    // ── 3. 发送后崩溃：按 idempotency key 找回同一 Delivery（不变量 #8）──

    [TestMethod]
    public async Task CrashAfterSend_RecoversSameDeliveryWithoutDuplicating()
    {
        var task = await CreateReadyTaskAsync();
        await _commands.ApplyCommandAsync(
            WorkspaceId, task.TaskId, TaskCommand.Assign, expectedVersion: 1, agentId: AgentId);

        // 模拟「发送成功但未绑定」：手动发送（持久 Delivery），Outbox 仍 pending。
        var pending = (await _outbox.PeekPendingOutboxAsync(DateTimeOffset.UtcNow)).Single();
        await _messages.SendAsync(pending.Envelope.ToMessageEnvelope());

        Assert.AreEqual(1, await CountDeliveriesAsync());
        var preExistingDeliveryId = await GetSingleDeliveryIdAsync();

        // Dispatcher 重放：SendAsync 去重（空 DeliveryIds）→ 按 message_id 找回同一 Delivery → 绑定。
        var processed = await _dispatcher.ProcessOnceAsync(CancellationToken.None);
        Assert.AreEqual(1, processed);

        // 不产生第二条 Delivery、绑定指向原有 Delivery。
        Assert.AreEqual(1, await CountDeliveriesAsync());
        var binding = await _outbox.GetBindingByDeliveryIdAsync(preExistingDeliveryId);
        Assert.IsNotNull(binding);
        Assert.AreEqual(preExistingDeliveryId, binding!.DeliveryId);

        var after = await _store.GetTaskAsync(WorkspaceId, task.TaskId);
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, after!.Status);
    }

    // ── helpers ─────────────────────────────────────────────

    private async Task<WorkspaceTask> CreateReadyTaskAsync()
    {
        var task = await _store.CreateTaskAsync(new CreateTaskRequest
        {
            WorkspaceId = WorkspaceId,
            Title = "Task",
        });
        await SetStatusAsync(task.TaskId, WorkspaceTaskStatus.Ready);
        return task;
    }

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkspaceTasks.SingleAsync(t => t.TaskId == taskId);
        entity.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<long> GetSingleOutboxIdAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TaskDispatchOutbox.Select(o => o.Id).SingleAsync();
    }

    private async Task<long> CountDeliveriesAsync()
        => await ExecuteScalarInt64Async("SELECT COUNT(*) FROM message_deliveries");

    private async Task<long> CountBindingsAsync()
        => await ExecuteScalarInt64Async("SELECT COUNT(*) FROM task_execution_bindings");

    private async Task<string> GetSingleDeliveryIdAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MessageDeliveries.Select(d => d.DeliveryId).SingleAsync();
    }

    private async Task<TaskEventType> GetLastEventTypeAsync(string taskId)
        => (TaskEventType)await ExecuteScalarInt64Async(
            "SELECT event_type FROM task_events WHERE task_id = @taskId ORDER BY sequence DESC LIMIT 1",
            ("@taskId", taskId));

    private async Task<long> ExecuteScalarInt64Async(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ── Message Fabric 测试替身 ─────────────────────────────

    private static WorkspaceAgentDto Agent(string agentId) => new(
        AgentId: agentId,
        Name: agentId,
        Description: null,
        DisplayName: agentId,
        AvatarId: null,
        AvatarUrl: null,
        SourceTemplateId: "global:general-assistant",
        MainSessionId: $"{agentId}-main",
        SystemPromptOverride: null,
        PreferredProviderId: null,
        PreferredModelId: null,
        IsEnabled: true,
        IsFrozen: false,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingWorkspaceAgentCatalog(params WorkspaceAgentDto[] agents) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(agents);
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
            => Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;

        public void Dispose()
        {
            IsActive = false;
        }
    }
}
