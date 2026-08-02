namespace PuddingDesktop.Core;

public sealed class CoreProcessStateChangedEventArgs : EventArgs
{
    public CoreProcessState Previous { get; }
    public CoreProcessState Current { get; }
    public CoreProcessSession? Session { get; }
    public string? Error { get; }

    public CoreProcessStateChangedEventArgs(
        CoreProcessState previous,
        CoreProcessState current,
        CoreProcessSession? session = null,
        string? error = null)
    {
        Previous = previous;
        Current = current;
        Session = session;
        Error = error;
    }
}

public sealed class CoreProcessExitedEventArgs : EventArgs
{
    public int ProcessId { get; }
    public int ExitCode { get; }
    public bool Expected { get; }

    public CoreProcessExitedEventArgs(int processId, int exitCode, bool expected)
    {
        ProcessId = processId;
        ExitCode = exitCode;
        Expected = expected;
    }
}
