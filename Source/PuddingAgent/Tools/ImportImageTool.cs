using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingAgent.Tools;

[Tool(
    id: "import_image",
    name: "Import image",
    description:
        "Download one public HTTPS image URL, verify it, and save it as a workspace Vision Artifact. " +
        "Use an image URL returned by doubao_search, then pass the exact artifactId to generate_image for reference editing " +
        "or use the returned localPath inside an image Markdown fence.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.RequiresNetwork,
    SortOrder = 18)]
public sealed class ImportImageTool(
    RemoteImageArtifactImportService importer,
    ILogger<ImportImageTool> logger)
    : PuddingToolBase<ImportImageArgs>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ImportImageArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var url = args.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return ToolExecutionResult.Fail("url is required.");

        try
        {
            var result = await importer.ImportAsync(
                context.WorkspaceId,
                url,
                ct);
            logger.LogInformation(
                "[ImportImageTool] Imported workspace={WorkspaceId} artifact={ArtifactId} reused={Reused}",
                context.WorkspaceId,
                result.ArtifactId,
                result.Reused);
            return ToolExecutionResult.Ok(
                JsonSerializer.Serialize(
                    new
                    {
                        artifactId = result.ArtifactId,
                        localPath = result.LocalPath,
                        mimeType = result.MimeType,
                        byteCount = result.ByteCount,
                        reused = result.Reused,
                        next =
                            "For image-to-image editing, call generate_image with mode=precision and referenceArtifactIds containing this artifactId. " +
                            "To display the existing image without editing, use send_image in a Feishu turn or put localPath in an image fence.",
                    },
                    JsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[ImportImageTool] Import failed workspace={WorkspaceId}",
                context.WorkspaceId);
            return ToolExecutionResult.Fail(ex.Message);
        }
    }
}

public sealed record ImportImageArgs
{
    [ToolParam("Public HTTPS image URL, typically copied exactly from a doubao_search Image result.")]
    public string? Url { get; init; }
}
