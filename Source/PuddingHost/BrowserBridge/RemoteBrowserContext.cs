using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

public sealed class RemoteBrowserContext : IBrowserContext
{
    private readonly RemoteBrowserRuntime _runtime;
    private bool _disposed;

    internal RemoteBrowserContext(
        RemoteBrowserRuntime runtime,
        BrowserContextDescriptor descriptor,
        bool persistent)
    {
        _runtime = runtime;
        Id = new BrowserContextId(descriptor.ContextId);
        Info = new BrowserContextInfo
        {
            Id = Id,
            UserDataDirectory = descriptor.UserDataDirectory,
            Persistent = persistent,
            PageCount = descriptor.PageCount
        };
    }

    public BrowserContextId Id { get; }
    public BrowserContextInfo Info { get; private set; }

    public async Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(options);
        var descriptor = await _runtime.ExecuteAsync<BrowserPageDescriptor>(
            BrowserBridgeCommandNames.PageCreate,
            new PageCreateArguments
            {
                InitialUrl = options.InitialUrl?.ToString(),
                Activate = options.Activate
            },
            Id,
            pageId: null,
            timeout: options.InitialUrl is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60),
            ct: ct);
        Info = Info with { PageCount = Info.PageCount + 1 };
        return new RemoteBrowserPage(_runtime, descriptor);
    }

    public async Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct)
    {
        EnsureNotDisposed();
        try
        {
            var descriptor = await _runtime.ExecuteAsync<BrowserPageDescriptor>(
                BrowserBridgeCommandNames.PageGetInfo,
                new { },
                Id,
                id,
                TimeSpan.FromSeconds(10),
                ct);
            return string.Equals(descriptor.ContextId, Id.Value, StringComparison.Ordinal)
                ? new RemoteBrowserPage(_runtime, descriptor)
                : null;
        }
        catch (BrowserOperationException ex)
            when (ex.Code == BrowserBridgeErrorCodes.BrowserPageNotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct)
    {
        EnsureNotDisposed();
        var result = await _runtime.ExecuteAsync<BrowserPageListDescriptor>(
            BrowserBridgeCommandNames.PageList,
            new { },
            Id,
            pageId: null,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct);
        var pages = result.Pages
            .Where(page => string.Equals(page.ContextId, Id.Value, StringComparison.Ordinal))
            .Select(RemoteBrowserPage.ToPageInfo)
            .ToArray();
        Info = Info with { PageCount = pages.Length };
        return pages;
    }

    public async Task ClosePageAsync(PageId id, CancellationToken ct)
    {
        EnsureNotDisposed();
        await _runtime.ExecuteEmptyAsync(
            BrowserBridgeCommandNames.PageClose,
            new PageCloseArguments { PageId = id.Value },
            Id,
            id,
            TimeSpan.FromSeconds(30),
            ct);
        Info = Info with { PageCount = Math.Max(0, Info.PageCount - 1) };
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        IReadOnlyList<Uri>? urls, CancellationToken ct) => Unsupported<IReadOnlyList<BrowserCookie>>();
    public Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct) => Unsupported();
    public Task ClearCookiesAsync(CancellationToken ct) => Unsupported();
    public Task GrantPermissionsAsync(
        Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct) => Unsupported();
    public Task ResetPermissionsAsync(CancellationToken ct) => Unsupported();

    private static Task Unsupported()
        => Task.FromException(new BrowserOperationException(
            BrowserBridgeErrorCodes.BrowserOperationNotSupported,
            "This browser context operation is not available in Phase 2A-2"));

    private static Task<T> Unsupported<T>()
        => Task.FromException<T>(new BrowserOperationException(
            BrowserBridgeErrorCodes.BrowserOperationNotSupported,
            "This browser context operation is not available in Phase 2A-2"));

    private void EnsureNotDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
