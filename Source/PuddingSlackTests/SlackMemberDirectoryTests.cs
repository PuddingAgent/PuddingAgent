using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingSlack;

namespace PuddingSlackTests;

[TestClass]
public sealed class SlackMemberDirectoryTests
{
    [TestMethod]
    public async Task ResolvesBotDisplayNameAndCachesOnlyMinimalIdentity()
    {
        using var handler = new Handler(Page([Member("UDOT", "dot-login", "wangxianqiangcn-dot", true)]));
        using var http = new HttpClient(handler);
        var directory = new SlackMemberDirectory(http, "secret");
        var result = await directory.LookupAsync("@wangxianqiangcn-dot", default);
        Assert.AreEqual("resolved", result.Status); Assert.AreEqual("<@UDOT>", result.Mention);
        Assert.IsTrue(result.Candidates.Single().IsBot);
        Assert.DoesNotContain("email", JsonSerializer.Serialize(result));
        Assert.AreEqual("<@UDOT>", (await directory.LookupAsync("UDOT", default)).Mention);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task PaginatesAndNeverSelectsAnAmbiguousOrDeletedMember()
    {
        using var handler = new Handler(Page([Member("U1", "one", "same", false)], "next"),
            Page([Member("U2", "two", "same", true), Member("U3", "three", "gone", false, true)]));
        using var http = new HttpClient(handler);
        var directory = new SlackMemberDirectory(http, "secret");
        var result = await directory.LookupAsync("same", default);
        Assert.AreEqual("ambiguous", result.Status); Assert.IsNull(result.Mention); Assert.HasCount(2, result.Candidates);
        Assert.AreEqual("not_found", (await directory.LookupAsync("gone", default)).Status);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task PartialScanDoesNotClaimUniqueMatchOrCacheAbsence()
    {
        using var handler = new Handler(Page([Member("U1", "one", "one", false)], "more"), Page([]));
        using var http = new HttpClient(handler);
        var directory = new SlackMemberDirectory(http, "secret", maxPages: 1);
        var result = await directory.LookupAsync("one", default);
        Assert.AreEqual("incomplete", result.Status); Assert.IsFalse(result.Complete); Assert.IsNull(result.Mention);
        Assert.AreEqual("not_found", (await directory.LookupAsync("one", default)).Status);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task MissingScopeIsDiagnosticAndNeverEchoesRemoteErrorText()
    {
        using var handler = new Handler("{\"ok\":false,\"error\":\"missing_scope\",\"provided\":\"secret\"}");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackMemberDirectory(http, "secret").LookupAsync("dot", default));
        Assert.AreEqual("directory_requires_users_read", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static object Member(string id, string name, string display, bool bot, bool deleted = false) => new
    { id, name, is_bot = bot, deleted, profile = new { display_name = display, real_name = display, email = "private@example.com" } };
    private static string Page(object[] members, string next = "") => JsonSerializer.Serialize(new
    { ok = true, members, response_metadata = new { next_cursor = next } });
    private sealed class Handler(params string[] pages) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.AreEqual("slack.com", request.RequestUri!.Host);
            Assert.AreEqual("/api/users.list", request.RequestUri.AbsolutePath);
            Assert.AreEqual("secret", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pages[Calls++]) });
        }
    }
}
