using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class PlatformApiCompactionDiagnosticsTests
{
    [TestMethod]
    public async Task RebindFailure_PreservesOnlyAllowlistedStorageDiagnostics()
    {
        using var http = new HttpClient(new ReplyHandler(JsonContent.Create(new
        {
            errorId = "92f1268954f4", nativeErrorCode = 1175, writeAttempts = 7,
            message = "private raw error with secret",
        }))) { BaseAddress = new Uri("http://localhost") };
        var api = new PlatformApiClient(http, NullLogger<PlatformApiClient>.Instance);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.RebindMainSessionAsync(new RebindMainSessionRequest
        { WorkspaceId = "default", PrincipalKind = "agent", PrincipalId = "agent", SuccessorSessionId = "next" }));
        Assert.AreEqual(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.AreEqual("92f1268954f4", error.Data["upstream_error_id"]);
        Assert.AreEqual(1175, error.Data["native_error_code"]);
        Assert.AreEqual(7, error.Data["atomic_write_attempts"]);
        Assert.IsFalse(error.ToString().Contains("secret"));
    }

    [DataTestMethod]
    [DataRow("<html>private error</html>")]
    [DataRow("[]")]
    [DataRow("{\"nativeErrorCode\":\"bad\",\"writeAttempts\":null}")]
    public async Task RebindFailure_MalformedBodyDoesNotMaskHttpFailure(string body)
    {
        using var http = new HttpClient(new ReplyHandler(new StringContent(body))) { BaseAddress = new Uri("http://localhost") };
        var api = new PlatformApiClient(http, NullLogger<PlatformApiClient>.Instance);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.RebindMainSessionAsync(new RebindMainSessionRequest
        { WorkspaceId = "default", PrincipalKind = "agent", PrincipalId = "agent", SuccessorSessionId = "next" }));
        Assert.AreEqual(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    private sealed class ReplyHandler(HttpContent body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = body });
    }
}
