using Serilog.Core;
using Serilog.Events;

namespace PuddingCode.Observability;

public sealed class TraceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var trace = RuntimeTraceContextAccessor.Current;
        if (trace is null) return;

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("TraceId", trace.TraceId));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("CorrelationId", trace.CorrelationId));
        if (trace.SessionId is not null)
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("SessionId", trace.SessionId));
        if (trace.AgentInstanceId is not null)
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("AgentInstanceId", trace.AgentInstanceId));
    }
}
