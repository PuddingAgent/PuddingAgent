using System.Net;
using System.Text;
using System.Text.Json;
using PuddingChat;

namespace PuddingChatTests;

public class ChatTests
{
    private static readonly RoleKey A = new("workspace", "builder");
    private static Conversation View(RoleKey role, long cursor, string session = "session") =>
        new(role.WorkspaceId, role.AgentId, session, [], null, cursor);

    [Fact] public void SelectionRejectsLateResponsesAndKeepsDraftsPerWorkspace()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "first";
        var epoch = state.Generation;
        var other = new RoleKey("other", A.AgentId); state.Select(other); state.Draft = "second";
        Assert.False(state.Apply(epoch, View(A, 10)));
        Assert.False(state.Apply(state.Generation, View(A, 10)));
        Assert.Equal("second", state.Draft);
        state.Select(A); Assert.Equal("first", state.Draft);
    }
    [Fact] public void RetryRetainsCommandIdentityAndDoesNotClearNewDraft()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "implement";
        var pending = state.Prepare("session"); state.Draft = "new draft";
        Assert.Same(pending, state.Prepare("session"));
        state.Select(new("other", "reviewer")); state.Draft = "review";
        state.Accept(pending); Assert.Equal("review", state.Draft);
        state.Select(A); Assert.Equal("new draft", state.Draft); Assert.Null(state.Pending);
        Assert.NotEqual(pending.ClientRequestId, state.Prepare("session").ClientRequestId);
    }
    [Fact] public void MatchingReceiptClearsOnlyOriginalDraft()
    {
        var state = new ChatSelection(); state.Select(A); state.Draft = "hello";
        state.Accept(state.Prepare("s")); Assert.Equal("", state.Draft);
    }
    [Fact] public void CursorOrderingAllowsSessionRotation()
    {
        var state = new ChatSelection(); state.Select(A);
        Assert.True(state.Apply(state.Generation, View(A, 20)));
        Assert.False(state.Apply(state.Generation, View(A, 19)));
        Assert.True(state.Apply(state.Generation, View(A, 1, "rotated")));
    }
    [Fact] public void EventsUseCanonicalSequenceAndNeverPairToolsByName()
    {
        var first = new ProcessItem("a", "tool_call", "running", "", 5, "shell", ToolCallId: "call1");
        var second = new ProcessItem("b", "tool_call", "running", "", 3, "shell", ToolCallId: "call2");
        Assert.Equal(new[] { second, first }, ChatSelection.Ordered([first, second, first]));
        Assert.Null(ChatSelection.ActiveTurn(View(A, 0)));
    }
    [Fact] public async Task AuthenticatedSendUsesAdmissionAndStableIdsAfterUncertainResponse()
    {
        var attempts = new List<string>(); var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login/account") return Json("{\"status\":\"ok\",\"token\":\"test-token\"}");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("workspace", request.Headers.GetValues("X-Workspace-Id").Single());
            Assert.Equal("/api/v1/conversations/session/turns", request.RequestUri.AbsolutePath);
            attempts.Add(await request.Content!.ReadAsStringAsync(ct));
            if (attempts.Count == 1) throw new HttpRequestException("network lost after acceptance");
            return Json("{\"conversationId\":\"session\",\"messageId\":\"m\",\"turnIds\":[\"t\"],\"acceptedSequence\":42}", HttpStatusCode.Accepted);
        });
        using var client = new HttpChatClient(new("http://127.0.0.1:12345/admin/"), handler);
        await client.LoginAsync("user", "password", default);
        var send = PendingSend.Create(A, "session", "hello");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(send, default));
        var accepted = await client.SendAsync(send, default);
        Assert.Equal("t", accepted.TurnIds.Single()); Assert.Equal(attempts[0], attempts[1]);
        using var body = JsonDocument.Parse(attempts[1]);
        Assert.Equal("agent", body.RootElement.GetProperty("recipients").GetProperty("type").GetString());
        Assert.Equal("builder", body.RootElement.GetProperty("recipients").GetProperty("agentIds")[0].GetString());
    }
    [Fact] public async Task NotModifiedAndUnauthorizedAreDistinct()
    {
        using var client = new HttpChatClient(new("http://localhost:1234"), new Handler((r, _) =>
        { Assert.EndsWith("?knownCursor=12", r.RequestUri!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }));
        Assert.Null(await client.GetConversationAsync(A, 12, default));
        using var unauthorized = new HttpChatClient(new("http://localhost:1234"), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unauthorized.GetWorkspacesAsync(default));
    }
    [Fact] public async Task CancelUsesServerTurnIdentityAndWorkspaceHeader()
    {
        using var client = new HttpChatClient(new("http://localhost:1234"), new Handler((r, _) =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("/api/v1/conversations/s/turns/server-turn/cancel", r.RequestUri!.AbsolutePath);
            Assert.Equal("workspace", r.Headers.GetValues("X-Workspace-Id").Single());
            return Task.FromResult(Json("{}"));
        }));
        await client.CancelAsync("workspace", "s", "server-turn", default);
    }
    [Fact] public void BoundaryContainsOnlyBclDependencies()
    {
        var refs = typeof(ChatSelection).Assembly.GetReferencedAssemblies();
        Assert.All(refs, r => Assert.True(r.Name!.StartsWith("System.") || r.Name is "System" or "netstandard", r.Name));
        Assert.Throws<ArgumentException>(() => new HttpChatClient(new("https://example.com")));
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}

