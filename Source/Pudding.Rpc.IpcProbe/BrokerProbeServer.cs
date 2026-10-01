using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using CapabilityBrokerHost = Pudding.CapabilityBroker.CapabilityBroker;

namespace Pudding.Rpc.IpcProbe;

/// <summary>
/// 探针的 Core 侧 Kestrel 服务端：<b>直接使用产品适配层</b>
/// （<see cref="CapabilityChannelHostExtensions.AddCapabilityChannel"/> / <c>MapCapabilityChannel</c> /
/// <c>ListenForCapabilityChannel</c>），因此探针验证的是产品代码路径，而不是探针自带的替身。
/// </summary>
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
        var options = BuildOptions(desktopId, pipeName, loopbackPort: null);
        var (app, broker) = await StartAsync(options, controlToken).ConfigureAwait(false);
        return new BrokerProbeServer(app, broker, $@"\\.\pipe\{pipeName}");
    }

    public static async Task<BrokerProbeServer> StartLoopbackAsync(string controlToken, string desktopId)
    {
        var port = FreeLoopbackPort();
        var options = BuildOptions(desktopId, pipeName: null, loopbackPort: port);
        var (app, broker) = await StartAsync(options, controlToken).ConfigureAwait(false);
        return new BrokerProbeServer(app, broker, $"http://127.0.0.1:{port}");
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

    private static CapabilityChannelOptions BuildOptions(string desktopId, string? pipeName, int? loopbackPort) => new()
    {
        ExpectedDesktopId = new DesktopInstanceId(desktopId),
        // 探针：授予 Desktop 声明的四个能力（产品会继承 Tool Runtime 准入）。
        Grantable = DesktopCapability.WebViewNavigate
            | DesktopCapability.WebViewExecuteJavascript
            | DesktopCapability.WebViewPageState
            | DesktopCapability.ShellNotification
            | DesktopCapability.ShellStatus
            | DesktopCapability.BrowserSnapshot
            | DesktopCapability.BrowserLocate
            | DesktopCapability.BrowserInteract
            | DesktopCapability.BrowserWaitFor
            | DesktopCapability.BrowserContexts
            | DesktopCapability.BrowserTabs
            | DesktopCapability.ShellDialog
            | DesktopCapability.ShellClipboard,
        MaxInFlightPerConnection = 8,
        MaxQueuedFrames = 128,
        MaxMessageBytes = MaxMessageBytes,
        HandshakeTimeout = TimeSpan.FromSeconds(10),
        NamedPipeName = pipeName,
        LoopbackPort = loopbackPort,
    };

    private static async Task<(WebApplication App, CapabilityBrokerHost Broker)> StartAsync(
        CapabilityChannelOptions options, string controlToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenForCapabilityChannel(options));

        builder.Services.AddCapabilityChannel(
            options,
            new StaticHeaderCapabilityAuthenticator("x-pudding-control-token", controlToken),
            // 探针显式放行；产品里必须换成继承 Tool Runtime 准入的授权器。
            AllowAllDesktopCapabilities.Instance);

        var app = builder.Build();
        app.MapCapabilityChannel();
        await app.StartAsync().ConfigureAwait(false);

        var broker = app.Services.GetRequiredService<CapabilityBrokerHost>();
        return (app, broker);
    }

    private static int FreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
