using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuddingDesktop.Foundation;
using PuddingHost.Hosting;

namespace PuddingDesktop.Composition;

/// <summary>The only Desktop assembly that knows the Core composition root.</summary>
public sealed class DesktopKernelFactory(IDesktopServices desktop) : IKernelSessionFactory
{
    private static readonly SemaphoreSlim ProcessHost = new(1, 1);
    public async Task<IKernelSession> StartAsync(string dataRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataRoot);
        // Prevent different preview profiles from loading the same databases concurrently.
        var lease = new PuddingDataRootLease(dataRoot);
        bool entered;
        try { entered = await ProcessHost.WaitAsync(0, cancellationToken).ConfigureAwait(false); }
        catch { lease.Dispose(); throw; }
        if (!entered)
        {
            lease.Dispose();
            throw new InvalidOperationException("Only one Core host can be loaded in a Desktop process.");
        }
        WebApplication? app = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = PuddingHostOptionsFactory.ForDesktop(dataRoot) with { BrowserAutomationEnabled = false };
            var builder = PuddingApplicationHost.CreateBuilder([], options);
            builder.Services.AddSingleton(desktop);
            builder.Services.AddControllers().AddApplicationPart(typeof(DesktopPresentationController).Assembly);
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            app = PuddingApplicationHost.Build(builder);
            await PuddingApplicationHost.InitializeAsync(app, cancellationToken).ConfigureAwait(false);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = PuddingApplicationHost.CaptureBoundAddresses(app);
            return new Session(app, lease, address);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            ProcessHost.Release();
            await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(WebApplication app, PuddingDataRootLease lease, Uri address) : IKernelSession
    {
        private bool _disposed;
        public Uri WorkbenchAddress => new(address, "/admin/");
        public CancellationToken Stopping => app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        public Task StopAsync(CancellationToken cancellationToken) => app.StopAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            await app.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
            _disposed = true;
            ProcessHost.Release();
        }
    }

    private sealed class DesktopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
