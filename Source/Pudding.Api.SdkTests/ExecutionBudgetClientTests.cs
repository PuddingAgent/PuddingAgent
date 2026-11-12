using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

/// <summary>PA-01 01D: the budget facade is read-only and transparent — no retry, no rewriting, no swallowing.</summary>
[TestClass]
public sealed class ExecutionBudgetClientTests
{
    [TestMethod]
    public async Task Get_ForwardsTheAccountVerbatim_AndReturnsTheOriginalSnapshot()
    {
        var api = new Fake
        {
            Snapshot = new("account-1", 7, new(1000, 10m, 8, 2, 500), new(10, 20, 5, 0.25m, "usd"),
                new(30, 0.5m), new(40, 0.75m), ActiveAttempts: 1, Cancelled: false, CancellationRevision: 0,
                ReasonCode: null),
        };
        var client = new ExecutionBudgetClient(api);
        using var cts = new CancellationTokenSource();
        var snapshot = await client.GetAsync("account-1", cts.Token);

        Assert.AreSame(api.Snapshot, snapshot);
        Assert.AreEqual(1, api.Calls);
        Assert.AreEqual("account-1", api.AccountId);
        Assert.AreEqual(cts.Token, api.Token);
    }

    [TestMethod]
    public async Task Get_MissingAccount_ReturnsNull_WithoutInventingABudget()
    {
        var api = new Fake();
        var client = new ExecutionBudgetClient(api);
        Assert.IsNull(await client.GetAsync("account-missing"));
        Assert.AreEqual(1, api.Calls);
    }

    [TestMethod]
    public async Task Get_DoesNotSwallowTypedOrCancellationExceptions()
    {
        var typed = new Fake { Failure = new ExecutionBudgetException("budget.account_missing", "missing") };
        var client = new ExecutionBudgetClient(typed);
        var error = await Assert.ThrowsExactlyAsync<ExecutionBudgetException>(() => client.GetAsync("account-1"));
        Assert.AreEqual("budget.account_missing", error.Code);

        var cancelled = new Fake { Failure = new OperationCanceledException() };
        var cancelling = new ExecutionBudgetClient(cancelled);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cancelling.GetAsync("account-1"));
    }

    private sealed class Fake : IExecutionBudgetApi
    {
        public int Calls;
        public string? AccountId;
        public CancellationToken Token;
        public ExecutionBudgetSnapshot? Snapshot;
        public Exception? Failure;

        public Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
        {
            Calls++;
            AccountId = accountId;
            Token = ct;
            if (Failure is not null) return Task.FromException<ExecutionBudgetSnapshot?>(Failure);
            return Task.FromResult(Snapshot);
        }
    }
}
