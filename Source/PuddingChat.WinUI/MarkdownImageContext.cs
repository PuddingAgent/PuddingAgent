using Microsoft.UI.Xaml;

namespace PuddingChat.WinUI;

/// <summary>Workspace-scoped native resource access, supplied by the message owner.</summary>
public sealed class MarkdownImageContext(IImageAttachmentClient client, string workspace, CancellationToken lifetime, IRemoteImageSource? remote = null)
{
    public FrameworkElement? Create(string reference, string label)
    {
        var artifact = MarkdownImageReference.Resolve(reference, workspace);
        if (artifact is not null) return new ImageAttachmentView(client, workspace, artifact,
            string.IsNullOrWhiteSpace(label) ? "Agent 生成的图片" : label, lifetime, expanded: true);
        return RemoteImageReference.Resolve(reference) is { } uri
            ? new RemoteImageView(uri, label, remote ?? RemoteImageSource.Shared, lifetime) : null;
    }
}
