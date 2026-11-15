using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PuddingSpeech;

namespace PuddingSpeechTests;

[TestClass]
public sealed class RealtimeAsrTests
{
    [TestMethod]
    public async Task Qwen_TranscribesWhileInputIsStillOpen_ThenDrainsFinal()
    {
        var socket = new LiveSocket();
        var audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        audio.Writer.TryWrite(new byte[3200]);
        await using var results = new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket))
            .RecognizePcmAsync(audio.Reader.ReadAllAsync()).GetAsyncEnumerator();
        Assert.IsTrue(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual("临时", results.Current.Text);
        Assert.IsFalse(results.Current.IsFinal);
        Assert.IsFalse(socket.Finished, "An intermediate result must arrive before finish-task.");
        audio.Writer.TryComplete();
        Assert.IsTrue(await results.MoveNextAsync());
        Assert.AreEqual("确认。", results.Current.Text);
        Assert.IsTrue(results.Current.IsFinal);
        Assert.IsFalse(await results.MoveNextAsync());
        Assert.IsTrue(socket.Disposed);
        Assert.AreEqual(3200, socket.AudioBytes);
    }

    [TestMethod]
    public async Task Qwen_CancelOpenInput_ReleasesSocketWithoutFinish()
    {
        var socket = new LiveSocket();
        var audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        using var cancel = new CancellationTokenSource();
        await using var results = new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket))
            .RecognizePcmAsync(audio.Reader.ReadAllAsync(), ct: cancel.Token).GetAsyncEnumerator();
        var pending = results.MoveNextAsync().AsTask();
        await socket.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsTrue(socket.Disposed);
        Assert.IsFalse(socket.Finished);
    }

    [TestMethod]
    public async Task Qwen_InvalidPcmFrame_IsTypedAndReleasesSocket()
    {
        var socket = new LiveSocket();
        var audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(); audio.Writer.TryWrite(new byte[3]);
        var failure = await Assert.ThrowsAsync<SpeechFailure>(async () =>
        { await foreach (var _ in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizePcmAsync(audio.Reader.ReadAllAsync())) { } });
        Assert.AreEqual("voice_unsupported_audio", failure.Code);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public async Task Session_ReplacesSentenceAndDrainsFinal_WithoutDuplicateText()
    {
        var peer = new Peer();
        var run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Transcribe));
        await peer.Next("ready");
        peer.Input.Writer.TryWrite(new(new byte[3200]));
        Assert.AreEqual("你", (await peer.Next("transcript")).Transcript);
        Assert.AreEqual("你好", (await peer.Next("transcript")).Transcript);
        Assert.AreEqual("你好。", (await peer.Next("transcript")).ConfirmedText);
        peer.Input.Writer.TryWrite(new(Control: "finish"));
        Assert.AreEqual("你好。再见。", (await peer.Next("transcript")).Transcript);
        Assert.AreEqual("你好。再见。", (await peer.Next("completed")).Text);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        static async IAsyncEnumerable<TranscriptSegment> Transcribe(IAsyncEnumerable<ReadOnlyMemory<byte>> input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var _ in input.WithCancellation(ct))
            {
                yield return new(1, "你", false); yield return new(1, "你好", false);
                yield return new(1, "你好。", true); yield return new(1, "迟到的旧临时结果", false);
            }
            yield return new(2, "再见。", true);
        }
    }

    [TestMethod]
    public async Task Qwen_AsyncInputFailureRetainsItsErrorInsteadOfBecomingCancellation()
    {
        var socket = new LiveSocket();
        var error = await Assert.ThrowsAsync<SpeechFailure>(async () =>
        { await foreach (var _ in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizePcmAsync(Input())) { } });
        Assert.AreEqual("voice_invalid_frame", error.Code); Assert.IsTrue(socket.Disposed);
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Input()
        {
            await Task.Yield();
            if (!socket.Finished) throw new SpeechFailure("voice_invalid_frame", "test input failed", 400);
            yield return new byte[2];
        }
    }

    [TestMethod]
    public async Task Socket_CancelDuringHandshake_ClosesItsConnection()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var connection = ClientSpeechSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/"), "synthetic-test", cancellation.Token);
            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var buffer = new byte[4096];
            await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => connection);
            Assert.AreEqual(0, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task Session_CancelDuringDrain_DoesNotCommitOrComplete()
    {
        var peer = new Peer();
        var disposed = false;
        var run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Transcribe));
        await peer.Next("ready"); peer.Input.Writer.TryWrite(new(new byte[3200]));
        await peer.Next("transcript");
        peer.Input.Writer.TryWrite(new(Control: "finish")); peer.Input.Writer.TryWrite(new(Control: "cancel"));
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(disposed); Assert.IsFalse(peer.Output.Reader.TryRead(out _));

        async IAsyncEnumerable<TranscriptSegment> Transcribe(IAsyncEnumerable<ReadOnlyMemory<byte>> input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await foreach (var _ in input.WithCancellation(ct)) yield return new(1, "未提交", false);
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { disposed = true; }
        }
    }

    [TestMethod]
    public async Task Session_PrematureDisconnect_CancelsProvider()
    {
        var peer = new Peer(); var run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Echo));
        await peer.Next("ready"); peer.Input.Writer.TryComplete();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(peer.Output.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task Session_BoundedQueueFailsInsteadOfAccumulatingAudio()
    {
        var peer = new Peer(); var run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Slow));
        await peer.Next("ready"); peer.Input.Writer.TryWrite(new(new byte[3200]));
        await peer.Next("transcript");
        for (var i = 0; i < 10; i++) peer.Input.Writer.TryWrite(new(new byte[3200]));
        Assert.AreEqual("voice_backpressure", (await peer.Next("failed")).Code);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        static async IAsyncEnumerable<TranscriptSegment> Slow(IAsyncEnumerable<ReadOnlyMemory<byte>> input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await using var audio = input.GetAsyncEnumerator(ct); await audio.MoveNextAsync();
            yield return new(1, "临时", false); await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [TestMethod]
    public async Task Session_RejectsInvalidAudioFrame_AndEmptySpeech()
    {
        var peer = new Peer(); var run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Echo));
        await peer.Next("ready"); peer.Input.Writer.TryWrite(new(new byte[3]));
        Assert.AreEqual("voice_invalid_frame", (await peer.Next("failed")).Code);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        peer = new Peer(); run = new RealtimeAsrSession().RunAsync(peer, new Recognizer(Echo));
        await peer.Next("ready"); peer.Input.Writer.TryWrite(new(Control: "finish"));
        Assert.AreEqual("voice_no_speech", (await peer.Next("failed")).Code);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async IAsyncEnumerable<TranscriptSegment> Echo(IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        [EnumeratorCancellation] CancellationToken ct)
    { await foreach (var _ in audio.WithCancellation(ct)) yield return new(1, "", false); }

    private sealed class Recognizer(Func<IAsyncEnumerable<ReadOnlyMemory<byte>>, CancellationToken, IAsyncEnumerable<TranscriptSegment>> run) : IStreamingSpeechRecognizer
    { public IAsyncEnumerable<TranscriptSegment> RecognizePcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, string? language, CancellationToken ct) => run(audio, ct); }

    private sealed class Peer : IRealtimeSpeechPeer
    {
        public Channel<RealtimeSpeechInput> Input { get; } = Channel.CreateUnbounded<RealtimeSpeechInput>();
        public Channel<RealtimeSpeechEvent> Output { get; } = Channel.CreateUnbounded<RealtimeSpeechEvent>();
        public async Task<RealtimeSpeechInput?> ReceiveAsync(CancellationToken ct) => await Input.Reader.WaitToReadAsync(ct) ? await Input.Reader.ReadAsync(ct) : null;
        public Task SendAsync(RealtimeSpeechEvent message, CancellationToken ct) { Output.Writer.TryWrite(message); return Task.CompletedTask; }
        public async Task<RealtimeSpeechEvent> Next(string type)
        { var result = await Output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); Assert.AreEqual(type, result.Type); return result; }
    }

    private sealed class LiveSocket : ISpeechSocket
    {
        private readonly Channel<string> events = Channel.CreateUnbounded<string>();
        private string task = "";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished { get; private set; }
        public bool Disposed { get; private set; }
        public int AudioBytes { get; private set; }
        public Task SendTextAsync(string text, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(text); var header = doc.RootElement.GetProperty("header");
            task = header.GetProperty("task_id").GetString()!;
            if (header.GetProperty("action").GetString() == "run-task") { events.Writer.TryWrite(Event("task-started")); Started.TrySetResult(); }
            else { Finished = true; events.Writer.TryWrite(Result("确认。", true)); events.Writer.TryWrite(Event("task-finished")); }
            return Task.CompletedTask;
        }
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        { AudioBytes += audio.Length; events.Writer.TryWrite(Result("临时", false)); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await events.Reader.ReadAsync(ct);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private string Event(string name) => JsonSerializer.Serialize(new { header = new { task_id = task, @event = name } });
        private string Result(string text, bool final) => JsonSerializer.Serialize(new { header = new { task_id = task, @event = "result-generated" }, payload = new { output = new { sentence = new { sentence_id = 1, text, sentence_end = final } } } });
    }
}
