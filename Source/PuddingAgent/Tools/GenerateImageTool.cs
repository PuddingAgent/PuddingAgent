using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingAgent.Tools;

[Tool(
    id: "generate_image",
    name: "Generate image",
    description:
        "Generate or edit images and store them as workspace Vision Artifacts. " +
        "Use mode=precision with referenceArtifactIds for Seedream precise edits, including normalized <point>/<bbox> coordinates; " +
        "use mode=sequence and imageCount 2-4 for a coherent image series. " +
        "Call send_image once for every returned artifactId in the current Feishu turn.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresNetwork,
    SortOrder = 19)]
public sealed class GenerateImageTool(
    IImageGenerationService imageGeneration,
    ILogger<GenerateImageTool> logger)
    : PuddingToolBase<GenerateImageArgs>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GenerateImageArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var prompt = args.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolExecutionResult.Fail("prompt is required.");

        try
        {
            var result = await imageGeneration.GenerateAsync(
                new ImageGenerationRequest
                {
                    WorkspaceId = context.WorkspaceId,
                    Prompt = prompt,
                    ProviderId = args.ProviderId,
                    ModelId = args.ModelId,
                    Mode = string.IsNullOrWhiteSpace(args.Mode)
                        ? "default"
                        : args.Mode.Trim(),
                    Size = string.IsNullOrWhiteSpace(args.Size)
                        ? "2K"
                        : args.Size.Trim(),
                    Watermark = args.Watermark ?? true,
                    OutputFormat = string.IsNullOrWhiteSpace(args.OutputFormat)
                        ? "png"
                        : args.OutputFormat.Trim(),
                    OptimizePromptMode =
                        string.IsNullOrWhiteSpace(args.OptimizePromptMode)
                            ? "standard"
                            : args.OptimizePromptMode.Trim(),
                    EnableWebSearch = args.EnableWebSearch ?? false,
                    ImageCount = args.ImageCount ?? 1,
                    ReferenceArtifactIds =
                        args.ReferenceArtifactIds?
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item.Trim())
                            .ToList()
                        ?? [],
                },
                ct);
            logger.LogInformation(
                "[GenerateImageTool] Generated workspace={WorkspaceId} artifact={ArtifactId} provider={ProviderId} model={ModelId}",
                context.WorkspaceId,
                result.ArtifactId,
                result.ProviderId,
                result.ModelId);
            return ToolExecutionResult.Ok(
                JsonSerializer.Serialize(
                    new
                    {
                        artifactId = result.ArtifactId,
                        artifactIds = result.Artifacts
                            .Select(item => item.ArtifactId)
                            .ToList(),
                        artifacts = result.Artifacts,
                        mimeType = result.MimeType,
                        providerId = result.ProviderId,
                        modelId = result.ModelId,
                        next = "Call send_image once for every artifactId to send all generated images to the current Feishu conversation.",
                    },
                    JsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[GenerateImageTool] Generation failed workspace={WorkspaceId}",
                context.WorkspaceId);
            return ToolExecutionResult.Fail(ex.Message);
        }
    }
}

public sealed record GenerateImageArgs
{
    [ToolParam("Natural-language description of the image to generate.")]
    public string? Prompt { get; init; }

    [ToolParam("Provider-neutral mode: default, precision (reference-image editing/position control), or sequence (coherent multi-image output).")]
    public string? Mode { get; init; }

    [ToolParam("Image size tier or exact dimensions, for example 2K or 2048x2048. Defaults to 2K; supported values depend on the selected model.")]
    public string? Size { get; init; }

    [ToolParam("Whether the generated image includes the provider watermark. Defaults to true.")]
    public bool? Watermark { get; init; }

    [ToolParam("Output format: png or jpeg. Defaults to png.")]
    public string? OutputFormat { get; init; }

    [ToolParam("Prompt optimization: standard or fast. Seedream 5.0 Lite supports standard only.")]
    public string? OptimizePromptMode { get; init; }

    [ToolParam("Enable current-information web search when the selected model supports it. Seedream 5.0 Lite supports this; Pro does not.")]
    public bool? EnableWebSearch { get; init; }

    [ToolParam("Number of images to generate, 1-4. Values above 1 require a sequence-capable model.")]
    public int? ImageCount { get; init; }

    [ToolParam("Optional workspace Vision Artifact ids used as reference images. Copy exact vision-* ids from the attached-image notice; up to 10.")]
    public List<string>? ReferenceArtifactIds { get; init; }

    [ToolParam("Optional provider id. Must be supplied together with model id.")]
    public string? ProviderId { get; init; }

    [ToolParam("Optional image model id. Must be supplied together with provider id.")]
    public string? ModelId { get; init; }
}
