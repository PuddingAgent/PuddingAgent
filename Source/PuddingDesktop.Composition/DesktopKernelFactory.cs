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
    private Session? _active;
    public PuddingChat.IChatClient CreateChatClient() => _active?.CreateChatClient()
        ?? throw new InvalidOperationException("Core 尚未就绪。");
    /// <summary>Settings domain adapters are bound to the kernel lifecycle, so readiness is checked per call.</summary>
    public ILlmResourceSettings CreateLlmSettings(IDesktopKernel kernel) => new DesktopLlmResourceSettings(kernel);
    public IVoiceResourceSettings CreateVoiceSettings(IDesktopKernel kernel) => new DesktopVoiceResourceSettings(kernel);
    public IAgentDirectorySettings CreateAgentDirectorySettings(IDesktopKernel kernel) => new DesktopAgentDirectorySettings(kernel);
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
            builder.Services.AddScoped<PuddingPlatform.Services.AgentChat.AgentMainSessionService>();
            builder.Services.AddScoped<PuddingPlatform.Services.AgentChat.LocalWorkspaceSetupService>();
            app = PuddingApplicationHost.Build(builder);
            await PuddingApplicationHost.InitializeAsync(app, cancellationToken).ConfigureAwait(false);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = PuddingApplicationHost.CaptureBoundAddresses(app);
            _active = new Session(app, lease, address);
            return _active;
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

    private sealed class Session(WebApplication app, PuddingDataRootLease lease, Uri address) : IKernelSession, ISettingsOperationHost
    {
        private bool _disposed;
        private bool _stopping;
        private readonly object _gate = new();
        private readonly List<InProcessChatClient> _clients = [];
        private readonly List<Task> _settings = [];
        private readonly IServiceScopeFactory _scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        public PuddingChat.IChatClient CreateChatClient()
        {
            lock (_gate)
            {
                if (_disposed || _stopping) throw new InvalidOperationException("Core 正在停止。");
                var client = new InProcessChatClient(_scopes, Stopping);
                _clients.Add(client); return client;
            }
        }
        public Uri WorkbenchAddress => new(address, "/admin/");
        public CancellationToken Stopping => app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        public Task<T> RunAsync<T>(Func<ISettingsScope, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        {
            Task<T> operation;
            lock (_gate)
            {
                // Refuse rather than queue: a stopping host must not accept new settings writes.
                if (_disposed || _stopping)
                    throw new SettingsUnavailableException(SettingsUnavailable.KernelStopping, "Core 正在停止，已拒绝新的设置操作。");
                operation = Task.Run(async () =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
                    linked.Token.ThrowIfCancellationRequested();
                    await using var scope = new SettingsOperationScope(_scopes);
                    return await body(scope, linked.Token).ConfigureAwait(false);
                }, CancellationToken.None);
                _settings.Add(operation);
                _ = operation.ContinueWith(completed => { lock (_gate) _settings.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return operation;
        }
        private (InProcessChatClient[] Clients, Task[] Settings) BeginStop()
        {
            lock (_gate) { _stopping = true; return (_clients.ToArray(), _settings.ToArray()); }
        }
        private static async Task DrainSettingsAsync(Task[] settings, CancellationToken cancellationToken)
        {
            if (settings.Length == 0) return;
            try { await Task.WhenAll(settings).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch when (!cancellationToken.IsCancellationRequested) { /* each caller observes its own failure */ }
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var (clients, settings) = BeginStop();
            foreach (var client in clients) await client.DrainAsync(cancellationToken).ConfigureAwait(false);
            await DrainSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            var (clients, settings) = BeginStop();
            foreach (var client in clients) await client.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainSettingsAsync(settings, CancellationToken.None).ConfigureAwait(false);
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
