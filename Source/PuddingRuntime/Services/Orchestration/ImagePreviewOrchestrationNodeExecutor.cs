using PuddingCode.Orchestration;

namespace PuddingRuntime.Services.Orchestration;

/// <summary>
/// Resolves the image artifact delivered to the component's <c>images</c> input and commits the
/// same durable reference as its output. Rendering remains a component-owned Admin concern; the
/// runtime never copies or embeds the image bytes in orchestration state.
/// </summary>
public sealed class ImagePreviewOrchestrationNodeExecutor : IAgentOrchestrationNodeExecutor
{
    public bool CanExecute(AgentOrchestrationNodeDefinition node)
        => string.Equals(
               node.Component.ComponentType,
               AgentOrchestrationComponentTypes.ImagePreview,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(node.Executor?.ToolId, "preview_image", StringComparison.OrdinalIgnoreCase);

    public Task<AgentOrchestrationNodeExecutionResult> ExecuteAsync(
        AgentOrchestrationNodeExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(context.Node))
            throw new InvalidOperationException($"Node '{context.Node.NodeId}' is not an image-preview component.");

        var artifacts = AgentOrchestrationNodeInputResolver.ResolveArtifacts(context, "images");
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Image Preview node '{context.Node.NodeId}' received no artifact on its images input.");
        }

        return Task.FromResult(new AgentOrchestrationNodeExecutionResult
        {
            Summary = artifacts.Count == 1
                ? "Previewed 1 image artifact."
                : $"Previewed {artifacts.Count} image artifacts.",
            ArtifactReference = artifacts[0].ArtifactId,
            Outputs = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal)
            {
                ["images"] = new()
                {
                    DataType = AgentOrchestrationDataTypes.Artifact,
                    Artifacts = artifacts
                }
            }
        });
    }
}
