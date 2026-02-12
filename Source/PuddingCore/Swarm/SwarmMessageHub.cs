using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

public sealed class SwarmMessageHub : ISwarmMessageHub, IAsyncDisposable
{
    private readonly ISwarmTransport _transport;
    private readonly object _gate = new();
    private readonly List<SwarmMessage> _inbox = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;

    public SwarmMessageHub(ISwarmTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _listenerTask = Task.Run(ListenLoopAsync);
    }

    public async Task PostAsync(
        string from,
        string to,
        string type,
        string content,
        SwarmMessagePriority priority = SwarmMessagePriority.Normal,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var message = new SwarmMessage(
            Id: $"msg-{Guid.NewGuid():N}",
            From: from,
            To: to,
            Type: type,
            Content: content,
            Timestamp: DateTimeOffset.Now,
            Priority: priority,
            Metadata: metadata);
        await _transport.SendAsync(to, message, ct);
    }

    public async Task BroadcastAsync(
        string from,
        string type,
        string content,
        SwarmMessagePriority priority = SwarmMessagePriority.Normal,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var message = new SwarmMessage(
            Id: $"msg-{Guid.NewGuid():N}",
            From: from,
            To: null,
            Type: type,
            Content: content,
            Timestamp: DateTimeOffset.Now,
            Priority: priority,
            Metadata: metadata);
        await _transport.BroadcastAsync(message, ct);
    }

    public async Task<IReadOnlyList<SwarmMessage>> ReadInboxAsync(bool clear = true, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        lock (_gate)
        {
            var output = _inbox
                .OrderByDescending(m => m.Priority)
                .ThenBy(m => m.Timestamp)
                .ToList();
            if (clear)
                _inbox.Clear();
            return output;
        }
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            await foreach (var message in _transport.ReceiveAsync(_cts.Token))
            {
                lock (_gate)
                {
                    _inbox.Add(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _listenerTask;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        _cts.Dispose();
    }
}

