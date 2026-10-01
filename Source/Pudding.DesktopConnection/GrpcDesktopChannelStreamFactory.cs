using System.IO.Pipes;
using Grpc.Core;
using Grpc.Net.Client;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

/// <summary>
/// 生产传输：Core 托管唯一的 Kestrel gRPC 服务，Desktop 主动拨入（Pipe/UDS/Loopback/TLS 均为 HTTP/2 传输）。
/// </summary>
public sealed class GrpcDesktopChannelStreamFactory : IDesktopChannelStreamFactory, IAsyncDisposable
{
    private readonly DesktopChannelTransport _transport;
    private readonly DesktopChannelAuthentication? _authentication;
    private readonly int _maxMessageBytes;
    private GrpcChannel? _channel;

    public GrpcDesktopChannelStreamFactory(
        DesktopChannelTransport transport,
        DesktopChannelAuthentication? authentication = null,
        int maxMessageBytes = 1024 * 1024)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication;
        _maxMessageBytes = maxMessageBytes;
    }

    public ValueTask<DesktopChannelStream> OpenAsync(CancellationToken cancellationToken)
    {
        _channel ??= CreateChannel(_transport, _maxMessageBytes);

        var client = new Proto.DesktopCapability.DesktopCapabilityClient(_channel);
        var call = client.Connect(BuildHeaders(), deadline: null, cancellationToken);

        return ValueTask.FromResult<DesktopChannelStream>(
            new GrpcDesktopChannelStream(call));
    }

    public ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        _channel = null;
        return ValueTask.CompletedTask;
    }

    internal static GrpcChannel CreateChannel(DesktopChannelTransport transport, int maxMessageBytes)
    {
        var options = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = maxMessageBytes,
            MaxSendMessageSize = maxMessageBytes,
        };

        switch (transport.Kind)
        {
            case DesktopChannelTransportKind.NamedPipe:
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    ConnectCallback = async (_, ct) =>
                    {
                        var pipe = new NamedPipeClientStream(
                            ".", transport.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try
                        {
                            await pipe.ConnectAsync(ct).ConfigureAwait(false);
                            return pipe;
                        }
                        catch
                        {
                            await pipe.DisposeAsync().ConfigureAwait(false);
                            throw;
                        }
                    },
                };
                options.HttpHandler = handler;
                return GrpcChannel.ForAddress("http://localhost", options);

            case DesktopChannelTransportKind.LoopbackHttp2:
            case DesktopChannelTransportKind.Tls:
                return GrpcChannel.ForAddress(transport.Address, options);

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport.Kind, "Unsupported transport kind.");
        }
    }

    private Metadata? BuildHeaders()
    {
        if (_authentication is null)
        {
            return null;
        }

        return [new Metadata.Entry(_authentication.HeaderName, _authentication.HeaderValue)];
    }
}

internal sealed class GrpcDesktopChannelStream : DesktopChannelStream
{
    private readonly AsyncDuplexStreamingCall<Proto.DesktopFrame, Proto.CoreFrame> _call;

    public GrpcDesktopChannelStream(AsyncDuplexStreamingCall<Proto.DesktopFrame, Proto.CoreFrame> call)
    {
        _call = call;
    }

    public override async ValueTask<Proto.CoreFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
                ? _call.ResponseStream.Current
                : null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("gRPC read cancelled.", ex, cancellationToken);
        }
    }

    public override async ValueTask WriteAsync(Proto.DesktopFrame frame, CancellationToken cancellationToken)
    {
        await _call.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask CompleteRequestStreamAsync()
    {
        await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        _call.Dispose();
        return ValueTask.CompletedTask;
    }
}
