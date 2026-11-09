using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingModels;
using PuddingModels.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingRuntime.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// T1/T2/T6 end-to-end: real SQLite ledger + real gate + real Platform budget port + real runtime transport. A retry or
/// a streaming send must land as separate ledger attempt rows; the account must settle costs and keep no active attempts.
/// </summary>
[TestClass]
public sealed class TransportBudgetLedgerTests
{
    [TestMethod]
    public async Task BufferedRetry_TwoSends_TwoLedgerAttempts_AndAccountSettles()
    {
        await using var host = await Host.CreateAsync(new SequenceHandler(HttpStatusCode.InternalServerError,
            HttpStatusCode.OK));
        var facade = host.Facade();

        var result = await facade.InvokeAsync(Request(host.Account));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, host.Handler.Sends, "one failure + one retry = two real sends");
        var (settled, unknown, total) = await host.CountAttemptStatesAsync();
        Assert.AreEqual(2, total, "two ledger attempt rows");
        Assert.AreEqual(1, settled);
        Assert.AreEqual(1, unknown);
        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.Cost > 0m, "actual usage cost is recorded");
    }

    [TestMethod]
    public async Task StreamingSend_SettlesTheLedger()
    {
        await using var host = await Host.CreateAsync(new SseHandler());
        var facade = host.Facade();

        await foreach (var _ in facade.InvokeStreamAsync(Request(host.Account)))
        {
        }

        var snapshot = await host.Store.ReadAsync(host.Account);
        Assert.AreEqual(0, snapshot.ActiveAttempts);
        Assert.AreEqual(0m, snapshot.ReservedCost);
        Assert.IsTrue(snapshot.Cost > 0m);
        var (settled, unknown, total) = await host.CountAttemptStatesAsync();
        Assert.AreEqual(1, total);
        Assert.AreEqual(1, settled);
        Assert.AreEqual(0, unknown);
    }

    private static LlmInvocationRequest Request(BudgetAccountId account)
    {
        var binding = new LlmAttemptBudget(account.Value, [], "Work", "work-1", "ws-1", "agent-1", "session-1",
            "attempt-base", "rev-1", "pricing-v1", 1_000_000, 100_000, "usd");
        return new()
        {
            WorkspaceId = "ws-1", SessionId = "session-1", AgentInstanceId = "agent-1",
            AgentTemplateId = "template-1",
            Profile = new LlmInvocationProfile { ProviderId = "provider-a", ProfileId = "profile-a", ModelId = "model-a" },
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ConfigOverride = new LlmConfig
            {
                Endpoint = "http://llm.test/v1", ApiKey = "test-key", ModelId = "model-a",
                MaxOutputTokens = 4_096, Protocol = "openai",
            },
            BudgetAttempt = binding,
        };
    }

    private sealed class Host(SqliteConnection Connection, ExecutionBudgetStore Store, FakeHandler Handler)
        : IAsyncDisposable
    {
        public ExecutionBudgetStore Store { get; } = Store;
        public FakeHandler Handler { get; } = Handler;
        public BudgetAccountId Account { get; } = new("account-ledger");
        private readonly DbFactory _factory = new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(Connection).Options);

        public async Task<(int Settled, int Unknown, int Total)> CountAttemptStatesAsync()
        {
            await using var db = _factory.CreateDbContext();
            var states = await db.ExecutionBudgetReservations.AsNoTracking()
                .Where(r => r.AccountId == Account.Value).Select(r => r.State).ToListAsync();
            return (states.Count(s => s == 3), states.Count(s => s == 2), states.Count);
        }

        public LlmInvocationService Facade()
        {
            var client = new DirectLlmClient(new Factory(Handler), new FakeConfigs(),
                NullLogger<DirectLlmClient>.Instance);
            var gate = new ModelAttemptBudgetGate(Store, new Quotes());
            return new LlmInvocationService(client, NullLogger<LlmInvocationService>.Instance,
                budgetPort: new PlatformModelAttemptBudgetPort(gate));
        }

        public static async Task<Host> CreateAsync(FakeHandler handler)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new DbFactory(options);
            using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            var store = new ExecutionBudgetStore(factory);
            var account = new BudgetAccountId("account-ledger");
            await store.CreateAccountAsync(account, null, new BudgetLimits(MaxOutputTokens: 1_000_000, MaxCost: 10m),
                new BudgetIdentity(BudgetAccountKind.Work, "work-1", "ws-1", "agent-1", "session-1"), "usd",
                BudgetEnforcementMode.Enforced);
            return new(connection, store, handler);
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed class Quotes : IModelQuoteSource
    {
        public ValueTask<ModelQuoteSnapshot?> QuoteAsync(ModelQuoteRequest request, CancellationToken ct = default)
            => ValueTask.FromResult<ModelQuoteSnapshot?>(new("quote-1", request.ModelId, request.Revision, "usd",
                "per-1m-tokens", 1m, 1m, 2m, 1_000_000, request.DispatchAt.AddHours(-1), request.DispatchAt.AddHours(1),
                "utc", "declared", false, 10m, "pricing-v1", DateTimeOffset.UtcNow));
    }

    private sealed class FakeConfigs : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() =>
        [new() { ProviderId = "provider-a", Name = "provider-a", BaseUrl = "http://llm.test", IsEnabled = true }];
        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [new() { ModelId = "model-a", ProviderId = "provider-a", Name = "model-a", Protocol = "openai",
            MaxContextTokens = 128_000, MaxOutputTokens = 8_192, MaxConcurrentRequests = 8 }];
        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => new() { MaxRetries = 1, RetryDelaySeconds = 0 };
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }

    private sealed class Factory(FakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DbFactory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private abstract class FakeHandler : HttpMessageHandler
    {
        public int Sends { get; protected set; }
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : FakeHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            var status = _index < statuses.Length ? statuses[_index] : statuses[^1];
            _index++;
            const string body =
                "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model-a\"," +
                "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? body : "{\"error\":\"boom\"}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SseHandler : FakeHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            const string body =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
