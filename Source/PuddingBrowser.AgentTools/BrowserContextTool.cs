using Pudding.Contracts;
using Pudding.Contracts.Desktop;
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
    id: BrowserAgentToolIds.Context,
    name: "Browser context",
    description: "Create, list, inspect, or close Desktop browser contexts.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserContextTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopContextCapabilitySurface contexts,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserContextArgs>(originAccessor)
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
        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        // 迁移前固定 Persistent = true；缺口 #1 之后由能力通道承载（不再依赖进程内运行时）。
        var created = await contexts.CreateContextAsync(
            new BrowserContextCreateRequest(args.ContextId, persistent: true), call, ct);
        return created.IsFailure
            ? Failure(created.Error!)
            : BrowserToolResponse.Success(Value(created.Value), new BrowserContextId(created.Value.ContextId));
    }

    private async Task<ToolExecutionResult> ListAsync(CancellationToken ct)
    {
        var read = await ReadContextsAsync(ct);
        if (read is null)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        if (read.Value.IsFailure)
        {
            return Failure(read.Value.Error!);
        }

        return BrowserToolResponse.Success(read.Value.Value.Contexts.Select(Value).ToArray());
    }

    private async Task<ToolExecutionResult> GetAsync(BrowserContextArgs args, CancellationToken ct)
    {
        var read = await ReadContextsAsync(ct);
        if (read is null)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        if (read.Value.IsFailure)
        {
            return Failure(read.Value.Error!);
        }

        var listed = read.Value.Value.Contexts;
        var wanted = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
        // 不给 id 时沿用"用第一个可用上下文"的既有语义；给了就必须命中（否则如实报不存在）。
        var found = wanted is null
            ? listed.Count > 0 ? listed[0] : null
            : listed.FirstOrDefault(c => string.Equals(c.ContextId, wanted, StringComparison.Ordinal));
        return found is null
            ? BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found")
            : BrowserToolResponse.Success(Value(found), new BrowserContextId(found.ContextId));
    }

    private async Task<ToolExecutionResult> CloseAsync(BrowserContextArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ContextId))
        {
            return BrowserToolResponse.Failure("browser_invalid_arguments", "context_id is required for close");
        }

        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        var closed = await contexts.CloseContextAsync(
            new BrowserContextCloseRequest(args.ContextId.Trim()), call, ct);
        return closed.IsFailure
            ? Failure(closed.Error!)
            // 沿用迁移前的可见形状：{ closed: true } + context id。
            : BrowserToolResponse.Success(new { closed = true }, new BrowserContextId(closed.Value.ContextId));
    }

    /// <summary>读上下文清单；<c>null</c> 表示未连接 Desktop（与"读取失败"区分，两者错误码不同）。</summary>
    private async Task<CapabilityResult<DesktopContexts>?> ReadContextsAsync(CancellationToken ct)
    {
        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return null;
        }

        return await surface.GetContextsAsync(call, ct);
    }

    private static BrowserContextToolValue Value(DesktopContextInfo info) => new()
    {
        ContextId = info.ContextId,
        Persistent = info.Persistent,
        PageCount = info.PageCount,
    };

    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_context_failed", "stale_page_version");
}
