using PuddingChat;

namespace PuddingChatTests;

public class VoiceInputTests
{
    private static ChatSelection Selection()
    { var selection = new ChatSelection(); selection.Select(new("work", "author")); selection.Draft = "existing"; return selection; }
    private sealed class Capture : IVoiceCapture
    {
        public TaskCompletionSource<IVoiceRecording> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public Task<IVoiceRecording> OpenAsync(CancellationToken ct) { Token = ct; return Opened.Task; }
    }
    private sealed class Recording : IVoiceRecording
    {
        public bool Disposed;
        public int Finishes;
        public RecordedSpeech Audio = new([1, 2, 3]);
        public TaskCompletionSource<RecordedSpeech>? Finalized;
        public TaskCompletionSource? Released;
        public Task<RecordedSpeech> FinishAsync(CancellationToken ct) { Finishes++; return Finalized?.Task ?? Task.FromResult(Audio); }
        public ValueTask DisposeAsync() { Disposed = true; return Released is null ? ValueTask.CompletedTask : new(Released.Task); }
    }
    private sealed class Client : IChatTranscriptionClient
    {
        public TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public int Calls;
        public Task<string> TranscribeAsync(RoleKey role, RecordedSpeech audio, CancellationToken ct)
        { Token = ct; Calls++; Started.TrySetResult(); return Result.Task; }
    }

    [Fact]
    public async Task FinishesCaptureBeforeTranscribingAndAppendsOnlyOnExplicitAcceptance()
    {
        var capture = new Capture(); var client = new Client(); var recording = new Recording(); var selection = Selection();
        await using var session = new VoiceInputSession(capture, client);
        var pending = session.StartAsync(VoiceDraftAnchor.Capture(selection));
        Assert.Equal(VoiceInputPhase.Opening, session.State.Phase);
        session.FinishRecording(); session.FinishRecording(); capture.Opened.SetResult(recording);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recording.Disposed); Assert.Equal(1, recording.Finishes);
        Assert.Equal(VoiceInputPhase.Transcribing, session.State.Phase);
        client.Result.SetResult(" spoken words "); await pending;
        Assert.Equal("existing", selection.Draft);
        Assert.Equal(VoiceInputPhase.Completed, session.State.Phase);
        Assert.True(session.State.Result!.TryAppendTo(selection));
        Assert.Equal("existing" + Environment.NewLine + "spoken words", selection.Draft);
        Assert.False(session.State.Result.TryAppendTo(selection));
    }

    [Fact]
    public async Task CancelDuringOpeningClosesLateDeviceBeforeAllowingAnotherCapture()
    {
        var capture = new Capture(); var client = new Client(); var recording = new Recording();
        await using var session = new VoiceInputSession(capture, client);
        var anchor = VoiceDraftAnchor.Capture(Selection()); var pending = session.StartAsync(anchor);
        session.Cancel(); Assert.True(capture.Token.IsCancellationRequested);
        Assert.Equal(VoiceInputPhase.Cancelling, session.State.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(anchor));
        capture.Opened.SetResult(recording); await pending;
        Assert.True(recording.Disposed); Assert.Equal(0, recording.Finishes); Assert.Equal(0, client.Calls);
        Assert.Equal(VoiceInputPhase.Idle, session.State.Phase);
    }

    [Fact]
    public async Task DisposalDuringRecordingReleasesDeviceWithoutTranscription()
    {
        var capture = new Capture(); var client = new Client(); var recording = new Recording(); capture.Opened.SetResult(recording);
        var session = new VoiceInputSession(capture, client);
        var pending = session.StartAsync(VoiceDraftAnchor.Capture(Selection()));
        Assert.Equal(VoiceInputPhase.Recording, session.State.Phase);
        await session.DisposeAsync(); await pending;
        Assert.True(recording.Disposed); Assert.Equal(0, client.Calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(VoiceDraftAnchor.Capture(Selection())));
    }

    [Fact]
    public async Task LateTranscriptionAfterCancelCannotPublishResult()
    {
        var capture = new Capture(); var client = new Client(); capture.Opened.SetResult(new Recording());
        await using var session = new VoiceInputSession(capture, client);
        var pending = session.StartAsync(VoiceDraftAnchor.Capture(Selection())); session.FinishRecording();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); session.Cancel(); await pending;
        client.Result.SetResult("late");
        Assert.True(client.Token.IsCancellationRequested); Assert.Null(session.State.Result);
        Assert.Equal(VoiceInputPhase.Idle, session.State.Phase);
    }

    [Fact]
    public async Task CancelWhileFinalizingWaitsForReleaseAndNeverInvokesAsr()
    {
        var capture = new Capture(); var client = new Client();
        var recording = new Recording { Finalized = new(), Released = new() }; capture.Opened.SetResult(recording);
        var session = new VoiceInputSession(capture, client);
        var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) => { if (session.State.Phase == VoiceInputPhase.Finalizing) finalized.TrySetResult(); };
        var pending = session.StartAsync(VoiceDraftAnchor.Capture(Selection())); session.FinishRecording();
        await finalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstDispose = session.DisposeAsync().AsTask(); var secondDispose = session.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted); Assert.False(secondDispose.IsCompleted);
        recording.Finalized.SetResult(recording.Audio); recording.Released.SetResult();
        await Task.WhenAll(firstDispose, secondDispose, pending).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recording.Disposed); Assert.Equal(0, client.Calls); Assert.Null(session.State.Result);
    }

    [Fact]
    public async Task AlreadyCancelledOperationDoesNotOpenMicrophone()
    {
        var capture = new Capture(); var client = new Client();
        await using var session = new VoiceInputSession(capture, client);
        await session.StartAsync(VoiceDraftAnchor.Capture(Selection()), new CancellationToken(true));
        Assert.False(capture.Token.CanBeCanceled); Assert.Equal(VoiceInputPhase.Idle, session.State.Phase);
    }

    [Fact]
    public void ChangedDraftOrRoleRejectsAutomaticInsertionButRetainsText()
    {
        var selection = Selection(); var result = new VoiceDraftResult(VoiceDraftAnchor.Capture(selection), "spoken words");
        selection.Draft = "edited"; Assert.False(result.TryAppendTo(selection)); Assert.Equal("edited", selection.Draft);
        selection.Draft = "existing"; selection.Select(new("work", "reviewer"));
        Assert.False(result.TryAppendTo(selection));
        selection.Select(new("work", "author")); Assert.False(result.TryAppendTo(selection));
        Assert.Equal("spoken words", result.Text);
    }

    [Fact]
    public async Task MicrophoneFailureIsVisibleWithoutLeakingRawErrorAndAllowsRetry()
    {
        var capture = new Capture(); var client = new Client();
        await using var session = new VoiceInputSession(capture, client);
        capture.Opened.SetException(new UnauthorizedAccessException("device secret"));
        await session.StartAsync(VoiceDraftAnchor.Capture(Selection()));
        Assert.Equal(VoiceInputPhase.Failed, session.State.Phase); Assert.DoesNotContain("secret", session.State.Error);
        capture.Opened = new(); capture.Opened.SetResult(new Recording());
        var retry = session.StartAsync(VoiceDraftAnchor.Capture(Selection())); session.Cancel(); await retry;
        Assert.Equal(VoiceInputPhase.Idle, session.State.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidAudioOrEmptyTranscriptionCannotProduceDraft(bool badAudio)
    {
        var capture = new Capture(); var client = new Client();
        capture.Opened.SetResult(new Recording { Audio = new(badAudio ? [] : [1]) }); client.Result.SetResult(" ");
        await using var session = new VoiceInputSession(capture, client);
        var pending = session.StartAsync(VoiceDraftAnchor.Capture(Selection())); session.FinishRecording(); await pending;
        Assert.Equal(VoiceInputPhase.Failed, session.State.Phase); Assert.Null(session.State.Result);
        Assert.Equal(badAudio ? 0 : 1, client.Calls);
    }
}
