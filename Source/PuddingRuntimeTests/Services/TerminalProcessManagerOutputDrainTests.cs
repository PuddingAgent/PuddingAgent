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
            // NOTE (2026-09-19): the historical reason for avoiding quotes here — ArgumentList mangling
            // embedded quotes into \" — has been fixed at the root in TerminalProcessManager (the command
            // now travels verbatim through ProcessStartInfo.Arguments with /d /s /c). The quote-free for-loop
            // form is kept because it is still the cheapest way to emit thousands of lines in one shot;
            // see QuotedPipeCommand_SurvivesCmdWrapper below for the quote-handling regression lock.
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

    /// <summary>
    /// Regression lock (2026-09-19; card f1d45a15 item 2 — “管道表达式内嵌引号时报拒绝访问”).
    ///
    /// Background: the Windows launch path passed the whole command as a single
    /// <see cref="ProcessStartInfo.ArgumentList"/> entry. .NET escapes such arguments with CRT/MSVCRT
    /// rules, so an inner <c>"</c> became <c>\"</c> — but cmd.exe only understands <c>""</c> / <c>^"</c>
    /// escaping, never <c>\"</c>. Result: every quoted command was silently destroyed — non-zero exit
    /// with completely empty stdout/stderr (verified: `echo "a b" | findstr "a"` → exit=1 and a 0-byte
    /// redirection file, so even the diagnostic message was lost). Bisecting showed the trigger is the
    /// quote, not the pipe: the same command without quotes worked on both launch paths.
    ///
    /// The sibling test worked around it by avoiding quotes. This test pins the root fix: the command is
    /// now passed verbatim through <see cref="ProcessStartInfo.Arguments"/> with <c>/d /s /c</c>, where
    /// <c>/s</c> strips the outer quote pair and leaves inner quotes untouched.
    /// </summary>
    [TestMethod]
    public async Task QuotedPipeCommand_SurvivesCmdWrapper()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-terminal-quote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var manager = new TerminalProcessManager(
            NullLogger<TerminalProcessManager>.Instance,
            PuddingDataPaths.FromRoot(root));
        try
        {
            var command = OperatingSystem.IsWindows()
                ? "echo \"alpha beta\" | findstr \"alpha\""
                : "echo \"alpha beta\" | grep \"alpha\"";

            var info = await manager.StartAsync("session-quote", command, root);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            int? exitCode = null;
            var text = string.Empty;
            var totalLines = 0;
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await manager.ReadOutputAsync(info.ProcessId, 0, 200, null, CancellationToken.None);
                if (snapshot is null) break;
                totalLines = snapshot.TotalLines;
                text = string.Join("\n", snapshot.Lines);
                if (snapshot.Process.ExitCode.HasValue)
                {
                    exitCode = snapshot.Process.ExitCode;
                    break;
                }
                await Task.Delay(150);
            }

            Assert.IsTrue(exitCode.HasValue, $"The command never terminated. Output='{text}'.");
            Assert.AreEqual(0, exitCode,
                $"A quoted, piped command must survive the cmd wrapper. Output='{text}'.");
            Assert.IsTrue(totalLines > 0, "The quoted command produced no output at all.");
            Assert.IsTrue(text.Contains("alpha", StringComparison.Ordinal),
                $"Expected the echoed payload in stdout; got '{text}'.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
