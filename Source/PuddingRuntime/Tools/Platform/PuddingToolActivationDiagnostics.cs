using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// One native tool whose container activation failed.
/// A tool that cannot be constructed is a capability gap, not a reason to take down the
/// whole registry: every other tool must stay callable. But a silently vanished capability
/// is worse than a loud one, so each failure is kept here and read back through
/// <see cref="IToolActivationDiagnostics"/>.
/// </summary>
public sealed record ToolActivationFailure(
    string ToolType,
    string Message,
    DateTimeOffset OccurredAtUtc)
{
    public static ToolActivationFailure Create(Type toolType, Exception error) =>
        new(
            toolType.FullName ?? toolType.Name,
            error.Message,
            DateTimeOffset.UtcNow);
}

/// <summary>
/// Read-only view of tools that failed to activate from the container.
/// Kept separate from the registry so controllers, diagnostics surfaces and tests can observe
/// activation gaps without depending on registry internals.
/// </summary>
public interface IToolActivationDiagnostics
{
    IReadOnlyList<ToolActivationFailure> Failures { get; }
}

/// <summary>
/// Collects activation failures in process memory.
/// <para>
/// <see cref="PuddingToolRegistry"/> rebuilds its snapshot on every lookup, so the same broken tool
/// is offered many times. Failures are therefore de-duplicated by tool type: this sink records
/// <em>which</em> tools are unreachable, not how often the registry happened to look at them.
/// </para>
/// </summary>
public sealed class ToolActivationDiagnostics : IToolActivationDiagnostics
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolActivationFailure> _failures = new(StringComparer.Ordinal);
    private readonly ILogger<ToolActivationDiagnostics>? _logger;

    public ToolActivationDiagnostics(ILogger<ToolActivationDiagnostics>? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToolActivationFailure> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.Values
                    .OrderBy(f => f.ToolType, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>Records one failure. Returns false when this tool type was already recorded.</summary>
    public bool Record(ToolActivationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        lock (_gate)
        {
            if (!_failures.TryAdd(failure.ToolType, failure))
                return false;
        }

        // Log once, at the moment the gap is first observed, so an operator reading the log
        // never has to infer it from a missing tool id.
        _logger?.LogWarning(
            "[Tools] Tool {ToolType} could not be activated and is excluded from the registry: {Message}",
            failure.ToolType,
            failure.Message);

        return true;
    }
}
