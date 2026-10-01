using System.Threading.Channels;
using Grpc.Core;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.IpcProbe;

/// <summary>
/// 探针服务端替身：扮演 Core 侧的 DesktopCapability 服务。
/// 认证先于握手；收到 hello 后回 ack；随后由探针注入 CoreFrame（命令/取消）并收集结果/事件。
/// </summary>
internal sealed class ProbeCapabilityService : Proto.DesktopCapability.DesktopCapabilityBase
{
    public const string AuthHeader = "x-pudding-control-token";

    private readonly string _expectedToken;
    private readonly Channel<Proto.CoreFrame> _outbound = Channel.CreateUnbounded<Proto.CoreFrame>();
    private readonly Channel<Proto.DesktopHello> _hellos = Channel.CreateUnbounded<Proto.DesktopHello>();
    private readonly Channel<Proto.OperationResult> _results = Channel.CreateUnbounded<Proto.OperationResult>();
    private readonly Channel<Proto.DesktopEvent> _events = Channel.CreateUnbounded<Proto.DesktopEvent>();
    private readonly List<string> _authenticationFailures = [];
    private readonly object _sync = new();

    public ProbeCapabilityService(string expectedToken) => _expectedToken = expectedToken;

    public IReadOnlyList<string> AuthenticationFailures
    {
        get
        {
            lock (_sync)
            {
                return _authenticationFailures.ToArray();
            }
        }
    }

    public Proto.CoreHelloAck? LastAck { get; private set; }

    public void Enqueue(Proto.CoreFrame frame) => _outbound.Writer.TryWrite(frame);

    public async Task<Proto.DesktopHello> WaitForHelloAsync(TimeSpan timeout) =>
        await _hellos.Reader.ReadAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);

    public async Task<Proto.OperationResult> WaitForResultAsync(string operationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var read = _results.Reader.ReadAsync().AsTask();
            var completed = await Task.WhenAny(read, Task.Delay(deadline - DateTime.UtcNow)).ConfigureAwait(false);
            if (completed != read)
            {
                break;
            }

            var result = await read.ConfigureAwait(false);
            if (result.OperationId == operationId)
            {
                return result;
            }
        }

        throw new TimeoutException($"probe: no result for operation '{operationId}' within {timeout}");
    }

    public override async Task Connect(
        IAsyncStreamReader<Proto.DesktopFrame> requestStream,
        IServerStreamWriter<Proto.CoreFrame> responseStream,
        ServerCallContext context)
    {
        // 认证先于握手：凭据只经传输元数据传递，失败立即 Unauthenticated。
        var token = context.RequestHeaders.FirstOrDefault(entry =>
            string.Equals(entry.Key, AuthHeader, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.Equals(token, _expectedToken, StringComparison.Ordinal))
        {
            lock (_sync)
            {
                _authenticationFailures.Add(token ?? "<missing>");
            }

            throw new RpcException(new Status(StatusCode.Unauthenticated, "control token rejected"));
        }

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var hello = requestStream.Current.Hello
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "hello must be the first desktop frame"));
        _hellos.Writer.TryWrite(hello);

        var ack = new Proto.CoreHelloAck
        {
            ConnectionId = "probe-connection",
            Generation = 7,
            NegotiatedVersion = new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
            Limits = new Proto.ChannelLimits
            {
                MaxFrameBytes = 4 * 1024 * 1024,
                MaxInFlightOperations = 8,
                MaxQueuedBytes = 4 * 1024 * 1024,
                HeartbeatIntervalMs = 15000,
                HeartbeatTimeoutMs = 45000,
            },
        };

        foreach (var declaration in hello.Capabilities)
        {
            ack.Capabilities.Add(declaration);
        }

        LastAck = ack;
        await responseStream.WriteAsync(new Proto.CoreFrame { HelloAck = ack }).ConfigureAwait(false);

        var pump = PumpAsync(responseStream, context.CancellationToken);

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                switch (frame.FrameCase)
                {
                    case Proto.DesktopFrame.FrameOneofCase.Result when frame.Result is not null:
                        _results.Writer.TryWrite(frame.Result);
                        break;

                    case Proto.DesktopFrame.FrameOneofCase.Event when frame.Event is not null:
                        _events.Writer.TryWrite(frame.Event);
                        break;

                    case Proto.DesktopFrame.FrameOneofCase.HeartbeatAck:
                        break;
                }
            }
        }
        catch (RpcException)
        {
            // 客户端断开：按正常结束处理。
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 探针收尾。
            }
        }
    }

    private async Task PumpAsync(IServerStreamWriter<Proto.CoreFrame> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(frame).ConfigureAwait(false);
        }
    }
}
