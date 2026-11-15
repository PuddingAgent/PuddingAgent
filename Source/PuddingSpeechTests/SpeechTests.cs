using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using PuddingSpeech;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace PuddingSpeechTests;

[TestClass]
public sealed class SpeechTests
{
    [TestMethod]
    public void Component_HasNoHostReferences()
    {
        Assert.IsFalse(typeof(QwenAsr).Assembly.GetReferencedAssemblies().Any(x => x.Name!.StartsWith("Pudding")));
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "PuddingAgentNetwork.slnx"))) path = path.Parent;
        Assert.IsNotNull(path);
        var project = File.ReadAllText(Path.Combine(path.FullName, "Source/PuddingSpeech/PuddingSpeech.csproj"));
        Assert.IsFalse(XDocument.Parse(project).Descendants().Any(x => x.Name.LocalName is "ProjectReference" or "PackageReference"));
    }

    [TestMethod]
    public void Wave_ValidatesActualEncoding()
    {
        Assert.HasCount(320, PcmWave.ReadPcm16KhzMono(Wave()));
        var invalid = Wave(); invalid[22] = 2;
        Assert.Throws<SpeechFailure>(() => PcmWave.ReadPcm16KhzMono(invalid));
        Assert.Throws<SpeechFailure>(() => PcmWave.ReadPcm16KhzMono(Encoding.UTF8.GetBytes("webm renamed to wav")));
    }

    [TestMethod]
    public void Wave_RejectsTruncatedChunkAndDuration()
    {
        var invalid = Wave(); BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(40), 9999);
        Assert.Throws<SpeechFailure>(() => PcmWave.ReadPcm16KhzMono(invalid));
        Assert.AreEqual("voice_input_too_large", Assert.Throws<SpeechFailure>(() => PcmWave.ReadPcm16KhzMono(Wave(16000 * 2 * 121))).Code);
    }

    [TestMethod]
    public void QuotaError_IsTypedAndDoesNotLeakResponse()
    {
        var failure = SpeechFailure.FromProvider(403, """{"code":"AllocationQuota.FreeTierOnly","message":"secret transcript and key"}""");
        Assert.AreEqual("voice_quota_exhausted", failure.Code);
        Assert.AreEqual(503, failure.Status);
        Assert.IsFalse(failure.Retryable);
        Assert.IsFalse(failure.ToString().Contains("secret"));
        Assert.AreEqual("voice_credentials_invalid", SpeechFailure.FromProvider(401, "bad key").Code);
        Assert.AreEqual("voice_rate_limited", SpeechFailure.FromProvider(429, "{}").Code);
    }

    [TestMethod]
    public async Task Asr_DrainsFinalAfterFinish_AndDisposesSocket()
    {
        var socket = new FakeSocket();
        var segments = new List<TranscriptSegment>();
        await foreach (var segment in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizeAsync(Wave())) segments.Add(segment);
        Assert.HasCount(2, segments);
        Assert.AreEqual("你好", segments[1].Text);
        Assert.IsTrue(segments[1].IsFinal);
        Assert.IsTrue(socket.StartedBeforeAudio);
        Assert.IsTrue(socket.Finished);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public async Task Asr_ProviderFailure_DisposesAndDoesNotSendAudio()
    {
        var socket = new FakeSocket { Fail = true };
        var failure = await Assert.ThrowsAsync<SpeechFailure>(async () =>
        { await foreach (var _ in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizeAsync(Wave())) { } });
        Assert.AreEqual("voice_quota_exhausted", failure.Code);
        Assert.AreEqual(0, socket.AudioBytes);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public async Task Asr_CancelWhileWaitingForStarted_ReleasesEverything()
    {
        var socket = new FakeSocket { Silent = true };
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizeAsync(Wave(), ct: cancel.Token)) { } });
        Assert.IsTrue(socket.Disposed);
        Assert.AreEqual(0, socket.AudioBytes);
    }

    [TestMethod]
    public async Task Asr_EofWithoutTaskFinished_IsNotSuccess()
    {
        var socket = new FakeSocket { DropFinish = true };
        await Assert.ThrowsAsync<SpeechFailure>(async () =>
        { await foreach (var _ in new QwenAsr(_ => Task.FromResult<ISpeechSocket>(socket)).RecognizeAsync(Wave())) { } });
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public async Task Tts_YieldsBeforeTheNextNetworkReadCompletes()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var stream = new BlockingTailStream();
        await using var events = QwenTts.ReadAudioAsync(stream, cancel.Token).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.HasCount(2, events.Current);
    }

    [TestMethod]
    public async Task Tts_ReadsAudioBeforeEnd_AndRejectsPrematureEof()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"output\":{\"audio\":{\"data\":\"AQI=\"}}}\n\n"));
        await using var events = QwenTts.ReadAudioAsync(stream).GetAsyncEnumerator();
        Assert.IsTrue(await events.MoveNextAsync());
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, events.Current);
        await Assert.ThrowsAsync<SpeechFailure>(async () => await events.MoveNextAsync());
    }

    [TestMethod]
    public async Task Tts_MultilineDataAndStopAreParsed()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": ping\r\ndata: {\"output\":\r\ndata: {\"audio\":{\"data\":\"AQI=\"},\"finish_reason\":\"stop\"}}\r\n\r\n"));
        var count = 0;
        await foreach (var _ in QwenTts.ReadAudioAsync(stream)) count++;
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void Tts_ProtocolSelectionDoesNotGuessModelPrefix()
    {
        using var doc = JsonDocument.Parse(QwenTts.BuildRequest(QwenTts.AudioProtocol, "qwen-audio-3.1-tts-flash", "你好", "voice", "wav", 24000));
        Assert.AreEqual(24000, doc.RootElement.GetProperty("input").GetProperty("sample_rate").GetInt32());
        Assert.IsFalse(doc.RootElement.GetProperty("input").TryGetProperty("language_type", out _));
        Assert.Throws<SpeechFailure>(() => QwenTts.BuildRequest("unknown", "qwen-new", "text", "voice", "wav", 24000));
    }

    private static byte[] Wave(int size = 320)
    {
        var data = new byte[44 + size];
        "RIFF"u8.CopyTo(data); "WAVEfmt "u8.CopyTo(data.AsSpan(8)); "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 36 + size);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), size);
        return data;
    }

    private sealed class FakeSocket : ISpeechSocket
    {
        private readonly Channel<string> events = Channel.CreateUnbounded<string>();
        private string taskId = "";
        public bool Fail { get; init; }
        public bool Silent { get; init; }
        public bool DropFinish { get; init; }
        public bool Finished { get; private set; }
        public bool Disposed { get; private set; }
        public bool StartedBeforeAudio { get; private set; }
        public int AudioBytes { get; private set; }
        public Task SendTextAsync(string text, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(text);
            var header = doc.RootElement.GetProperty("header");
            taskId = header.GetProperty("task_id").GetString()!;
            if (header.GetProperty("action").GetString() == "run-task")
            {
                Assert.AreEqual(QwenAsr.Model, doc.RootElement.GetProperty("payload").GetProperty("model").GetString());
                if (!Silent) events.Writer.TryWrite(Event(Fail ? "task-failed" : "task-started", Fail ? ",\"error_code\":\"AllocationQuota.FreeTierOnly\"" : ""));
            }
            else
            {
                Finished = true;
                events.Writer.TryWrite(Result("你", false)); events.Writer.TryWrite(Result("你好", true));
                if (!DropFinish) events.Writer.TryWrite(Event("task-finished"));
                else events.Writer.TryComplete();
            }
            return Task.CompletedTask;
        }
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) { StartedBeforeAudio = true; AudioBytes += audio.Length; return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            if (!await events.Reader.WaitToReadAsync(ct)) return null;
            return await events.Reader.ReadAsync(ct);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private string Event(string name, string extra = "") => $"{{\"header\":{{\"task_id\":\"{taskId}\",\"event\":\"{name}\"{extra}}}}}";
        private string Result(string text, bool final) => $"{{\"header\":{{\"task_id\":\"{taskId}\",\"event\":\"result-generated\"}},\"payload\":{{\"output\":{{\"sentence\":{{\"sentence_id\":1,\"text\":\"{text}\",\"sentence_end\":{final.ToString().ToLowerInvariant()}}}}}}}}}";
    }

    private sealed class BlockingTailStream : Stream
    {
        private bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (sent) { await Task.Delay(Timeout.Infinite, ct); return 0; }
            sent = true;
            var bytes = Encoding.UTF8.GetBytes("data: {\"output\":{\"audio\":{\"data\":\"AQI=\"}}}\n\n");
            bytes.CopyTo(buffer); return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
