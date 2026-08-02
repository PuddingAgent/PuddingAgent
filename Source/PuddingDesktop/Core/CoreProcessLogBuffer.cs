using System.Collections.Concurrent;

namespace PuddingDesktop.Core;

/// <summary>
/// Thread-safe ring buffer for Core process stdout/stderr lines.
/// Exposes the last N lines for the "运行日志" view.
/// </summary>
public sealed class CoreProcessLogBuffer
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly int _capacity;
    private int _count;

    public CoreProcessLogBuffer(int capacity = 500)
    {
        _capacity = capacity;
    }

    public void Append(string line)
    {
        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            _lines.TryDequeue(out _);
            Interlocked.Decrement(ref _count);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _lines.ToArray();
    }

    public string GetTail(int maxLines = 100)
    {
        var lines = _lines.Reverse().Take(maxLines).Reverse();
        return string.Join(Environment.NewLine, lines);
    }
}
