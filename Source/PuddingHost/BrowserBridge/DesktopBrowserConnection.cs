using System.Threading.Channels;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// Represents a single Desktop WebSocket connection with its own outbound channel,
/// generation counter, and handshake state. Each connection owns exactly one send loop.
/// </summary>
public sealed class DesktopBrowserConnection : IAsyncDisposable
{
    private readonly Channel<BrowserBridgeEnvelope> _outbound;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _handshakeAccepted;
    private DateTimeOffset _lastReceivedAt = DateTimeOffset.UtcNow;

    public Guid ConnectionId { get; }
    public long Generation { get; }
    public bool IsHandshakeAccepted => _handshakeAccepted;
    public DateTimeOffset LastReceivedAt => _lastReceivedAt;
    public ChannelReader<BrowserBridgeEnvelope> Outbound => _outbound.Reader;
    public CancellationToken ConnectionToken => _cts.Token;

    public string DesktopInstanceId { get; private set; } = string.Empty;
    public int ProtocolVersion { get; private set; }
    public IReadOnlyList<string> Capabilities { get; private set; } = [];

    public DesktopBrowserConnection(Guid connectionId, long generation)
    {
        ConnectionId = connectionId;
        Generation = generation;
        _outbound = Channel.CreateBounded<BrowserBridgeEnvelope>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Attempts to accept the Hello handshake. Returns false if already accepted.
    /// </summary>
    public bool TryAcceptHello(BrowserBridgeHello hello, out BrowserBridgeHelloAck ack)
    {
        if (_handshakeAccepted)
        {
            ack = new BrowserBridgeHelloAck
            {
                ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
                Accepted = false,
                ErrorCode = BrowserBridgeErrorCodes.BrowserInvalidCommand,
                ErrorMessage = "Hello already accepted"
            };
            return false;
        }

        if (hello.ProtocolVersion != BrowserBridgeProtocol.CurrentVersion)
        {
            ack = new BrowserBridgeHelloAck
            {
                ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
                Accepted = false,
                ErrorCode = BrowserBridgeErrorCodes.BrowserProtocolMismatch,
                ErrorMessage = $"Unsupported protocol version: {hello.ProtocolVersion}"
            };
            return false;
        }

        DesktopInstanceId = hello.DesktopInstanceId;
        ProtocolVersion = hello.ProtocolVersion;
        Capabilities = hello.Capabilities;
        _handshakeAccepted = true;

        ack = new BrowserBridgeHelloAck
        {
            ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
            Accepted = true
        };
        return true;
    }

    /// <summary>
    /// Enqueues an envelope to this connection's outbound channel.
    /// Only the connection's own Send Loop should consume from Outbound.
    /// </summary>
    public ValueTask EnqueueAsync(BrowserBridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        return _outbound.Writer.WriteAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// Tries to enqueue without waiting. Returns false if channel is full or completed.
    /// </summary>
    public bool TryEnqueue(BrowserBridgeEnvelope envelope)
    {
        return _outbound.Writer.TryWrite(envelope);
    }

    public void MarkReceived(DateTimeOffset now)
    {
        _lastReceivedAt = now;
    }

    /// <summary>
    /// Signals this connection to shut down. Completes the outbound channel.
    /// </summary>
    public void Complete()
    {
        _outbound.Writer.TryComplete();
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        _cts.Dispose();
        await Task.CompletedTask;
    }
}
