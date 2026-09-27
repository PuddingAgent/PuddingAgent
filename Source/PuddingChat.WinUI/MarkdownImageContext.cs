namespace PuddingChat.WinUI;

/// <summary>Workspace-scoped native resource access, supplied by the message owner.</summary>
public sealed class MarkdownImageContext(IImageAttachmentClient client, string workspace, CancellationToken lifetime)
{
    public ImageAttachmentView? Create(string reference, string label)
    {
        var artifact = MarkdownImageReference.Resolve(reference, workspace);
        return artifact is null ? null : new ImageAttachmentView(client, workspace, artifact,
            string.IsNullOrWhiteSpace(label) ? "Agent 生成的图片" : label, lifetime, expanded: true);
    }
}
