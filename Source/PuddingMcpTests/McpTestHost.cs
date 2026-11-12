using System.Collections.Immutable;
using PuddingMcp.Contracts;

namespace PuddingMcpTests;

public sealed class FakeMcpTransport : IMcpTransport
{
    public TimeSpan StartDelay { get; set; }
    public Exception? StartFailure { get; set; }
    public Exception? SendFailure { get; set; }
    public Func<McpRequest, Task<McpResponse>>? OnSend { get; set; }
    public ImmutableArray<McpEvent> Events { get; set; } = [];
    public int StartCalls { get; private set; }
    public int SendCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public async Task StartAsync(McpServerRef server, CancellationToken ct)
    {
        StartCalls++;
        if (StartFailure is not null) throw StartFailure;
        if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, ct);
    }

    public Task<McpResponse> SendAsync(McpServerRef server, McpRequest request, CancellationToken ct)
    {
        SendCalls++;
        if (SendFailure is not null) return Task.FromException<McpResponse>(SendFailure);
        if (OnSend is not null) return OnSend(request);
        return Task.FromResult(new McpResponse(request.RequestId, true, "{}", null, false));
    }

    public Task<ImmutableArray<McpEvent>> ReadEventsAsync(McpServerRef server, long afterCursor, CancellationToken ct)
        => Task.FromResult(Events);

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeCursorStore : IMcpCursorStore
{
    public Dictionary<string, long> Cursors { get; } = new(StringComparer.Ordinal);
    public int Sets { get; private set; }

    public ValueTask<long> GetAsync(string serverId, CancellationToken ct = default)
        => ValueTask.FromResult(Cursors.GetValueOrDefault(serverId));

    public ValueTask SetAsync(string serverId, long cursor, CancellationToken ct = default)
    {
        Sets++;
        Cursors[serverId] = cursor;
        return ValueTask.CompletedTask;
    }
}

internal static class McpFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public static McpServerRef Server(long generation = 1, string serverId = "mcp-1") =>
        new(serverId, "ws-1", generation, McpTransportKind.HttpSse);
    public static McpRequest Request(string id = "req-1", int deadlineSeconds = 30) =>
        new(id, "tools/list", "{}", Now.AddSeconds(deadlineSeconds));

    public static McpRequest RequestAt(DateTimeOffset now, string id = "req-1")
        => new(id, "tools/list", "{}", now.AddSeconds(30));

    public sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
