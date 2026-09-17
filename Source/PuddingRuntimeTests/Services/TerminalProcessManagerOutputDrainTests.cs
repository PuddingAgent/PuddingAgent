using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// Regression lock: after a process exits, its buffered stdout must contain ALL lines — including
/// the very last one.
///
/// Background (2026-09-17; the real root cause of G92-0's controlled check always reporting
/// test_count_unknown): <see cref="TerminalProcessManager"/>'s Exited handler disposed the process
/// immediately. .NET documents that when output is redirected the Exited event may be raised BEFORE
/// all stdout/stderr processing completes, so trailing lines were dropped before ever entering the
/// output buffer. The controlled check needs the trailing VSTest summary line of `dotnet test`
/// ("Passed! - Failed: N, Passed: N"), so it could not parse a test count even with exitCode=0. That
/// failure is independent of the read window (head vs tail) and of the character budget, because the
/// line was never buffered.
///
/// This test reproduces the shape (large output + marker as the last line) and asserts by
/// <c>TotalLines</c> — i.e. whether the lines are IN the buffer — which is independent of the read
/// window. Before the fix, TotalLines is short by the trailing batch and this assertion fails.
/// </summary>
[TestClass]
public sealed class TerminalProcessManagerOutputDrainTests
{
    private const string Marker = "PUDDING_OUTPUT_DRAIN_LAST_LINE_OK";
    private const int FillerLines = 2000;
    private const int ExpectedLines = FillerLines + 1;
    private const int TailWindow = 400;

    [TestMethod]
    public async Task ProcessExit_KeepsAllStdoutLines_Readable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-terminal-drain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var manager = new TerminalProcessManager(
            NullLogger<TerminalProcessManager>.Instance,
            PuddingDataPaths.FromRoot(root));
        try
        {
            // The command travels through `cmd /d /s /c` via ProcessStartInfo.ArgumentList, where
            // embedded quotes get mangled (verified: `call "path"` degenerated into a cmd error).
            // Therefore use a quote-free inline cmd command: parenthesised for-loop plus `&` chaining.
            var command = OperatingSystem.IsWindows()
                ? $"(for /L %i in (1,1,{FillerLines}) do @echo filler-%i) & echo {Marker}"
                : $"seq 1 {FillerLines}; echo {Marker}";

            var info = await manager.StartAsync("session-drain", command, root);

            // Poll until the process has finished AND its buffer stopped growing.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var totalLines = 0;
            var tail = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                var probe = await manager.ReadOutputAsync(info.ProcessId, 0, 1, null, CancellationToken.None);
                if (probe is null)
                    break;
                totalLines = probe.TotalLines;

                // Same read shape the controlled check uses: probe TotalLines, then read the tail.
                var tailSnapshot = await manager.ReadOutputAsync(
                    info.ProcessId,
                    Math.Max(0, totalLines - TailWindow),
                    TailWindow,
                    200_000,
                    CancellationToken.None);
                tail = tailSnapshot is null ? string.Empty : string.Join("\n", tailSnapshot.Lines);

                if (totalLines >= ExpectedLines && tail.Contains(Marker, StringComparison.Ordinal))
                    break;
                await Task.Delay(200);
            }

            Assert.IsTrue(
                totalLines >= ExpectedLines,
                $"Trailing stdout lines were dropped at process exit: expected >= {ExpectedLines} buffered lines, "
                + $"got {totalLines}. The Exited handler must await the asynchronous output drain "
                + "(parameterless WaitForExit) before Dispose.");

            Assert.IsTrue(
                tail.Contains(Marker, StringComparison.Ordinal),
                $"The last stdout line is not readable from the tail window (totalLines={totalLines}, "
                + $"tailChars={tail.Length}).");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
