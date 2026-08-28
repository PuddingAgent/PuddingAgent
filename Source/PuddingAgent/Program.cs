using PuddingHost.Hosting;

// ── PuddingAgent Console/DesktopChild Host (thin entry point) ──────
// All composition root logic lives in PuddingHost;
// Program.cs only delegates to PuddingApplicationHost.
//
// Modes:
//   Default (no flag):   Console dev server
//   --desktop-child:     Child process launched by PuddingDesktop.exe
//
// Calling order: Parse args → CreateBuilder → Build → InitializeAsync →
//                StartAsync → CaptureBoundAddresses → Ready signal

var isDesktopChild = args.Contains("--desktop-child");

var options = isDesktopChild
    ? PuddingHostOptionsFactory.ForDesktopChild(args)
    : PuddingHostOptionsFactory.ForConsole(args);

var builder = PuddingApplicationHost.CreateBuilder(args, options);
var app = PuddingApplicationHost.Build(builder);
CancellationTokenSource? startupLeaseCts = null;
Task startupLeaseTask = Task.CompletedTask;

if (isDesktopChild)
{
    startupLeaseCts = new CancellationTokenSource();
    startupLeaseTask = EmitDesktopStartupLeaseAsync(startupLeaseCts.Token);
}

try
{
    await PuddingApplicationHost.InitializeAsync(app, CancellationToken.None);

    if (!isDesktopChild)
        Console.WriteLine("[Startup] Starting server...");

    await app.StartAsync();
}
finally
{
    if (startupLeaseCts is not null)
    {
        await startupLeaseCts.CancelAsync();
        try
        {
            await startupLeaseTask;
        }
        catch (OperationCanceledException)
        {
        }
        startupLeaseCts.Dispose();
    }
}

var address = PuddingApplicationHost.CaptureBoundAddresses(app);

if (isDesktopChild)
{
    // Emit PUDDING_DESKTOP_READY signal on stdout so Desktop can parse it
    var readyJson = $$"""
        {"protocolVersion":1,"processId":{{Environment.ProcessId}},"baseAddress":"{{address}}"}
        """;
    Console.WriteLine($"PUDDING_DESKTOP_READY {readyJson}");

    // Register shutdown endpoint for Desktop
    // (DesktopLifecycleEndpointExtensions handles this)
}
else
{
    Console.WriteLine($"[Startup] Server running at {address} — waiting for shutdown...");
}

try
{
    await app.WaitForShutdownAsync();
}
finally
{
    Serilog.Log.CloseAndFlush();
}

static async Task EmitDesktopStartupLeaseAsync(CancellationToken cancellationToken)
{
    var sequence = 0L;
    var startedAt = DateTimeOffset.UtcNow;

    while (!cancellationToken.IsCancellationRequested)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            processId = Environment.ProcessId,
            sequence = Interlocked.Increment(ref sequence),
            phase = "initializing",
            elapsedMilliseconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
        });
        Console.WriteLine($"PUDDING_DESKTOP_STARTING {payload}");

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}

/// <summary>
/// Public partial class required for WebApplicationFactory&lt;Program&gt; integration tests.
/// </summary>
public partial class Program { }
