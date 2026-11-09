using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingSlack;

namespace PuddingSlackTests;

[TestClass]
public sealed class SlackTokenRotationTests
{
    private static SlackCredentials Credentials() => new()
    {
        AppToken = "xoxe.xapp-original", BotToken = "xoxb-bot", AppRefreshToken = "refresh-original",
        ClientId = "client", ClientSecret = "client-secret"
    };
    private static HttpResponseMessage Success(string access = "xoxe.xapp-new", string refresh = "refresh-new", string app = "A1")
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { ok = true, access_token = access, refresh_token = refresh, expires_in = 43200, app_id = app, token_type = "app_level" })) };

    [TestMethod] public async Task RotatingTokenReachesSocketEndpointUnmodified()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.AreEqual("xoxe.xapp-original", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link\"}") });
        }));
        var credentials = Credentials();
        await new SlackApiClient(http, credentials).OpenConnectionAsync(default);
        Assert.DoesNotContain("original", credentials.ToString());
        credentials.AppToken += "\r\n";
        Assert.Throws<SlackProtocolException>(() => credentials.Validate());
    }

    [TestMethod] public async Task RefreshPersistsBeforeSendAndKeepsBothNewTokensAcrossRestart()
    {
        var store = new Store(); var clock = new Clock(); var calls = 0;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            calls++;
            Assert.IsNotNull(store.Current.PendingRefreshTokenHash);
            Assert.AreEqual("/api/oauth.v2.access", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", body);
            Assert.Contains(calls == 1 ? "refresh_token=refresh-original" : "refresh_token=refresh-new", body);
            return Success(refresh: calls == 1 ? "refresh-new" : "refresh-next");
        }));
        await Provider().GetAsync(default);
        Assert.AreEqual("refresh-new", store.Current.AppRefreshToken);
        Assert.IsNull(store.Current.PendingRefreshTokenHash);
        Assert.AreEqual(clock.GetUtcNow().AddHours(12), store.Current.AppTokenExpiresAt);
        await Provider().GetAsync(default);
        Assert.AreEqual(1, calls);
        clock.Now = clock.Now.AddHours(12);
        await Provider().GetAsync(default);
        Assert.AreEqual(2, calls);
        Assert.AreEqual("refresh-next", store.Current.AppRefreshToken);
        SlackAppTokenProvider Provider() => new(http, store, "A1", clock);
    }

    [TestMethod] public async Task ConcurrentRefreshUsesOneRequest()
    {
        var store = new Store(); var calls = 0;
        using var http = new HttpClient(new Handler(async (_, _) => { Interlocked.Increment(ref calls); await Task.Delay(20); return Success(); }));
        var provider = new SlackAppTokenProvider(http, store, "A1");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetAsync(default)));
        Assert.AreEqual(1, calls);
    }

    [TestMethod] public async Task UnknownRefreshSurvivesRestartAndNeverReusesRefreshToken()
    {
        var store = new Store(); var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException("secret must not escape"); }));
        var ex = await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        Assert.AreEqual("app_refresh_outcome_unknown", ex.Code);
        await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        Assert.AreEqual(1, calls);
        Assert.IsNotNull(store.Current.PendingRefreshTokenHash);
    }

    [TestMethod] public async Task FailureToPersistPendingPreventsRefreshRequest()
    {
        var store = new Store { FailWrite = 1 }; var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Success()); }));
        await Assert.ThrowsAsync<IOException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        Assert.AreEqual(0, calls);
    }

    [TestMethod] public async Task FailureToPersistResponseLeavesUnknownAndDoesNotReturnNewToken()
    {
        var store = new Store { FailWrite = 2 }; var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Success()); }));
        await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        Assert.AreEqual(1, calls);
        Assert.AreEqual("xoxe.xapp-original", store.Current.AppToken);
    }

    [TestMethod] public async Task WrongAppResponseCannotReplaceCredentialPair()
    {
        var store = new Store();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Success(app: "AOTHER"))));
        var ex = await Assert.ThrowsAsync<SlackProtocolException>(() => new SlackAppTokenProvider(http, store, "A1").GetAsync(default));
        Assert.AreEqual("app_refresh_response_mismatch", ex.Code);
        Assert.AreEqual("refresh-original", store.Current.AppRefreshToken);
        Assert.IsNotNull(store.Current.PendingRefreshTokenHash);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : ISlackCredentialStore
    {
        public SlackCredentials Current = Credentials();
        public int FailWrite; private int _writes;
        public SlackCredentialSnapshot Read() => new(Current.Copy(), _writes.ToString());
        public SlackCredentialSnapshot Write(SlackCredentials value, string expectedRevision)
        {
            Assert.AreEqual(_writes.ToString(), expectedRevision);
            if (++_writes == FailWrite) throw new IOException();
            Current = value.Copy(); return Read();
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
