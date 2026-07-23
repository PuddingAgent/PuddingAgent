using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingAgent.Tools;

/// <summary>
/// Reads a local image through a configured vision-capable model and returns a
/// textual observation to the calling Agent. The main Agent keeps its own model;
/// this tool is the explicit fallback when that model is text-only.
/// </summary>
[Tool(
    id: "image_reader",
    name: "Image Reader",
    description: "Analyze a local PNG/JPEG/WebP image and return a textual description. Use this when an attached-image notice contains a local path and the current model cannot inspect the image directly.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 35)]
public sealed class ImageReaderTool(
    VisionArtifactStorageService artifactStorage,
    IVisualArtifactLocalFileResolver localFileResolver,
    ILlmResolver llmResolver,
    ILlmInvocationService invocationService,
    ILogger<ImageReaderTool> logger) : PuddingToolBase<ImageReaderArgs>
{
    private const long MaxImageBytes = 20L * 1024 * 1024;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ImageReaderArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var path = args.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return ToolExecutionResult.Fail("path is required.");
        if (!Path.IsPathFullyQualified(path))
            return ToolExecutionResult.Fail("path must be an absolute local file path.");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return ToolExecutionResult.Fail($"Image file does not exist: {fullPath}");

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length <= 0 || fileInfo.Length > MaxImageBytes)
            return ToolExecutionResult.Fail("Image must be between 1 byte and 20 MB.");

        var mimeType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null,
        };
        if (mimeType is null)
            return ToolExecutionResult.Fail("Unsupported image type. Use PNG, JPEG, or WebP.");

        var artifactId = await ResolveOrImportArtifactAsync(
            context.WorkspaceId,
            fullPath,
            mimeType,
            ct);
        var route = await llmResolver.ResolveRouteAsync(
            requiredCapabilityTags: ["vision"],
            ct: ct);
        var prompt = string.IsNullOrWhiteSpace(args.Prompt)
            ? "Describe the image accurately. Include visible text and important details. Do not infer anything that is not visible."
            : args.Prompt.Trim();

        logger.LogInformation(
            "[ImageReader] Analyze file={FileName} provider={ProviderId} model={ModelId}",
            Path.GetFileName(fullPath),
            route.ProviderId,
            route.ModelId);

        var result = await invocationService.InvokeAsync(new LlmInvocationRequest
        {
            WorkspaceId = context.WorkspaceId,
            SessionId = context.SessionId,
            AgentInstanceId = context.AgentInstanceId,
            AgentTemplateId = context.AgentTemplateId ?? "system:image-reader",
            Profile = new LlmInvocationProfile
            {
                ProviderId = route.ProviderId,
                ProfileId = $"tool:image_reader:{route.ProviderId}/{route.ModelId}",
                ModelId = route.ModelId,
                Role = "conscious",
            },
            ConfigOverride = route.Config,
            Messages =
            [
                new ChatMessage(
                    ChatRole.User,
                    prompt,
                    VisualArtifactIds: [artifactId]),
            ],
            Trace = context.Trace,
        }, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.ReplyText))
            return ToolExecutionResult.Fail(result.Error ?? "The vision model returned no description.");

        return ToolExecutionResult.Ok(result.ReplyText.Trim());
    }

    private async Task<string> ResolveOrImportArtifactAsync(
        string workspaceId,
        string fullPath,
        string mimeType,
        CancellationToken ct)
    {
        var candidateId = Path.GetFileNameWithoutExtension(fullPath);
        var existing = await localFileResolver.ResolveLocalFileAsync(
            workspaceId,
            candidateId,
            ct);
        if (existing is not null
            && string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return existing.ArtifactId;
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var uploaded = await artifactStorage.SaveAsync(
            workspaceId,
            stream,
            mimeType,
            capturedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct: ct);
        return uploaded.ArtifactId;
    }
}

public sealed record ImageReaderArgs
{
    [ToolParam("Absolute path to a local PNG, JPEG, or WebP image")]
    public string? Path { get; init; }

    [ToolParam("Question or analysis instruction for the image")]
    public string? Prompt { get; init; }
}
