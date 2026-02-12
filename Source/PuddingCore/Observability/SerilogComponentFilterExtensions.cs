using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace PuddingCode.Observability;

public static class SerilogComponentFilterExtensions
{
    public static LoggerConfiguration ByIncludingComponent(
        this LoggerFilterConfiguration cfg, string component)
    {
        return cfg.ByIncludingOnly(evt =>
            evt.Properties.TryGetValue("Component", out var comp) &&
            comp is ScalarValue sv &&
            sv.Value?.ToString() == component);
    }
}
