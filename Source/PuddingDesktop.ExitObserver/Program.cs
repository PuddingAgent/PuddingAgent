using System.Diagnostics;
using PuddingDesktop.Diagnostics;

// Separate process: no UI, no Core ownership, no automatic restart, no database/config access.
if (args.Length != 1) return 2;
var directory = Path.GetFullPath(args[0]);
var state = DesktopRunJournal.ReadState(directory);
if (state is null) return 2;
try
{
    using var parent = Process.GetProcessById(state.Process.ProcessId);
    if (parent.StartTime.ToUniversalTime() != state.Process.StartedUtc) return 3;
    _ = parent.Handle; // Retain the original process handle; PID reuse must not change our target.
    DesktopRunJournal.WriteAtomic(Path.Combine(directory, "observer.json"), new
    {
        observerPid = Environment.ProcessId, attachedAt = DateTimeOffset.UtcNow, target = state.Process
    });
    await parent.WaitForExitAsync();
    DesktopRunJournal.RecordObservedExit(directory, parent.ExitCode, "observer");
    return 0;
}
catch (ArgumentException)
{
    DesktopRunJournal.RecordObservedExit(directory, null, "observer");
    return 0;
}
catch (Exception ex)
{
    // A failed observer is not evidence that Desktop died.
    try { DesktopRunJournal.WriteAtomic(Path.Combine(directory, "observer.json"), new { error = ex.GetType().Name, at = DateTimeOffset.UtcNow }); } catch { }
    return 1;
}
