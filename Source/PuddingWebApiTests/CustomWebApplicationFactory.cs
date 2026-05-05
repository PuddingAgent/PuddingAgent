using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

/// <summary>
/// 测试用 WebApplicationFactory。
/// 替换真实 DB 为 SQLite 内存数据库，跳过 P2P/Cron 等后台服务。
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _platformConnection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            RemoveCronHostedService(services);

            services.RemoveAll<IP2pDiscoveryService>();
            services.AddSingleton<IP2pDiscoveryService, NoOpP2pDiscoveryService>();

            services.RemoveAll<DbContextOptions<PlatformDbContext>>();
            services.RemoveAll<PlatformDbContext>();
            services.RemoveAll<IDbContextFactory<PlatformDbContext>>();

            _platformConnection = new SqliteConnection("Data Source=:memory:");
            _platformConnection.Open();

            services.AddSingleton(_platformConnection);

            services.AddDbContext<PlatformDbContext>(options =>
            {
                options.UseSqlite(_platformConnection)
                       .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning));
            }, ServiceLifetime.Singleton);

            services.AddDbContextFactory<PlatformDbContext>(options =>
            {
                options.UseSqlite(_platformConnection)
                       .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning));
            }, ServiceLifetime.Singleton);

            // 将 PlatformApiClient 的内部调用指向当前 TestServer，避免访问外部端口。
            services.AddHttpClient<PlatformApiClient>(client =>
            {
                client.BaseAddress = new Uri("http://localhost");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken());
            }).ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        context.Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _platformConnection?.Dispose();
            _platformConnection = null;
        }

        base.Dispose(disposing);
    }

    private static void RemoveCronHostedService(IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType == typeof(CronSchedulerService))
            .ToList();

        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }

    private sealed class NoOpP2pDiscoveryService : IP2pDiscoveryService
    {
        public event EventHandler<PeerNode>? PeerDiscovered;

        public event EventHandler<PeerNode>? PeerLost;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public IReadOnlyList<PeerNode> GetPeers() => [];
    }
}
