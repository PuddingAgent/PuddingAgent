using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// Core-side browser runtime proxy. It translates platform-neutral browser calls into
/// authenticated Bridge commands and never references WPF or WebView2.
/// </summary>
public sealed class RemoteBrowserRuntime : IBrowserRuntime
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDesktopBrowserCommandBroker _broker;
    private readonly IBrowserOperationOriginAccessor? _originAccessor;
    private bool _disposed;

    public RemoteBrowserRuntime(
        IDesktopBrowserCommandBroker broker,
        IBrowserOperationOriginAccessor? originAccessor = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _originAccessor = originAccessor;
    }

    public BrowserRuntimeState State => _disposed
        ? BrowserRuntimeState.Disposed
        : _broker.IsDesktopConnected
            ? BrowserRuntimeState.Ready
            : BrowserRuntimeState.Created;

    public async Task<IBrowserContext> CreateContextAsync(
        BrowserContextOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var descriptor = await ExecuteAsync<BrowserContextDescriptor>(
            BrowserBridgeCommandNames.ContextCreate,
            new ContextCreateArguments { ContextId = options.Id?.Value },
            options.Id,
            pageId: null,
            timeout: TimeSpan.FromSeconds(30),
            ct: ct);
        return new RemoteBrowserContext(this, descriptor, options.Persistent);
    }

    public async Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct)
    {
        try
        {
            var descriptor = await ExecuteAsync<BrowserContextDescriptor>(
                BrowserBridgeCommandNames.ContextGetInfo,
                new ContextGetInfoArguments { ContextId = id.Value },
                id,
                pageId: null,
                timeout: TimeSpan.FromSeconds(10),
                ct: ct);
            return new RemoteBrowserContext(this, descriptor, persistent: true);
        }
        catch (BrowserOperationException ex)
            when (ex.Code == BrowserBridgeErrorCodes.BrowserContextNotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct)
    {
        var result = await ExecuteAsync<BrowserContextListDescriptor>(
            BrowserBridgeCommandNames.ContextList,
            new { },
            contextId: null,
            pageId: null,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct);

        return result.Contexts.Select(descriptor => new BrowserContextInfo
        {
            Id = new BrowserContextId(descriptor.ContextId),
            UserDataDirectory = descriptor.UserDataDirectory,
            Persistent = true,
            PageCount = descriptor.PageCount
        }).ToArray();
    }

    public Task CloseContextAsync(BrowserContextId id, CancellationToken ct)
        => ExecuteEmptyAsync(
            BrowserBridgeCommandNames.ContextClose,
            new ContextCloseArguments { ContextId = id.Value },
            id,
            pageId: null,
            timeout: TimeSpan.FromSeconds(30),
            ct: ct);

    public async IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    internal async Task<T> ExecuteAsync<T>(
        string name,
        object arguments,
        BrowserContextId? contextId,
        PageId? pageId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        EnsureNotDisposed();
        ct.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid();

        // Snapshot the current origin at command creation time.
        // Do NOT cache it lazily — AsyncLocal must be read on the calling thread.
        BrowserBridgeCommandOrigin? origin = null;
        var currentOrigin = _originAccessor?.Current;
        if (currentOrigin is not null)
        {
            origin = new BrowserBridgeCommandOrigin
            {
                WorkspaceId = Truncate(currentOrigin.WorkspaceId, 128),
                AgentInstanceId = Truncate(currentOrigin.AgentInstanceId, 128),
                SessionId = Truncate(currentOrigin.SessionId, 128),
                ConversationId = Truncate(currentOrigin.ConversationId, 128),
                RunId = Truncate(currentOrigin.RunId, 128),
                ToolCallId = Truncate(currentOrigin.ToolCallId, 128),
                ToolName = Truncate(currentOrigin.ToolName, 128)
            };
        }

        var result = await _broker.ExecuteAsync(new BrowserBridgeCommand
        {
            OperationId = operationId,
            ContextId = contextId?.Value,
            PageId = pageId?.Value,
            DeadlineUtc = DateTimeOffset.UtcNow.Add(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30)),
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments, s_jsonOptions),
            Origin = origin
        }, ct);

        if (!result.Success)
        {
            if (result.ErrorCode == BrowserBridgeErrorCodes.BrowserCancelled && ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            throw new BrowserOperationException(
                result.ErrorCode ?? BrowserBridgeErrorCodes.BrowserOperationFailed,
                result.ErrorMessage ?? "Browser command failed");
        }

        if (result.Value is not { } value)
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationFailed,
                $"Browser command '{name}' returned no value");
        }

        try
        {
            return value.Deserialize<T>(s_jsonOptions)
                   ?? throw new JsonException($"Browser command '{name}' returned an empty value");
        }
        catch (JsonException ex)
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationFailed,
                $"Browser command '{name}' returned an invalid payload: {ex.Message}");
        }
    }

    internal async Task ExecuteEmptyAsync(
        string name,
        object arguments,
        BrowserContextId? contextId,
        PageId? pageId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        _ = await ExecuteAsync<JsonElement>(name, arguments, contextId, pageId, timeout, ct);
    }

    private void EnsureNotDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
