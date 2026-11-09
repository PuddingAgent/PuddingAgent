using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingRuntime.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 end-to-end account binding: the real mapper creates Enforced accounts, and the runtime facade gates one
/// provider attempt through the real port/gate/persistent store. Attempt/model/price values are fixtures; account id,
/// ancestors, identity and cost unit always come from the mapper result (never hand-written in the test).
/// </summary>
[TestClass]
public sealed class RuntimeBudgetAccountBindingTests
{
    [TestMethod]
    public async Task B01_MapperBoundLeaf_ReachesProviderOnce_AndSettlesRootAndLeafExactlyOnce()
    {
        await using var host = await Host.CreateAsync(new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 });
        var root = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.GoalRun, "goal-1"));
        var leaf = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1", root.AccountId));

        var result = await host.Service.InvokeAsync(Request(Budget(leaf, "attempt-1")));

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, host.Client.Calls, "The provider must be called exactly once.");
        var rootSnapshot = await host.Store.ReadAsync(root.AccountId);
        var leafSnapshot = await host.Store.ReadAsync(leaf.AccountId);
        foreach (var snapshot in new[] { rootSnapshot, leafSnapshot })
        {
            Assert.AreEqual(1, snapshot.Calls, "Each account on the bound path records the attempt once.");
            Assert.AreEqual(0, snapshot.ActiveAttempts);
            Assert.AreEqual(0m, snapshot.ReservedCost);
            Assert.IsTrue(snapshot.Cost > 0m, "Actual usage must be settled on every account.");
        }
        Assert.AreEqual(rootSnapshot.Cost, leafSnapshot.Cost, "Every ancestor records the same settled cost once.");
        Assert.AreEqual(100L, rootSnapshot.InputTokens);
        Assert.AreEqual(10L, rootSnapshot.OutputTokens);
    }

    [TestMethod]
    public async Task B02_MissingProviderUsage_StaysUnknownOnRootAndLeaf_AndIsNotRefunded()
    {
        await using var host = await Host.CreateAsync(usage: null);
        var root = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.GoalRun, "goal-1"));
        var leaf = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1", root.AccountId));

        var result = await host.Service.InvokeAsync(Request(Budget(leaf, "attempt-1")));

        Assert.IsTrue(result.Success, "A usable reply with missing usage is not a transport failure.");
        Assert.AreEqual(1, host.Client.Calls);
        foreach (var account in new[] { root.AccountId, leaf.AccountId })
        {
            var snapshot = await host.Store.ReadAsync(account);
            Assert.AreEqual(0, snapshot.ActiveAttempts);
            Assert.AreEqual(0m, snapshot.ReservedCost);
            Assert.IsTrue(snapshot.UnknownCost > 0m, "A dispatched attempt without usage must remain Unknown.");
            Assert.IsTrue(snapshot.UnknownOutputTokens > 0L);
        }
    }

    [TestMethod]
    public async Task B03_ExhaustedRootMaxCalls_BlocksTheSecondChild_AndTheProviderIsNotCalledAgain()
    {
        await using var host = await Host.CreateAsync(new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 });
        var root = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.GoalRun, "goal-1",
            limits: new(MaxOutputTokens: 100_000, MaxCost: 10m, MaxCalls: 1)));
        var firstChild = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1", root.AccountId));
        var secondChild = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-2", root.AccountId));

        var first = await host.Service.InvokeAsync(Request(Budget(firstChild, "attempt-1")));
        Assert.IsTrue(first.Success);
        Assert.AreEqual(1, host.Client.Calls);

        var second = await host.Service.InvokeAsync(Request(Budget(secondChild, "attempt-2")));
        Assert.IsFalse(second.Success, "A second child cannot reset or bypass the shared root budget.");
        Assert.AreEqual(BudgetErrorCodes.Exhausted, second.ErrorCode);
        Assert.AreEqual(1, host.Client.Calls, "The blocked attempt must never reach the provider.");
        Assert.AreEqual(1, (await host.Store.ReadAsync(root.AccountId)).Calls);
        var secondSnapshot = await host.Store.ReadAsync(secondChild.AccountId);
        Assert.AreEqual(0, secondSnapshot.Calls);
        Assert.AreEqual(0, secondSnapshot.ActiveAttempts);
        Assert.AreEqual(0m, secondSnapshot.ReservedCost);
    }

    [TestMethod]
    public async Task B04_PersistentlyCancelledRoot_BlocksTheBoundChild_WithNoProviderCallOrReservation()
    {
        await using var host = await Host.CreateAsync(new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 });
        var root = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.GoalRun, "goal-1"));
        var leaf = await host.Mapper.ResolveAsync(Descriptor(BudgetAccountKind.Work, "work-1", root.AccountId));

        var cancelled = await host.Store.CancelAsync(new CancelBudget(root.AccountId, "user.cancelled", 1));
        Assert.IsFalse(cancelled.AlreadyCancelled);

        var result = await host.Service.InvokeAsync(Request(Budget(leaf, "attempt-1")));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(BudgetErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(0, host.Client.Calls, "A persistently cancelled root must stop dispatch, not just a cancelled token.");
        var leafSnapshot = await host.Store.ReadAsync(leaf.AccountId);
        Assert.AreEqual(0, leafSnapshot.Calls);
        Assert.AreEqual(0, leafSnapshot.ActiveAttempts);
        Assert.AreEqual(0m, leafSnapshot.ReservedCost);
        Assert.AreEqual(0, (await host.Store.ReadAsync(root.AccountId)).Calls);
    }

    private static BudgetAccountDescriptor Descriptor(BudgetAccountKind kind, string rootId,
        BudgetAccountId? parent = null, BudgetLimits? limits = null) => new(kind, rootId, "ws-1", "agent-1",
        "session-1", limits ?? new(MaxOutputTokens: 100_000, MaxCost: 10m), "usd", BudgetEnforcementMode.Enforced,
        parent);

    /// <summary>Account identity, ancestors and cost unit come from the real mapper result; only attempt/model/price are fixtures.</summary>
    private static LlmAttemptBudget Budget(BudgetAccountScope scope, string attemptId) => new(scope.AccountId.Value,
        [.. scope.Ancestors.Select(account => account.Value)], scope.Identity.RootKind.ToString(), scope.Identity.RootId,
        scope.Identity.WorkspaceId, scope.Identity.AgentId, scope.Identity.SessionId, attemptId, "rev-1", "pricing-v1",
        1_000, 100, scope.CostUnit);

    private static LlmInvocationRequest Request(LlmAttemptBudget budget) => new()
    {
        WorkspaceId = budget.WorkspaceId,
        SessionId = budget.SessionId,
        AgentInstanceId = budget.AgentId,
        AgentTemplateId = "template-1",
        Profile = new LlmInvocationProfile { ProviderId = "provider", ProfileId = "profile", ModelId = "model-a" },
        Messages = [new ChatMessage(ChatRole.User, "hello")],
        BudgetAttempt = budget,
    };

    private sealed class Host : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Host(SqliteConnection connection, ExecutionBudgetStore store, ExecutionBudgetAccountMapper mapper,
            LlmInvocationService service, FakeClient client)
        {
            _connection = connection;
            Store = store;
            Mapper = mapper;
            Service = service;
            Client = client;
        }

        public ExecutionBudgetStore Store { get; }
        public ExecutionBudgetAccountMapper Mapper { get; }
        public LlmInvocationService Service { get; }
        public FakeClient Client { get; }

        public static async Task<Host> CreateAsync(TokenUsageDto? usage)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var store = new ExecutionBudgetStore(factory);
            var mapper = new ExecutionBudgetAccountMapper(store);
            var port = new PlatformModelAttemptBudgetPort(new ModelAttemptBudgetGate(store, new Quotes()));
            var client = new FakeClient(usage);
            var service = new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance,
                budgetPort: port);
            return new(connection, store, mapper, service, client);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeClient(TokenUsageDto? usage) : IRuntimeLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse("ok", [], null, usage, null));
        }

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class Quotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 0.5m, 2m, 1_000_000, request.DispatchAt.AddHours(-1),
                request.DispatchAt.AddHours(1), "utc", "declared", false, 10m, "pricing-v1", DateTimeOffset.UtcNow));
    }
}
