using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingRuntime.Services.Orchestration;

/// <summary>
/// Resolves one component input port from immutable graph inputs and committed upstream output
/// ports. Runtime executors consume this shared projection instead of reaching into another
/// component's summary or implementation-specific fields.
/// </summary>
public static class AgentOrchestrationNodeInputResolver
{
    public static IReadOnlyList<AgentOrchestrationValueEnvelope> Resolve(
        AgentOrchestrationNodeExecutionContext context,
        string targetPortId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(targetPortId))
            throw new ArgumentException("Target port id is required.", nameof(targetPortId));

        var values = new List<AgentOrchestrationValueEnvelope>();
        foreach (var binding in context.Node.GraphInputBindings.Where(binding =>
                     string.Equals(binding.TargetPortId, targetPortId, StringComparison.OrdinalIgnoreCase)))
        {
            EnsureTargetKeyIsSupported(binding.TargetKey, context.Node.NodeId, targetPortId);
            if (context.Run.Inputs.TryGetValue(binding.InputId, out var value))
                values.Add(value);
        }

        var sourceRuns = context.Run.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var edge in context.Definition.Edges
                     .Where(edge =>
                         edge.Kind == AgentOrchestrationEdgeKind.Data &&
                         string.Equals(edge.ToNodeId, context.Node.NodeId, StringComparison.Ordinal))
                     .OrderBy(edge => edge.EdgeId, StringComparer.Ordinal))
        {
            if (!sourceRuns.TryGetValue(edge.FromNodeId, out var sourceRun))
            {
                throw new InvalidOperationException(
                    $"Data edge '{edge.EdgeId}' references missing source node '{edge.FromNodeId}'.");
            }

            foreach (var binding in edge.Bindings.Where(binding =>
                         string.Equals(binding.TargetPortId, targetPortId, StringComparison.OrdinalIgnoreCase)))
            {
                EnsureTargetKeyIsSupported(binding.TargetKey, context.Node.NodeId, targetPortId);
                if (!sourceRun.Outputs.TryGetValue(binding.SourcePortId, out var sourceValue))
                {
                    throw new InvalidOperationException(
                        $"Upstream node '{edge.FromNodeId}' produced no value on output port " +
                        $"'{binding.SourcePortId}' for '{context.Node.NodeId}.{targetPortId}'.");
                }

                var resolved = ResolveSourcePath(sourceValue, binding.SourcePath, edge.EdgeId);
                if (binding.Aggregation == AgentOrchestrationDataAggregation.Replace)
                    values.Clear();
                values.Add(resolved);
            }
        }

        return values.AsReadOnly();
    }

    public static string ResolveInlineText(
        AgentOrchestrationNodeExecutionContext context,
        string targetPortId,
        string separator = "\n\n")
        => string.Join(
            separator,
            Resolve(context, targetPortId)
                .Select(ToInlineText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static IReadOnlyList<AgentOrchestrationArtifactReference> ResolveArtifacts(
        AgentOrchestrationNodeExecutionContext context,
        string targetPortId)
        => Resolve(context, targetPortId)
            .SelectMany(value => value.Artifacts)
            .GroupBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static string ToInlineText(AgentOrchestrationValueEnvelope value)
    {
        if (value.InlineValue is not { } inline)
            return string.Empty;
        return inline.ValueKind == JsonValueKind.String
            ? inline.GetString() ?? string.Empty
            : inline.GetRawText();
    }

    private static AgentOrchestrationValueEnvelope ResolveSourcePath(
        AgentOrchestrationValueEnvelope value,
        string? sourcePath,
        string edgeId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.Equals(sourcePath.Trim(), "$", StringComparison.Ordinal))
            return value;

        throw new NotSupportedException(
            $"Data edge '{edgeId}' uses sourcePath '{sourcePath}'. This runtime slice supports only '$'.");
    }

    private static void EnsureTargetKeyIsSupported(
        string? targetKey,
        string nodeId,
        string targetPortId)
    {
        if (!string.IsNullOrWhiteSpace(targetKey))
        {
            throw new NotSupportedException(
                $"Input binding '{nodeId}.{targetPortId}' uses targetKey '{targetKey}'. " +
                "Object assembly is not implemented by this runtime slice.");
        }
    }
}
