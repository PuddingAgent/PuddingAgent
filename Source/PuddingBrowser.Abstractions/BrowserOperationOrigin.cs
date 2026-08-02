namespace PuddingBrowser.Abstractions;

/// <summary>
/// Non-sensitive origin information about the Agent call that triggered a browser operation.
/// Used for UI, diagnostics, and correlation only — not for authorization.
/// Never contains prompt text, tool parameters, DOM content, URLs, cookies, tokens, or API keys.
/// </summary>
public sealed record BrowserOperationOrigin
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public string? ConversationId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public required string ToolName { get; init; }
}

/// <summary>
/// AsyncLocal-scoped accessor for the current browser operation origin.
/// Supports nested Push/Dispose to restore the previous origin after tool execution.
/// Concurrent agent calls never leak origin across execution contexts.
/// </summary>
public interface IBrowserOperationOriginAccessor
{
    BrowserOperationOrigin? Current { get; }
    IDisposable Push(BrowserOperationOrigin origin);
}

public sealed class BrowserOperationOriginAccessor : IBrowserOperationOriginAccessor
{
    private readonly AsyncLocal<ImmutableStack<BrowserOperationOrigin>> _stack = new();

    public BrowserOperationOrigin? Current => _stack.Value?.Peek();

    public IDisposable Push(BrowserOperationOrigin origin)
    {
        var previous = _stack.Value ?? ImmutableStack<BrowserOperationOrigin>.Empty;
        _stack.Value = previous.Push(origin);
        return new PopDisposable(this, previous);
    }

    private void Pop(ImmutableStack<BrowserOperationOrigin> previous)
    {
        _stack.Value = previous.IsEmpty ? null : previous;
    }

    private sealed class PopDisposable : IDisposable
    {
        private BrowserOperationOriginAccessor? _owner;
        private ImmutableStack<BrowserOperationOrigin> _previous;

        public PopDisposable(BrowserOperationOriginAccessor owner, ImmutableStack<BrowserOperationOrigin> previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_owner is null) return;
            _owner.Pop(_previous);
            _owner = null;
        }
    }
}

/// <summary>
/// Minimal immutable stack used by AsyncLocal to avoid allocation of new collection per push.
/// </summary>
internal sealed class ImmutableStack<T>
{
    public static readonly ImmutableStack<T> Empty = new(default!, null);

    private readonly T _head;
    private readonly ImmutableStack<T>? _tail;

    private ImmutableStack(T head, ImmutableStack<T>? tail)
    {
        _head = head;
        _tail = tail;
    }

    public bool IsEmpty => _tail is null && EqualityComparer<T>.Default.Equals(_head, default);

    public T Peek() => _head;

    public ImmutableStack<T> Push(T value) => new(value, this);
}
