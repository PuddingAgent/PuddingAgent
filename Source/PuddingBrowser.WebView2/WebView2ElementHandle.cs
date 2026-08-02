using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

internal sealed class WebView2ElementHandle : IElementHandle
{
    public WebView2ElementHandle(PageId pageId, long pageVersion, string fingerprint, BrowserElementInfo info)
    {
        PageId = pageId;
        PageVersion = pageVersion;
        LocatorFingerprint = fingerprint;
        Info = info;
        Id = new ElementHandleId(info.Ref);
    }

    public ElementHandleId Id { get; }
    public PageId PageId { get; }
    public long PageVersion { get; }
    public int? BackendNodeId => null;
    public string LocatorFingerprint { get; }
    public BrowserElementInfo Info { get; }

    public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Info.BoundingBox);
    }

    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct)
        => Task.FromException<BrowserScriptValue>(new BrowserOperationException(
            "browser_operation_not_supported",
            "Element evaluate is not available in Phase 2A-3"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
