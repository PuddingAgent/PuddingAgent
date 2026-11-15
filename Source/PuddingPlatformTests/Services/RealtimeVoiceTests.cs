using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Services;
using PuddingSpeech;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RealtimeVoiceTests
{
    [TestMethod]
    public async Task Ticket_IsSingleUseUnderConcurrentConsumers()
    {
        var tickets = new VoiceStreamTicketStore(); var ticket = tickets.Issue("user-id");
        Assert.AreEqual(64, ticket.Length);
        Assert.AreEqual(1, (await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => tickets.TryConsume(ticket))))).Count(x => x));
        Assert.IsFalse(tickets.TryConsume("unknown"));
    }

    [TestMethod]
    public void Ticket_ExpiresAndRequiresAnAuthenticatedOwner()
    {
        var clock = new Clock(); var tickets = new VoiceStreamTicketStore(clock);
        var ticket = tickets.Issue("user-id"); clock.Now = clock.Now.AddSeconds(31);
        Assert.IsFalse(tickets.TryConsume(ticket));
        Assert.Throws<SpeechFailure>(() => tickets.Issue(""));
    }

    [TestMethod]
    public async Task UnifiedService_ResolvesStreamingCapabilityWithoutProviderNames()
    {
        var recognizer = new Mock<IAsrHttpRecognizer>(); recognizer.As<IStreamingSpeechRecognizer>();
        await WithService(recognizer.Object, async service =>
        { Assert.AreSame<object>(recognizer.Object, await service.CreateAsync(default)); Assert.IsTrue(await service.IsAvailableAsync(default)); });
    }

    [TestMethod]
    public async Task UnifiedService_RejectsAFileOnlyAdapterWithSafeError()
    {
        await WithService(Mock.Of<IAsrHttpRecognizer>(), async service =>
        {
            var error = await Assert.ThrowsAsync<SpeechFailure>(() => service.CreateAsync(default));
            Assert.AreEqual("voice_streaming_unavailable", error.Code);
            Assert.IsFalse(await service.IsAvailableAsync(default));
        });
    }

    [TestMethod]
    public async Task BrowserPeer_ReadsFirstFrameTicketThenBinaryAudioAndFinish()
    {
        var socket = new Socket();
        socket.Add("""{"type":"start","format":"pcm","sampleRate":16000,"ticket":"opaque"}""");
        socket.Frames.Enqueue((new byte[3200], WebSocketMessageType.Binary, true));
        socket.Add("""{"type":"finish"}""");
        var peer = new WebSocketSpeechPeer(socket, NullLogger.Instance);
        Assert.AreEqual("opaque", await peer.ReadTicketAsync(default));
        Assert.HasCount(3200, (await peer.ReceiveAsync(default))!.Audio!);
        Assert.AreEqual("finish", (await peer.ReceiveAsync(default))!.Control);
        await peer.SendAsync(new("transcript", 1, "你好", false, "你好", ""), default);
        using var json = JsonDocument.Parse(socket.Sent.Single());
        Assert.AreEqual("你好", json.RootElement.GetProperty("transcript").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("provider", out _));
    }

    [TestMethod]
    public async Task BrowserPeer_RejectsOversizedAndInvalidStartFrames()
    {
        var socket = new Socket(); socket.Add(new string('x', 8192));
        var peer = new WebSocketSpeechPeer(socket, NullLogger.Instance);
        await Assert.ThrowsAsync<SpeechFailure>(() => peer.ReadTicketAsync(default));
        socket = new Socket(); socket.Add("""{"type":"start","format":"wav","sampleRate":16000,"ticket":"opaque"}""");
        peer = new WebSocketSpeechPeer(socket, NullLogger.Instance);
        await Assert.ThrowsAsync<SpeechFailure>(() => peer.ReadTicketAsync(default));
    }

    private static async Task WithService(IAsrHttpRecognizer recognizer, Func<RealtimeAudioTranscriptionService, Task> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-realtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root); var file = paths.SystemConfigFile("voice/providers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new PuddingVoiceProvidersConfig
            { DefaultAsrProviderId = "custom-route", DefaultAsrModelId = "custom-model" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var factory = new Mock<IVoiceProviderFactory>();
            factory.Setup(f => f.CreateAsrProvider(It.IsAny<PuddingVoiceProvidersConfig>(), null, null)).Returns(recognizer);
            await check(new(new(paths, NullLogger<VoiceProviderFileService>.Instance), factory.Object));
            factory.Verify(f => f.CreateAsrProvider(It.IsAny<PuddingVoiceProvidersConfig>(), null, null), Times.AtLeastOnce());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Socket : WebSocket
    {
        public Queue<(byte[] Bytes, WebSocketMessageType Kind, bool End)> Frames { get; } = new();
        public List<byte[]> Sent { get; } = [];
        public void Add(string text) => Frames.Enqueue((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        { var frame = Frames.Dequeue(); frame.Bytes.CopyTo(buffer.AsSpan()); return Task.FromResult(new WebSocketReceiveResult(frame.Bytes.Length, frame.Kind, frame.End)); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType kind, bool end, CancellationToken ct)
        { Sent.Add(buffer.ToArray()); return Task.CompletedTask; }
    }
}
