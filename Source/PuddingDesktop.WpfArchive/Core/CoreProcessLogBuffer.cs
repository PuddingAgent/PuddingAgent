using System.Collections.Concurrent;

namespace PuddingDesktop.Core;

/// <summary>
/// Thread-safe ring buffer for Core process stdout/stderr lines.
/// Exposes the last N lines for the "运行日志" view.
/// </summary>
public sealed class CoreProcessLogBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;
    private string? _cachedTail;
    private int _cachedMaxLines;

    public CoreProcessLogBuffer(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            if (_lines.Count > _capacity)
                _lines.Dequeue();
            _cachedTail = null;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }

    public string GetTail(int maxLines = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines);
        lock (_gate)
        {
            if (_cachedTail is not null && _cachedMaxLines == maxLines)
                return _cachedTail;
            _cachedMaxLines = maxLines;
            return _cachedTail = string.Join(Environment.NewLine, _lines.Skip(Math.Max(0, _lines.Count - maxLines)));
        }
    }
}
