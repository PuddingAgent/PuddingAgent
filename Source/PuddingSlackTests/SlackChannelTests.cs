using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingSlack;

namespace PuddingSlackTests;

[TestClass]
public sealed class SlackChannelTests
{
    private static readonly SlackAdmission Admission = new("T1", "USELF", ["UHUMAN"], ["C1"], "A1", ["UDOT"]);
    private static SlackInboundMessage Message(string ts, string root, bool bot) => new("Ev" + ts, "T1", "C1",
        bot ? "UDOT" : "UHUMAN", ts, root, false, "hello", bot);

    [TestMethod] public void ExplicitPeerBotMentionAcceptedButSelfUnlistedBotsAndEditsRejected()
    {
        SlackInboundMessage? Parse(string user, string? subtype = null, string text = "<@USELF> hello", string channel = "C1")
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "event_callback", team_id = "T1", api_app_id = "A1", event_id = "Ev1",
                @event = new { type = "app_mention", user, bot_id = "B1", channel, ts = "1.0", subtype, text } }));
            return SlackEventParser.Parse(doc.RootElement, Admission);
        }
        Assert.IsTrue(Parse("UDOT", "bot_message")!.IsBot);
        Assert.IsNull(Parse("USELF")); Assert.IsNull(Parse("UOTHER"));
        Assert.IsNull(Parse("UDOT", "message_changed")); Assert.IsNull(Parse("UDOT", text: "hello"));
        Assert.IsNull(Parse("UDOT", channel: "COTHER"));
    }

    [TestMethod] public void PeerBotCanTriggerOnlyOncePerThreadUntilNewHumanMessageEvenAfterRestart()
    {
        var root = TemporaryDirectory();
        try
        {
            using (var store = new SlackStore(root))
            {
                store.Enqueue(Message("2.0", "1.0", true));
                store.Complete(store.Pending().Single());
            }
            using (var store = new SlackStore(root))
            {
                store.Enqueue(Message("3.0", "1.0", true));
                Assert.HasCount(0, store.Pending()); Assert.AreEqual(1L, store.SuppressedBotMessages);
                store.Enqueue(Message("4.0", "1.0", false));
                store.Enqueue(Message("3.0", "1.0", true)); // a suppressed replay must not consume the new human credit
                Assert.HasCount(1, store.Pending());
                store.Enqueue(Message("5.0", "1.0", true));
                Assert.HasCount(2, store.Pending());
                store.Enqueue(Message("4.0", "1.0", false)); // duplicate human input must not grant more credit
                store.Enqueue(Message("6.0", "1.0", true));
                Assert.HasCount(2, store.Pending());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod] public void BotCannotBypassBudgetByStartingNewThreads()
    {
        var root = TemporaryDirectory();
        try
        {
            using var store = new SlackStore(root);
            for (var i = 1; i <= 5; i++) store.Enqueue(Message(i + ".0", i + ".0", true));
            Assert.HasCount(3, store.Pending()); Assert.AreEqual(2L, store.SuppressedBotMessages);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod] public async Task OutboundPreservesExplicitMemberMentionsWithoutEnablingBroadcastOrNameExpansion()
    {
        using var http = new HttpClient(new Handler());
        var api = new SlackApiClient(http, new SlackCredentials { AppToken = "app", BotToken = "bot" });
        var receipt = await api.PostAsync(new("C1", "1.0", "<@UDOT> @bob <!channel> <!subteam^S1> <https://example.com> & <tag>"), default);
        Assert.AreEqual(SlackSendStatus.Sent, receipt.Status);
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var body = doc.RootElement;
            Assert.AreEqual("<@UDOT> @bob &lt;!channel&gt; &lt;!subteam^S1&gt; &lt;https://example.com&gt; &amp; &lt;tag&gt;", body.GetProperty("text").GetString());
            Assert.IsTrue(body.GetProperty("mrkdwn").GetBoolean()); Assert.IsFalse(body.GetProperty("link_names").GetBoolean());
            Assert.AreEqual("none", body.GetProperty("parse").GetString()); Assert.AreEqual("1.0", body.GetProperty("thread_ts").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"channel\":\"C1\",\"ts\":\"2.0\"}") };
        }
    }
    private static string TemporaryDirectory() => Path.Combine(Environment.GetEnvironmentVariable("PUDDING_TEST_ROOT") ?? Path.GetTempPath(), "SlackChannelTests", Guid.NewGuid().ToString("N"));
}
