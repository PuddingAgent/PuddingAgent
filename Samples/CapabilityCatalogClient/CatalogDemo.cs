using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Samples.CapabilityCatalogClient;

/// <summary>
/// Offline, injectable read-only consumer of the capability catalog API. It accepts the single positional
/// command <c>list &lt;kind&gt; [cursor]</c> for kind tool/mcp/context, makes exactly one
/// <c>ICapabilityCatalogApi.ListAsync</c> call and prints the original page as one camelCase JSON line with
/// every revision, generation, completeness/partial and missing-reason field untouched. It never pages
/// automatically, invokes a tool, executes MCP, mutates the catalog or treats a catalog entry as an
/// executable authorization.
/// </summary>
public static class CatalogDemo
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = "Usage: CatalogDemo list <kind> [cursor]";

    public static async Task<int> RunAsync(string[] args, ICapabilityCatalogApi api, TextWriter output,
        TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Argument errors are decided before any API call: fixed usage on stderr, exit 2.
        if (!TryParse(args, out var kind, out var cursor))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            // Exactly one query: the page is printed as received and the sample never pages on its own.
            var page = await api.ListAsync(new CapabilityQuery(kind, cursor), ct).ConfigureAwait(false);
            if (page is null)
            {
                // A missing page means the catalog source is unavailable, never "the catalog is empty".
                await error.WriteLineAsync("code=capability.source_unavailable").ConfigureAwait(false);
                return 1;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(page, OutputJson)).ConfigureAwait(false);
            return 0;
        }
        catch (CapabilityCatalogException exception)
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

    private static bool TryParse(string[] args, out string kind, out string? cursor)
    {
        kind = string.Empty;
        cursor = null;
        // Exactly one command: two or three tokens; values are passed verbatim and never trimmed or joined.
        if (args.Length is not (2 or 3) || args[0] != "list") return false;
        // The published kinds are case-sensitive; anything else is a usage error, not a query.
        if (args[1] is not ("tool" or "mcp" or "context")) return false;
        if (args.Length == 3 && string.IsNullOrWhiteSpace(args[2])) return false;
        kind = args[1];
        cursor = args.Length == 3 ? args[2] : null;
        return true;
    }
}
