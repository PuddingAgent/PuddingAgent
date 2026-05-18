using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace PuddingAgent.Controllers;

/// <summary>
/// 本地开发与端到端测试使用的 OpenAI 兼容 Fake LLM。
/// 默认 data/config/llm.providers.json 指向该端点，避免新环境启动时依赖外部 LLM。
/// </summary>
[ApiController]
[Route("__fake_llm/v1")]
public sealed class FakeLlmController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost("chat/completions")]
    public async Task ChatCompletions(CancellationToken cancellationToken)
    {
        var request = await JsonNode.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        var model = request?["model"]?.GetValue<string>() ?? "fake-chat";
        var stream = request?["stream"]?.GetValue<bool>() == true;
        var content = BuildAssistantContent(request);

        if (stream)
        {
            await WriteStreamResponseAsync(model, content, cancellationToken);
            return;
        }

        Response.ContentType = "application/json; charset=utf-8";
        await Response.WriteAsync(BuildChatResponse(model, content).ToJsonString(JsonOptions), cancellationToken);
    }

    private static string BuildAssistantContent(JsonNode? request)
    {
        var userMessage = LastUserMessage(request);
        return string.IsNullOrWhiteSpace(userMessage)
            ? "Fake LLM ready."
            : $"Fake LLM response: {userMessage}";
    }

    private static string? LastUserMessage(JsonNode? request)
    {
        if (request?["messages"] is not JsonArray messages)
            return null;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            var role = message?["role"]?.GetValue<string>();
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            return ReadContent(message?["content"]);
        }

        return null;
    }

    private static string? ReadContent(JsonNode? content)
    {
        if (content is null)
            return null;

        if (content is JsonValue)
            return content.GetValue<string>();

        if (content is not JsonArray parts)
            return content.ToJsonString(JsonOptions);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part?["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static JsonObject BuildChatResponse(string model, string content)
    {
        var usage = BuildUsage(content);
        return new JsonObject
        {
            ["id"] = $"chatcmpl-fake-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content,
                    },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = usage,
        };
    }

    private async Task WriteStreamResponseAsync(string model, string content, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        await WriteSseDataAsync(BuildStreamChunk(model, content, finishReason: null, includeUsage: false), cancellationToken);
        await WriteSseDataAsync(BuildStreamChunk(model, null, finishReason: "stop", includeUsage: false), cancellationToken);
        await WriteSseDataAsync(new JsonObject
        {
            ["id"] = $"chatcmpl-fake-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(),
            ["usage"] = BuildUsage(content),
        }, cancellationToken);
        await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }

    private async Task WriteSseDataAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"data: {payload.ToJsonString(JsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static JsonObject BuildStreamChunk(
        string model,
        string? content,
        string? finishReason,
        bool includeUsage)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = content is null
                ? new JsonObject()
                : new JsonObject { ["content"] = content },
            ["finish_reason"] = finishReason,
        };

        var payload = new JsonObject
        {
            ["id"] = $"chatcmpl-fake-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray { choice },
        };

        if (includeUsage)
            payload["usage"] = BuildUsage(content ?? string.Empty);

        return payload;
    }

    private static JsonObject BuildUsage(string content)
    {
        var completionTokens = Math.Max(1, content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var promptTokens = 8;
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
        };
    }
}
