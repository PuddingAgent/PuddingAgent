using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Orchestration;

namespace PuddingRuntime.Services.Orchestration;

public sealed record AgentOrchestrationNodeExecutionContext
{
    public required AgentOrchestrationGraphDefinition Definition { get; init; }
    public required AgentOrchestrationRunSnapshot Run { get; init; }
    public required AgentOrchestrationNodeDefinition Node { get; init; }
    public required AgentOrchestrationNodeClaim Claim { get; init; }
}

public sealed record AgentOrchestrationNodeExecutionResult
{
    public required string Summary { get; init; }
    public string? ArtifactReference { get; init; }
    public IReadOnlyDictionary<string, AgentOrchestrationValueEnvelope> Outputs { get; init; }
        = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal);
    public string? ExecutionRunId { get; init; }
    public string? SubSessionId { get; init; }
}

public interface IAgentOrchestrationNodeExecutor
{
    bool CanExecute(AgentOrchestrationNodeDefinition node);

    Task<AgentOrchestrationNodeExecutionResult> ExecuteAsync(
        AgentOrchestrationNodeExecutionContext context,
        CancellationToken ct);
}

/// <summary>
/// Executes the first media component on top of the existing image-generation service. Prompts and
/// image bytes stay outside the orchestration event log; the durable node projection stores only a
/// concise provider/model summary and the workspace-scoped artifact id.
/// </summary>
public sealed class ImageGenerateOrchestrationNodeExecutor(
    IImageGenerationService imageGenerationService) : IAgentOrchestrationNodeExecutor
{
    public bool CanExecute(AgentOrchestrationNodeDefinition node)
        => string.Equals(
               node.Component.ComponentType,
               AgentOrchestrationComponentTypes.ImageGenerate,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(node.Executor?.ToolId, "generate_image", StringComparison.OrdinalIgnoreCase);

    public async Task<AgentOrchestrationNodeExecutionResult> ExecuteAsync(
        AgentOrchestrationNodeExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CanExecute(context.Node))
            throw new InvalidOperationException($"Node '{context.Node.NodeId}' is not an image-generation component.");

        var prompt = ResolvePrompt(context);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("The image-generation prompt is empty.");

        var request = new ImageGenerationRequest
        {
            WorkspaceId = context.Run.WorkspaceId,
            Prompt = prompt.Trim(),
            ProviderId = GetOptionalString(context.Node.Configuration, "providerId"),
            ModelId = GetOptionalString(context.Node.Configuration, "modelId"),
            Mode = GetString(context.Node.Configuration, "mode", "default"),
            Size = GetString(context.Node.Configuration, "size", "2K"),
            Watermark = GetBoolean(context.Node.Configuration, "watermark", true),
            OutputFormat = GetString(context.Node.Configuration, "outputFormat", "png"),
            OptimizePromptMode = GetString(context.Node.Configuration, "optimizePromptMode", "standard"),
            EnableWebSearch = GetBoolean(context.Node.Configuration, "enableWebSearch", false),
            ImageCount = 1,
            ReferenceArtifactIds = ResolveReferenceArtifactIds(context),
            IdempotencyKey = $"orchestration:{context.Run.RunId}:{context.Node.NodeId}:{context.Claim.Attempt}"
        };

        var result = await imageGenerationService.GenerateAsync(request, ct);
        if (result.Artifacts.Count == 0)
            throw new InvalidOperationException("The image provider returned no artifacts.");

        return new AgentOrchestrationNodeExecutionResult
        {
            Summary = $"Generated image with {result.ProviderId}/{result.ModelId}.",
            ArtifactReference = result.Artifacts[0].ArtifactId,
            Outputs = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal)
            {
                ["images"] = new()
                {
                    DataType = AgentOrchestrationDataTypes.Artifact,
                    Artifacts = result.Artifacts.Select(artifact =>
                        new AgentOrchestrationArtifactReference
                        {
                            ArtifactId = artifact.ArtifactId,
                            ContentType = artifact.MimeType,
                            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["providerId"] = result.ProviderId,
                                ["modelId"] = result.ModelId,
                                ["width"] = artifact.Width?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                                ["height"] = artifact.Height?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                            }
                        }).ToArray()
                }
            }
        };
    }

    private static string ResolvePrompt(AgentOrchestrationNodeExecutionContext context)
    {
        return AgentOrchestrationNodeInputResolver.ResolveInlineText(context, "prompt");
    }

    private static IReadOnlyList<string> ResolveReferenceArtifactIds(
        AgentOrchestrationNodeExecutionContext context)
    {
        return AgentOrchestrationNodeInputResolver.ResolveArtifacts(context, "references")
            .Select(artifact => artifact.ArtifactId)
            .ToArray();
    }

    private static string GetString(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string key,
        string fallback)
        => GetOptionalString(configuration, key) ?? fallback;

    private static string? GetOptionalString(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string key)
        => configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;

    private static bool GetBoolean(
        IReadOnlyDictionary<string, JsonElement> configuration,
        string key,
        bool fallback)
        => configuration.TryGetValue(key, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
