using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PuddingAgent.Services;
using PuddingBrowser.Protocol;
using PuddingHost.BrowserBridge;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.Tests.BrowserBridge;

internal sealed class BrowserBridgeTestHost : IAsyncDisposable
{
    public const string ValidToken = "bridge-test-token";

    private readonly string _dataRoot;
    private readonly WebApplication _application;

    public HttpClient HttpClient { get; }
    public TestBrowserBridgeClock Clock { get; }
    public IDesktopBrowserConnectionRegistry Registry { get; }
    public IDesktopBrowserCommandBroker Broker { get; }
    public IServiceProvider Services => _application.Services;

    private BrowserBridgeTestHost(
        string dataRoot,
        WebApplication application,
        HttpClient httpClient,
        TestBrowserBridgeClock clock,
        IDesktopBrowserConnectionRegistry registry,
        IDesktopBrowserCommandBroker broker)
    {
        _dataRoot = dataRoot;
        _application = application;
        HttpClient = httpClient;
        Clock = clock;
        Registry = registry;
        Broker = broker;
    }

    public static async Task<BrowserBridgeTestHost> StartAsync(
        PuddingHostMode mode = PuddingHostMode.DesktopChild)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"pudding-bridge-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "config", "system.json"),
            $$"""
            {
              "desktop": {
                "core": {
                  "controlToken": "{{ValidToken}}"
                }
              }
            }
            """);

        var clock = new TestBrowserBridgeClock();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(BrowserBridgeTestHost).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        var hostOptions = new PuddingHostOptions
        {
            Mode = mode,
            DataRoot = dataRoot,
            Urls = ["http://127.0.0.1:0"],
            BrowserAutomationEnabled = mode == PuddingHostMode.DesktopChild
        };
        if (mode == PuddingHostMode.DesktopChild)
        {
            builder.Services.AddDesktopBrowserAutomation(hostOptions);
        }
        else
        {
            builder.Services.AddSingleton<IDesktopBrowserConnectionRegistry, DesktopBrowserConnectionRegistry>();
            builder.Services.AddSingleton<IDesktopBrowserCommandBroker, DesktopBrowserCommandBroker>();
        }
        builder.Services.AddSingleton<IBrowserBridgeClock>(clock);
        builder.Services.AddPuddingToolRegistry();
        builder.Services.AddSingleton(new DesktopControlTokenValidator(dataRoot));
        builder.Services.AddSingleton(hostOptions);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var requestedAddress = context.Request.Headers["X-Test-Remote-IP"].FirstOrDefault();
            context.Connection.RemoteIpAddress = IPAddress.TryParse(requestedAddress, out var parsed)
                ? parsed
                : IPAddress.Loopback;
            await next();
        });
        app.MapDesktopBrowserBridgeEndpoint();
        await app.StartAsync();

        return new BrowserBridgeTestHost(
            dataRoot,
            app,
            app.GetTestClient(),
            clock,
            app.Services.GetRequiredService<IDesktopBrowserConnectionRegistry>(),
            app.Services.GetRequiredService<IDesktopBrowserCommandBroker>());
    }

    public async Task<WebSocket> ConnectWebSocketAsync(
        string? token = ValidToken,
        string? remoteIp = null,
        CancellationToken cancellationToken = default)
    {
        var client = _application.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (token is not null)
                request.Headers[BrowserBridgeProtocol.ControlTokenHeader] = token;
            if (remoteIp is not null)
                request.Headers["X-Test-Remote-IP"] = remoteIp;
        };
        return await client.ConnectAsync(
            new Uri("ws://localhost" + BrowserBridgeProtocol.EndpointPath),
            cancellationToken);
    }

    public static async Task SendEnvelopeAsync(
        WebSocket socket,
        BrowserBridgeEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var bytes = BrowserBridgeSerializer.Serialize(envelope);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<BrowserBridgeEnvelope> ReceiveEnvelopeAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BrowserBridgeProtocol.MaxMessageBytes];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"Socket closed before an envelope arrived: {result.CloseStatus}");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return BrowserBridgeSerializer.Deserialize(message.ToArray());
    }

    public static BrowserBridgeEnvelope HelloEnvelope(int? protocolVersion = null) => new()
    {
        MessageId = Guid.NewGuid(),
        Kind = BrowserBridgeMessageKind.Hello,
        CreatedAt = DateTimeOffset.UtcNow,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new BrowserBridgeHello
        {
            ProtocolVersion = protocolVersion ?? BrowserBridgeProtocol.CurrentVersion,
            DesktopInstanceId = "host-test-desktop",
            Capabilities = ["context", "page", "navigation", "snapshot", "locator", "interact", "wait"]
        }, BrowserBridgeTestJson.Options)
    };

    public static async Task<BrowserBridgeHelloAck> CompleteHelloAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        await SendEnvelopeAsync(socket, HelloEnvelope(), cancellationToken);
        var ackEnvelope = await ReceiveEnvelopeAsync(socket, cancellationToken);
        Assert.Equal(BrowserBridgeMessageKind.HelloAck, ackEnvelope.Kind);
        return BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(ackEnvelope);
    }

    public static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached before the test timeout.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
        try { Directory.Delete(_dataRoot, true); }
        catch { }
    }
}

internal sealed class TestBrowserBridgeClock : IBrowserBridgeClock
{
    private readonly object _gate = new();
    private readonly List<ScheduledDelay> _delays = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return _utcNow; }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _delays.Add(new ScheduledDelay(_utcNow + delay, completion, registration));
            return completion.Task;
        }
    }

    public void Advance(TimeSpan amount)
    {
        List<ScheduledDelay> ready;
        lock (_gate)
        {
            _utcNow += amount;
            ready = _delays.Where(delay => delay.DueAt <= _utcNow).ToList();
            _delays.RemoveAll(delay => delay.DueAt <= _utcNow);
        }

        foreach (var delay in ready)
        {
            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }
    }

    private sealed record ScheduledDelay(
        DateTimeOffset DueAt,
        TaskCompletionSource Completion,
        CancellationTokenRegistration Registration);
}

internal static class BrowserBridgeTestJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
}
