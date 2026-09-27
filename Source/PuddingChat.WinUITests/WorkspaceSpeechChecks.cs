using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class WorkspaceSpeechPlayer : ISpeechAudioPlayer
    {
        public int Plays;
        public bool Disposed;
        public CancellationToken Token;
        public Task PlayAsync(SpeechAudio audio, CancellationToken ct) { Plays++; Token = ct; return Task.Delay(Timeout.Infinite, ct); }
        public void Dispose() => Disposed = true;
    }
    private async Task VerifyWorkspaceSpeechAsync(Grid root)
    {
        var fixture = new Fixture(Path.ChangeExtension(Report, ".speech.png")) { Terminal = true,
            Sent = PendingSend.Create(new("test", "builder"), "session", "test") };
        var player = new WorkspaceSpeechPlayer();
        using var view = new ChatWorkspace(fixture, speechPlayer: player);
        Grid.SetColumnSpan(view, 2); root.Children.Add(view);
        async Task<SpeechPlaybackButton> Button()
        {
            view.ScrollToLatest(); root.UpdateLayout();
            await NextVisualFrameAsync();
            await UntilAsync(() => Descendants<SpeechPlaybackButton>(view).Any(button => button.IsLoaded && button.Visibility == Visibility.Visible && ((StackPanel)button.Content).Children.OfType<Button>().Single().IsEnabled));
            return Descendants<SpeechPlaybackButton>(view).First(button => button.IsLoaded && button.Visibility == Visibility.Visible && ((StackPanel)button.Content).Children.OfType<Button>().Single().IsEnabled);
        }
        try
        {
            await view.InitializeAsync(); await view.SelectRoleAsync("test", fixture.Builder);
            var pending = (await Button()).ToggleAsync();
            await UntilAsync(() => player.Plays == 1);
            Check(fixture.LastSpeech?.Role == new RoleKey("test", "builder") && fixture.LastSpeech.MessageId == "a",
                "product message card routes explicit speech with role and message identity");
            await view.SelectRoleAsync("test", fixture.Reviewer); await pending;
            Check(player.Token.IsCancellationRequested && !player.Disposed, "role switch stops speech while preserving workspace player");
            var nextButton = await Button();
            pending = nextButton.ToggleAsync();
            await UntilAsync(() => player.Plays == 2);
            view.SetActive(false); await pending;
            Check(player.Token.IsCancellationRequested, "leaving chat cancels speech");
            view.SetActive(true); pending = (await Button()).ToggleAsync(); await UntilAsync(() => player.Plays == 3);
            view.Dispose(); await pending;
            Check(player.Disposed && player.Token.IsCancellationRequested, "workspace shutdown disposes speech player and cancels audio");
        }
        finally { root.Children.Remove(view); }
    }
}
