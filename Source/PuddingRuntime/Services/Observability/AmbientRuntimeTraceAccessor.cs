using PuddingCode.Observability;

namespace PuddingRuntime.Services.Observability;

public sealed class AmbientRuntimeTraceAccessor : IRuntimeTraceAccessor
{
    private static readonly AsyncLocal<RuntimeTraceContext?> CurrentHolder = new();

    public RuntimeTraceContext? Current
    {
        get => CurrentHolder.Value;
        set => CurrentHolder.Value = value;
    }
}
