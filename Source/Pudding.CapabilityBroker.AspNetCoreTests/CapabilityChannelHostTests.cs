using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerAspNetCoreTests;

/// <summary>
/// Core 侧宿主适配层：真实 Kestrel + 真实 gRPC 客户端（测试扮演 Desktop）。
/// 关键验证之一是「新增能力通道端点后，既有 REST/HTTP1.1 绑定不丢」——这是装配产品组合根前的最大风险点。
/// </summary>
public sealed class CapabilityChannelHostTests
{
    [Fact]
    public async Task KestrelListenOverridesUseUrls_SoTheAssemblyBindsBothExplicitly()
    {
        // 可执行记录的装配约束：一旦调用 Kestrel.Listen* ，UseUrls 的地址就被丢弃。
        // 因此组合根必须「显式 Listen REST + 显式 Listen 能力通道」，否则会静默丢掉 REST 绑定。
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenNamedPipe(
            $"pudding-capability-pitfall-{Guid.NewGuid():N}"[..40],
            listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));

        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Text("ok"));
        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses.ToArray() ?? [];

        Assert.DoesNotContain(addresses, address =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0
            && !string.Equals(uri.Host, "pipe", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Preflight_ConfirmsRestAndCapabilityEndpointsAreBothBound()
    {
        var description = DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-1");

        var report = CapabilityChannelPreflight.Check(
            addresses: ["http://127.0.0.1:5099", @"\\.\pipe\pudding-capability-abc"],
            expectedRestAddresses: ["http://127.0.0.1:5099"],
            description);

        Assert.True(report.IsHealthy);
        Assert.Contains(report.Checks, check => check.Contains("REST 仍在监听", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check => check.Contains("能力端点已监听", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_NamesTheListenOverridesUseUrlsTrapWhenRestDisappears()
    {
        // 这正是「Listen* 覆盖 UseUrls」的失败形态：REST 静默消失。
        var report = CapabilityChannelPreflight.Check(
            addresses: [@"\\.\pipe\pudding-capability-abc"],
            expectedRestAddresses: ["http://127.0.0.1:5099"],
            DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-1"));

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Failures, failure => failure.Contains("覆盖了 UseUrls", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preflight_ReportsAMissingCapabilityEndpointAndInstanceId()
    {
        var noEndpoint = CapabilityChannelPreflight.Check(
            addresses: ["http://127.0.0.1:5099"],
            expectedRestAddresses: ["http://127.0.0.1:5099"],
            DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-1"));

        Assert.False(noEndpoint.IsHealthy);
        Assert.Contains(noEndpoint.Failures, failure => failure.Contains("能力端点", StringComparison.Ordinal));

        // 缺少 Core 实例 ID：不是致命错误，但重连语义会退化，必须提示。
        var noInstance = CapabilityChannelPreflight.Check(
            addresses: [@"\\.\pipe\pudding-capability-abc"],
            expectedRestAddresses: [],
            DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1));

        Assert.False(noInstance.IsHealthy);
        Assert.Contains(noInstance.Failures, failure => failure.Contains("实例 ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_DisabledChannelOnlyRequiresRestToSurvive()
    {
        var report = CapabilityChannelPreflight.Check(
            addresses: ["http://127.0.0.1:5099"],
            expectedRestAddresses: ["http://127.0.0.1:5099/"],
            description: null);

        Assert.True(report.IsHealthy);
        Assert.Contains(report.Checks, check => check.Contains("未启用", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Host_WithNamedPipeChannel_KeepsServingHttp11RestEndpoints()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var health = await harness.GetHealthAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Host_WithLoopbackChannelAlsoKeepsRest()
    {
        var port = CapabilityChannelOptionsBuilder.FreeLoopbackPort();
        await using var harness = await CapabilityHostHarness.StartAsync(options =>
        {
            options.LoopbackPort = port;
            options.UseNamedPipe = false;
        });

        Assert.Equal(port, int.Parse(new Uri(harness.LoopbackUrl!).Port.ToString()));

        var health = await harness.GetHealthAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
        var ack = await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack over h2c");
        Assert.Equal(1UL, ack.HelloAck.Generation);
    }

    [Fact]
    public async Task Connect_WithWrongCredential_IsUnauthenticatedAndCreatesNoSession()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect(token: "wrong-token");

        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
            await harness.ReadAsync(call);
        });

        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, error.StatusCode);
        // 认证先于握手：被拒的连接不产生任何会话与注册项。
        Assert.Empty(harness.Broker.Sessions);
    }

    [Fact]
    public async Task Connect_WithoutCredential_IsRejectedByDefault()
    {
        await using var harness = await CapabilityHostHarness.StartAsync(options =>
            options.Authenticator = null);

        var call = harness.Connect(token: null);

        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
            await harness.ReadAsync(call);
        });

        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, error.StatusCode);
        Assert.Empty(harness.Broker.Sessions);
    }

    [Fact]
    public async Task Connect_HandshakesAndNegotiatesOverTheNamedPipe()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));

        var ackFrame = await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var ack = ackFrame.HelloAck;

        Assert.Equal(1UL, ack.Generation);
        Assert.Equal(
            ["webview.navigate", "webview.execute_javascript", "webview.page_state", "shell.notification"],
            ack.Capabilities.Select(declaration => declaration.Capability).ToArray());
        Assert.Equal(8u, ack.Limits.MaxInFlightOperations);
        Assert.Equal((uint)DesktopProtocolVersion.Current, ack.NegotiatedVersion.Maximum);

        await TestWait.UntilAsync(
            () => harness.Broker.Sessions.Count == 1 && harness.Broker.Sessions[0].State == DesktopLinkState.Ready,
            "broker registered a ready session");
    }

    [Fact]
    public async Task BrokerCommand_TravelsOverTheWireAndItsResultCompletesTheCall()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");

        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId("desk-test"), new OperationId("op-wire"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")), context);

        var commandFrame = await harness.ReadAsync(call, frame => frame.Command is not null, "navigate command");
        Assert.Equal("webview.navigate", commandFrame.Command.Capability);
        Assert.Equal("op-wire", commandFrame.Command.OperationId);
        Assert.Equal(1UL, commandFrame.Command.Generation);

        // 测试扮演 Desktop：回一个类型化结果。
        await call.RequestStream.WriteAsync(new Proto.DesktopFrame
        {
            Result = new Proto.OperationResult
            {
                OperationId = "op-wire",
                Generation = 1,
                Navigate = new Proto.NavigateOutcome
                {
                    Disposition = Proto.NavigateDisposition.Completed,
                    CurrentUrl = "https://example.com/done",
                    PageVersion = 4,
                },
            },
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess);
        Assert.Equal(NavigateDisposition.Completed, result.Value.Disposition);
        Assert.Equal("https://example.com/done", result.Value.CurrentUrl!.AbsoluteUri);
        Assert.Equal(4, result.Value.PageVersion.Value);
    }

    [Fact]
    public async Task CancelRequest_TravelsOverTheWireAndTheResultIsHonest()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");

        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId("desk-test"), new OperationId("op-cancel"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.ExecuteJavascriptAsync(
            new JavascriptRequest(new DesktopPageTarget("ctx-1", "page-1"), "hang();"), context);

        await harness.ReadAsync(call, frame => frame.Command is not null, "javascript command");
        Assert.True(await session.CancelAsync(context.OperationId));

        var cancelFrame = await harness.ReadAsync(call, frame => frame.Cancel is not null, "cancel frame");
        Assert.Equal("op-cancel", cancelFrame.Cancel.OperationId);

        await call.RequestStream.WriteAsync(new Proto.DesktopFrame
        {
            Result = new Proto.OperationResult
            {
                OperationId = "op-cancel",
                Generation = 1,
                Error = new Proto.ErrorOutcome
                {
                    Code = "cancelled",
                    Message = "cancelled by core",
                    Retryable = true,
                    MayHaveSideEffects = true,
                },
            },
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DesktopCapabilityErrorCode.Cancelled, result.Error!.Code);
        Assert.True(result.Error.MayHaveSideEffects);
    }

    [Fact]
    public async Task Disconnect_ClearsTheBrokerRegistry()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        Assert.Single(harness.Broker.Sessions);

        await call.RequestStream.CompleteAsync();

        await TestWait.UntilAsync(() => harness.Broker.Sessions.Count == 0, "session removed after disconnect");
    }

    [Fact]
    public async Task SecondConnection_ForTheSameDesktop_IsRejectedWhileTheFirstIsActive()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var first = harness.Connect();
        await first.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
        await harness.ReadAsync(first, frame => frame.HelloAck is not null, "first hello_ack");

        var second = harness.Connect();
        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await second.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-test"));
            await harness.ReadAsync(second);
        });

        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, error.StatusCode);
        Assert.Single(harness.Broker.Sessions);
    }

    [Fact]
    public async Task ConnectorWithUnexpectedDesktopIdentity_IsRejected()
    {
        await using var harness = await CapabilityHostHarness.StartAsync();

        var call = harness.Connect();
        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello("desk-other"));
            await harness.ReadAsync(call);
        });

        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, error.StatusCode);
        Assert.Empty(harness.Broker.Sessions);
    }

    [Fact]
    public void Options_RejectUnusableConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityChannelOptions
        {
            ExpectedDesktopId = new DesktopInstanceId("desk-test"),
            Grantable = DesktopCapability.None,
        }.Validate());

        Assert.Throws<ArgumentException>(() => new CapabilityChannelOptions
        {
            ExpectedDesktopId = new DesktopInstanceId("desk-test"),
            Grantable = DesktopCapability.WebViewNavigate,
            NamedPipeName = @"\\.\pipe\bad",
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityChannelOptions
        {
            ExpectedDesktopId = new DesktopInstanceId("desk-test"),
            Grantable = DesktopCapability.WebViewNavigate,
            LoopbackPort = 70000,
        }.Validate());
    }

    [Fact]
    public void ListenForCapabilityChannel_RequiresAtLeastOneEndpoint()
    {
        var kestrel = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions();
        var options = new CapabilityChannelOptions
        {
            ExpectedDesktopId = new DesktopInstanceId("desk-test"),
            Grantable = DesktopCapability.WebViewNavigate,
        };

        Assert.Throws<InvalidOperationException>(() => kestrel.ListenForCapabilityChannel(options));
    }
}
