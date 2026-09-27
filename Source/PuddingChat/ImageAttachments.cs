namespace PuddingChat;

public sealed record AttachedImage(string ArtifactId, string Name, string MimeType, int? Width, int? Height);
/// <summary>A Core-resolved local file for native preview only; never part of a submitted message.</summary>
public sealed record ImagePreview(string LocalPath, string MimeType, int? Width, int? Height);
public interface IImageAttachmentClient
{
    int MaxImagesPerMessage { get; }
    Task<AttachedImage> ImportImageAsync(RoleKey role, string localPath, CancellationToken ct);
    Task<ImagePreview> GetImagePreviewAsync(string workspace, string artifactId, CancellationToken ct);
}
