using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PuddingWebApiTests;

[TestClass]
public sealed class FakeLlmControllerTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private static HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task ChatCompletions_ReturnsOpenAiCompatibleResponse()
    {
        var response = await _client.PostAsJsonAsync("/__fake_llm/v1/chat/completions", new
        {
            model = "fake-chat",
            messages = new[]
            {
                new { role = "user", content = "ping" }
            }
        });

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.AreEqual("chat.completion", json["object"]!.GetValue<string>());
        Assert.AreEqual("fake-chat", json["model"]!.GetValue<string>());
        Assert.AreEqual("assistant", json["choices"]![0]!["message"]!["role"]!.GetValue<string>());
        StringAssert.Contains(json["choices"]![0]!["message"]!["content"]!.GetValue<string>(), "ping");
        Assert.IsTrue(json["usage"]!["total_tokens"]!.GetValue<int>() > 0);
    }

    [TestMethod]
    public async Task ChatCompletions_Stream_ReturnsSseDeltasAndDone()
    {
        using var response = await _client.PostAsJsonAsync("/__fake_llm/v1/chat/completions", new
        {
            model = "fake-chat",
            stream = true,
            messages = new[]
            {
                new { role = "user", content = "stream ping" }
            }
        });

        response.EnsureSuccessStatusCode();
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "data: ");
        StringAssert.Contains(body, "\"object\":\"chat.completion.chunk\"");
        StringAssert.Contains(body, "[DONE]");
    }
}
