using PuddingChat;

namespace PuddingChatTests;

public sealed class SpeechPlaybackTests
{
    private static SpeechRequest Request(string id = "one") => new(new("workspace", "role"), id, "需要朗读的消息");
    private static readonly SpeechAudio Audio = new([1, 2, 3], "audio/wav");
    private sealed class Client : IChatSpeechClient
    {
        public readonly List<(SpeechRequest Request, TaskCompletionSource<SpeechAudio> Result)> Requests = [];
        public Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<SpeechAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((request, completion)); return completion.Task;
        }
    }
    private sealed class Player : ISpeechAudioPlayer
    {
        public int Plays;
        public bool Disposed;
        public CancellationToken Token;
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PlayAsync(SpeechAudio audio, CancellationToken ct) { Plays++; Token = ct; Started.TrySetResult(); return Finished.Task; }
        public void Dispose() => Disposed = true;
    }
    [Fact]
    public async Task ReportsSynthesisPlaybackAndNaturalCompletion()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var task = session.SpeakAsync(Request());
        Assert.Equal(SpeechPlaybackPhase.Synthesizing, session.State.Phase);
        client.Requests[0].Result.SetResult(Audio);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SpeechPlaybackPhase.Playing, session.State.Phase);
        player.Finished.SetResult(); await task;
        Assert.Equal(SpeechPlaybackPhase.Idle, session.State.Phase);
    }
    [Fact]
    public async Task ReplacedRequestCannotPlayOrOverwriteNewStateWhenSynthesisIgnoresCancellation()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var first = session.SpeakAsync(Request()); var second = session.SpeakAsync(Request("two"));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        client.Requests[0].Result.SetResult(Audio);
        Assert.Equal("two", session.State.Request!.MessageId); Assert.Equal(0, player.Plays);
        session.Stop(); await second; Assert.Equal(SpeechPlaybackPhase.Idle, session.State.Phase);
    }
    [Fact]
    public async Task StopCancelsDeviceAndLateCompletionCannotChangeNextRequest()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var first = session.SpeakAsync(Request()); client.Requests[0].Result.SetResult(Audio);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stateAtCancellation = SpeechPlaybackPhase.Playing;
        using var observed = player.Token.Register(() => stateAtCancellation = session.State.Phase);
        session.Stop(); await first;
        Assert.Equal(SpeechPlaybackPhase.Idle, stateAtCancellation);
        Assert.True(player.Token.IsCancellationRequested);
        var second = session.SpeakAsync(Request("two")); player.Finished.SetException(new IOException("late device failure"));
        Assert.Equal("two", session.State.Request!.MessageId);
        session.Stop(); await second;
    }
    [Fact]
    public async Task FailedSynthesisIsVisibleWithoutLeakingProviderExceptionAndCanRetry()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var task = session.SpeakAsync(Request()); client.Requests[0].Result.SetException(new Exception("secret-token")); await task;
        Assert.Equal(SpeechPlaybackPhase.Failed, session.State.Phase); Assert.DoesNotContain("secret-token", session.State.Error);
        var retry = session.SpeakAsync(Request()); Assert.Equal(SpeechPlaybackPhase.Synthesizing, session.State.Phase);
        session.Stop(); await retry;
    }
    [Fact]
    public async Task InvalidAudioNeverReachesDevice()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var task = session.SpeakAsync(Request()); client.Requests[0].Result.SetResult(new([], "audio/wav")); await task;
        Assert.Equal(0, player.Plays); Assert.Equal(SpeechPlaybackPhase.Failed, session.State.Phase);
        Assert.Throws<InvalidDataException>(() => new SpeechAudio([1], "audio/unknown").Validate());
    }
    [Fact]
    public async Task DisposalCancelsPendingSynthesisAndDisposesDevice()
    {
        var client = new Client(); var player = new Player(); var session = new SpeechPlaybackSession(client, player);
        var task = session.SpeakAsync(Request()); session.Dispose(); await task.WaitAsync(TimeSpan.FromSeconds(2));
        client.Requests[0].Result.SetResult(Audio);
        Assert.True(player.Disposed); Assert.Equal(0, player.Plays);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SpeakAsync(Request()));
    }
    [Fact]
    public async Task InvalidTextDoesNotInterruptCurrentPlaybackRequest()
    {
        var client = new Client(); var player = new Player(); using var session = new SpeechPlaybackSession(client, player);
        var first = session.SpeakAsync(Request());
        await Assert.ThrowsAsync<ArgumentException>(() => session.SpeakAsync(Request("two") with { Text = new string('x', SpeechRequest.MaxCharacters + 1) }));
        Assert.Single(client.Requests); Assert.Equal("one", session.State.Request!.MessageId);
        session.Stop(); await first;
    }
}
