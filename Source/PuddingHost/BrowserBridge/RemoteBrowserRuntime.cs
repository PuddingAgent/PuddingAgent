using System.Runtime.CompilerServices;
using System.Text.Json;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.AgentTools;
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
    private readonly IBrowserCallerIdentityAccessor? _callerIdentity;

    private readonly IBrowserBindingGate? _bindingGate;
    private bool _disposed;

    public RemoteBrowserRuntime(
        IDesktopBrowserCommandBroker broker,
        IBrowserOperationOriginAccessor? originAccessor = null,
        IBrowserCallerIdentityAccessor? callerIdentity = null,
        // 控制面（§4.1）接线缝：发命令前过门。未注入 ⇒ 行为不变。
        IBrowserBindingGate? bindingGate = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _originAccessor = originAccessor;
        _callerIdentity = callerIdentity;
        _bindingGate = bindingGate;
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

    /// <summary>Bridge 传输的截图：把请求参数放上 <c>page.screenshot</c>，由桌面侧执行同一套采集与准入。</summary>
    public async Task<BrowserScreenshotDescriptor> ScreenshotAsync(
        BrowserScreenshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = await ExecuteAsync<BrowserScreenshotDescriptor>(
            BrowserBridgeCommandNames.PageScreenshot,
            new PageScreenshotArguments
            {
                ExpectedPageVersion = request.ExpectedPageVersion.Value,
                Scope = DesktopScreenshotScopeWire.NameOf(request.Scope),
                Detail = DesktopScreenshotDetailWire.NameOf(request.Detail),
                Format = request.Format,
                RegionX = request.RegionX,
                RegionY = request.RegionY,
                RegionWidth = request.RegionWidth,
                RegionHeight = request.RegionHeight,
            },
            contextId: new BrowserContextId(request.Target.ContextId),
            pageId: new PageId(request.Target.PageId),
            timeout: TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        return descriptor;
    }

    /// <summary>
    /// 有值命令：成功结果必须携带合法载荷；解码失败**不代表未执行**，
    /// 错误里带命令、operationId、期望 DTO、协议修订与阶段，便于定位而不泄露载荷。
    /// </summary>
    internal async Task<T> ExecuteAsync<T>(
        string name,
        object arguments,
        BrowserContextId? contextId,
        PageId? pageId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await SendAsync(name, arguments, contextId, pageId, timeout, ct).ConfigureAwait(false);

        if (result.Value is not { } value)
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationFailed,
                $"Browser command '{name}' (operation {result.OperationId}) returned no value; "
                + $"expected payload '{typeof(T).Name}' (protocol {BrowserBridgeProtocol.CurrentVersion})");
        }

        try
        {
            return value.Deserialize<T>(s_jsonOptions)
                   ?? throw new JsonException($"empty value for '{typeof(T).Name}'");
        }
        catch (JsonException ex)
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationFailed,
                $"Browser command '{name}' (operation {result.OperationId}) returned an invalid payload: "
                + $"expected '{typeof(T).Name}' (protocol {BrowserBridgeProtocol.CurrentVersion}, decode stage). {ex.Message}");
        }
    }

    /// <summary>
    /// 空响应命令（close 等）：成功**无需** Value；有值也不拒绝（消费者不读）。
    /// 失败/取消语义与原路径一致——副作用可能已完成，调用方不得据此自动重试。
    /// </summary>
    internal async Task ExecuteEmptyAsync(
        string name,
        object arguments,
        BrowserContextId? contextId,
        PageId? pageId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        _ = await SendAsync(name, arguments, contextId, pageId, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>统一的“发送 → 身份/期限/错误/关联”流程；不检查成功载荷形状。</summary>
    private async Task<BrowserBridgeCommandResult> SendAsync(
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

        // §3.2：Bridge 的每一条命令都是浏览器操作 ⇒ **必须**有可信调用者身份。
        // 缺身份/冒充 User/无运行身份时**不发送**（fail-closed），也不给默认身份。
        if (!BrowserCallerRequirement.TryRequireCaller(_callerIdentity?.Current, out var caller, out var callerReason))
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserUnauthorized,
                callerReason);
        }

        // §4.1：**发命令前**过一次控制面门（只对带页面的命令）。未注入 ⇒ 不做任何事（行为不变）。
        if (_bindingGate is { } gate && pageId is { } gatePage && contextId is { } gateContext)
        {
            await gate.EnsureAsync(caller, gateContext.Value, gatePage.Value, ct)
                .ConfigureAwait(false);
        }

        var result = await _broker.ExecuteAsync(new BrowserBridgeCommand
        {
            OperationId = operationId,
            Caller = new BrowserBridgeCallerIdentity
            {
                Kind = BrowserCallerKindWire.NameOf(caller.Kind),
                CallerId = caller.CallerId,
                TaskId = caller.TaskId,
                ParentTaskId = caller.ParentTaskId,
            },
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

        if (result.OperationId != operationId)
        {
            // 关联错位：不能把别的操作结果当成本次结果（更不得重试/重放）。
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationFailed,
                $"Browser command '{name}' returned a mismatched operation id");
        }

        return result;
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
