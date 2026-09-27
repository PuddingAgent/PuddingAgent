using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class SpeechFixture : IChatSpeechClient, ISpeechAudioPlayer
    {
        public TaskCompletionSource<SpeechAudio> Synthesized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public CancellationToken PlaybackToken;
        public Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => Synthesized.Task;
        public Task PlayAsync(SpeechAudio audio, CancellationToken ct) { PlaybackToken = ct; return Finished.Task; }
        public void Dispose() => Disposed = true;
    }
    private static async Task VerifySpeechButtonAsync(Grid root)
    {
        var fixture = new SpeechFixture(); using var session = new SpeechPlaybackSession(fixture, fixture);
        using var control = new SpeechPlaybackButton(session, new(new("workspace", "role"), "message", "朗读内容"));
        root.Children.Add(control);
        var action = ((StackPanel)control.Content).Children.OfType<Button>().Single();
        try
        {
            await UntilAsync(() => control.IsLoaded); await NextVisualFrameAsync();
            var speaking = control.ToggleAsync();
            Check(action.Content?.ToString() == "取消合成", "native speech button exposes synthesis cancellation");
            fixture.Synthesized.SetResult(SilentWave(100));
            await UntilAsync(() => action.Content?.ToString() == "停止朗读");
            await control.ToggleAsync(); await speaking;
            Check(fixture.PlaybackToken.IsCancellationRequested && action.Content?.ToString() == "朗读", "native stop cancels the shared player");
            fixture.Synthesized = new(TaskCreationOptions.RunContinuationsAsynchronously);
            speaking = control.ToggleAsync(); fixture.Synthesized.SetException(new InvalidOperationException("private provider detail")); await speaking;
            Check(action.Content?.ToString() == "重试朗读" && ((StackPanel)control.Content).Children.OfType<TextBlock>().Single().Visibility == Visibility.Visible,
                "native synthesis failure offers visible retry feedback");
            fixture.Synthesized = new(TaskCreationOptions.RunContinuationsAsynchronously);
            speaking = control.ToggleAsync(); root.Children.Remove(control);
            await speaking.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => !control.IsLoaded);
            Check(session.State.Phase == SpeechPlaybackPhase.Idle && !fixture.Disposed, "recycled message cancels playback without disposing workspace session: " + session.State.Phase + ", disposed=" + fixture.Disposed);
            control.Update(new(new("workspace", "role"), "message", new string('x', SpeechRequest.MaxCharacters + 1)));
            Check(!action.IsEnabled, "oversized text disables speech action without truncating input");
        }
        finally { root.Children.Remove(control); }
    }
}
