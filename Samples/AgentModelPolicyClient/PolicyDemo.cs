using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Samples.AgentModelPolicyClient;

/// <summary>
/// Offline, injectable read-only consumer of the effective agent model policy API. It accepts the single
/// positional command <c>get &lt;workspaceId&gt; &lt;agentId&gt;</c>, makes exactly one
/// <c>IAgentModelPolicyApi.GetEffectiveAsync</c> call and prints the original effective snapshot as one
/// camelCase JSON line with every source, revision, missing-purpose and pending-application field untouched.
/// It never calls <c>UpdateAsync</c>, reads configuration files or environment variables, sends a model
/// request, fills in a default model, computes a price, retries or owns the injected API/writers.
/// </summary>
public static class PolicyDemo
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = "Usage: PolicyDemo get <workspaceId> <agentId>";

    public static async Task<int> RunAsync(string[] args, IAgentModelPolicyApi api, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Argument errors are decided before any API call: fixed usage on stderr, exit 2.
        if (!TryParse(args, out var workspaceId, out var agentId))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            // Exactly one read: the effective snapshot is printed as received and never completed by hand.
            var snapshot = await api.GetEffectiveAsync(new AgentRef(workspaceId, agentId), ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new NotFoundLine(false), OutputJson))
                    .ConfigureAwait(false);
                return 3;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, OutputJson)).ConfigureAwait(false);
            return 0;
        }
        catch (AgentModelPolicyException exception)
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

    private static bool TryParse(string[] args, out string workspaceId, out string agentId)
    {
        workspaceId = string.Empty;
        agentId = string.Empty;
        // Exactly one command: strictly three tokens; ids are passed verbatim and never trimmed, repaired or joined.
        if (args.Length != 3 || args[0] != "get") return false;
        if (string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2])) return false;
        workspaceId = args[1];
        agentId = args[2];
        return true;
    }

    private sealed record NotFoundLine(bool Found);
}
