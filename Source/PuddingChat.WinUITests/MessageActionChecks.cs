using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;
using Windows.ApplicationModel.DataTransfer;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyMessageActionsAsync(Grid root)
    {
        var fixture = new SpeechFixture(); using var session = new SpeechPlaybackSession(fixture, fixture);
        var message = new ChatMessage("actions", null, "assistant", "编码助手", DateTimeOffset.UtcNow, "原始回复", "done", []);
        using var card = new MessageCard(message, speech: session, speechRole: new("workspace", "role")) { Width = 320 };
        root.Children.Add(card);
        try
        {
            await UntilAsync(() => card.IsLoaded); await NextVisualFrameAsync(); root.UpdateLayout();
            var copy = Descendants<Button>(card).Single(b => b.Content?.ToString() == "复制");
            var speech = Descendants<SpeechPlaybackButton>(card).Single();
            var speak = Descendants<Button>(speech).Single();
            Check(Descendants<InfoBar>(card).All(bar => bar.Visibility == Visibility.Collapsed)
                && ((StackPanel)((Border)card.Content).Child).Children.OfType<StackPanel>()
                    .Where(panel => panel is not TurnContentView).All(panel => panel.Visibility == Visibility.Collapsed),
                "empty attachments and successful outcome reserve no message space");
            Check(Math.Abs(copy.TransformToVisual(card).TransformPoint(new()).Y - speak.TransformToVisual(card).TransformPoint(new()).Y) < 1,
                "message copy and speech actions share a row in a narrow card");
            card.CopyText(_ => throw new InvalidOperationException("clipboard busy")); root.UpdateLayout();
            Check(copy.Content?.ToString() == "重试复制"
                && Descendants<TextBlock>(card).Any(t => t.Visibility == Visibility.Visible && t.Text.Contains("无法写入剪贴板")),
                "clipboard failure is contained and offers visible retry");
            DataPackage? copied = null; card.CopyText(data => copied = data);
            Check(copy.Content?.ToString() == "已复制" && await copied!.GetView().GetTextAsync() == message.Content,
                "copy retry publishes exact message text and reports success");
            card.Update(message with { Content = "更新后的回复" }); card.CopyText(data => copied = data);
            Check(await copied!.GetView().GetTextAsync() == "更新后的回复",
                "message copy reads latest streamed content rather than constructor snapshot");
            fixture.Synthesized.SetException(new IOException("fixture failure")); await speech.ToggleAsync();
            await NextVisualFrameAsync(); root.UpdateLayout();
            Check(speak.Content?.ToString() == "重试朗读" && Descendants<TextBlock>(speech).Where(t => t.Visibility == Visibility.Visible)
                .All(t => t.TransformToVisual(card).TransformPoint(new()).X + t.ActualWidth <= card.ActualWidth + 1),
                $"speech failure wraps inside remaining toolbar width; action={speak.Content}, cardWidth={card.ActualWidth}, speechWidth={speech.ActualWidth}");
            card.Dispose(); var published = false; card.CopyText(_ => published = true);
            Check(!published && !copy.IsEnabled, "recycled message action cannot write stale clipboard text");
        }
        finally { root.Children.Remove(card); }
    }
}
