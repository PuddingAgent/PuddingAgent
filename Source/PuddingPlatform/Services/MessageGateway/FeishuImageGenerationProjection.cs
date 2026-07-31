using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.MessageGateway;

public sealed record FeishuImageGenerationProjectionResult(
    bool HasDirective,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> MessageIds,
    int FailedBlocks);

/// <summary>
/// Executes explicit final-reply ImageGeneration fences and queues the resulting
/// workspace artifacts as durable Feishu image deliveries.
/// </summary>
public static class FeishuImageGenerationProjection
{
    public static async Task<FeishuImageGenerationProjectionResult> QueueAsync(
        IImageGenerationService imageGeneration,
        IMessageSystem messageSystem,
        ILogger logger,
        string commandStatus,
        string content,
        string stableSourceId,
        string workspaceId,
        string agentInstanceId,
        string connectorId,
        string conversationId,
        string turnId,
        string? externalMessageId,
        string externalConversationId,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
    {
        if (!string.Equals(
                commandStatus,
                "succeeded",
                StringComparison.Ordinal)
            || IsTrue(Get(
                metadata,
                MessageGatewayMetadata.ImageToolSuppressDirective)))
        {
            return Empty();
        }

        var directive = AgentReplyImageGenerationDirective.Parse(content);
        if (!directive.HasImageGeneration)
            return Empty();

        var artifacts = new List<string>();
        var messages = new List<string>();
        var failures = 0;
        foreach (var item in directive.Items)
        {
            try
            {
                var generated = await imageGeneration.GenerateAsync(
                    new ImageGenerationRequest
                    {
                        WorkspaceId = workspaceId,
                        Prompt = item.Prompt,
                        Mode = item.Mode,
                        Size = item.Size,
                        Watermark = item.Watermark,
                        OutputFormat = item.OutputFormat,
                        OptimizePromptMode = item.OptimizePromptMode,
                        EnableWebSearch = item.EnableWebSearch,
                        ImageCount = item.ImageCount,
                        ReferenceArtifactIds = item.ReferenceArtifactIds,
                        IdempotencyKey =
                            $"{stableSourceId}:image-generation:{item.Index}",
                    },
                    ct);
                for (var imageIndex = 0;
                     imageIndex < generated.Artifacts.Count;
                     imageIndex++)
                {
                    var artifact = generated.Artifacts[imageIndex];
                    var messageId = await FeishuImageProjection.QueueAsync(
                        messageSystem,
                        $"{stableSourceId}:image-generation:{item.Index}:{imageIndex}",
                        workspaceId,
                        agentInstanceId,
                        connectorId,
                        conversationId,
                        turnId,
                        externalMessageId,
                        externalConversationId,
                        artifact.ArtifactId,
                        metadata,
                        ct);
                    artifacts.Add(artifact.ArtifactId);
                    messages.Add(messageId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                logger.LogWarning(
                    ex,
                    "[ImageGenerationDirective] Block failed source={StableSourceId} block={BlockIndex}",
                    stableSourceId,
                    item.Index);
            }
        }

        logger.LogInformation(
            "[ImageGenerationDirective] Projected source={StableSourceId} blocks={BlockCount} artifacts={ArtifactCount} messages={MessageCount} failures={FailureCount}",
            stableSourceId,
            directive.Items.Count,
            artifacts.Count,
            messages.Count,
            failures);
        return new FeishuImageGenerationProjectionResult(
            HasDirective: true,
            artifacts,
            messages,
            failures);
    }

    private static FeishuImageGenerationProjectionResult Empty()
        => new(
            HasDirective: false,
            ArtifactIds: [],
            MessageIds: [],
            FailedBlocks: 0);

    private static string? Get(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "1", StringComparison.Ordinal);
}
