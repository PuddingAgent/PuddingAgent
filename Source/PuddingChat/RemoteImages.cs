namespace PuddingChat;

public sealed record RemoteImageData(byte[] Bytes, string MimeType)
{
    public const int MaxBytes = 8 * 1024 * 1024;
}
public interface IRemoteImageSource
{
    Task<RemoteImageData> LoadAsync(Uri uri, CancellationToken ct);
}
public static class RemoteImageReference
{
    public static Uri? Resolve(string reference) => reference.Length <= 4096
        && Uri.TryCreate(reference, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo)
        ? uri : null;
}
