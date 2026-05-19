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

    [TestMethod]
    public async Task ChatCompletions_WithTools_ReturnsToolCall()
    {
        var response = await _client.PostAsJsonAsync("/__fake_llm/v1/chat/completions", new
        {
            model = "fake-chat",
            messages = new[]
            {
                new { role = "user", content = "read the file test.txt" }
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "read_file",
                        description = "Read a file",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string", description = "File path" }
                            },
                            required = new[] { "path" }
                        }
                    }
                }
            }
        });

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.AreEqual("chat.completion", json["object"]!.GetValue<string>());
        var message = json["choices"]![0]!["message"]!;
        Assert.AreEqual("assistant", message["role"]!.GetValue<string>());
        Assert.IsNotNull(message["tool_calls"]);
        var toolCall = message["tool_calls"]![0]!;
        Assert.AreEqual("function", toolCall["type"]!.GetValue<string>());
        Assert.AreEqual("read_file", toolCall["function"]!["name"]!.GetValue<string>());
        Assert.AreEqual("tool_calls", json["choices"]![0]!["finish_reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChatCompletions_AfterToolResult_ReturnsTextResponse()
    {
        var response = await _client.PostAsJsonAsync("/__fake_llm/v1/chat/completions", new
        {
            model = "fake-chat",
            messages = new object[]
            {
                new { role = "user", content = "read file test.txt" },
                new { role = "assistant", content = (string?)null, tool_calls = new[]
                    {
                        new
                        {
                            id = "call_12345",
                            type = "function",
                            function = new { name = "read_file", arguments = "{\"path\":\"test.txt\"}" }
                        }
                    }
                },
                new { role = "tool", tool_call_id = "call_12345", content = "file contents here" }
            }
        });

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.AreEqual("stop", json["choices"]![0]!["finish_reason"]!.GetValue<string>());
        var content = json["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        StringAssert.Contains(content, "file contents here");
    }

    [TestMethod]
    public async Task ChatCompletions_WithTools_Stream_ReturnsSseToolCallDeltas()
    {
        using var response = await _client.PostAsJsonAsync("/__fake_llm/v1/chat/completions", new
        {
            model = "fake-chat",
            stream = true,
            messages = new[]
            {
                new { role = "user", content = "run tool" }
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "echo",
                        description = "Echo back",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                message = new { type = "string", description = "Message" }
                            },
                            required = new[] { "message" }
                        }
                    }
                }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "data: ");
        StringAssert.Contains(body, "[DONE]");
        StringAssert.Contains(body, "\"function\"");
        StringAssert.Contains(body, "\"tool_calls\"");
    }
}
