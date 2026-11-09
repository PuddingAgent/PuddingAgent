using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>
/// PA-01 read-only budget facade. Callers can only read an already-authorized account; the account identity is
/// opaque and cannot be created, topped up or uncancelled through this client.
/// </summary>
public sealed class ExecutionBudgetClient(IExecutionBudgetApi api)
{
    public Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
        => api.GetAsync(accountId, ct);
}
