namespace PuddingCode.Observability;

public static class RuntimeTraceContextAccessor
{
    private static readonly AsyncLocal<RuntimeTraceContext?> _current = new();

    public static RuntimeTraceContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static IDisposable Scope(RuntimeTraceContext trace)
    {
        var previous = _current.Value;
        _current.Value = trace;
        return new ScopeDisposable(() => _current.Value = previous);
    }

    private sealed class ScopeDisposable : IDisposable
    {
        private Action? _onDispose;

        public ScopeDisposable(Action onDispose)
            => _onDispose = onDispose;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _onDispose, null);
            action?.Invoke();
        }
    }
}
