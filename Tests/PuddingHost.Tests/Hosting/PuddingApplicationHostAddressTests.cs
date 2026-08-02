using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public class PuddingApplicationHostAddressTests
{
    [Fact]
    public void CaptureBoundAddresses_BeforeStart_Throws()
    {
        // Build an app but do NOT start it.
        var builder = WebApplication.CreateBuilder(new[] { "--urls", "http://127.0.0.1:0" });
        builder.Services.AddSingleton<IPuddingServerAddressAccessor, PuddingServerAddressAccessor>();
        var app = builder.Build();

        // CaptureBoundAddresses must be called AFTER StartAsync.
        // Before start, IServerAddressesFeature has no addresses.
        Assert.Throws<InvalidOperationException>(() =>
        {
            PuddingApplicationHost.CaptureBoundAddresses(app);
        });
    }

    [Fact]
    public async Task CaptureBoundAddresses_AfterStart_ReturnsDynamicPort()
    {
        // Build with 127.0.0.1:0 (dynamic port), start, then capture.
        var builder = WebApplication.CreateBuilder(new[] { "--urls", "http://127.0.0.1:0" });
        builder.Services.AddSingleton<IPuddingServerAddressAccessor, PuddingServerAddressAccessor>();
        var app = builder.Build();

        await app.StartAsync();

        try
        {
            var baseAddress = PuddingApplicationHost.CaptureBoundAddresses(app);

            Assert.NotNull(baseAddress);
            Assert.True(baseAddress.IsLoopback);
            Assert.Equal("http", baseAddress.Scheme);
            Assert.NotEqual(0, baseAddress.Port); // Dynamic port was assigned
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
