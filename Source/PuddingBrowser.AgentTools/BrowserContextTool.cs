using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserContextArgs
{
    [ToolParam("Action: create, list, get, or close.")]
    public required string Action { get; init; }

    [ToolParam("Optional context id. Required for close; optional for get and create.")]
    public string? ContextId { get; init; }
}

[Tool(
    id: "browser_context",
    name: "Browser context",
    description: "Create, list, inspect, or close Desktop browser contexts.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserContextTool(IBrowserRuntime runtime) : PuddingToolBase<BrowserContextArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserContextArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        try
        {
            return action switch
            {
                "create" => await CreateAsync(args, ct),
                "list" => await ListAsync(ct),
                "get" => await GetAsync(args, ct),
                "close" => await CloseAsync(args, ct),
                _ => BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "action must be one of: create, list, get, close")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BrowserOperationException ex)
        {
            return BrowserToolResponse.FromException(ex);
        }
    }

    private async Task<ToolExecutionResult> CreateAsync(BrowserContextArgs args, CancellationToken ct)
    {
        var browserContext = await runtime.CreateContextAsync(new BrowserContextOptions
        {
            Id = string.IsNullOrWhiteSpace(args.ContextId)
                ? null
                : new BrowserContextId(args.ContextId.Trim()),
            Persistent = true
        }, ct);
        return BrowserToolResponse.Success(
            BrowserToolResponse.Context(browserContext.Info),
            browserContext.Id);
    }

    private async Task<ToolExecutionResult> ListAsync(CancellationToken ct)
    {
        var contexts = await runtime.ListContextsAsync(ct);
        return BrowserToolResponse.Success(contexts.Select(BrowserToolResponse.Context).ToArray());
    }

    private async Task<ToolExecutionResult> GetAsync(BrowserContextArgs args, CancellationToken ct)
    {
        var browserContext = await BrowserToolRuntimeResolver.ResolveContextAsync(
            runtime, args.ContextId, createIfMissing: false, ct: ct);
        return browserContext is null
            ? BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found")
            : BrowserToolResponse.Success(
                BrowserToolResponse.Context(browserContext.Info),
                browserContext.Id);
    }

    private async Task<ToolExecutionResult> CloseAsync(BrowserContextArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ContextId))
            return BrowserToolResponse.Failure("browser_invalid_arguments", "context_id is required for close");

        var id = new BrowserContextId(args.ContextId.Trim());
        await runtime.CloseContextAsync(id, ct);
        return BrowserToolResponse.Success(new { closed = true }, id);
    }
}
