using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pudding.Api.Contracts;
using PuddingAgentWork;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// CORE-I1-05 W1/W2/W3: a canonical single-turn Work accepted through the real public API is resolved into one frozen
/// managed budget binding over real SQLite. Evidence queries reuse the frozen canonical backend; inconsistent or
/// unmanaged commands produce explicit results/errors, and account cancellation fails before any runtime dispatch.
/// </summary>
[TestClass]
public sealed class CanonicalWorkBudgetBindingTests
{
    private static readonly AgentRef Agent = new("ws-1", "agent-1");

    [TestMethod]
    public async Task W1_ManagedWork_FreezesAccountAndCarriesTheFrozenBinding()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var receipt = await host.SubmitAsync();

        var resolved = await host.ResolveAsync(receipt);
        Assert.IsTrue(resolved.IsManaged);
        var binding = resolved.Binding!;
        Assert.AreEqual("work:" + receipt.WorkId, binding.Runtime.AccountId);
        Assert.AreEqual("Work", binding.Runtime.RootKind);
        Assert.AreEqual(receipt.WorkId, binding.Runtime.RootId);
        Assert.AreEqual("ws-1", binding.Runtime.WorkspaceId);
        Assert.AreEqual("agent-1", binding.Runtime.AgentId);
        Assert.AreEqual(receipt.ConversationId, binding.Runtime.SessionId);
        Assert.AreEqual("usd", binding.Runtime.CostUnit);
        Assert.AreEqual("rev-1", binding.Runtime.ModelRevision);
        Assert.AreEqual("pr-1", binding.Runtime.PriceVersion);
        Assert.AreEqual(128_000, binding.Runtime.MaxInputTokensPerAttempt);
        Assert.AreEqual(8_192, binding.Runtime.MaxOutputTokensPerAttempt);
        Assert.AreEqual(0, binding.Runtime.Ancestors.Count);

        var snapshot = await host.Store.ReadAsync(new BudgetAccountId(binding.Runtime.AccountId));
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await host.Store.GetEnforcementModeAsync(new BudgetAccountId(binding.Runtime.AccountId)));
        Assert.AreEqual(200_000L, snapshot.Limits.MaxOutputTokens);
        Assert.AreEqual(25m, snapshot.Limits.MaxCost);
        Assert.AreEqual(10, snapshot.Limits.MaxCalls);
        Assert.AreEqual(0, snapshot.Calls);
    }

    [TestMethod]
    public async Task W1_RepeatedResolutionAndRestart_ReuseTheSameAccountWithoutResettingTheLedger()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var receipt = await host.SubmitAsync();
        var first = (await host.ResolveAsync(receipt)).Binding!;
        var second = (await host.ResolveAsync(receipt)).Binding!;
        Assert.AreEqual(first.Runtime.AccountId, second.Runtime.AccountId);
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);

        // A fresh store instance over the same database is the restart path: the frozen definition must be re-read.
        var restarted = new ExecutionBudgetStore(host.Factory);
        var store = await restarted.ReadAsync(new BudgetAccountId(first.Runtime.AccountId));
        Assert.AreEqual(200_000L, store.Limits.MaxOutputTokens);
        Assert.AreEqual(25m, store.Limits.MaxCost);
        Assert.AreEqual(10, store.Limits.MaxCalls);
        Assert.AreEqual(0, store.Calls);
    }

    [TestMethod]
    public async Task W2_UnmanagedOrInconsistentCommands_AreExplicitNeverSilentFallbacks()
    {
        await using var disabled = await BindingHost.CreateAsync(enabled: false);
        var receipt = await disabled.SubmitAsync();
        Assert.IsFalse((await disabled.ResolveAsync(receipt)).IsManaged, "A disabled policy is an explicit unmanaged result.");

        await using var host = await BindingHost.CreateAsync(enabled: true);
        var managed = await host.SubmitAsync();

        var mismatched = host.Lease(managed) with { CommandId = "cmd-other" };
        var command = await host.ReadCommandAsync(managed);
        var identity = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await host.ResolveWithAsync(mismatched, command, host.Profile));
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.IdentityMismatch, identity.Code);

        var forged = command with { ClientRequestId = "work-forged-without-acceptance" };
        var evidence = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await host.ResolveWithAsync(host.Lease(managed), forged, host.Profile));
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.EvidenceMissing, evidence.Code);

        var unpriced = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await host.ResolveWithAsync(host.Lease(managed), command,
                new LlmInvocationProfile { ProviderId = "p", ProfileId = "prof", ModelId = "unknown-model" }));
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.ModelUnpriced, unpriced.Code);

        await using var legacyPolicy = await BindingHost.CreateAsync(enabled: true, mode: "Legacy");
        var legacyReceipt = await legacyPolicy.SubmitAsync();
        var invalid = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await legacyPolicy.ResolveAsync(legacyReceipt));
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.PolicyInvalid, invalid.Code);
    }

    [TestMethod]
    public async Task W3_CancelledAccount_FailsBeforeAnyDispatch()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var receipt = await host.SubmitAsync();
        var first = (await host.ResolveAsync(receipt)).Binding!;
        var account = new BudgetAccountId(first.Runtime.AccountId);
        await host.Store.CancelAsync(new CancelBudget(account, "user.cancelled", 1));

        var failure = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await host.ResolveAsync(receipt));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, failure.Code);
    }

    [TestMethod]
    public async Task W3_RecoveredWork_KeepsInitialLimits_WhileNewWorkUsesTheCurrentPolicy()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var first = await host.SubmitAsync("op-stable");
        var initial = (await host.ResolveAsync(first)).Binding!;
        var initialSnapshot = await host.Store.ReadAsync(new BudgetAccountId(initial.Runtime.AccountId));
        Assert.AreEqual(10, initialSnapshot.Limits.MaxCalls);

        // The host policy tightens to 8 while the same Work is recovered by a new scope (restart/re-lease).
        host.Policy.MaxCalls = 8;
        host.Policy.MaxInputTokensPerRequest = 1_000;
        var recovered = (await host.ResolveAsync(first)).Binding!;
        var recoveredSnapshot = await host.Store.ReadAsync(new BudgetAccountId(recovered.Runtime.AccountId));
        Assert.AreEqual(initial.Runtime.AccountId, recovered.Runtime.AccountId);
        Assert.AreEqual(10, recoveredSnapshot.Limits.MaxCalls,
            "A recovered Work keeps its frozen limits; the changed host policy must not fail or widen it.");
        Assert.AreEqual(0, recoveredSnapshot.Calls);
        Assert.AreEqual(128_000, recovered.Runtime.MaxInputTokensPerAttempt,
            "A recovered Work keeps its frozen per-attempt input capacity (persisted null -> model context), not the changed policy.");

        // A brand-new Work under the same policy uses the current (tightened) definition.
        var second = await host.SubmitAsync("op-new");
        var fresh = (await host.ResolveAsync(second)).Binding!;
        var freshSnapshot = await host.Store.ReadAsync(new BudgetAccountId(fresh.Runtime.AccountId));
        Assert.AreEqual(8, freshSnapshot.Limits.MaxCalls);
        Assert.AreEqual(1_000, fresh.Runtime.MaxInputTokensPerAttempt,
            "New Work uses the current policy per-attempt capacity.");
    }

    [TestMethod]
    public async Task OperationIdLength_MatchesThePublicWorkContract()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        foreach (var length in new[] { 64, 65, 128 })
        {
            var receipt = await host.SubmitAsync(new string('o', length));
            var binding = (await host.ResolveAsync(receipt)).Binding!;
            Assert.AreEqual("work:" + receipt.WorkId, binding.Runtime.AccountId,
                $"An operation id of {length} chars must bind successfully.");
        }

        var rejected = await Assert.ThrowsExactlyAsync<AgentWorkException>(async () =>
            await host.SubmitAsync(new string('x', 129)));
        Assert.AreEqual("request.invalid", rejected.Code, "129 chars must be rejected by the public Work entry.");
    }

    [TestMethod]
    public async Task W2_ForgedChatTurn_CannotBecomeManagedWorkBinding()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var forgedId = "work-" + new string('a', 59);
        var forgedMetadata = new Dictionary<string, string>
        {
            ["agent_work_actor"] = "actor-1",
            ["agent_work_operation"] = "op-evil",
            ["agent_work_request_hash"] = "forged-hash",
        };
        var (_, command) = await host.SubmitClientTurnAsync(forgedId, forgedMetadata);

        // The public chat ingress strips reserved canonical-Work markers before anything is persisted.
        Assert.IsFalse(host.MessageMetadata(command.UserMessageId).Contains("agent_work_", StringComparison.Ordinal),
            "Client-written agent_work_* markers must never reach persisted acceptance facts.");
        var failure = await Assert.ThrowsExactlyAsync<ManagedWorkBudgetException>(async () =>
            await host.ResolveWithAsync(host.LeaseFor(command), command, host.Profile));
        Assert.AreEqual(ManagedWorkBudgetErrorCodes.EvidenceMissing, failure.Code);
        Assert.AreEqual(0, (await host.Store.ListAccountIdsAsync()).Length,
            "A forged chat turn must never create a managed Work budget account.");
    }

    [TestMethod]
    public async Task W2_TrustedCanonicalWorkIngress_KeepsProvenanceAndBinds()
    {
        await using var host = await BindingHost.CreateAsync(enabled: true);
        var workId = "work-" + new string('b', 59);
        var (_, command) = await host.SubmitTrustedCanonicalWorkAsync(workId);

        var resolved = await host.ResolveWithAsync(host.LeaseFor(command), command, host.Profile);
        Assert.IsTrue(resolved.IsManaged);
        Assert.AreEqual("work:" + workId, resolved.Binding!.Runtime.AccountId);
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);
    }

    private sealed class BindingHost(
        SqliteConnection connection, ServiceProvider provider, FakeDbContextFactory factory,
        ExecutionBudgetStore store, FakePolicy policy) : IAsyncDisposable
    {
        public FakeDbContextFactory Factory { get; } = factory;
        public ExecutionBudgetStore Store { get; } = store;
        public FakePolicy Policy { get; } = policy;
        public LlmInvocationProfile Profile { get; } = new()
        {
            ProviderId = "provider-a", ProfileId = "profile-a", ModelId = "model-a", Role = "conscious",
        };

        public IManagedWorkBudgetBindingResolver Resolver =>
            provider.GetRequiredService<IServiceScopeFactory>().CreateScope()
                .ServiceProvider.GetRequiredService<IManagedWorkBudgetBindingResolver>();

        /// <summary>Resolves inside a fresh scope (the resolver/command reader are scoped services).</summary>
        public async Task<ManagedWorkBudgetBindingResult> ResolveWithAsync(
            ExecutionLease lease, ExecutionCommandRecord command, LlmInvocationProfile profile)
        {
            using var scope = provider.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IManagedWorkBudgetBindingResolver>();
            return await resolver.ResolveAsync(lease, command, profile, CancellationToken.None);
        }

        public ExecutionLease Lease(AgentWorkReceipt receipt) => new(
            receipt.CommandId, "worker-1", "ws-1", receipt.ConversationId, receipt.TurnId, "run-1", 1,
            DateTimeOffset.UtcNow.AddMinutes(5)) { TraceId = null };

        public async Task<ExecutionCommandRecord> ReadCommandAsync(AgentWorkReceipt receipt)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IExecutionCommandReader>()
                .GetAsync(receipt.CommandId) ?? throw new InvalidOperationException("command missing");
        }

        public async Task<ManagedWorkBudgetBindingResult> ResolveAsync(AgentWorkReceipt receipt)
        {
            var command = await ReadCommandAsync(receipt);
            return await ResolveWithAsync(Lease(receipt), command, Profile);
        }

        public async Task<AgentWorkReceipt> SubmitAsync(string operationId = "op-1")
        {
            var work = new AgentWorkService(provider.GetRequiredService<IAgentWorkBackend>(), new AllowAll());
            var api = work.ForCaller(new WorkCaller("actor-1"));
            return await api.SubmitAsync(Agent, new AgentWorkRequest(operationId, "hello managed work"),
                CancellationToken.None);
        }

        /// <summary>Public chat ingress: no trusted flags, so reserved Work markers are stripped by SubmitTurnHandler.</summary>
        public async Task<(AcceptanceResult Result, ExecutionCommandRecord Command)> SubmitClientTurnAsync(
            string clientRequestId, IReadOnlyDictionary<string, string>? metadata)
            => await SubmitTurnAsync(clientRequestId, metadata, trustedCanonicalWork: false);

        /// <summary>In-process canonical Work backend: marks the submission as trusted provenance.</summary>
        public async Task<(AcceptanceResult Result, ExecutionCommandRecord Command)> SubmitTrustedCanonicalWorkAsync(
            string workId)
            => await SubmitTurnAsync(workId, new Dictionary<string, string>
            {
                ["agent_work_actor"] = "actor-1",
                ["agent_work_operation"] = "op-1",
                ["agent_work_request_hash"] = "trusted-hash",
            }, trustedCanonicalWork: true);

        private async Task<(AcceptanceResult Result, ExecutionCommandRecord Command)> SubmitTurnAsync(
            string clientRequestId, IReadOnlyDictionary<string, string>? metadata, bool trustedCanonicalWork)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var store = new FakeAcceptanceStore(db);
            var handler = new SubmitTurnHandler(store, new AllowAllVisual(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmitTurnHandler>.Instance);
            var command = new SubmitTurnCommand("conv-1", "ws-1", "actor-1", clientRequestId,
                "msg-" + Guid.NewGuid().ToString("N")[..8],
                new RecipientRequest { Type = "agent", AgentIds = ["agent-1"] },
                [new ContentPart { Type = "text", Text = "hello" }], metadata)
            {
                IsTrustedCanonicalWorkIngress = trustedCanonicalWork,
            };
            var result = await handler.HandleAsync(command, CancellationToken.None);
            var record = await scope.ServiceProvider.GetRequiredService<IExecutionCommandReader>()
                .GetAsync(result.CommandIds[0]) ?? throw new InvalidOperationException("command missing");
            return (result, record);
        }

        public ExecutionLease LeaseFor(ExecutionCommandRecord command) => new(
            command.CommandId, "worker-1", command.WorkspaceId, command.ConversationId, command.TurnId,
            "run-1", 1, DateTimeOffset.UtcNow.AddMinutes(5)) { TraceId = null };

        public string MessageMetadata(string messageId)
        {
            using var scope = provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<PlatformDbContext>().ChatMessages
                .Single(message => message.MessageId == messageId).MetadataJson ?? string.Empty;
        }

        public static async Task<BindingHost> CreateAsync(bool enabled, string mode = "Enforced")
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var factory = new FakeDbContextFactory(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            var services = new ServiceCollection();
            services.AddSingleton<IDbContextFactory<PlatformDbContext>>(factory);
            services.AddScoped(_ => factory.CreateDbContext());
            services.AddScoped<IAgentWorkTargetResolver>(_ => new FakeResolver());
            services.AddScoped<ISubmitTurnHandler>(sp => new FakeSubmit(sp.GetRequiredService<PlatformDbContext>()));
            services.AddSingleton<IAgentWorkBackend, CanonicalAgentWorkBackend>();
            services.AddScoped<IExecutionCommandReader, ExecutionCommandReader>();
            services.AddSingleton<ExecutionBudgetStore>();
            services.AddSingleton<ExecutionBudgetAccountMapper>();
            services.AddSingleton<IModelCatalogSource>(new FakeCatalog());
            var policy = new FakePolicy(enabled, mode);
            services.AddSingleton<IManagedWorkBudgetPolicyProvider>(policy);
            services.AddScoped<IManagedWorkBudgetBindingResolver, ManagedWorkBudgetBindingResolver>();
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }

            var store = provider.GetRequiredService<ExecutionBudgetStore>();
            return new(connection, provider, factory, store, policy);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed class FakeDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeResolver : IAgentWorkTargetResolver
    {
        public Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct) => Task.FromResult("conv-1");
    }

    private sealed class AllowAll : IAgentWorkAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(WorkCaller caller, AgentRef agent, WorkAccess access, CancellationToken ct)
            => ValueTask.FromResult(true);
    }

    private sealed class FakePolicy(bool enabled, string mode) : IManagedWorkBudgetPolicyProvider
    {
        /// <summary>Mutable so a test can represent the host changing its policy between recoveries.</summary>
        public int MaxCalls { get; set; } = 10;
        public long? MaxInputTokensPerRequest { get; set; }

        public ManagedWorkBudgetOptions? GetPolicy() => enabled
            ? new ManagedWorkBudgetOptions
            {
                Enabled = true,
                Mode = mode,
                CostUnit = "usd",
                MaxOutputTokens = 200_000,
                MaxCost = 25m,
                MaxCalls = MaxCalls,
                MaxInputTokensPerRequest = MaxInputTokensPerRequest,
                MaxConcurrentAttempts = 2,
            }
            : null;
    }

    private sealed class FakeCatalog : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult<ModelCapabilityRecord?>(modelId == "model-a"
                ? new ModelCapabilityRecord("model-a", "rev-1", 128_000, 8_192,
                    ["text"], true, true, "available",
                    2m, 1m, 4m, "usd", "catalog", false,
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), "UTC", 100m, "pr-1")
                : null);

        public ValueTask<System.Collections.Immutable.ImmutableArray<ModelCapabilityRecord>> ListAsync(
            string workspaceId, string? cursor, int maxCount, CancellationToken ct = default)
            => ValueTask.FromResult(System.Collections.Immutable.ImmutableArray<ModelCapabilityRecord>.Empty);
    }

    private sealed class FakeAcceptanceStore(PlatformDbContext db) : IConversationAcceptanceStore
    {
        private static long _tick;

        public async Task<AcceptanceResult> AcceptBatchAsync(SubmitTurnRequest request, string workspaceId,
            string conversationId, string? userId, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _tick);
            var agentId = request.Recipients.AgentIds![0];
            var messageId = request.ClientMessageId;
            var turnId = "turn-" + seq;
            var commandId = "cmd-" + seq;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.AcceptanceBatches.Add(new AcceptanceBatchEntity
            {
                BatchId = "batch-" + seq, WorkspaceId = workspaceId, ClientRequestId = request.ClientRequestId,
                ConversationId = conversationId, MessageId = messageId, UserId = userId, TurnCount = 1,
                AcceptedSequence = seq, CreatedAt = now,
            });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = commandId, BatchId = "batch-" + seq, ClientRequestId = request.ClientRequestId,
                WorkspaceId = workspaceId, SessionId = conversationId, MessageId = messageId + "-assistant",
                UserMessageId = messageId, TurnId = turnId, AgentInstanceId = agentId, UserId = userId,
                Status = "pending", CreatedAt = now,
            });
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = messageId, SessionId = conversationId, WorkspaceId = workspaceId,
                AgentInstanceId = agentId, Role = "user", Content = "hello", UserId = userId,
                TurnId = turnId, CommandId = commandId, CreatedAt = now,
                MetadataJson = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = conversationId, Sequence = seq, EventId = "evt-" + seq,
                WorkspaceId = workspaceId, TurnId = turnId, CommandId = commandId, MessageId = messageId,
                Type = ConversationEventTypes.TurnAccepted, Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"), CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationTurns.Add(new ConversationTurnEntity
            {
                ConversationId = conversationId, TurnId = turnId, CommandId = commandId,
                WorkspaceId = workspaceId, UserId = userId, Status = "accepted",
                AcceptedSequence = seq, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new AcceptanceResult
            {
                ConversationId = conversationId, MessageId = messageId, TurnIds = [turnId],
                CommandIds = [commandId], AcceptedSequence = seq,
            };
        }
    }

    private sealed class AllowAllVisual : IVisualArtifactLocalFileResolver
    {
        public Task<VisualArtifactLocalFile?> ResolveLocalFileAsync(string workspaceId, string artifactId,
            CancellationToken ct = default) => Task.FromResult<VisualArtifactLocalFile?>(null);
    }

    private sealed class FakeSubmit(PlatformDbContext db) : ISubmitTurnHandler
    {
        private static long _tick;

        public async Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _tick);
            var acceptedSequence = seq;
            var messageId = "msg-" + seq;
            var turnId = "turn-" + seq;
            var commandId = "cmd-" + seq;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.AcceptanceBatches.Add(new AcceptanceBatchEntity
            {
                BatchId = "batch-" + seq, WorkspaceId = command.WorkspaceId, ClientRequestId = command.ClientRequestId,
                ConversationId = command.ConversationId, MessageId = messageId, UserId = command.UserId,
                TurnCount = 1, AcceptedSequence = acceptedSequence, CreatedAt = now,
            });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = commandId, BatchId = "batch-" + seq, ClientRequestId = command.ClientRequestId,
                WorkspaceId = command.WorkspaceId, SessionId = command.ConversationId, MessageId = messageId + "-assistant",
                UserMessageId = messageId, TurnId = turnId, AgentInstanceId = "agent-1", UserId = command.UserId,
                Status = "pending", CreatedAt = now,
            });
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = messageId, SessionId = command.ConversationId, WorkspaceId = command.WorkspaceId,
                AgentInstanceId = "agent-1", Role = "user", Content = string.Concat(command.Content.Select(c => c.Text)),
                UserId = command.UserId, TurnId = turnId, CommandId = commandId, CreatedAt = now,
                MetadataJson = JsonSerializer.Serialize(command.Metadata),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = command.ConversationId, Sequence = acceptedSequence, EventId = "evt-" + seq,
                WorkspaceId = command.WorkspaceId, TurnId = turnId, CommandId = commandId, MessageId = messageId,
                Type = ConversationEventTypes.TurnAccepted, Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"), CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationTurns.Add(new ConversationTurnEntity
            {
                ConversationId = command.ConversationId, TurnId = turnId, CommandId = commandId,
                WorkspaceId = command.WorkspaceId, UserId = command.UserId, Status = "accepted",
                AcceptedSequence = acceptedSequence, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new AcceptanceResult
            {
                ConversationId = command.ConversationId, MessageId = messageId, TurnIds = [turnId],
                CommandIds = [commandId], AcceptedSequence = acceptedSequence,
            };
        }
    }
}