using PuddingCode.Observability;

namespace PuddingRuntime.Services.Observability;

public sealed class AmbientRuntimeTraceAccessor : IRuntimeTraceAccessor
{
    public RuntimeTraceContext? Current
    {
        get => RuntimeTraceContextAccessor.Current;
        set => RuntimeTraceContextAccessor.Current = value;
    }
}
