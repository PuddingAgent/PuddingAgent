using System.Text.Json;
using PuddingDesktop.Guardian;

if (args.Length is not (2 or 4) || args[0] != "--config" || (args.Length == 4 && args[2] != "--adopt-pid")) return 2;
try
{
    var settings = JsonSerializer.Deserialize<GuardianSettings>(File.ReadAllText(args[1]), GuardianJson.Options)
        ?? throw new InvalidDataException("Missing guardian settings.");
    settings.Validate();
    SafeFiles.NoLinks(settings.StateDirectory);
    Directory.CreateDirectory(settings.StateDirectory);
    FileStream lease;
    try { lease = new FileStream(Path.Combine(settings.StateDirectory, "guardian.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { return 0; } // Another guardian owns this installation. Never delete the lease file.
    using var ownedLease = lease;
    using var lifetime = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
    var store = new GuardianStore(settings);
    using var runtime = new GuardianRuntime(settings, store.Load(), args.Length == 4 ? int.Parse(args[3]) : null);
    var controller = new GuardianController(settings, store, runtime, log: new GuardianLog(settings.StateDirectory));
    using var server = new GuardianHttpServer(settings, controller);
    server.Start();
    var listener = server.RunAsync(lifetime.Token);
    await controller.RecoverInterruptedAsync(lifetime.Token);
    while (!lifetime.IsCancellationRequested)
    {
        await controller.TickAsync(lifetime.Token);
        await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), lifetime.Token);
        if (listener.IsCompleted) await listener;
    }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception ex) { Console.Error.WriteLine("Guardian stopped: " + ex.GetType().Name); return 1; }
