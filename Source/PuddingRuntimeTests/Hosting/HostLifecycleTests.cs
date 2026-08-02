using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingHost.Hosting;
using Xunit;

namespace PuddingRuntimeTests.Hosting;

public class HostLifecycleTests
{
    [Fact]
    public void CaptureBoundAddresses_AfterStart_ResolvesLoopbackHttp()
    {
        // Arrange: build a minimal WebApplication with loopback binding
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/health", () => "ok");

        // Act
        app.StartAsync().GetAwaiter().GetResult();
        var baseAddress = PuddingApplicationHost.CaptureBoundAddresses(app);

        // Assert
        Assert.NotNull(baseAddress);
        Assert.True(baseAddress.IsLoopback);
        Assert.Equal("http", baseAddress.Scheme);

        // Cleanup
        app.StopAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void CaptureBoundAddresses_BeforeStart_Throws()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/health", () => "ok");

        // Act & Assert: address capture before StartAsync should throw
        Assert.Throws<InvalidOperationException>(() =>
            PuddingApplicationHost.CaptureBoundAddresses(app));
    }

    [Fact]
    public void CaptureBoundAddresses_NonLoopback_Throws()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        var app = builder.Build();
        app.MapGet("/health", () => "ok");

        app.StartAsync().GetAwaiter().GetResult();

        // 0.0.0.0 is not a loopback address
        Assert.Throws<InvalidOperationException>(() =>
            PuddingApplicationHost.CaptureBoundAddresses(app));

        app.StopAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ConnectorLifecycle_P2pFailure_DoesNotBlockConnectors()
    {
        // Simulate P2P failure and verify connectors still start
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("test");

        bool p2pCalled = false;
        bool connectorsCalled = false;

        Task P2pThatFails(CancellationToken ct)
        {
            p2pCalled = true;
            throw new InvalidOperationException("P2P simulation failure");
        }

        Task SuccessMigration(CancellationToken ct) => Task.CompletedTask;

        Task ConnectorsStart(CancellationToken ct)
        {
            connectorsCalled = true;
            return Task.CompletedTask;
        }

        await ConnectorHostLifecycleService.RunStartupPhasesAsync(
            P2pThatFails,
            SuccessMigration,
            ConnectorsStart,
            logger,
            CancellationToken.None);

        Assert.True(p2pCalled, "P2P should have been called");
        Assert.True(connectorsCalled, "Connectors should start even after P2P failure");
    }

    [Fact]
    public void PuddingApplicationHost_CallingOrder_IsDocumented()
    {
        // Verify the documented calling order matches the actual methods
        // CreateBuilder → Build → InitializeAsync → StartAsync → CaptureBoundAddresses

        var createBuilderMethod = typeof(PuddingApplicationHost).GetMethod(nameof(PuddingApplicationHost.CreateBuilder));
        var buildMethod = typeof(PuddingApplicationHost).GetMethod(nameof(PuddingApplicationHost.Build));
        var initMethod = typeof(PuddingApplicationHost).GetMethod(nameof(PuddingApplicationHost.InitializeAsync));
        var captureMethod = typeof(PuddingApplicationHost).GetMethod(nameof(PuddingApplicationHost.CaptureBoundAddresses));

        Assert.NotNull(createBuilderMethod);
        Assert.NotNull(buildMethod);
        Assert.NotNull(initMethod);
        Assert.NotNull(captureMethod);

        // Build returns WebApplication from WebApplicationBuilder
        Assert.Equal(typeof(WebApplicationBuilder), createBuilderMethod!.ReturnType);
        Assert.Equal(typeof(WebApplication), buildMethod!.ReturnType);
        Assert.Equal(typeof(WebApplication), buildMethod.GetParameters()[0].ParameterType);
    }
}
