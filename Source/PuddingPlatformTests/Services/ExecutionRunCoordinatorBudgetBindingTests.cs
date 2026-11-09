using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingAgentWork.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// CORE-I1-05 acceptance: the real <see cref="ExecutionRunCoordinator"/> is exercised with the real managed-Work
/// resolver, real canonical command reader and real SQLite budget ledger, under both host policy configurations.
/// The turn executor is a capturing fake (the provider boundary): it proves a managed turn receives the frozen
/// carrier, ordinary chat is unaffected, and a rejected managed-looking command never reaches the executor.
/// </summary>
[TestClass]
public sealed class ExecutionRunCoordinatorBudgetBindingTests
{
    [TestMethod]
    public async Task PolicyDisabled_OrdinaryChat_RunsWithoutBinding()
        => await RunAsync(enabled: false, managed: false, expectBinding: false, expectCompleted: true);

    [TestMethod]
    public async Task PolicyEnabled_OrdinaryChat_RemainsUnmanaged()
        => await RunAsync(enabled: true, managed: false, expectBinding: false, expectCompleted: true);

    [TestMethod]
    public async Task PolicyEnabled_ManagedWork_ReceivesTheFrozenCarrier()
        => await RunAsync(enabled: true, managed: true, expectBinding: true, expectCompleted: true);

    [TestMethod]
    public async Task PolicyDisabled_ManagedWork_KeepsTheLegacyPath()
        => await RunAsync(enabled: false, managed: true, expectBinding: false, expectCompleted: true);

    [TestMethod]
    public async Task PolicyEnabled_ManagedLookingCommand_WithoutProvenance_IsRejectedBeforeTheExecutor()
    {
        await using var host = await CoordinatorHost.CreateAsync(enabled: true);
        var command = await host.SeedAsync("work-" + new string('c', 59), metadataJson: null);

        var result = await host.RunAsync(command);

        Assert.IsFalse(result.Outcome.Terminal.Kind == TurnTerminalKind.Completed);
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.EvidenceMissing, result.Outcome.Terminal.ErrorCode);
        Assert.AreEqual(0, result.ExecutorCalls, "A rejected managed command must never reach the provider boundary.");
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ExecutionBudgetStore>();
        Assert.AreEqual(0, (await store.ListAccountIdsAsync()).Length);
    }

    private static async Task RunAsync(bool enabled, bool managed, bool expectBinding, bool expectCompleted)
    {
        await using var host = await CoordinatorHost.CreateAsync(enabled);
        var workId = "work-" + new string(managed ? 'a' : 'b', 59);
        var metadata = managed
            ? JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["agent_work_actor"] = "actor-1",
                ["agent_work_operation"] = "op-1",
                ["agent_work_request_hash"] = "hash-1",
            })
            : null;
        var command = await host.SeedAsync(managed ? workId : "req-1", metadata);

        var result = await host.RunAsync(command);

        Assert.AreEqual(expectCompleted, result.Outcome.Terminal.Kind == TurnTerminalKind.Completed);
        Assert.AreEqual(1, result.ExecutorCalls, "The real coordinator must invoke the executor exactly once.");
        Assert.IsNotNull(result.Context);
        var binding = result.Context!.ExecutionIdentity?.WorkBudget;
        Assert.AreEqual(expectBinding, binding is not null);
        if (expectBinding)
        {
            Assert.AreEqual("work:" + workId, binding!.AccountId);
            Assert.AreEqual("Work", binding.RootKind);
            Assert.AreEqual(workId, binding.RootId);

            using var scope = host.Provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ExecutionBudgetStore>();
            var snapshot = await store.ReadAsync(new BudgetAccountId(binding.AccountId));
            Assert.AreEqual(10, snapshot.Limits.MaxCalls);
        }
    }

    private sealed class CoordinatorHost(
        SqliteConnection connection, ServiceProvider provider, FakeDbContextFactory factory,
        FakeManagedWorkPolicy policy) : IAsyncDisposable
    {
        private static long _seed;

        public ServiceProvider Provider { get; } = provider;
        public ExecutionBudgetStore Store =>
            provider.GetRequiredService<IServiceScopeFactory>().CreateScope()
                .ServiceProvider.GetRequiredService<ExecutionBudgetStore>();

        public async Task<SeededCommand> SeedAsync(string clientRequestId, string? metadataJson)
        {
            var seq = Interlocked.Increment(ref _seed);
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var messageId = "msg-" + seq;
            var turnId = "turn-" + seq;
            var commandId = "cmd-" + seq;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.AcceptanceBatches.Add(new AcceptanceBatchEntity
            {
                BatchId = "batch-" + seq, WorkspaceId = "ws-1", ClientRequestId = clientRequestId,
                ConversationId = "conv-1", MessageId = messageId, UserId = "actor-1", TurnCount = 1,
                AcceptedSequence = seq, CreatedAt = now,
            });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = commandId, BatchId = "batch-" + seq, ClientRequestId = clientRequestId,
                WorkspaceId = "ws-1", SessionId = "conv-1", MessageId = messageId + "-assistant",
                UserMessageId = messageId, TurnId = turnId, AgentInstanceId = "agent-1", UserId = "actor-1",
                Status = "pending", CreatedAt = now,
            });
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = messageId, SessionId = "conv-1", WorkspaceId = "ws-1", AgentInstanceId = "agent-1",
                Role = "user", Content = "hello", UserId = "actor-1", TurnId = turnId, CommandId = commandId,
                CreatedAt = now, MetadataJson = metadataJson,
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = "conv-1", Sequence = seq, EventId = "evt-" + seq, WorkspaceId = "ws-1",
                TurnId = turnId, CommandId = commandId, MessageId = messageId, Type = ConversationEventTypes.TurnAccepted,
                Payload = "{}", OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationTurns.Add(new ConversationTurnEntity
            {
                ConversationId = "conv-1", TurnId = turnId, CommandId = commandId, WorkspaceId = "ws-1",
                UserId = "actor-1", Status = "accepted", AcceptedSequence = seq, CreatedAt = now,
            });
            await db.SaveChangesAsync();
            return new SeededCommand(commandId, turnId, messageId, seq);
        }

        public async Task<RunResult> RunAsync(SeededCommand seeded)
        {
            using var scope = Provider.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IManagedWorkBudgetBindingResolver>();
            var commandReader = scope.ServiceProvider.GetRequiredService<IExecutionCommandReader>();
            var executor = new CapturingExecutor();
            var coordinator = new ExecutionRunCoordinator(
                new FakeLeaseStore(), new FakeJournal(), executor, new FakeProfileResolver(),
                new FakeSnapshotFactory(), new FakeMessageRepository(), commandReader, new FakeInbox(),
                new FakeAudioResolver(), new FakeLlmConfigService(),
                NullLogger<ExecutionRunCoordinator>.Instance,
                managedWorkBudgetBinding: resolver);
            var lease = new ExecutionLease(seeded.CommandId, "worker-1", "ws-1", "conv-1", seeded.TurnId,
                "run-1", 1, DateTimeOffset.UtcNow.AddMinutes(5)) { TraceId = null };
            var outcome = await coordinator.ExecuteAsync(lease, CancellationToken.None);
            return new RunResult(outcome, executor.Calls, executor.LastContext);
        }

        public static async Task<CoordinatorHost> CreateAsync(bool enabled)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new FakeDbContextFactory(options);
            var services = new ServiceCollection();
            services.AddSingleton<IDbContextFactory<PlatformDbContext>>(factory);
            services.AddScoped(_ => factory.CreateDbContext());
            services.AddScoped<IExecutionCommandReader, ExecutionCommandReader>();
            services.AddSingleton<ExecutionBudgetStore>();
            services.AddSingleton<ExecutionBudgetAccountMapper>();
            services.AddSingleton<IModelCatalogSource>(new FakeCatalog());
            var policy = new FakeManagedWorkPolicy(enabled);
            services.AddSingleton<IManagedWorkBudgetPolicyProvider>(policy);
            services.AddScoped<IManagedWorkBudgetBindingResolver, ManagedWorkBudgetBindingResolver>();
            services.AddSingleton<IAgentWorkBackend, CanonicalAgentWorkBackend>();
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }

            return new(connection, provider, factory, policy);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed record SeededCommand(string CommandId, string TurnId, string MessageId, long Sequence);
    public sealed record RunResult(ExecutionRunOutcome Outcome, int ExecutorCalls, TurnExecutionContext? Context);

    public sealed class FakeDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeManagedWorkPolicy(bool enabled) : IManagedWorkBudgetPolicyProvider
    {
        public ManagedWorkBudgetOptions? GetPolicy() => enabled
            ? new ManagedWorkBudgetOptions
            {
                Enabled = true, Mode = "Enforced", CostUnit = "usd",
                MaxOutputTokens = 200_000, MaxCost = 25m, MaxCalls = 10, MaxConcurrentAttempts = 2,
            }
            : null;
    }

    private sealed class FakeCatalog : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult<ModelCapabilityRecord?>(modelId == "model-a"
                ? new ModelCapabilityRecord("model-a", "rev-1", 128_000, 8_192, ["text"], true, true,
                    "available", 2m, 1m, 4m, "usd", "catalog", false,
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), "UTC", 100m, "pr-1")
                : null);

        public ValueTask<System.Collections.Immutable.ImmutableArray<ModelCapabilityRecord>> ListAsync(
            string workspaceId, string? cursor, int maxCount, CancellationToken ct = default)
            => ValueTask.FromResult(System.Collections.Immutable.ImmutableArray<ModelCapabilityRecord>.Empty);
    }

    private sealed class FakeLeaseStore : IExecutionLeaseStore
    {
        public Task<ExecutionLease?> TryAcquireAsync(string workerId, TimeSpan duration, CancellationToken ct)
            => Task.FromResult<ExecutionLease?>(null);
        public Task<bool> RenewAsync(ExecutionLease lease, TimeSpan duration, CancellationToken ct)
            => Task.FromResult(true);
        public Task ReleaseAsync(ExecutionLease lease, RunStatus outcome, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeJournal : IExecutionJournal
    {
        public Task<AppendResult> StartRunAsync(ExecutionLease lease, string snapshotId,
            NewConversationEvent startedEvent, CancellationToken ct) => Task.FromResult(new AppendResult(1, 1, 1));
        public Task<AppendResult> AppendOutputAsync(ExecutionLease lease, IReadOnlyList<NewConversationEvent> events,
            CancellationToken ct) => Task.FromResult(new AppendResult(1, events.Count, events.Count));
        public Task<AppendResult> CommitTerminalAsync(ExecutionLease lease, TurnTerminal terminal,
            IReadOnlyList<NewConversationEvent> pendingEvents, CancellationToken ct)
            => Task.FromResult(new AppendResult(1, 2, 1 + pendingEvents.Count));
        public Task<AppendResult?> TryCommitInfrastructureFailureAsync(ExecutionLease lease, TurnTerminal terminal,
            IReadOnlyList<NewConversationEvent> pendingEvents, CancellationToken ct)
            => Task.FromResult<AppendResult?>(new AppendResult(1, 1, 1));
        public Task<ExecutionParkResult?> ParkForChildrenAsync(ExecutionLease lease, TurnTerminal deferredTerminal,
            IReadOnlyList<NewConversationEvent> pendingEvents, CancellationToken ct)
            => Task.FromResult<ExecutionParkResult?>(null);
        public Task<ExecutionParkFinalizeResult?> TryFinalizeWaitingTurnAsync(string parentTurnId,
            CancellationToken ct) => Task.FromResult<ExecutionParkFinalizeResult?>(null);
    }

    private sealed class CapturingExecutor : ITurnExecutor
    {
        public int Calls { get; private set; }
        public TurnExecutionContext? LastContext { get; private set; }

        public async IAsyncEnumerable<TurnExecutionEvent> ExecuteAsync(TurnExecutionContext context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            LastContext = context;
            await Task.Yield();
            using var payload = JsonDocument.Parse("{\"reply\":\"ok\"}");
            yield return new TurnExecutionEvent("evt-done", ConversationEventTypes.TurnCompleted, 1,
                payload.RootElement.Clone(), true, TurnTerminalInfo.Success("ok", null));
        }
    }

    private sealed class FakeProfileResolver : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(string workspaceId, string agentId,
            CancellationToken ct = default) => Task.FromResult(new AgentRuntimeProfile
            {
                WorkspaceId = workspaceId, AgentId = agentId, DisplayName = "Agent",
                SourceTemplateId = "global:general-assistant", PreferredProviderId = "provider-a",
                PreferredModelId = "model-a",
            });
    }

    private sealed class FakeSnapshotFactory : IAgentExecutionSnapshotFactory
    {
        public Task<AgentExecutionSnapshot> CreateAsync(AgentRuntimeProfile profile,
            AgentExecutionSnapshot? previousSnapshot, CancellationToken ct) => Task.FromResult(Snapshot());

        public Task<AgentExecutionSnapshot?> FindByIdAsync(string snapshotId, CancellationToken ct)
            => Task.FromResult<AgentExecutionSnapshot?>(Snapshot());

        private static AgentExecutionSnapshot Snapshot() => new("snap-1", "ws-1", "agent-1", 1, "hash-1", "Agent",
            null, null, null, "provider-a", "profile-a", "model-a", null, null, null, null, null, 10, 20, null,
            DateTimeOffset.UtcNow, ["text"], null);
    }

    private sealed class FakeMessageRepository : IChatMessageRepository
    {
        public Task<IReadOnlyList<ChatMessageRow>> GetMessagesCursorAsync(string sessionId, long? beforeId = null,
            int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessageRow>>([]);

        public Task<bool> AnyBySessionIdAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<ChatMessageRow?> GetByMessageIdAsync(string messageId, CancellationToken ct = default)
            => Task.FromResult<ChatMessageRow?>(new ChatMessageRow
            {
                MessageId = messageId, SessionId = "conv-1", WorkspaceId = "ws-1", Role = "user", Content = "hello",
                AgentInstanceId = "agent-1", TurnId = "turn-1", MetadataJson = null,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
    }

    private sealed class FakeInbox : IControlInbox
    {
        public Task<IReadOnlyList<ControlMessageRecord>> ReadPendingAsync(ExecutionLease lease, long afterSequence,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ControlMessageRecord>>([]);
        public Task AcknowledgeAsync(ExecutionLease lease, string controlId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAudioResolver : IAudioArtifactLocalFileResolver
    {
        public Task<AudioArtifactLocalFile?> ResolveLocalFileAsync(string workspaceId, string artifactId,
            CancellationToken ct = default) => Task.FromResult<AudioArtifactLocalFile?>(null);
    }

    private sealed class FakeLlmConfigService : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [];
        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
