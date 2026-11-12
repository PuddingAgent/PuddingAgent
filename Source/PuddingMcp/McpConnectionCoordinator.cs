using System.Collections.Immutable;
using System.Diagnostics;
using PuddingMcp.Contracts;

namespace PuddingMcp;

/// <summary>
/// PA-05C MCP connection coordination: start has a bounded timeout and never reports Ready on failure; requests
/// require a Ready connection of the same generation; timeouts and uncertain transport failures are Unknown and
/// are never retried automatically; drain disposes the transport; events are accepted in sequence order with
/// duplicate/out-of-order events ignored, gaps reported, and the cursor persisted for reconnect.
/// </summary>
public sealed class McpConnectionCoordinator : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly IMcpCursorStore _cursors;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _startTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpConnectionState _state = McpConnectionState.New;
    private long _generation;
    private string? _failureCode;
    private string? _diagnostics;

    public McpConnectionCoordinator(IMcpTransport transport, IMcpCursorStore cursors, TimeProvider? clock = null,
        TimeSpan? startTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        _clock = clock ?? TimeProvider.System;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
        if (_startTimeout < TimeSpan.FromMilliseconds(1) || _startTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(startTimeout));
    }

    public McpConnectionStatus Status => new(_state, _generation, _failureCode, _diagnostics);

    public async ValueTask<McpConnectionStatus> ConnectAsync(McpServerRef server, CancellationToken ct = default)
    {
        if (server is null || !server.IsValid) throw Error(McpErrorCodes.ServerInvalid, "A bounded server ref is required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == McpConnectionState.Ready && _generation == server.Generation) return Status;
            _state = McpConnectionState.Starting;
            _failureCode = null;
            _diagnostics = null;
            using var timeout = new CancellationTokenSource(_startTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await _transport.StartAsync(server, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await SafeDisposeAsync().ConfigureAwait(false);
                _state = McpConnectionState.Failed;
                _failureCode = McpErrorCodes.StartTimeout;
                return Status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _state = McpConnectionState.New;
                throw;
            }
            catch (Exception ex)
            {
                await SafeDisposeAsync().ConfigureAwait(false);
                _state = McpConnectionState.Failed;
                _failureCode = McpErrorCodes.StartFailed;
                _diagnostics = Bounded(ex.Message);
                return Status;
            }
            _generation = server.Generation;
            _state = McpConnectionState.Ready;
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<McpResponse> RequestAsync(McpServerRef server, McpRequest request,
        CancellationToken ct = default)
    {
        if (server is null || !server.IsValid) throw Error(McpErrorCodes.ServerInvalid, "A bounded server ref is required.");
        if (request is null || !request.IsValid) throw Error(McpErrorCodes.RequestInvalid, "A bounded MCP request is required.");
        if (_state != McpConnectionState.Ready) throw Error(McpErrorCodes.NotReady, "The MCP connection is not ready.");
        if (_generation != server.Generation)
            throw Error(McpErrorCodes.StaleGeneration, "The request belongs to another connection generation.");
        var remaining = request.Deadline - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return new(request.RequestId, Success: false, Result: null, McpErrorCodes.RequestTimeout, Unknown: true);
        using var timeout = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var sendTask = _transport.SendAsync(server, request, linked.Token);
        try
        {
            var completed = await Task.WhenAny(sendTask, Task.Delay(remaining, ct)).ConfigureAwait(false);
            if (completed != sendTask)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return new(request.RequestId, Success: false, Result: null, McpErrorCodes.RequestTimeout, Unknown: true);
            }
            return await sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _diagnostics = Bounded(ex.Message);
            return new(request.RequestId, Success: false, Result: null, McpErrorCodes.RequestUnknown, Unknown: true);
        }
    }

    public async ValueTask<McpConnectionStatus> DrainAsync(McpServerRef server, CancellationToken ct = default)
    {
        if (server is null || !server.IsValid) throw Error(McpErrorCodes.ServerInvalid, "A bounded server ref is required.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is McpConnectionState.Stopped or McpConnectionState.New) return Status;
            _state = McpConnectionState.Draining;
            await SafeDisposeAsync().ConfigureAwait(false);
            _state = McpConnectionState.Stopped;
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<McpEventBatch> AcceptEventsAsync(McpServerRef server, CancellationToken ct = default)
    {
        if (server is null || !server.IsValid) throw Error(McpErrorCodes.ServerInvalid, "A bounded server ref is required.");
        if (_state != McpConnectionState.Ready)
            throw Error(McpErrorCodes.NotReady, "The MCP connection is not ready for events.");
        if (_generation != server.Generation)
            throw Error(McpErrorCodes.StaleGeneration, "The events belong to another connection generation.");
        var cursor = await _cursors.GetAsync(server.ServerId, ct).ConfigureAwait(false);
        var events = await _transport.ReadEventsAsync(server, cursor, ct).ConfigureAwait(false);
        var accepted = new List<McpEvent>();
        var gap = false;
        foreach (var evt in events.Where(e => e.Sequence > cursor).OrderBy(e => e.Sequence))
        {
            if (evt.Sequence <= cursor) continue;
            if (evt.Sequence > cursor + 1) gap = true;
            accepted.Add(evt);
            cursor = evt.Sequence;
        }
        if (accepted.Count > 0)
            await _cursors.SetAsync(server.ServerId, cursor, ct).ConfigureAwait(false);
        return new(accepted.ToImmutableArray(), cursor, gap);
    }

    public async ValueTask DisposeAsync()
    {
        await SafeDisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async ValueTask SafeDisposeAsync()
    {
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"mcp transport dispose failed: {Bounded(ex.Message)}");
        }
    }

    private static string Bounded(string message) => message.Length <= 512 ? message : message[..512];

    private static McpException Error(string code, string message) => new(code, message);
}
