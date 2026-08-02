using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserNavigateArgs
{
    [ToolParam("Action: goto, back, forward, reload, or stop.")]
    public required string Action { get; init; }

    [ToolParam("Browser page id to navigate.")]
    public required string PageId { get; init; }

    [ToolParam("Optional browser context id. Use it to disambiguate page ids.")]
    public string? ContextId { get; init; }

    [ToolParam("Absolute http/https URL. Required for goto.")]
    public string? Url { get; init; }

    [ToolParam("Navigation timeout in milliseconds. Defaults to 30000.")]
    public int TimeoutMs { get; init; } = 30_000;
}

[Tool(
    id: "browser_navigate",
    name: "Browser navigate",
    description: "Navigate a visible Desktop browser tab with goto, back, forward, reload, or stop.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserNavigateTool(IBrowserRuntime runtime, IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserNavigateArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserNavigateArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        if (action is not ("goto" or "back" or "forward" or "reload" or "stop"))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "action must be one of: goto, back, forward, reload, stop");
        }

        try
        {
            var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(
                runtime, args.ContextId, args.PageId, ct);
            if (resolved is null)
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");

            var page = resolved.Value.Page;
            NavigationResult? navigation = null;
            switch (action)
            {
                case "goto":
                    if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var url)
                        || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    {
                        return BrowserToolResponse.Failure(
                            "browser_invalid_arguments", "url must be an absolute http/https URL",
                            page.ContextId, page.Id);
                    }
                    if (args.TimeoutMs is < 1 or > 300_000)
                    {
                        return BrowserToolResponse.Failure(
                            "browser_invalid_arguments", "timeout_ms must be between 1 and 300000",
                            page.ContextId, page.Id);
                    }
                    navigation = await page.GotoAsync(
                        url, new NavigationOptions { TimeoutMs = args.TimeoutMs }, ct);
                    break;
                case "back":
                    await page.GoBackAsync(ct);
                    break;
                case "forward":
                    await page.GoForwardAsync(ct);
                    break;
                case "reload":
                    await page.ReloadAsync(ct);
                    break;
                case "stop":
                    await page.StopAsync(ct);
                    break;
            }

            return BrowserToolResponse.Success(new BrowserNavigationToolValue
            {
                Action = action,
                Page = BrowserToolResponse.Page(page.Info),
                NavigationOk = navigation?.Ok,
                StatusCode = navigation?.StatusCode,
                ErrorText = navigation?.ErrorText
            }, page.ContextId, page.Id, page.PageVersion);
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
}
