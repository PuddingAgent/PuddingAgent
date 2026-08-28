using System.Threading;

namespace PuddingRuntime.Services;

/// <summary>
/// Async-flow-local purpose for one logical LLM invocation. It lets the gateway billing
/// ledger distinguish agent, approval and compaction calls without changing the
/// model-visible request body or relying on SessionId naming conventions.
/// </summary>
public sealed class LlmInvocationPurposeAccessor
{
    private readonly AsyncLocal<string?> _current = new();

    public string Current => string.IsNullOrWhiteSpace(_current.Value)
        ? "agent"
        : _current.Value!;

    public IDisposable Push(string? purpose)
    {
        var previous = _current.Value;
        _current.Value = Normalize(purpose);
        return new Scope(this, previous);
    }

    private static string Normalize(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return "agent";

        var normalized = new string(purpose.Trim().ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(16)
            .ToArray());
        return normalized.Length == 0 ? "agent" : normalized;
    }

    private sealed class Scope(LlmInvocationPurposeAccessor owner, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
