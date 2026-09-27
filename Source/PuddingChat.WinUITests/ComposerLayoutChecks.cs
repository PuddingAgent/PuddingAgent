using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;
using PuddingChat;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyComposerLayoutAsync(Grid root)
    {
        var composer = new ChatComposer { Width = 320, VerticalAlignment = VerticalAlignment.Top, Draft = "保留多行草稿\n继续工作" };
        var device = new VoiceInputFixture(); await using var voice = new VoiceInputSession(device, device);
        using var input = new VoiceInputControl(voice, () => new(new("workspace", "role"), 0, composer.Draft), _ => true);
        composer.SetVoiceInput(input);
        composer.SetAvailability(true, true, true);
        var draft = composer.Draft;
        var longName = new string('图', 200) + ".png";
        composer.SetImages([new("image", longName, "image/png", 1, 1)]);
        Grid.SetColumnSpan(composer, 2); root.Children.Add(composer);
        try
        {
            await UntilAsync(() => composer.IsLoaded); root.UpdateLayout();
            var panel = (StackPanel)((Border)composer.Content).Child;
            var toolbar = panel.Children.OfType<Grid>().Single();
            var groups = toolbar.Children.OfType<StackPanel>().ToArray();
            static bool Within(FrameworkElement item, FrameworkElement host)
            {
                var left = item.TransformToVisual(host).TransformPoint(new()).X;
                return item.ActualWidth > 0 && left >= -1 && left + item.ActualWidth <= host.ActualWidth + 1;
            }
            var buttons = groups.SelectMany(g => g.Children.OfType<Button>()).ToArray();
            Check(Grid.GetRow(groups[1]) == 1 && buttons.All(b => Within(b, composer)),
                "320 DIP composer keeps attachment, stop and retry buttons inside its bounds");
            var images = (StackPanel)panel.Children.OfType<ScrollViewer>().Single().Content;
            var imageRow = (Grid)images.Children.Single();
            Check(Within(imageRow.Children.OfType<Button>().Single(), composer)
                && imageRow.Children.OfType<TextBlock>().Single().Text == longName,
                "long attachment name keeps full value while remove stays visible");
            composer.Width = 520; root.UpdateLayout(); await Task.Delay(30); root.UpdateLayout();
            Check(Grid.GetRow(groups[1]) == 1 && buttons.All(b => Within(b, composer)),
                "voice-enabled composer wraps before the additional action crowds attachment buttons");
            composer.Width = 900; root.UpdateLayout(); await Task.Delay(30); root.UpdateLayout();
            Check(Grid.GetRow(groups[1]) == 0 && buttons.All(b => Within(b, composer)),
                "wide composer restores a single toolbar row");
            composer.Width = 360; root.UpdateLayout(); await Task.Delay(30); root.UpdateLayout();
            Check(Grid.GetRow(groups[1]) == 1 && composer.Draft == draft && composer.ImageCount == 1 && buttons.All(b => b.IsEnabled),
                "resizing preserves draft, button identity and enabled state");
            var withAttachment = composer.ActualHeight;
            composer.SetImages([]); root.UpdateLayout();
            Check(panel.Children.OfType<ScrollViewer>().Single().Visibility == Visibility.Collapsed
                && panel.Children.OfType<InfoBar>().Single().Visibility == Visibility.Collapsed
                && composer.ActualHeight < withAttachment - 20, "empty attachments and closed errors do not reserve composer space");
        }
        finally { root.Children.Remove(composer); }
    }
}
