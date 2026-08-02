using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public class ConnectorHostLifecycleServiceTests
{
    [Fact]
    public async Task P2pFailure_DoesNotPreventConnectorStartup()
    {
        var phases = new List<string>();

        await ConnectorHostLifecycleService.RunStartupPhasesAsync(
            _ =>
            {
                phases.Add("p2p");
                throw new InvalidOperationException("simulated P2P failure");
            },
            _ =>
            {
                phases.Add("migration");
                return Task.CompletedTask;
            },
            _ =>
            {
                phases.Add("connectors");
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["p2p", "migration", "connectors"], phases);
    }

    [Fact]
    public async Task Cancellation_DoesNotContinueToConnectorStartup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var connectorsStarted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConnectorHostLifecycleService.RunStartupPhasesAsync(
                ct => Task.FromCanceled(ct),
                _ => Task.CompletedTask,
                _ =>
                {
                    connectorsStarted = true;
                    return Task.CompletedTask;
                },
                NullLogger.Instance,
                cts.Token));

        Assert.False(connectorsStarted);
    }

    [Fact]
    public void Service_IsRegisteredAsHostedService()
    {
        Assert.True(
            typeof(IHostedService).IsAssignableFrom(
                typeof(ConnectorHostLifecycleService)));
    }
}
