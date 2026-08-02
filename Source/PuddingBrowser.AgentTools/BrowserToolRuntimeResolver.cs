using PuddingBrowser.Abstractions;

namespace PuddingBrowser.AgentTools;

internal static class BrowserToolRuntimeResolver
{
    public static async Task<IBrowserContext?> ResolveContextAsync(
        IBrowserRuntime runtime,
        string? contextId,
        bool createIfMissing,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(contextId))
            return await runtime.GetContextAsync(new BrowserContextId(contextId.Trim()), ct);

        var contexts = await runtime.ListContextsAsync(ct);
        if (contexts.Count > 0)
            return await runtime.GetContextAsync(contexts[0].Id, ct);

        return createIfMissing
            ? await runtime.CreateContextAsync(new BrowserContextOptions(), ct)
            : null;
    }

    public static async Task<(IBrowserContext Context, IBrowserPage Page)?> ResolvePageAsync(
        IBrowserRuntime runtime,
        string? contextId,
        string? pageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            return null;

        var id = new PageId(pageId.Trim());
        if (!string.IsNullOrWhiteSpace(contextId))
        {
            var context = await runtime.GetContextAsync(new BrowserContextId(contextId.Trim()), ct);
            if (context is null)
                return null;
            var page = await context.GetPageAsync(id, ct);
            return page is null ? null : (context, page);
        }

        foreach (var contextInfo in await runtime.ListContextsAsync(ct))
        {
            var context = await runtime.GetContextAsync(contextInfo.Id, ct);
            if (context is null)
                continue;
            var page = await context.GetPageAsync(id, ct);
            if (page is not null)
                return (context, page);
        }

        return null;
    }
}
