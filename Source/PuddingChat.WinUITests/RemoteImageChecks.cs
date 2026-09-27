using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class ImageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class NonSeekableImageStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class RemoteImageFixture(byte[] bytes) : IRemoteImageSource
    {
        public int Reads;
        public TaskCompletionSource<RemoteImageData>? Pending;
        public CancellationToken Token;
        public Task<RemoteImageData> LoadAsync(Uri uri, CancellationToken ct)
        { Reads++; Token = ct; return Pending?.Task ?? Task.FromResult(new RemoteImageData(bytes, "image/png")); }
    }
    private static async Task VerifyRemoteImagesAsync(Grid root, Fixture fixture)
    {
        var file = await fixture.GetImagePreviewAsync("image-workspace", "fixture", CancellationToken.None);
        var png = await File.ReadAllBytesAsync(file.LocalPath);
        var uri = new Uri("https://images.example.test/image.png");
        static HttpResponseMessage Response(HttpContent body)
        { body.Headers.ContentType = new MediaTypeHeaderValue("image/png"); return new(HttpStatusCode.OK) { Content = body }; }
        using (var http = new HttpClient(new ImageHandler((request, _) =>
        {
            if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie")) throw new InvalidOperationException("unexpected credentials");
            return Task.FromResult(Response(new ByteArrayContent(png)));
        })))
            Check((await new RemoteImageSource(http).LoadAsync(uri, CancellationToken.None)).Bytes.SequenceEqual(png), "bounded remote image fetch preserves bytes without application credentials");
        var huge = new byte[RemoteImageData.MaxBytes + 1];
        foreach (var streamed in new[] { false, true })
        {
            using var http = new HttpClient(new ImageHandler((_, _) => Task.FromResult(Response(streamed
                ? new StreamContent(new NonSeekableImageStream(huge)) : new ByteArrayContent(huge)))));
            var rejected = false;
            try { await new RemoteImageSource(http).LoadAsync(uri, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, streamed ? "streamed image size limit does not trust Content-Length" : "oversized declared image is rejected");
        }
        var redirects = 0;
        using (var http = new HttpClient(new ImageHandler((_, _) =>
        {
            redirects++; var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://user:password@images.example.test/private.png"); return Task.FromResult(response);
        })))
        {
            var rejected = false;
            try { await new RemoteImageSource(http).LoadAsync(uri, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && redirects == 1, "redirect cannot introduce embedded credentials");
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancel = new CancellationTokenSource())
        using (var http = new HttpClient(new ImageHandler(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(new ByteArrayContent(png)); })))
        {
            var pending = new RemoteImageSource(http).LoadAsync(uri, cancel.Token); await started.Task; cancel.Cancel();
            var cancelled = false; try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "remote image request follows view cancellation");
        }
        var source = new RemoteImageFixture(png);
        var markdownText = $"![示例]({uri})";
        var markdown = new MarkdownView(markdownText, new MarkdownImageContext(fixture, "image-workspace", CancellationToken.None, source)) { Width = 320 };
        var rich = (RichTextBlock)markdown.Children.Single();
        var view = (RemoteImageView)((Paragraph)rich.Blocks.Single()).Inlines.OfType<InlineUIContainer>().Single().Child;
        root.Children.Add(markdown);
        try
        {
            await UntilAsync(() => view.IsLoaded); await NextVisualFrameAsync();
            Check(source.Reads == 0, "remote image makes no request before explicit expansion");
            var expander = (Expander)view.Content; expander.IsExpanded = true;
            await UntilAsync(() => view.PreviewLoaded);
            Check(source.Reads == 1, "expanded external image decodes in native image control");
            markdown.Update(markdownText + "\n\n追加文本");
            Check(ReferenceEquals(rich, markdown.Children[0]) && view.PreviewLoaded && source.Reads == 1,
                "streaming Markdown append preserves remote preview and does not refetch it");
            expander.IsExpanded = false; source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); expander.IsExpanded = true;
            await UntilAsync(() => source.Reads == 2);
            expander.IsExpanded = false; source.Pending.SetResult(new(png, "image/png"));
            await NextVisualFrameAsync();
            Check(source.Token.IsCancellationRequested && !view.PreviewLoaded, "collapse cancels remote preview and rejects late decoded content");
        }
        finally { root.Children.Remove(markdown); }
    }
}
