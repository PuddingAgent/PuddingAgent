using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Samples.AgentDirectoryClient;

/// <summary>
/// Offline, injectable consumer of the read-only agent directory API. It accepts exactly one of two
/// positional commands, makes exactly one List or one Get call, and prints the original page/entry as one
/// camelCase JSON line. It never pages, trims or repairs its arguments, retries a query, creates, archives,
/// cancels or destroys an agent, or owns the injected API/writers. Entries and pages are borrowed handles.
/// </summary>
public static class DirectoryDemo
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = string.Join(Environment.NewLine,
        "Usage: DirectoryDemo get <workspaceId> <agentId>",
        "       DirectoryDemo list <workspaceId> [cursor]");

    private enum CommandKind { Get, List }

    private readonly record struct Command(CommandKind Kind, string WorkspaceId, string? AgentId, string? Cursor);

    public static async Task<int> RunAsync(string[] args, IAgentDirectoryApi api, TextWriter output, TextWriter error,
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
            if (command.Kind == CommandKind.Get)
            {
                var entry = await api.GetAsync(new AgentRef(command.WorkspaceId, command.AgentId!), ct)
                    .ConfigureAwait(false);
                if (entry is null)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new NotFoundLine(false), OutputJson))
                        .ConfigureAwait(false);
                    return 3;
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(entry, OutputJson)).ConfigureAwait(false);
                return 0;
            }

            // MaxCount stays at the contract default 50; an absent cursor is passed as null, never invented.
            var page = await api.ListAsync(new AgentListQuery(command.WorkspaceId, 50, command.Cursor), ct)
                .ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(page, OutputJson)).ConfigureAwait(false);
            return 0;
        }
        catch (AgentDirectoryException exception)
        {
            // Only the stable failure code: never the server message or the request content.
            await error.WriteLineAsync($"code={exception.Code}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
    }

    private static bool TryParse(string[] args, out Command command)
    {
        command = default;
        if (args.Length > 0 && args[0] == "get")
        {
            // Strictly three tokens; ids are passed verbatim and are never trimmed or joined.
            if (args.Length != 3 || HasBlank(args, 1, 2)) return false;
            command = new Command(CommandKind.Get, args[1], args[2], null);
            return true;
        }
        if (args.Length > 0 && args[0] == "list")
        {
            // Two or three tokens; an absent cursor stays null while an explicitly blank one is a usage error.
            if (args.Length is not (2 or 3) || HasBlank(args, 1, args.Length - 1)) return false;
            command = new Command(CommandKind.List, args[1], null, args.Length == 3 ? args[2] : null);
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
