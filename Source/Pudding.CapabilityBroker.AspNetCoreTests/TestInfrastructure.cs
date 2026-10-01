using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Proto = Pudding.Rpc.Protocol.V1;
using CapabilityBrokerHost = Pudding.CapabilityBroker.CapabilityBroker;

namespace CapabilityBrokerAspNetCoreTests;

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException($"Condition was not met within the test budget: {because}");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>记录型授权器：放行或拒绝。</summary>
internal sealed class TestAuthorizer : IDesktopCapabilityAuthorizer
{
    private readonly DesktopCapabilityError? _denial;

    public TestAuthorizer(DesktopCapabilityError? denial = null) => _denial = denial;

    public ValueTask<DesktopCapabilityError?> AuthorizeAsync(
        DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_denial);
}

/// <summary>
/// 最小 Core 宿主：REST 端点（HTTP/1.1，走 UseUrls）+ 能力通道（Named Pipe 与/或 Loopback h2c）。
/// 这一形态刻意与产品组合根相同，用来验证「新增管道端点后既有 REST 绑定不丢」。
/// </summary>
internal sealed class CapabilityHostHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _clientChannel;

    private CapabilityHostHarness(
        WebApplication app,
        CapabilityBrokerHost broker,
        CapabilityChannelOptions options,
        GrpcChannel clientChannel,
        string restUrl,
        string? loopbackUrl)
    {
        _app = app;
        Broker = broker;
        Options = options;
        _clientChannel = clientChannel;
        RestUrl = restUrl;
        LoopbackUrl = loopbackUrl;
    }

    public CapabilityBrokerHost Broker { get; }

    public CapabilityChannelOptions Options { get; }

    public string RestUrl { get; }

    public string? LoopbackUrl { get; }

    public static async Task<CapabilityHostHarness> StartAsync(
        Action<CapabilityChannelOptionsBuilder>? configure = null)
    {
        var builder = new CapabilityChannelOptionsBuilder();
        configure?.Invoke(builder);
        var options = builder.Build();

        var broker = new CapabilityBrokerHost(
            new CapabilityBrokerOptions
            {
                HostDesktopId = options.ExpectedDesktopId,
                Policy = options.ToPolicy(),
                CoreInstanceId = options.CoreInstanceId,
                AuditSink = options.AuditSink,
            },
            builder.Authorizer);

        var authenticator = builder.Authenticator ?? RejectAllCapabilityAuthenticator.Instance;

        var webBuilder = WebApplication.CreateSlimBuilder();
        webBuilder.Logging.ClearProviders();

        // 装配形态（实测约束）：Kestrel 的 Listen* 会覆盖 UseUrls，因此 REST 与能力通道必须
        // 一起显式绑定；产品组合根也应采用这一形态，否则会丢掉既有 REST 绑定。
        var restPort = CapabilityChannelOptionsBuilder.FreeLoopbackPort();
        webBuilder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, restPort, listen => listen.Protocols = HttpProtocols.Http1);
            kestrel.ListenForCapabilityChannel(options);
        });

        var services = webBuilder.Services;
        services.AddSingleton(broker);
        services.AddSingleton(options);
        services.AddSingleton(authenticator);
        services.AddSingleton<DesktopCapabilityService>();
        services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = options.MaxMessageBytes;
            grpc.MaxSendMessageSize = options.MaxMessageBytes;
        });

        var app = webBuilder.Build();
        app.MapGet("/health", () => Results.Text("ok", "text/plain"));
        app.MapCapabilityChannel();

        await app.StartAsync();

        var restUrl = $"http://127.0.0.1:{restPort}";

        GrpcChannel clientChannel;
        string? loopbackUrl = null;

        if (options.NamedPipeName is { Length: > 0 } pipeName)
        {
            clientChannel = CreatePipeChannel(pipeName);
        }
        else if (options.LoopbackPort is { } port)
        {
            loopbackUrl = $"http://127.0.0.1:{port}";
            clientChannel = GrpcChannel.ForAddress(loopbackUrl, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true },
            });
        }
        else
        {
            throw new InvalidOperationException("test host requires at least one capability endpoint");
        }

        return new CapabilityHostHarness(app, broker, options, clientChannel, restUrl, loopbackUrl);
    }

    /// <summary>用静态令牌发起一次 Connect 调用（返回前不等待服务端响应）。</summary>
    public AsyncDuplexStreamingCall<Proto.DesktopFrame, Proto.CoreFrame> Connect(string? token = "test-token")
    {
        var client = new Proto.DesktopCapability.DesktopCapabilityClient(_clientChannel);
        Metadata? headers = null;
        if (token is not null)
        {
            headers = [new Metadata.Entry("x-pudding-control-token", token)];
        }

        return client.Connect(headers);
    }

    public async Task<Proto.CoreFrame?> ReadAsync(
        AsyncDuplexStreamingCall<Proto.DesktopFrame, Proto.CoreFrame> call, TimeSpan? timeout = null)
    {
        using var budget = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await call.ResponseStream.MoveNext(budget.Token) ? call.ResponseStream.Current : null;
    }

    public async Task<Proto.CoreFrame> ReadAsync(
        AsyncDuplexStreamingCall<Proto.DesktopFrame, Proto.CoreFrame> call, Func<Proto.CoreFrame, bool> predicate,
        string because, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < limit)
        {
            var frame = await ReadAsync(call, limit - DateTime.UtcNow);
            if (frame is null)
            {
                break;
            }

            if (predicate(frame))
            {
                return frame;
            }
        }

        throw new TimeoutException($"Expected frame was not received: {because}");
    }

    public async Task<HttpResponseMessage> GetHealthAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(RestUrl) };
        return await http.GetAsync("/health");
    }

    public static GrpcChannel CreatePipeChannel(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(cancellationToken);
                return pipe;
            },
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }

    public static Proto.DesktopFrame Hello(
        string desktopId,
        DesktopCapability declared =
            DesktopCapability.WebViewNavigate
            | DesktopCapability.WebViewExecuteJavascript
            | DesktopCapability.WebViewPageState
            | DesktopCapability.ShellNotification)
    {
        var hello = new Proto.DesktopHello
        {
            DesktopId = desktopId,
            ProcessInstanceId = "proc-test",
            SupportedVersions = new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
        };

        foreach (var declaration in DesktopCapabilities.DeclareFor(declared))
        {
            hello.Capabilities.Add(new Proto.CapabilityDeclaration
            {
                Capability = declaration.Name,
                Version = (uint)declaration.Version,
            });
        }

        return new Proto.DesktopFrame { Hello = hello };
    }

    public async ValueTask DisposeAsync()
    {
        _clientChannel.Dispose();

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 测试收尾。
        }

        await _app.DisposeAsync();
        await Broker.DisposeAsync();
    }
}

/// <summary>测试用的选项构造器（探针/测试不直接依赖 internal 成员）。</summary>
internal sealed class CapabilityChannelOptionsBuilder
{
    public string DesktopId { get; set; } = "desk-test";

    public DesktopCapability Grantable { get; set; } =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification;

    public string? NamedPipeName { get; set; }

    /// <summary>为真时自动生成一个测试用管道名（否则按 <see cref="NamedPipeName"/> 原样使用）。</summary>
    public bool UseNamedPipe { get; set; } = true;

    public int? LoopbackPort { get; set; }

    public bool EnforceSingleActiveTransport { get; set; } = true;

    public int MaxMessageBytes { get; set; } = 1024 * 1024;

    public IDesktopCapabilityAuthorizer Authorizer { get; set; } = new TestAuthorizer();

    public ICoreCapabilityAuthenticator? Authenticator { get; set; } =
        new StaticHeaderCapabilityAuthenticator("x-pudding-control-token", "test-token");

    public IDesktopCapabilityAuditSink AuditSink { get; set; } = NullDesktopCapabilityAuditSink.Instance;

    public CapabilityChannelOptions Build() => new()
    {
        ExpectedDesktopId = new DesktopInstanceId(DesktopId),
        Grantable = Grantable,
        NamedPipeName = UseNamedPipe
            ? NamedPipeName ?? $"pudding-capability-test-{Guid.NewGuid():N}"[..40]
            : null,
        LoopbackPort = LoopbackPort,
        EnforceSingleActiveTransport = EnforceSingleActiveTransport,
        MaxMessageBytes = MaxMessageBytes,
        AuditSink = AuditSink,
    };

    public static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (PuddingAgentNetwork.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
