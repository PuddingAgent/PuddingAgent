using System.Net;
using System.Net.Http.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

/// <summary>PA-02 SDK Work control: observe/cancel use the documented routes, unsafe ids never reach HttpClient, and a
/// lost cancel response is reported as unknown rather than "stopped".</summary>
[TestClass]
public sealed class WorkControlSdkTests
{
    private static readonly AgentRef Agent = new("workspace", "agent");

    [TestMethod]
    public async Task ObserveAndCancel_UseTheControlRoutes()
    {
        var seen = new List<string>();
        var receipts = new List<CancelWorkRequest>();
        using var client = Client(request =>
        {
            seen.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Post)
            {
                receipts.Add(request.Content!.ReadFromJsonAsync<CancelWorkRequest>().GetAwaiter().GetResult()!);
                return Task.FromResult(Ok(new CancelWorkReceipt("cancel-1", "op", CancelWorkEffect.Requested, 3,
                    "Running")));
            }
            return Task.FromResult(Ok(new WorkObservation("op", "Running", [], 1, false, false)));
        });
        var control = new AgentWorkControlClient(new HttpAgentWorkControlApi(client)).For(Agent);
        var observation = await control.ObserveAsync("op", afterSequence: 0, maxCount: 50);
        Assert.AreEqual("op", observation.OperationId);
        var receipt = await control.CancelAsync("cancel-1", "op", expectedRevision: 3);
        Assert.AreEqual(CancelWorkEffect.Requested, receipt.Effect);
        Assert.AreEqual("op", receipts.Single().TargetOperationId);
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/workspaces/workspace/agents/agent/work-control/op?afterSequence=0&maxCount=50",
            "POST /api/workspaces/workspace/agents/agent/work-control/op/cancel",
        }, seen);
    }

    [TestMethod]
    public async Task UnsafeIds_AreRejectedBeforeTransport()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return Task.FromResult(Ok(new WorkObservation("op", "Running", [], 0,
            false, false))); });
        var api = new HttpAgentWorkControlApi(client);
        foreach (var id in new[] { ".", "..", "other/agent", "" })
        {
            Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<AgentWorkControlException>(() =>
                api.ObserveAsync(new(Agent, id, 0)))).Code);
            Assert.AreEqual("request.invalid", (await Assert.ThrowsExactlyAsync<AgentWorkControlException>(() =>
                api.CancelAsync(new("cancel-1", Agent, id, 1)))).Code);
        }
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task LostCancelResponse_IsUnknown_NotStopped()
    {
        using var client = Client(_ => throw new HttpRequestException("lost"));
        var failure = await Assert.ThrowsExactlyAsync<AgentWorkControlException>(() =>
            new HttpAgentWorkControlApi(client).CancelAsync(new("cancel-1", Agent, "op", 1)));
        Assert.AreEqual("cancel.unknown", failure.Code);
    }

    private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> action)
        => new(new Handler((request, _) => action(request))) { BaseAddress = new("http://localhost/") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => action(request, ct);
    }
}
