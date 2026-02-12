namespace PuddingCode.Observability;

/// <summary>
/// Central switch for high-detail telemetry payloads.
/// Production-safe metrics should always be cheap and redacted; large previews
/// of prompts, tool arguments, and tool outputs are enabled only when this
/// switch is explicitly set by the development launcher or operator.
/// </summary>
public static class TelemetryDebugSwitch
{
    public const string UnifiedEnvironmentVariable = "PUDDING_DEBUG";
    public const string LegacyTelemetryEnvironmentVariable = "PUDDING_TELEMETRY_DEBUG";

    public static bool IsEnabled()
        => IsTruthy(Environment.GetEnvironmentVariable(UnifiedEnvironmentVariable))
           || IsTruthy(Environment.GetEnvironmentVariable(LegacyTelemetryEnvironmentVariable));

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "debug", StringComparison.OrdinalIgnoreCase);
}
