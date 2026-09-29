using System.Diagnostics;

namespace PuddingDesktop.Runtime;

public sealed record CoreProcessMetrics(DateTimeOffset StartedAt, TimeSpan Uptime, long WorkingSetBytes, double? CpuPercent);

// Only samples the supervised Core PID. CPU uses total machine capacity (0-100%),
// not a per-core percentage; a new process needs two observations.
public sealed class CoreProcessMetricsSampler
{
    private readonly CoreCpuUsageTracker _cpu = new();
    public CoreProcessMetrics? Sample(int? processId)
    {
        if (processId is null) { _cpu.Reset(); return null; }
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited) { _cpu.Reset(); return null; }
            var started = new DateTimeOffset(process.StartTime);
            var cpu = _cpu.Observe(process.Id, started, process.TotalProcessorTime,
                TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency), Environment.ProcessorCount);
            return new(started, DateTimeOffset.Now - started, process.WorkingSet64, cpu);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { _cpu.Reset(); return null; }
    }
}

internal sealed class CoreCpuUsageTracker
{
    private (int Pid, DateTimeOffset Started, TimeSpan Cpu, TimeSpan At)? _previous;
    public void Reset() => _previous = null;
    public double? Observe(int pid, DateTimeOffset started, TimeSpan cpu, TimeSpan at, int processors)
    {
        var previous = _previous;
        _previous = (pid, started, cpu, at);
        if (previous is not { } old || old.Pid != pid || old.Started != started || at <= old.At || cpu < old.Cpu || processors < 1) return null;
        return Math.Clamp((cpu - old.Cpu).TotalSeconds / (at - old.At).TotalSeconds / processors * 100, 0, 100);
    }
}
