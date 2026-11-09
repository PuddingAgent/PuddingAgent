using System.Net;
using System.Net.Http.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class WorkSdkTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");
    private static readonly AgentWorkReceipt Receipt = new(Agent, "op", "work", "session", "input", "turn", "command", 1, DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task WorkFacade_SubmitsAndQueriesSameOperation_WithoutOwningTransport()
    {
        var seen = new List<string>();
        using var client = Client(request =>
        {
            seen.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.Method == HttpMethod.Post ? Ok(Receipt) : Ok(new AgentWorkSnapshot(Receipt, AgentWorkStatus.Accepted, null, null)));
        });
        var work = new AgentWorkClient(new HttpAgentWorkApi(client)).For(Agent);
        Assert.AreEqual(Receipt, await work.SubmitAsync(new("op", "你好")));
        Assert.AreEqual("not_requested", (await work.GetAsync("op"))!.Verification);
        CollectionAssert.AreEqual(new[] { "POST /api/workspaces/workspace/agents/agent/work", "GET /api/workspaces/workspace/agents/agent/work/op" }, seen);
    }

    [TestMethod]
    public async Task LostSubmitResponse_IsUnknown_AndDoesNotRetry()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; throw new HttpRequestException("lost"); });
        var ex = await Assert.ThrowsExactlyAsync<AgentWorkException>(() => new HttpAgentWorkApi(client).SubmitAsync(Agent, new("op", "hello")));
        Assert.AreEqual("unknown", ex.EffectOutcome); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task UnsafeRouteIds_AreRejectedBeforeHttpClientCanNormalizeThem()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return Task.FromResult(Ok(Receipt)); });
        var api = new HttpAgentWorkApi(client);
        foreach (var id in new[] { ".", "..", "../other", "other/agent", "" })
        {
            Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.GetAsync(Agent, id))).Code);
            await Assert.ThrowsExactlyAsync<AgentWorkException>(() => api.SubmitAsync(new(id, "agent"), new("op", "hello")));
        }
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task ForeignOrIncompleteReceipt_IsUnknown()
    {
        foreach (var body in new object[] { Receipt with { OperationId = "another" }, new { workId = "fake" } })
        {
            using var client = Client(_ => Task.FromResult(Ok(body)));
            Assert.AreEqual("unknown", (await Assert.ThrowsExactlyAsync<AgentWorkException>(() => new HttpAgentWorkApi(client).SubmitAsync(Agent, new("op", "hello")))).EffectOutcome);
        }
    }

    [TestMethod]
    public async Task MissingAcceptance_IsNull_ButMissingTerminalIsNotCompleted()
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        Assert.IsNull(await new HttpAgentWorkApi(client).GetAsync(Agent, "op"));
        using var corrupt = Client(_ => Task.FromResult(Ok(new AgentWorkSnapshot(Receipt, AgentWorkStatus.Completed, null, null))));
        await Assert.ThrowsExactlyAsync<AgentWorkException>(() => new HttpAgentWorkApi(corrupt).GetAsync(Agent, "op"));
    }

    [TestMethod]
    public async Task CancellingObservation_NeverSendsWorkCancellation()
    {
        using var cts = new CancellationTokenSource(); var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++; Assert.AreEqual(HttpMethod.Get, request.Method); cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Ok(Receipt);
        })) { BaseAddress = new("http://localhost/") };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new HttpAgentWorkApi(client).GetAsync(Agent, "op", cts.Token));
        Assert.AreEqual(1, calls);
    }

    private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => new(new Handler((request, _) => action(request))) { BaseAddress = new("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct);
    }
}
