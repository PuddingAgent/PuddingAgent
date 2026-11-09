using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Samples.ExecutionBudgetClient;

/// <summary>
/// Offline, injectable read-only consumer of the execution budget API. It accepts the single positional
/// command <c>get &lt;accountId&gt;</c>, makes exactly one <c>IExecutionBudgetApi.GetAsync</c> call and prints
/// the original execution budget snapshot as one camelCase JSON line with every limit, actual, reserved and
/// unknown amount, cost unit, revision and cancellation field untouched. It never implements a ledger, a
/// budget calculation, a derived "available balance", a poll, a retry or a cancel, and it never turns an
/// unknown amount/token into zero or reports an exhausted budget as a failed query.
/// </summary>
public static class BudgetDemo
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = "Usage: BudgetDemo get <accountId>";

    public static async Task<int> RunAsync(string[] args, IExecutionBudgetApi api, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Argument errors are decided before any API call: fixed usage on stderr, exit 2.
        if (!TryParse(args, out var accountId))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            // Exactly one read: the snapshot is printed as received and never completed or recalculated.
            var snapshot = await api.GetAsync(accountId, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new NotFoundLine(false), OutputJson))
                    .ConfigureAwait(false);
                return 3;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, OutputJson)).ConfigureAwait(false);
            return 0;
        }
        catch (ExecutionBudgetException exception)
        {
            // Only the stable failure code: never the message or the request content.
            await error.WriteLineAsync($"code={exception.Code}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
    }

    private static bool TryParse(string[] args, out string accountId)
    {
        accountId = string.Empty;
        // Exactly one command: strictly two tokens; the account id is passed verbatim and never trimmed or joined.
        if (args.Length != 2 || args[0] != "get") return false;
        if (string.IsNullOrWhiteSpace(args[1])) return false;
        accountId = args[1];
        return true;
    }

    private sealed record NotFoundLine(bool Found);
}
