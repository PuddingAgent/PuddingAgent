using System.Text.Json;
using PuddingDesktop.Guardian;

var resume = args.Contains("--resume", StringComparer.Ordinal);
var options = args.Where(arg => arg != "--resume").ToArray();
if (options.Length is not (2 or 4) || options[0] != "--config"
    || (options.Length == 4 && (options[2] != "--adopt-pid" || !int.TryParse(options[3], out _)))) return 2;
try
{
    var settings = JsonSerializer.Deserialize<GuardianSettings>(File.ReadAllText(options[1]), GuardianJson.Options)
        ?? throw new InvalidDataException("Missing guardian settings.");
    settings.Validate();
    SafeFiles.NoLinks(settings.StateDirectory);
    Directory.CreateDirectory(settings.StateDirectory);
    FileStream lease;
    try { lease = new FileStream(Path.Combine(settings.StateDirectory, "guardian.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException)
    {
        // Another guardian owns this installation. Never delete the lease file.
        // This path runs before any logger exists, and Desktop starts us with
        // CreateNoWindow, so a silent return 0 leaves no observable trace at all:
        // the outage is indistinguishable from a crash, and Desktop can only
        // report a generic endpoint timeout. Record the reason before exiting.
        RecordLeaseConflict(settings.StateDirectory);
        return 0;
    }
    using var ownedLease = lease;
    using var lifetime = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
    var store = new GuardianStore(settings);
    using var runtime = new GuardianRuntime(settings, store.Load(), options.Length == 4 ? int.Parse(options[3]) : null);
    var controller = new GuardianController(settings, store, runtime, log: new GuardianLog(settings.StateDirectory));
    if (resume) controller.ResumeAfterUserLaunch();
    using var server = new GuardianHttpServer(settings, controller);
    server.Start();
    var listener = server.RunAsync(lifetime.Token);
    await controller.RecoverInterruptedAsync(lifetime.Token);
    while (!lifetime.IsCancellationRequested)
    {
        await controller.TickAsync(lifetime.Token);
        if (controller.ShouldExit) break;
        await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), lifetime.Token);
        if (listener.IsCompleted) await listener;
    }
    lifetime.Cancel();
    await listener;
    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    await server.DrainAsync(drain.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception ex) { Console.Error.WriteLine("Guardian stopped: " + ex.GetType().Name); return 1; }

/// <summary>
/// Leaves an on-disk breadcrumb when this instance refuses to start because
/// another guardian holds the installation lease. Diagnostics must never alter
/// the exit path, so every failure here is swallowed.
/// </summary>
static void RecordLeaseConflict(string stateDirectory)
{
    try
    {
        var path = Path.Combine(stateDirectory, "guardian.lease-conflict.log");
        File.AppendAllText(
            path,
            DateTimeOffset.Now.ToString("O")
            + " lease held by another guardian; exited without starting the HTTP control endpoint"
            + Environment.NewLine);
    }
    catch (Exception) { /* never let diagnostics change the exit path */ }
}
