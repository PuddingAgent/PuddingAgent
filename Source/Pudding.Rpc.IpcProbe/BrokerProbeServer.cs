using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pudding.CapabilityBroker;
using CapabilityBrokerHost = Pudding.CapabilityBroker.CapabilityBroker;
using Pudding.Contracts;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.IpcProbe;

/// <summary>
/// 把服务端 gRPC 流适配成 <see cref="ICoreDesktopChannel"/>（唯一需要的宿主侧代码形态）。
/// <see cref="IServerStreamWriter{T}"/> 不支持并发写，因此这里串行化发送。
/// </summary>
internal sealed class BrokerServerChannel : ICoreDesktopChannel
{
    private readonly IAsyncStreamReader<Proto.DesktopFrame> _requests;
    private readonly IServerStreamWriter<Proto.CoreFrame> _responses;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public BrokerServerChannel(
        IAsyncStreamReader<Proto.DesktopFrame> requests,
        IServerStreamWriter<Proto.CoreFrame> responses)
    {
        _requests = requests;
        _responses = responses;
    }

    public async ValueTask SendAsync(Proto.CoreFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _responses.WriteAsync(frame).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<Proto.DesktopFrame?> ReadAsync(CancellationToken cancellationToken) =>
        await _requests.MoveNext(cancellationToken).ConfigureAwait(false) ? _requests.Current : null;
}

/// <summary>探针的 Core 侧服务：认证 → 交给 Broker → 驱动整条流。</summary>
internal sealed class BrokerCapabilityService : Proto.DesktopCapability.DesktopCapabilityBase
{
    private readonly CapabilityBrokerHost _broker;
    private readonly string _controlToken;

    public BrokerCapabilityService(CapabilityBrokerHost broker, string controlToken)
    {
        _broker = broker;
        _controlToken = controlToken;
    }

    public override async Task Connect(
        IAsyncStreamReader<Proto.DesktopFrame> requestStream,
        IServerStreamWriter<Proto.CoreFrame> responseStream,
        ServerCallContext context)
    {
        // 认证先于握手：凭据只经传输元数据传递。
        var token = context.RequestHeaders.FirstOrDefault(entry =>
            string.Equals(entry.Key, "x-pudding-control-token", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!string.Equals(token, _controlToken, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "control token rejected"));
        }

        var accepted = await _broker.AcceptAsync(
            new BrokerServerChannel(requestStream, responseStream), context.CancellationToken);

        if (!accepted.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, accepted.Error.WireCode));
        }

        await accepted.Value.Completion.ConfigureAwait(false);
    }
}

/// <summary>探针的 Core 侧 Kestrel 服务端（Named Pipe 或 Loopback h2c），内部使用真实 Broker。</summary>
internal sealed class BrokerProbeServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly WebApplication _app;

    private BrokerProbeServer(WebApplication app, CapabilityBrokerHost broker, string endpoint)
    {
        _app = app;
        Broker = broker;
        Endpoint = endpoint;
    }

    public CapabilityBrokerHost Broker { get; }

    public string Endpoint { get; }

    public static async Task<BrokerProbeServer> StartNamedPipeAsync(
        string pipeName, string controlToken, string desktopId)
    {
        return await StartAsync(
            controlToken,
            desktopId,
            options => options.ListenNamedPipe(pipeName, listen => listen.Protocols = HttpProtocols.Http2),
            $@"\\.\pipe\{pipeName}").ConfigureAwait(false);
    }

    public static async Task<BrokerProbeServer> StartLoopbackAsync(string controlToken, string desktopId)
    {
        var server = await StartAsync(
            controlToken,
            desktopId,
            options => options.Listen(
                System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2),
            endpoint: null).ConfigureAwait(false);

        return server;
    }

    /// <summary>等待 Broker 侧出现一个已经握手完成的会话。</summary>
    public async Task<DesktopSession> WaitForSessionAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var session = Broker.Sessions.FirstOrDefault(candidate => candidate.State == DesktopLinkState.Ready);
            if (session is not null)
            {
                return session;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("probe: the broker did not reach a ready session in time");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 探针收尾。
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        await Broker.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<BrokerProbeServer> StartAsync(
        string controlToken,
        string desktopId,
        Action<KestrelServerOptions> configure,
        string? endpoint)
    {
        var broker = new CapabilityBrokerHost(
            new CapabilityBrokerOptions
            {
                HostDesktopId = new DesktopInstanceId(desktopId),
                Policy = new DesktopCapabilityPolicy
                {
                    // 探针：授予 Desktop 声明的四个能力（真实产品会继承 Tool Runtime 准入）。
                    Grantable = DesktopCapability.WebViewNavigate
                        | DesktopCapability.WebViewExecuteJavascript
                        | DesktopCapability.WebViewPageState
                        | DesktopCapability.ShellNotification,
                    MaxInFlightPerConnection = 8,
                    MaxQueuedFrames = 128,
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                },
            },
            // 探针显式放行；产品里必须换成继承 Tool Runtime 准入的授权器。
            AllowAllDesktopCapabilities.Instance);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(configure);
        builder.Services.AddSingleton(broker);
        builder.Services.AddSingleton(new BrokerCapabilityService(broker, controlToken));
        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = MaxMessageBytes;
            options.MaxSendMessageSize = MaxMessageBytes;
        });

        var app = builder.Build();
        app.MapGrpcService<BrokerCapabilityService>();
        await app.StartAsync().ConfigureAwait(false);

        var resolved = endpoint
            ?? app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
                ?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("probe: kestrel did not report a bound address");

        return new BrokerProbeServer(app, broker, resolved);
    }
}
