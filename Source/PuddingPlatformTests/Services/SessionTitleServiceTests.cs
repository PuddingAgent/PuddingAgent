using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SessionTitleServiceTests
{
    [TestMethod]
    public async Task BuildDefaultTitleAsync_ShouldIncrementMatchingAgentSessionTitles()
    {
        var service = new SessionTitleService(
            CreateApiClient([
                CreateSession("hot-1", "默认助手", "global:general-assistant"),
                CreateSession("hot-2", "默认助手2", "global:general-assistant"),
                CreateSession("other-agent", "默认助手9", "global:code-agent"),
            ]),
            NullLogger<SessionTitleService>.Instance);

        var title = await service.BuildDefaultTitleAsync(
            "default",
            "global:general-assistant",
            "默认助手");

        Assert.AreEqual("默认助手3", title);
    }

    private static PlatformApiClient CreateApiClient(IReadOnlyList<SessionRecord> sessions)
    {
        var handler = new StubHandler(JsonSerializer.Serialize(sessions, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new PlatformApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://controller.local"),
        });
    }

    private static SessionRecord CreateSession(string sessionId, string title, string agentTemplateId)
        => new()
        {
            SessionId = sessionId,
            WorkspaceId = "default",
            AgentTemplateId = agentTemplateId,
            ChannelId = "admin",
            OwnerUserId = "admin",
            Title = title,
        };

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }
}
