using System.Net;
using System.Net.Http;
using PuddingDesktop.Guardian;

namespace PuddingDesktop.GuardianTests;

/// <summary>
/// Locks the status contract that lets a desktop decide whether a listening guardian
/// actually supervises it. Without <c>desktopPid</c> the caller cannot distinguish
/// "supervises me" from "supervises a desktop that already exited", which is exactly
/// how a live desktop ended up unmanaged with no warning.
/// </summary>
public sealed class GuardianHttpServerStatusTests
{
    [Fact]
    public async Task Status_ExposesSupervisedDesktopAndCorePid()
    {
        using var f = new GuardianTests.Fixture();
        using var server = new GuardianHttpServer(f.Settings, f.Controller);
        using var lifetime = new CancellationTokenSource();
        server.Start();
        var loop = server.RunAsync(lifetime.Token);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new($"http://127.0.0.1:{f.Settings.HttpPort}"),
        };
        client.DefaultRequestHeaders.Add("X-Control-Token", "test-secret");

        var status = await client.GetStringAsync("/guardian/status");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/guardian/status")).StatusCode);
        // Presence matters, not the value: a guardian that has not published its identity
        // snapshot yet must still answer with the field present and null rather than omit it.
        Assert.Contains("\"desktopPid\"", status);
        Assert.Contains("\"corePid\"", status);

        lifetime.Cancel();
        await loop;
    }
}
