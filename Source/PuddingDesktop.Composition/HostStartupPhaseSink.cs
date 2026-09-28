using PuddingDesktop.Foundation;
using PuddingHost.Hosting;

namespace PuddingDesktop.Composition;

/// <summary>
/// Adapts the Desktop startup recorder to the host's evidence port. This is the only place that knows
/// both sides, which is why Core stays free of any Desktop dependency.
/// </summary>
internal sealed class HostStartupPhaseSink(IStartupAttempt attempt) : IStartupPhaseSink
{
    public IStartupPhaseScope Phase(string name) => new HostStartupPhaseScope(attempt.Phase(name));

    public void Metric(string name, long value) => attempt.Metric(name, value);

    private sealed class HostStartupPhaseScope(IStartupPhase phase) : IStartupPhaseScope
    {
        public void Complete(string? detail = null) => phase.Complete(detail);
        public void Skip(string reason) => phase.Skip(reason);
        public void Dispose() => phase.Dispose();
    }
}
