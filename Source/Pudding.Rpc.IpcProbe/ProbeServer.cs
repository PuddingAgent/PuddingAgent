using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pudding.Rpc.IpcProbe;

/// <summary>探针的 Kestrel 服务端：Named Pipe 与 Loopback h2c 两种端点（都显式 HTTP/2）。</summary>
internal sealed class ProbeServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly WebApplication _app;

    private ProbeServer(WebApplication app, ProbeCapabilityService service, string endpoint)
    {
        _app = app;
        Service = service;
        Endpoint = endpoint;
    }

    public ProbeCapabilityService Service { get; }

    public string Endpoint { get; }

    public static async Task<ProbeServer> StartNamedPipeAsync(string pipeName, string controlToken)
    {
        var service = new ProbeCapabilityService(controlToken);
        var app = Build(service, options => options.ListenNamedPipe(
            pipeName, listen => listen.Protocols = HttpProtocols.Http2));

        await app.StartAsync().ConfigureAwait(false);
        return new ProbeServer(app, service, $@"\\.\pipe\{pipeName}");
    }

    public static async Task<ProbeServer> StartLoopbackAsync(string controlToken)
    {
        var service = new ProbeCapabilityService(controlToken);
        var app = Build(service, options => options.Listen(
            IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));

        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("probe: kestrel did not report a bound address");

        return new ProbeServer(app, service, address);
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
    }

    private static WebApplication Build(ProbeCapabilityService service, Action<KestrelServerOptions> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(configure);
        builder.Services.AddSingleton(service);
        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = MaxMessageBytes;
            options.MaxSendMessageSize = MaxMessageBytes;
        });

        var app = builder.Build();
        app.MapGrpcService<ProbeCapabilityService>();
        return app;
    }
}
