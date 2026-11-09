using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingSlack;

[assembly: DoNotParallelize]

namespace PuddingSlackTests;

[TestClass]
public sealed class SlackTests
{
    private static readonly SlackAdmission Admission = new("T1", "UBOT", ["U1"], ["C1"], "A1");
    private static SlackInboundMessage Message(string ts = "1730000000.000001", string? thread = null, bool dm = false)
        => new("Ev1", "T1", dm ? "D1" : "C1", "U1", ts, thread, dm, "中文问题");
    private static string Payload(string type = "app_mention", string team = "T1", string user = "U1", string? subtype = null)
        => JsonSerializer.Serialize(new { type = "event_callback", team_id = team, api_app_id = "A1", event_id = "Ev1",
            @event = new { type, channel_type = "im", channel = type == "message" ? "D1" : "C1", user, subtype,
                text = "<@UBOT> 中文问题", ts = "1730000000.000001" } });
    private static SlackInboundMessage? Parse(string payload, SlackAdmission? admission = null)
    {
        using var doc = JsonDocument.Parse(payload);
        return SlackEventParser.Parse(doc.RootElement, admission ?? Admission);
    }
    private static SlackApiClient Api(HttpMessageHandler handler) => new(new HttpClient(handler),
        new SlackCredentials { AppToken = "test-app-secret", BotToken = "test-bot-secret" });
    private static string TemporaryDirectory() => Path.Combine(
        Environment.GetEnvironmentVariable("PUDDING_TEST_ROOT") ?? Path.GetTempPath(), "PuddingSlackTests", Guid.NewGuid().ToString("N"));

    [TestMethod] public void MentionMapsTextAndKeepsTimestampPrecision()
    {
        var message = Parse(Payload())!;
        Assert.AreEqual("中文问题", message.Text);
        Assert.AreEqual("1730000000.000001", message.MessageTs);
        Assert.AreEqual(message.MessageTs, message.ReplyThreadTs);
    }
    [TestMethod] public void DirectMessagesUseOneConversationAndPreserveReplyThread()
    {
        Assert.IsNotNull(Parse(Payload(type: "message")));
        Assert.AreEqual(Message(dm: true).ConversationKey, Message("2.0", "1.0", true).ConversationKey);
        Assert.AreEqual("1.0", Message("2.0", "1.0", true).ReplyThreadTs);
    }
    [TestMethod] public void ChannelThreadsAreIsolatedButRepliesShareRoot()
    {
        Assert.AreNotEqual(Message("1.0").ConversationKey, Message("2.0").ConversationKey);
        Assert.AreEqual(Message("1.0").ConversationKey, Message("2.0", "1.0").ConversationKey);
    }
    [TestMethod] public void TenantAndUserAdmissionFailClosed()
    {
        Assert.IsNull(Parse(Payload(team: "T2")));
        Assert.IsNull(Parse(Payload(user: "U2")));
        Assert.IsNull(Parse(Payload(), Admission with { AllowedUserIds = [] }));
        Assert.IsNull(Parse(Payload(), Admission with { AllowedChannelIds = [] }));
        Assert.IsNull(Parse(Payload(), Admission with { AppId = "A2" }));
    }
    [TestMethod] public void BotAndEditedEventsDoNotStartTurns()
    {
        Assert.IsNull(Parse(Payload(user: "UBOT")));
        Assert.IsNull(Parse(Payload(subtype: "message_changed")));
        Assert.IsNull(Parse(Payload(subtype: "bot_message")));
    }
    [TestMethod] public void InboxSurvivesRestartAndDeduplicatesByMessageAcrossEventIds()
    {
        var root = TemporaryDirectory();
        using (var store = new SlackStore(root)) { store.Enqueue(Message()); store.Enqueue(Message() with { EventId = "Ev2" }); }
        using (var store = new SlackStore(root))
        {
            Assert.HasCount(1, store.Pending());
            store.Complete(store.Pending()[0]);
        }
        using (var store = new SlackStore(root)) { store.Enqueue(Message()); Assert.HasCount(0, store.Pending()); }
        Directory.Delete(root, true);
    }
    [TestMethod] public void InboxLeasePreventsTwoOwners()
    {
        var root = TemporaryDirectory();
        using (var store = new SlackStore(root)) Assert.Throws<IOException>(() => new SlackStore(root));
        Directory.Delete(root, true);
    }
    [TestMethod] public void DeferredMessagesDoNotStarveLaterInboxEntries()
    {
        var root = TemporaryDirectory();
        using (var store = new SlackStore(root))
        {
            store.Enqueue(Message("1.0")); store.Enqueue(Message("2.0"));
            var available = store.Pending(1, [Message("1.0").MessageKey]);
            Assert.HasCount(1, available);
            Assert.AreEqual("2.0", available[0].MessageTs);
            Assert.HasCount(2, store.Pending());
        }
        Directory.Delete(root, true);
    }
    [TestMethod] public async Task AckFollowsPersistenceAndDoesNotWaitForAgent()
    {
        var sequence = new List<string>();
        var socket = new FakeSocket(_ => sequence.Add("ack"));
        var mode = new SlackSocketMode(Api(new Handler()), Admission, _ => sequence.Add("persist"), _ => { });
        await mode.HandleAsync("{\"type\":\"events_api\",\"envelope_id\":\"env1\",\"payload\":" + Payload() + "}", socket, default);
        CollectionAssert.AreEqual(new[] { "persist", "ack" }, sequence);
    }
    [TestMethod] public async Task DiskFailureNeverAcknowledgesEvent()
    {
        var acks = 0;
        var mode = new SlackSocketMode(Api(new Handler()), Admission, _ => throw new IOException(), _ => { });
        await Assert.ThrowsAsync<IOException>(() => mode.HandleAsync(
            "{\"type\":\"events_api\",\"envelope_id\":\"env1\",\"payload\":" + Payload() + "}",
            new FakeSocket(_ => acks++), default));
        Assert.AreEqual(0, acks);
    }
    [TestMethod] public async Task IgnoredEventsStillGetAcknowledged()
    {
        var acks = 0;
        var mode = new SlackSocketMode(Api(new Handler()), Admission, _ => Assert.Fail(), _ => { });
        await mode.HandleAsync("{\"type\":\"events_api\",\"envelope_id\":\"env1\",\"payload\":" + Payload(user: "U2") + "}",
            new FakeSocket(_ => acks++), default);
        Assert.AreEqual(1, acks);
    }
    [TestMethod] public void InboxCapacityFailsWithoutLosingExistingMessages()
    {
        var root = TemporaryDirectory();
        using (var store = new SlackStore(root, 1))
        {
            store.Enqueue(Message());
            Assert.Throws<IOException>(() => store.Enqueue(Message("2.0")));
            store.Enqueue(Message());
            Assert.HasCount(1, store.Pending());
        }
        Directory.Delete(root, true);
    }
    [TestMethod] public async Task Http200WithOkFalseIsRejected()
    {
        var result = await Api(new Handler("{\"ok\":false,\"error\":\"missing_scope\"}")).PostAsync(new("C1", null, "hi"), default);
        Assert.AreEqual(SlackSendStatus.Rejected, result.Status);
        Assert.AreEqual("missing_scope", result.Code);
    }
    [TestMethod] public async Task RateLimitKeepsRetryAfter()
    {
        var handler = new Handler { Respond = _ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return response; } };
        var result = await Api(handler).PostAsync(new("C1", null, "hi"), default);
        Assert.AreEqual(SlackSendStatus.Retryable, result.Status);
        Assert.IsTrue(result.RetryAt > DateTimeOffset.UtcNow.AddSeconds(110));
    }
    [TestMethod] public async Task SendTimeoutIsUnknownAndRedactsException()
    {
        var handler = new Handler { Respond = _ => throw new HttpRequestException("test-bot-secret") };
        var result = await Api(handler).PostAsync(new("C1", null, "hi"), default);
        Assert.AreEqual(SlackSendStatus.Unknown, result.Status);
        Assert.DoesNotContain("secret", result.ToString());
    }
    [TestMethod] public async Task SuccessRequiresMatchingChannelAndTimestamp()
    {
        var good = await Api(new Handler("{\"ok\":true,\"channel\":\"C1\",\"ts\":\"1.000001\"}")).PostAsync(new("C1", null, "hi"), default);
        Assert.AreEqual(SlackSendStatus.Sent, good.Status);
        Assert.AreEqual("1.000001", good.MessageTs);
        var bad = await Api(new Handler("{\"ok\":true}")).PostAsync(new("C1", null, "hi"), default);
        Assert.AreEqual(SlackSendStatus.Unknown, bad.Status);
    }
    [TestMethod] public async Task OutgoingSentReceiptSurvivesRestartWithoutResending()
    {
        var root = TemporaryDirectory(); var calls = 0;
        Task<SlackSendReceipt> Send(SlackPost _, CancellationToken __) { calls++; return Task.FromResult(new SlackSendReceipt(SlackSendStatus.Sent, "slack_accepted", "C1", "1.0")); }
        using (var store = new SlackStore(root)) await store.SendAsync("op1", new("C1", null, "hi"), Send, default);
        using (var store = new SlackStore(root))
        {
            var result = await store.SendAsync("op1", new("C1", null, "hi"), Send, default);
            Assert.AreEqual(SlackSendStatus.Sent, result.Status);
            Assert.AreEqual(SlackSendStatus.Rejected, (await store.SendAsync("op1", new("C1", null, "changed"), Send, default)).Status);
        }
        Assert.AreEqual(1, calls); Directory.Delete(root, true);
    }
    [TestMethod] public async Task InterruptedSendStaysUnknownAcrossRestart()
    {
        var root = TemporaryDirectory();
        using (var store = new SlackStore(root))
            await store.SendAsync("op1", new("C1", null, "hi"), (_, _) => throw new IOException(), default);
        using (var store = new SlackStore(root))
        {
            var result = await store.SendAsync("op1", new("C1", null, "hi"), (_, _) => throw new AssertFailedException("must not resend"), default);
            Assert.AreEqual(SlackSendStatus.Unknown, result.Status);
            Assert.AreEqual("send_interrupted", result.Code);
        }
        Directory.Delete(root, true);
    }
    [TestMethod] public async Task RetryAfterPreventsEarlyRetry()
    {
        var root = TemporaryDirectory(); var calls = 0;
        using (var store = new SlackStore(root))
        {
            Task<SlackSendReceipt> Send(SlackPost _, CancellationToken __) { calls++; return Task.FromResult(new SlackSendReceipt(SlackSendStatus.Retryable, "rate_limited", RetryAt: DateTimeOffset.UtcNow.AddMinutes(1))); }
            await store.SendAsync("op1", new("C1", null, "hi"), Send, default);
            await store.SendAsync("op1", new("C1", null, "hi"), Send, default);
            Assert.AreEqual(1, calls);
        }
        Directory.Delete(root, true);
    }
    [TestMethod] public async Task BotAndAppTokensGoToTheirRespectiveEndpoints()
    {
        var tokens = new List<string?>();
        var handler = new Handler { Respond = request => { tokens.Add(request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("auth.test")
                ? "{\"ok\":true,\"team_id\":\"T1\",\"user_id\":\"UBOT\"}" : "{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link?ticket=secret\"}") }; } };
        var api = Api(handler);
        Assert.AreEqual("T1", (await api.AuthenticateAsync(default)).TeamId);
        await api.OpenConnectionAsync(default);
        CollectionAssert.AreEqual(new[] { "test-bot-secret", "test-app-secret" }, tokens);
    }
    [TestMethod] public async Task SocketReconnectDirectiveEndsCurrentConnection()
    {
        var mode = new SlackSocketMode(Api(new Handler()), Admission, _ => { }, _ => { });
        Assert.IsFalse(await mode.HandleAsync("{\"type\":\"disconnect\",\"reason\":\"refresh_requested\"}", new FakeSocket(_ => Assert.Fail()), default));
    }
    [TestMethod] public void AssemblyHasNoProjectDependencies()
    {
        Assert.IsFalse(typeof(SlackStore).Assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Pudding") && a.Name != "PuddingSlack"));
        Assert.DoesNotContain("secret", new SlackCredentials { AppToken = "xapp-secret", BotToken = "xoxb-secret" }.ToString());
    }
    private sealed class Handler(string body = "{}") : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond?.Invoke(request) ?? new(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
    private sealed class FakeSocket(Action<string> send) : ISlackSocket
    {
        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> ReceiveAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SendAsync(string text, CancellationToken ct) { send(text); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
