using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.MessageGateway;

public sealed record ResolvedAgentReplyImage(
    AgentReplyImageItem Directive,
    string ArtifactId);

public sealed record FeishuImageArtifactProjectionPlan(
    bool HasDirective,
    bool IsPureImage,
    bool Suppressed,
    string? TextContent,
    IReadOnlyList<ResolvedAgentReplyImage> Images,
    int FailedImages);

public sealed record FeishuImageArtifactProjectionResult(
    bool HasDirective,
    bool IsPureImage,
    IReadOnlyList<string> MessageIds,
    int FailedImages);

/// <summary>
/// Resolves explicit image fences only to artifacts owned by the current
/// workspace and queues them as durable Feishu image deliveries.
/// </summary>
public static class FeishuImageArtifactProjection
{
    public static async Task<FeishuImageArtifactProjectionPlan> CreatePlanAsync(
        VisionArtifactStorageService storage,
        ILogger logger,
        string workspaceId,
        string content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
    {
        var suppressed = IsTrue(Get(
            metadata,
            MessageGatewayMetadata.ImageToolSuppressDirective));

        var directive = AgentReplyImageDirective.Parse(content);
        if (!directive.HasImages)
        {
            return Empty(
                suppressed ? content : StripImageGenerationBlocks(content));
        }

        var resolved = new List<ResolvedAgentReplyImage>();
        var failed = 0;
        foreach (var item in directive.Items)
        {
            var local = await ResolveAuthorizedAsync(
                storage,
                workspaceId,
                item.Reference,
                ct);
            if (local is null)
            {
                failed++;
                logger.LogWarning(
                    "[ImageDirective] Rejected workspace={WorkspaceId} reference={Reference}",
                    workspaceId,
                    item.Reference);
                continue;
            }
            resolved.Add(new ResolvedAgentReplyImage(item, local.ArtifactId));
        }

        var text = RemoveResolvedBlocks(content, resolved);
        if (!suppressed)
            text = StripImageGenerationBlocks(text);
        return new FeishuImageArtifactProjectionPlan(
            HasDirective: true,
            IsPureImage:
                directive.IsPureImage
                && resolved.Count == 1
                && failed == 0,
            Suppressed: suppressed,
            TextContent: string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            Images: resolved,
            FailedImages: failed);
    }

    public static async Task<FeishuImageArtifactProjectionResult> QueueAsync(
        FeishuImageArtifactProjectionPlan plan,
        IMessageSystem messageSystem,
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
        if (!plan.HasDirective || plan.Suppressed)
        {
            return new FeishuImageArtifactProjectionResult(
                plan.HasDirective,
                plan.IsPureImage,
                [],
                plan.FailedImages);
        }

        var messages = new List<string>(plan.Images.Count);
        foreach (var image in plan.Images)
        {
            messages.Add(await FeishuImageProjection.QueueAsync(
                messageSystem,
                $"{stableSourceId}:image:{image.Directive.Index}",
                workspaceId,
                agentInstanceId,
                connectorId,
                conversationId,
                turnId,
                externalMessageId,
                externalConversationId,
                image.ArtifactId,
                metadata,
                ct));
        }
        return new FeishuImageArtifactProjectionResult(
            HasDirective: true,
            plan.IsPureImage,
            messages,
            plan.FailedImages);
    }

    private static async Task<VisualArtifactLocalFile?> ResolveAuthorizedAsync(
        VisionArtifactStorageService storage,
        string workspaceId,
        string reference,
        CancellationToken ct)
    {
        var artifactId = reference.StartsWith(
                "vision-",
                StringComparison.OrdinalIgnoreCase)
            ? reference.ToLowerInvariant()
            : Path.GetFileNameWithoutExtension(reference).ToLowerInvariant();
        var local = await storage.ResolveLocalFileAsync(
            workspaceId,
            artifactId,
            ct);
        if (local is null)
            return null;
        if (string.Equals(reference, artifactId, StringComparison.OrdinalIgnoreCase))
            return local;

        try
        {
            return string.Equals(
                    Path.GetFullPath(reference),
                    Path.GetFullPath(local.Path),
                    StringComparison.OrdinalIgnoreCase)
                ? local
                : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }

    private static string RemoveResolvedBlocks(
        string content,
        IReadOnlyList<ResolvedAgentReplyImage> images)
    {
        var result = content;
        foreach (var item in images
                     .OrderByDescending(image => image.Directive.MatchIndex))
        {
            result = result.Remove(
                item.Directive.MatchIndex,
                item.Directive.MatchLength);
        }
        return result;
    }

    private static string StripImageGenerationBlocks(string content)
        => AgentReplyImageGenerationDirective.StripBlocks(content);

    private static FeishuImageArtifactProjectionPlan Empty(string content)
        => new(
            HasDirective: false,
            IsPureImage: false,
            Suppressed: false,
            TextContent: content,
            Images: [],
            FailedImages: 0);

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
