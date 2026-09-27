using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyMarkdownImagesAsync(Grid root, Fixture fixture)
    {
        const string id = "vision-0123456789abcdef0123456789abcdef";
        const string source = "```image\nD:\\generated\\" + id + ".png\n```";
        var context = new MarkdownImageContext(fixture, "image-workspace", CancellationToken.None);
        var reads = fixture.PreviewReads;
        var markdown = new MarkdownView(source, context) { Width = 420, VerticalAlignment = VerticalAlignment.Top };
        var preview = (ImageAttachmentView)markdown.Children.Single();
        Check(fixture.PreviewReads == reads, "generated image does not read Core before native mounting");
        Grid.SetColumnSpan(markdown, 2); root.Children.Add(markdown);
        try
        {
            await UntilAsync(() => preview.PreviewLoaded);
            Check(fixture.LastPreview == ("image-workspace", id), "image fence resolves artifact in owning workspace rather than opening supplied path");
            markdown.Update(source + "\n\n流式追加");
            Check(ReferenceEquals(preview, markdown.Children[0]) && preview.PreviewLoaded, "stream append retains decoded generated image");
            ((Expander)preview.Content).IsExpanded = false;
            await UntilAsync(() => !preview.PreviewLoaded);
            ((Expander)preview.Content).IsExpanded = true;
            await UntilAsync(() => preview.PreviewLoaded);
            Check(fixture.PreviewReads == reads + 2, "image collapse releases bitmap and expansion reloads it");
            root.Children.Remove(markdown); await UntilAsync(() => !preview.PreviewLoaded);
            root.Children.Add(markdown); await UntilAsync(() => preview.PreviewLoaded);
            Check(fixture.PreviewReads == reads + 3, "reloaded native image restores expanded preview after visual recycling");
            markdown.Update("![生成图](" + id + ")");
            var rich = (RichTextBlock)markdown.Children.Single();
            var inline = (ImageAttachmentView)((Paragraph)rich.Blocks.Single()).Inlines.OfType<InlineUIContainer>().Single().Child;
            await UntilAsync(() => inline.PreviewLoaded);
            Check(((Expander)inline.Content).Header?.ToString() == "生成图", "Markdown image syntax uses native preview and alt label");
            markdown.Update("![生成图](" + id + ")\n\n更多文字");
            Check(ReferenceEquals(rich, markdown.Children[0]) && inline.PreviewLoaded, "ordinary Markdown images retain native controls across streaming appends");
            markdown.Update("![生成图][picture]\n\n[picture]: " + id);
            var firstReference = markdown.Children[0];
            markdown.Update("![生成图][picture]\n\n[picture]: vision-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Check(!ReferenceEquals(firstReference, markdown.Children[0]), "changing reference definition invalidates cached image target");
            reads = fixture.PreviewReads;
            markdown.Update("![远程图](https://example.invalid/" + id + ".png)");
            var fallback = (Paragraph)((RichTextBlock)markdown.Children.Single()).Blocks.Single();
            Check(fallback.Inlines.OfType<InlineUIContainer>().Single().Child is RemoteImageView remote
                && !((Expander)remote.Content).IsExpanded && fixture.PreviewReads == reads,
                "remote image offers an initially collapsed native preview without a Core artifact read");
        }
        finally { root.Children.Remove(markdown); }

        using var card = new MessageCard(new ChatMessage("generated", null, "assistant", "角色", DateTimeOffset.UtcNow, source, "completed", []),
            imageClient: fixture, workspace: "message-workspace");
        root.Children.Add(card);
        try
        {
            await UntilAsync(() => Descendants<ImageAttachmentView>(card).Any(image => image.PreviewLoaded));
            Check(fixture.LastPreview == ("message-workspace", id), "message card passes its workspace resource context into Markdown");
        }
        finally { root.Children.Remove(card); }
        var flow = new TurnContentView { Images = context };
        flow.Update([new ProcessItem("thinking-image", "thinking", "running", source, 1)], "");
        root.Children.Add(flow);
        try
        {
            await UntilAsync(() => Descendants<ImageAttachmentView>(flow).Any(image => image.PreviewLoaded));
            Check(fixture.LastPreview == ("image-workspace", id), "expanded execution activity receives the same workspace resource context");
        }
        finally { root.Children.Remove(flow); }
    }
}
