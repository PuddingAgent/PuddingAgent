using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private async Task VerifyWorkspaceVoiceAsync(Grid root)
    {
        var device = new VoiceInputFixture();
        var fixture = new Fixture(Path.ChangeExtension(Report, ".voice.png")) { Transcription = device.TranscribeAsync };
        await using var view = new ChatWorkspace(fixture, voiceCapture: device);
        Grid.SetColumnSpan(view, 2); root.Children.Add(view);
        try
        {
            await view.InitializeAsync(); await view.SelectRoleAsync("test", fixture.Builder);
            var voice = view.VoiceInput ?? throw new InvalidOperationException("Missing native voice control");
            var entry = Descendants<Button>(view.Composer).Single(b => b.Content?.ToString() == "语音");
            entry.Flyout.ShowAt(entry); await UntilAsync(() => voice.IsLoaded); await NextVisualFrameAsync();
            view.Composer.Draft = "draft";
            var pending = voice.ToggleAsync(); await voice.ToggleAsync();
            await UntilAsync(() => fixture.LastTranscriptionRole is not null);
            device.Result.SetResult("spoken"); await pending;
            var accepted = voice.AcceptResult();
            Check(entry.IsEnabled && fixture.LastTranscriptionRole == new RoleKey("test", "builder") && accepted
                && view.Composer.Draft.Replace("\r\n", "\n").Replace('\r', '\n') == "draft\nspoken",
                $"workspace voice routes selected role and explicitly appends transcription: enabled={entry.IsEnabled}, role={fixture.LastTranscriptionRole}, accepted={accepted}, draft={System.Text.Json.JsonSerializer.Serialize(view.Composer.Draft)}");
            await view.SendAsync();
            Check(fixture.Sent?.VoiceOrigin is { Provider: "fixture-provider", Model: "fixture-model", Language: null }
                && !string.IsNullOrEmpty(fixture.Sent.VoiceOrigin.SessionId), "confirmed voice origin travels with the native send snapshot");
            pending = voice.ToggleAsync(); await view.SelectRoleAsync("test", fixture.Reviewer); await pending;
            Check(device.Released && device.Token.IsCancellationRequested && view.Composer.Draft == "", "switching role cancels microphone and preserves separate draft");
            device.Result = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.LastTranscriptionRole = null;
            pending = voice.ToggleAsync(); await voice.ToggleAsync();
            await UntilAsync(() => fixture.LastTranscriptionRole is not null);
            view.Composer.Draft = "edited"; device.Result.SetResult("later"); await pending;
            Check(!voice.AcceptResult() && view.Composer.Draft == "edited", "workspace refuses late transcription over edited draft");
            pending = voice.ToggleAsync(); view.SetActive(false); await pending;
            Check(device.Released && device.Token.IsCancellationRequested, "leaving workspace stops microphone");
            view.SetActive(true); device.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = voice.ToggleAsync(); var closing = view.DisposeAsync().AsTask();
            var waiting = !closing.IsCompleted;
            device.Release.SetResult(); await closing; await pending;
            Check(waiting && device.Released && fixture.Disposed, "async workspace close waits for device release before completion");
        }
        finally { root.Children.Remove(view); }
    }
}
