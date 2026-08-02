using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PuddingAgent.Controllers;

/// <summary>
/// 本地开发与端到端测试使用的 OpenAI 兼容 Fake LLM。
/// 默认 data/config/llm.providers.json 指向该端点，避免新环境启动时依赖外部 LLM。
/// 支持非流式、流式和工具调用响应。
/// </summary>
[ApiController]
[AllowAnonymous]
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
        var isToolResult = IsLastMessageToolResult(request);
        var tools = request?["tools"]?.AsArray();

        // 如果有 tools 且最后一条不是 tool result，返回工具调用
        if (tools is { Count: > 0 } && !isToolResult)
        {
            var toolCall = BuildToolCall(request!, tools);
            if (stream)
            {
                await WriteStreamToolCallResponseAsync(model, toolCall, cancellationToken);
                return;
            }
            Response.ContentType = "application/json; charset=utf-8";
            await Response.WriteAsync(
                BuildToolCallResponse(model, toolCall).ToJsonString(JsonOptions), cancellationToken);
            return;
        }

        // 普通文本回复（含 tool result 后的回复）
        var content = BuildAssistantContent(request);
        if (stream)
        {
            await WriteStreamResponseAsync(model, content, cancellationToken);
            return;
        }

        Response.ContentType = "application/json; charset=utf-8";
        await Response.WriteAsync(BuildChatResponse(model, content).ToJsonString(JsonOptions), cancellationToken);
    }

    // ──────── Tool Call ────────

    /// <summary>
    /// 检查消息历史中最后一条是否为 tool 角色（即工具调用结果）。
    /// </summary>
    private static bool IsLastMessageToolResult(JsonNode? request)
    {
        if (request?["messages"] is not JsonArray messages || messages.Count == 0)
            return false;
        var lastRole = messages[^1]?["role"]?.GetValue<string>();
        return string.Equals(lastRole, "tool", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从请求的 tools 定义中随机选择一个工具，构造 tool_call。
    /// </summary>
    private static JsonObject BuildToolCall(JsonNode request, JsonArray tools)
    {
        var randomIndex = Random.Shared.Next(tools.Count);
        var toolDef = tools[randomIndex]!;
        var funcDef = toolDef["function"]!;
        var funcName = funcDef["name"]!.GetValue<string>();
        var toolCallId = $"call_fake_{Guid.NewGuid():N}";

        // 从工具参数定义中生成简单的参数值
        var argsObj = BuildMockArgs(funcDef["parameters"]);

        return new JsonObject
        {
            ["id"] = toolCallId,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = funcName,
                ["arguments"] = argsObj.ToJsonString(JsonOptions),
            },
        };
    }

    /// <summary>
    /// 根据工具的 parameters schema 生成 mock 参数值。
    /// 对 required 参数填占位值，可选参数跳过。
    /// </summary>
    private static JsonObject BuildMockArgs(JsonNode? parametersNode)
    {
        var args = new JsonObject();
        if (parametersNode is null)
            return args;

        var properties = parametersNode["properties"];
        if (properties is not JsonObject props)
            return args;

        var required = parametersNode["required"]?.AsArray();
        foreach (var prop in props)
        {
            // 只填充 required 参数
            if (required is not null)
            {
                var isRequired = required.Any(r =>
                    string.Equals(r?.GetValue<string>(), prop.Key, StringComparison.OrdinalIgnoreCase));
                if (!isRequired)
                    continue;
            }

            var propType = prop.Value?["type"]?.GetValue<string>() ?? "string";
            args[prop.Key] = GenerateMockValue(propType);
        }

        return args;
    }

    /// <summary>
    /// 根据 JSON Schema type 生成简单的 mock 值。
    /// </summary>
    private static JsonNode GenerateMockValue(string type) => type switch
    {
        "string" => "mock_value",
        "integer" or "number" => 42,
        "boolean" => true,
        "array" => new JsonArray(),
        "object" => new JsonObject(),
        _ => "mock_value",
    };

    /// <summary>
    /// 构造包含 tool_calls 的非流式响应。
    /// </summary>
    private static JsonObject BuildToolCallResponse(string model, JsonObject toolCall)
    {
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
                        ["content"] = (JsonNode?)null,
                        ["tool_calls"] = new JsonArray { toolCall.DeepClone() },
                    },
                    ["finish_reason"] = "tool_calls",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 20,
                ["completion_tokens"] = 10,
                ["total_tokens"] = 30,
            },
        };
    }

    // ──────── Text Response ────────

    private static string BuildAssistantContent(JsonNode? request)
    {
        // 如果是 tool result 后的回复，合并 tool 结果内容
        if (IsLastMessageToolResult(request))
        {
            var toolContent = ReadContent(request?["messages"]?.AsArray()?[^1]?["content"]);
            return $"Fake LLM processed tool result: {toolContent ?? "(empty)"}";
        }

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

    // ──────── Non-streaming Text Response ────────

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

    // ──────── Streaming Text Response ────────

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

    // ──────── Streaming Tool Call Response ────────

    /// <summary>
    /// SSE 流式发送 tool_call 响应，与 OpenAI 流式 tool_call 增量格式对齐。
    /// chunk1: name delta + id
    /// chunk2: arguments delta
    /// chunk3: finish_reason=tool_calls + usage
    /// </summary>
    private async Task WriteStreamToolCallResponseAsync(
        string model, JsonObject toolCall, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        var toolCallId = toolCall["id"]!.GetValue<string>();
        var funcName = toolCall["function"]!["name"]!.GetValue<string>();
        var funcArgs = toolCall["function"]!["arguments"]!.GetValue<string>();

        // Chunk 1: role + id + function name (not arguments yet)
        var chunk1 = BuildToolCallStreamChunk(model, index: 0, toolCallId, funcName, arguments: null);
        await WriteSseDataAsync(chunk1, cancellationToken);

        // Chunk 2: arguments
        var chunk2 = BuildToolCallStreamChunk(model, index: 0, toolCallId, null, funcArgs);
        await WriteSseDataAsync(chunk2, cancellationToken);

        // Chunk 3: usage only (no delta content)
        await WriteSseDataAsync(new JsonObject
        {
            ["id"] = $"chatcmpl-fake-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "tool_calls",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 20,
                ["completion_tokens"] = 10,
                ["total_tokens"] = 30,
            },
        }, cancellationToken);

        await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }

    private static JsonObject BuildToolCallStreamChunk(
        string model,
        int index,
        string toolCallId,
        string? funcName,
        string? arguments)
    {
        var delta = new JsonObject();

        if (funcName is not null || arguments is not null)
        {
            var functionObj = new JsonObject();
            if (funcName is not null)
                functionObj["name"] = funcName;
            if (arguments is not null)
                functionObj["arguments"] = arguments;

            delta["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = index,
                    ["id"] = funcName is not null ? toolCallId : null,
                    ["type"] = "function",
                    ["function"] = functionObj,
                },
            };
        }

        return new JsonObject
        {
            ["id"] = $"chatcmpl-fake-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                },
            },
        };
    }

    // ──────── Helpers ────────

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
