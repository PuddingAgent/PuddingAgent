using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class SdkTests
{
    [TestMethod]
    public async Task Sdk_UsesBoundResourceAndPreservesOperationId()
    {
        var reference = new AgentRef("workspace", "agent"); var calls = new List<string>();
        using var handler = new Handler(async request =>
        {
            calls.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new AgentConfigurationSnapshot(reference, "r1", "Old", null, "t", DateTimeOffset.UnixEpoch)) };
            var body = await request.Content!.ReadFromJsonAsync<ConfigurationUpdateRequest>();
            Assert.AreEqual("operation", body!.Options.OperationId);
            Assert.AreEqual("New", body.Patch.DisplayName.Value);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ConfigurationChangeReceipt("operation", reference,
                "r1", "r2", "r2", "configuration-source", true, 1, DateTimeOffset.UnixEpoch)) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var client = new PuddingClient(new HttpAgentConfigurationApi(http));
        var agent = await client.Workspace("workspace").Agents.GetAsync("agent");
        var result = await agent.Configuration.UpdateAsync(new() { DisplayName = "New" }, new("r1", "operation"));
        Assert.AreEqual("r2", result.PersistedRevision);
        CollectionAssert.AreEqual(new[] { "GET /api/workspaces/workspace/agents/agent/configuration", "PATCH /api/workspaces/workspace/agents/agent/configuration" }, calls);
    }

    [TestMethod]
    public async Task LostMutationResponse_IsUnknown_AndNeverAutomaticallyRetried()
    {
        var calls = 0;
        using var handler = new Handler(_ => { calls++; throw new HttpRequestException("Lost after server commit"); });
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var api = new HttpAgentConfigurationApi(http);
        var error = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => api.UpdateAsync(new("w", "a"), new() { DisplayName = "New" }, new("r", "op")));
        Assert.AreEqual("operation.unknown", error.Code);
        Assert.AreEqual("unknown", error.EffectOutcome);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task StructuredServerConflict_IsPreserved()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        { Content = JsonContent.Create(new ConfigurationApiError("revision.conflict", "Changed")) }));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var error = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => new HttpAgentConfigurationApi(http)
            .UpdateAsync(new("w", "a"), new() { DisplayName = "New" }, new("r", "op")));
        Assert.AreEqual("revision.conflict", error.Code);
        Assert.AreEqual("none", error.EffectOutcome);
    }

    [TestMethod]
    public async Task BrokenSuccessfulResponse_IsNotClaimedAsSuccess()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var error = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => new HttpAgentConfigurationApi(http)
            .UpdateAsync(new("w", "a"), new() { DisplayName = "New" }, new("r", "op")));
        Assert.AreEqual("operation.unknown", error.Code);
    }

    [TestMethod]
    public async Task ReceiptForAnotherOperation_IsUnknown()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new ConfigurationChangeReceipt("another", new("w", "a"), "r", "new", "new", "configuration-source", true, 1, DateTimeOffset.UnixEpoch)) }));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var failure = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => new HttpAgentConfigurationApi(http)
            .UpdateAsync(new("w", "a"), new() { DisplayName = "New" }, new("r", "op")));
        Assert.AreEqual("operation.unknown", failure.Code);
    }

    [TestMethod]
    public async Task MissingReceiptFields_CannotDeserializeAsSuccess()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var failure = await Assert.ThrowsExactlyAsync<ConfigurationApiException>(() => new HttpAgentConfigurationApi(http)
            .UpdateAsync(new("w", "a"), new() { DisplayName = "New" }, new("r", "op")));
        Assert.AreEqual("operation.unknown", failure.Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
}
