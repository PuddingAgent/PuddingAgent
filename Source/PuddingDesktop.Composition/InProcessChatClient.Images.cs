using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Core;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : IImageAttachmentClient
{
    public int MaxImagesPerMessage => ConversationContentValidator.MaxImagesPerTurn;
    public Task<AttachedImage> ImportImageAsync(RoleKey role, string localPath, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        if (!Path.IsPathFullyQualified(localPath)) throw new ArgumentException("请选择图片文件的完整路径。");
        if (!await services.GetRequiredService<PlatformDbContext>().Workspaces.AsNoTracking().AnyAsync(w => w.WorkspaceId == role.WorkspaceId, token))
            throw new InvalidOperationException("工作空间不存在。");
        var agent = await services.GetRequiredService<WorkspaceAgentFileService>().GetAgentAsync(role.WorkspaceId, role.AgentId, token);
        if (agent is null || !agent.IsEnabled || agent.IsFrozen) throw new InvalidOperationException("角色不存在、已停用或冻结。");
        var mime = Path.GetExtension(localPath).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
            ".gif" => "image/gif", ".bmp" => "image/bmp", _ => throw new ArgumentException("请选择 PNG、JPEG、WebP、GIF 或 BMP 图片。")
        };
        await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is <= 0 or > VisionImageInspector.MaxCanonicalImageBytes) throw new ArgumentException("图片为空或超过 64 MiB。");
        // Core inspects actual bytes and owns canonical storage; neither extension nor UI dimensions are trusted.
        var artifact = await services.GetRequiredService<VisionArtifactStorageService>().SaveAsync(role.WorkspaceId, source, mime, ct: token);
        return new AttachedImage(artifact.ArtifactId, Path.GetFileName(localPath), artifact.MimeType, artifact.Width, artifact.Height);
    }, ct);

    public Task<ImagePreview> GetImagePreviewAsync(string workspace, string artifactId, CancellationToken ct) => ExecuteAsync(async (services, token) =>
    {
        if (!await services.GetRequiredService<PlatformDbContext>().Workspaces.AsNoTracking().AnyAsync(w => w.WorkspaceId == workspace, token))
            throw new InvalidOperationException("工作空间不存在。");
        var file = await services.GetRequiredService<VisionArtifactStorageService>().ResolveLocalFileAsync(workspace, artifactId, token)
            ?? throw new FileNotFoundException("图片不存在或已清理。");
        return new ImagePreview(file.Path, file.MimeType, file.Width, file.Height);
    }, ct);
}
