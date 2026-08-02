namespace PuddingDesktop.Runtime;

public sealed class CoreRestartAttemptWindow
{
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;

    public CoreRestartAttemptWindow(int maxAttempts, TimeSpan window)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _maxAttempts = maxAttempts;
        _window = window;
    }

    public bool TryRegister(DateTimeOffset now, out int attemptNumber)
    {
        Prune(now);
        if (_attempts.Count >= _maxAttempts)
        {
            attemptNumber = _attempts.Count;
            return false;
        }

        _attempts.Enqueue(now);
        attemptNumber = _attempts.Count;
        return true;
    }

    public int Count(DateTimeOffset now)
    {
        Prune(now);
        return _attempts.Count;
    }

    public void Reset() => _attempts.Clear();

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_attempts.TryPeek(out var attempt) && attempt <= cutoff)
            _attempts.Dequeue();
    }
}
