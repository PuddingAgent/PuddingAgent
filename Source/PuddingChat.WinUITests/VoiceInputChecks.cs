using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class VoiceInputFixture : IVoiceCapture, IVoiceRecording, IChatTranscriptionClient
    {
        public bool Released;
        public TaskCompletionSource? Release;
        public CancellationToken Token;
        public TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IVoiceRecording> OpenAsync(CancellationToken ct) { Released = false; Token = ct; return Task.FromResult<IVoiceRecording>(this); }
        public Task<RecordedSpeech> FinishAsync(CancellationToken ct) => Task.FromResult(new RecordedSpeech([1]));
        public ValueTask DisposeAsync() { Released = true; return Release is null ? ValueTask.CompletedTask : new(Release.Task); }
        public async Task<VoiceTranscript> TranscribeAsync(RoleKey role, RecordedSpeech audio, CancellationToken ct)
        { Token = ct; return new(await Result.Task, "fixture-provider", "fixture-model"); }
    }
    private static async Task VerifyVoiceInputControlAsync(Grid root)
    {
        var fixture = new VoiceInputFixture(); await using var session = new VoiceInputSession(fixture, fixture);
        var selection = new ChatSelection(); selection.Select(new("workspace", "author")); selection.Draft = "draft";
        using var control = new VoiceInputControl(session, () => VoiceDraftAnchor.Capture(selection), result => result.TryAppendTo(selection));
        root.Children.Add(control);
        Button Button(string text) => Descendants<Button>(control).Single(b => b.Content?.ToString() == text);
        bool Status(string text) => Descendants<TextBlock>(control).Any(b => b.Text.Contains(text));
        try
        {
            await UntilAsync(() => control.IsLoaded); await NextVisualFrameAsync();
            var pending = control.ToggleAsync();
            Check(Button("结束录音").IsEnabled && Status("正在录音"), "voice input shows native recording and stop action");
            await control.ToggleAsync(); await UntilAsync(() => session.State.Phase == VoiceInputPhase.Transcribing);
            Check(fixture.Released && Button("取消转写").Visibility == Visibility.Visible && Status("正在转写"), "voice input releases microphone and shows transcription cancellation");
            fixture.Result.SetResult("spoken"); await pending;
            Check(selection.Draft == "draft" && Button("加入草稿").IsEnabled && Status("spoken"), "transcription preview does not modify or send draft");
            selection.Draft = "edited";
            Check(!control.AcceptResult() && selection.Draft == "edited" && Status("草稿已变化"), "edited draft rejects insertion and keeps selectable result");
            selection.Draft = "draft";
            Check(control.AcceptResult() && selection.Draft.EndsWith("spoken") && !control.AcceptResult()
                && !Button("加入草稿").IsEnabled && Status("尚未发送"), "explicit acceptance appends once without sending");
            fixture.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = control.ToggleAsync(); await control.ToggleAsync();
            await UntilAsync(() => session.State.Phase == VoiceInputPhase.Transcribing);
            fixture.Result.SetException(new InvalidOperationException("private provider failure")); await pending;
            Check(Button("重新录音").IsEnabled && Status("失败") && !Status("private"), "transcription failure exposes safe retry feedback");
            pending = control.ToggleAsync(); root.Children.Remove(control); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Released && fixture.Token.IsCancellationRequested && session.State.Phase == VoiceInputPhase.Idle,
                "unloading the voice input cancels capture and releases microphone");
        }
        finally { root.Children.Remove(control); }
    }
}
