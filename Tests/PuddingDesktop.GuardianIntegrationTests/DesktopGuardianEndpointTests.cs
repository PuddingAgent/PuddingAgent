using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingDesktop.Bootstrap;
using PuddingDesktop.Configuration;
using PuddingDesktop.Debug;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Bootstrap;

public sealed class DesktopGuardianEndpointTests
{
    [Fact]
    public async Task HealthAndExitRequireTokenAndGuardianBlocksLegacyDeployment()
    {
        var root = Directory.CreateTempSubdirectory("pudding-guardian-endpoint").FullName;
        try
        {
            using var portLease = new TcpListener(IPAddress.Loopback, 0);
            portLease.Start();
            var port = ((IPEndPoint)portLease.LocalEndpoint).Port;
            portLease.Stop();
            var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pings = 0;
            var tokens = new TokenService();
            await using var service = new DesktopBootstrapSignalService(new DesktopBootstrapTestHooks
            {
                StopCoreAsync = _ => throw new InvalidOperationException("Must not invoke legacy stop"),
                WaitForCoreFullyStoppedAsync = _ => Task.FromResult(true),
                StartCoreAsync = _ => throw new InvalidOperationException("Must not invoke legacy start"),
                BuildAsync = _ => throw new InvalidOperationException("Must not invoke legacy build"),
                RuntimeState = () => DesktopRuntimeState.Ready,
                CoreExecutablePath = () => Path.Combine(root, "PuddingAgent.exe"),
                FrontendDeployAsync = (_, _, _, _) => Task.FromResult(UnusedFrontend()),
            }, root, new PuddingDesktopBootstrapConfig(), tokens);
            await using var endpoint = new DesktopBootstrapHttpEndpoint(service, tokens, root, port,
                () => "Ready", _ => Task.FromResult(UnusedFrontend()), (_, _, _) => Task.FromResult(UnusedFrontend()),
                () => new() { CapturedAt = DateTimeOffset.UtcNow, DesktopVersion = "test", DesktopState = "WorkbenchReady", CoreState = "Ready", DataRoot = root },
                _ => { Interlocked.Increment(ref pings); return Task.CompletedTask; },
                () => exit.SetResult(), () => true);
            endpoint.Start(default);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{port}") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/desktop/guardian/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/desktop/guardian/exit", null)).StatusCode);
            Assert.False(exit.Task.IsCompleted);
            Assert.Equal(0, pings);
            client.DefaultRequestHeaders.Add("X-Control-Token", "endpoint-test-token");
            using var json = JsonDocument.Parse(await client.GetStringAsync("/desktop/guardian/health"));
            Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("desktopProcessId").GetInt32());
            Assert.Equal("Ready", json.RootElement.GetProperty("diagnostics").GetProperty("coreState").GetString());
            Assert.Equal(1, pings);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/desktop/bootstrap/start", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/desktop/bootstrap/core/stop", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/desktop/guardian/exit", null)).StatusCode);
            await exit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TokenService : IDesktopControlTokenService
    {
        public Task<string> GetOrCreateAsync(string dataRoot, CancellationToken cancellationToken) => Task.FromResult("endpoint-test-token");
        public Task<string> RegenerateAsync(string dataRoot, CancellationToken cancellationToken) => Task.FromResult("endpoint-test-token");
    }
    private static FrontendDeployResult UnusedFrontend() => new() { TargetAdminDirectory = "unused", CopiedFileCount = 0, RanInstall = false };
}
