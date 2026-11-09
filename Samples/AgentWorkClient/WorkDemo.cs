using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Samples.AgentWorkClient;

/// <summary>
/// Offline, injectable consumer of the single-turn work API. It accepts exactly two positional commands,
/// makes exactly one Submit or one Get call, and prints the original receipt/snapshot as one camelCase JSON line.
/// It never polls, retries, invents a replacement operation ID, sends a cancellation command or owns the
/// injected API/writers.
/// </summary>
public static class WorkDemo
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = string.Join(Environment.NewLine,
        "Usage: WorkDemo submit <workspaceId> <agentId> <operationId> <text>",
        "       WorkDemo get <workspaceId> <agentId> <operationId>");

    private enum CommandKind { Submit, Get }

    private readonly record struct Command(CommandKind Kind, string WorkspaceId, string AgentId,
        string OperationId, string? Text);

    public static async Task<int> RunAsync(string[] args, IAgentWorkApi api, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Argument errors are decided before any API call: fixed usage on stderr, exit 2.
        if (!TryParse(args, out var command))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var work = new Pudding.Api.Sdk.AgentWorkClient(api).For(new AgentRef(command.WorkspaceId, command.AgentId));
            if (command.Kind == CommandKind.Submit)
            {
                var receipt = await work.SubmitAsync(new AgentWorkRequest(command.OperationId, command.Text!), ct)
                    .ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(receipt, OutputJson)).ConfigureAwait(false);
                return 0;
            }

            var snapshot = await work.GetAsync(command.OperationId, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new NotFoundLine(false), OutputJson))
                    .ConfigureAwait(false);
                return 3;
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, OutputJson)).ConfigureAwait(false);
            return 0;
        }
        catch (AgentWorkException exception)
        {
            // Only stable failure facts: never the server message or the exception body.
            await error.WriteLineAsync(
                $"code={exception.Code} effectOutcome={exception.EffectOutcome} operationId={command.OperationId}")
                .ConfigureAwait(false);
            return exception.EffectOutcome == "unknown" ? 4 : 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
    }

    private static bool TryParse(string[] args, out Command command)
    {
        command = default;
        if (args.Length > 0 && args[0] == "submit")
        {
            // Strictly five arguments; the text is one argument and is never joined with neighbours.
            if (args.Length != 5 || HasBlank(args, 1, 4)) return false;
            command = new Command(CommandKind.Submit, args[1], args[2], args[3], args[4]);
            return true;
        }
        if (args.Length > 0 && args[0] == "get")
        {
            if (args.Length != 4 || HasBlank(args, 1, 3)) return false;
            command = new Command(CommandKind.Get, args[1], args[2], args[3], null);
            return true;
        }
        return false;
    }

    private static bool HasBlank(string[] args, int start, int end)
    {
        for (var index = start; index <= end; index++)
        {
            if (string.IsNullOrWhiteSpace(args[index])) return true;
        }
        return false;
    }

    private sealed record NotFoundLine(bool Found);
}
