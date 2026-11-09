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
/// PA-01 P2 at the real invocation boundary: the runtime facade gates through the real persistent ledger before the
/// provider call, settles actual usage, marks a missing-usage response Unknown, and a cancelled token never reaches the
/// provider.
/// </summary>
[TestClass]
public sealed class RuntimeBudgetBoundaryTests
{
    [TestMethod]
    public async Task BudgetedInvocation_ReachesProviderOnce_AndSettlesTheLedger()
    {
        await using var host = await Host.CreateAsync(new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 });
        var result = await host.Service.InvokeAsync(Request());
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, host.Client.Calls);
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.Cost > 0m);
        Assert.AreEqual(1, snapshot.Calls);
    }

    [TestMethod]
    public async Task MissingUsage_MarksTheAttemptUnknown_AndDoesNotRefund()
    {
        await using var host = await Host.CreateAsync(usage: null);
        var result = await host.Service.InvokeAsync(Request());
        Assert.IsTrue(result.Success, "The provider reply itself is usable even when usage is missing.");
        var snapshot = await host.Budget.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.UnknownCost > 0m, "A dispatched attempt without usage must remain Unknown.");
    }

    [TestMethod]
    public async Task CancelledBeforeDispatch_DoesNotCallTheProvider()
    {
        await using var host = await Host.CreateAsync(new TokenUsageDto { PromptTokens = 100, CompletionTokens = 10 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Service.InvokeAsync(Request(), cts.Token));
        Assert.AreEqual(0, host.Client.Calls);
        Assert.AreEqual(0, (await host.Budget.ReadAsync(host.Account)).ActiveAttempts);
    }

    private static LlmInvocationRequest Request() => new()
    {
        WorkspaceId = "ws-1", SessionId = "session-1", AgentInstanceId = "agent-1", AgentTemplateId = "template-1",
        Profile = new LlmInvocationProfile { ProviderId = "provider", ProfileId = "profile", ModelId = "model-a" },
        Messages = [new ChatMessage(ChatRole.User, "hello")],
        BudgetAttempt = new("account-runtime-1", [], "GoalRun", "goal-1", "ws-1", "agent-1", "session-1",
            "attempt-1", "rev-1", "pricing-v1", 1_000, 100, "usd"),
    };

    private sealed class Host(SqliteConnection connection, ExecutionBudgetStore budget, BudgetAccountId account,
        LlmInvocationService service, FakeClient client) : IAsyncDisposable
    {
        public ExecutionBudgetStore Budget { get; } = budget;
        public BudgetAccountId Account { get; } = account;
        public LlmInvocationService Service { get; } = service;
        public FakeClient Client { get; } = client;

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
            var budget = new ExecutionBudgetStore(factory);
            var account = new BudgetAccountId("account-runtime-1");
            await budget.CreateAccountAsync(account, null,
                new(MaxOutputTokens: 100_000, MaxCost: 10m, MaxConcurrentAttempts: 2),
                new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            var port = new PlatformModelAttemptBudgetPort(new ModelAttemptBudgetGate(budget, new Quotes()));
            var client = new FakeClient(new LlmResponse("ok", [], null, usage, null));
            return new(connection, budget, account,
                new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance, budgetPort: port), client);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();

        private sealed class Factory(DbContextOptions<PlatformDbContext> options)
            : IDbContextFactory<PlatformDbContext>
        {
            public PlatformDbContext CreateDbContext() => new(options);
        }
    }

    private sealed class FakeClient(LlmResponse response) : IRuntimeLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(response);
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
