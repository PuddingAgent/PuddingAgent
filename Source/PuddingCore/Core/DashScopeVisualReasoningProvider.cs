using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>Configuration for DashScope visual reasoning through the OpenAI-compatible API.</summary>
public sealed record DashScopeVisualReasoningOptions(string Endpoint, string ApiKey);

/// <summary>
/// DashScope visual reasoning provider for QVQ/Qwen-VL style streaming responses.
/// It normalizes provider SSE chunks into visual reasoning events and keeps provider
/// payloads out of UI-facing projections.
/// </summary>
public sealed class DashScopeVisualReasoningProvider(
    HttpClient httpClient,
    DashScopeVisualReasoningOptions options) : IVisualReasoningProvider
{
    private readonly string _chatEndpoint = NormalizeChatEndpoint(options.Endpoint);

    public VisualReasoningProviderCapabilities Capabilities { get; } = new()
    {
        Provider = VisualReasoningProviders.DashScope,
        SupportedTransports =
        [
            VisualReasoningTransports.OpenAiCompatibleSse,
        ],
        SupportedModels =
        [
            new VisualReasoningModelCapability
            {
                Model = "qvq-max",
                ThinkingMode = VisualReasoningThinkingModes.AlwaysOn,
                RequiresStreaming = true,
                SupportsEnableThinking = false,
                SupportsThinkingBudget = false,
                SupportedInputKinds = [VisualInputKinds.ImageUrl, VisualInputKinds.ImageArtifact],
            },
            new VisualReasoningModelCapability
            {
                Model = "qvq-plus",
                ThinkingMode = VisualReasoningThinkingModes.AlwaysOn,
                RequiresStreaming = true,
                SupportsEnableThinking = false,
                SupportsThinkingBudget = false,
                SupportedInputKinds = [VisualInputKinds.ImageUrl, VisualInputKinds.ImageArtifact],
            },
            new VisualReasoningModelCapability
            {
                Model = "qwen3-vl-plus",
                ThinkingMode = VisualReasoningThinkingModes.Toggleable,
                RequiresStreaming = false,
                SupportsEnableThinking = true,
                SupportsThinkingBudget = true,
                SupportedInputKinds = [VisualInputKinds.ImageUrl, VisualInputKinds.ImageArtifact, VisualInputKinds.VideoUrl],
            },
            new VisualReasoningModelCapability
            {
                Model = "qwen3-vl-flash",
                ThinkingMode = VisualReasoningThinkingModes.Toggleable,
                RequiresStreaming = false,
                SupportsEnableThinking = true,
                SupportsThinkingBudget = true,
                SupportedInputKinds = [VisualInputKinds.ImageUrl, VisualInputKinds.ImageArtifact, VisualInputKinds.VideoUrl],
            },
        ],
        RequiresServerSideCredential = true,
    };

    public async Task<VisualReasoningResult> AnalyzeAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default)
    {
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        string? requestId = null;
        VisualReasoningUsage? usage = null;

        await foreach (var chunk in ReadStreamAsync(request, ct))
        {
            if (!string.IsNullOrWhiteSpace(chunk.RequestId))
                requestId = chunk.RequestId;

            if (chunk.Usage is not null)
                usage = chunk.Usage;

            if (chunk.Event?.ReasoningDelta is { Length: > 0 } reasoningDelta)
                reasoning.Append(reasoningDelta);

            if (chunk.Event?.AnswerDelta is { Length: > 0 } answerDelta)
                answer.Append(answerDelta);
        }

        return new VisualReasoningResult
        {
            SessionId = request.SessionId,
            WorkspaceId = request.WorkspaceId,
            RoomId = request.RoomId,
            ParticipantId = request.ParticipantId,
            Answer = answer.ToString(),
            ReasoningSummary = reasoning.Length == 0 ? null : reasoning.ToString(),
            Provider = VisualReasoningProviders.DashScope,
            Model = request.Model,
            RequestId = requestId,
            InputTokens = usage?.PromptTokens,
            OutputTokens = usage?.CompletionTokens,
            ImageTokens = usage?.ImageTokens,
            VideoTokens = usage?.VideoTokens,
            Metadata =
            {
                ["inputMode"] = "vision",
                ["visionProvider"] = VisualReasoningProviders.DashScope,
                ["visionModel"] = request.Model,
            },
        };
    }

    public async IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
        VisualReasoningRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sequence = 0;
        string? requestId = null;

        yield return new VisualReasoningStreamEvent
        {
            Type = VisualReasoningStreamEventTypes.SessionStarted,
            SessionId = request.SessionId,
            Sequence = sequence++,
        };

        await foreach (var chunk in ReadStreamAsync(request, ct))
        {
            if (!string.IsNullOrWhiteSpace(chunk.RequestId))
                requestId = chunk.RequestId;

            if (chunk.Event is null)
                continue;

            yield return chunk.Event with
            {
                Sequence = sequence++,
                ProviderRequestId = chunk.RequestId ?? requestId,
                ProviderRawPayload = null,
            };
        }

        yield return new VisualReasoningStreamEvent
        {
            Type = VisualReasoningStreamEventTypes.Completed,
            SessionId = request.SessionId,
            Sequence = sequence,
            ProviderRequestId = requestId,
        };
    }

    private async IAsyncEnumerable<VisualReasoningProviderChunk> ReadStreamAsync(
        VisualReasoningRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(BuildRequestBody(request), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorJson = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"DashScope visual reasoning API error ({response.StatusCode}): {errorJson}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                yield break;

            var chunk = ParseOpenAiCompatibleChunk(request.SessionId, data);
            if (chunk is not null)
                yield return chunk;
        }
    }

    private string BuildRequestBody(VisualReasoningRequest request)
    {
        var content = new JsonArray();
        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Uri))
            {
                throw new InvalidOperationException(
                    $"Visual input '{input.ArtifactId}' must have a resolved URI before calling DashScope.");
            }

            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = input.Uri,
                },
            });
        }

        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = request.Prompt,
        });

        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt,
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = content,
        });

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject
            {
                ["include_usage"] = true,
            },
        };

        var model = FindModel(request.Model);
        if (model?.SupportsEnableThinking == true)
            body["enable_thinking"] = request.EnableThinking;

        if (request.ThinkingBudgetTokens is { } budget && model?.SupportsThinkingBudget == true)
            body["thinking_budget"] = budget;

        return body.ToJsonString();
    }

    private VisualReasoningModelCapability? FindModel(string model)
        => Capabilities.SupportedModels.FirstOrDefault(candidate =>
            string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase));

    private static VisualReasoningProviderChunk? ParseOpenAiCompatibleChunk(string sessionId, string json)
    {
        var root = JsonNode.Parse(json);
        var requestId = root?["id"]?.GetValue<string>();
        var usage = ParseUsage(root?["usage"]);
        var choices = root?["choices"]?.AsArray();
        if (choices is null || choices.Count == 0)
            return usage is null && requestId is null ? null : new VisualReasoningProviderChunk(null, requestId, usage);

        var delta = choices[0]?["delta"];
        if (delta is null)
            return usage is null && requestId is null ? null : new VisualReasoningProviderChunk(null, requestId, usage);

        var reasoningDelta = delta["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(reasoningDelta))
        {
            return new VisualReasoningProviderChunk(
                VisualReasoningStreamEvent.CreateReasoningDelta(sessionId, reasoningDelta, sequence: 0),
                requestId,
                usage);
        }

        var answerDelta = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(answerDelta))
        {
            return new VisualReasoningProviderChunk(
                VisualReasoningStreamEvent.CreateAnswerDelta(sessionId, answerDelta, sequence: 0),
                requestId,
                usage);
        }

        return usage is null && requestId is null ? null : new VisualReasoningProviderChunk(null, requestId, usage);
    }

    private static VisualReasoningUsage? ParseUsage(JsonNode? usage)
    {
        if (usage is null)
            return null;

        var promptDetails = usage["prompt_tokens_details"];
        return new VisualReasoningUsage(
            PromptTokens: ReadInt(usage, "prompt_tokens"),
            CompletionTokens: ReadInt(usage, "completion_tokens"),
            ImageTokens: ReadInt(promptDetails, "image_tokens") ?? ReadInt(usage, "image_tokens"),
            VideoTokens: ReadInt(promptDetails, "video_tokens") ?? ReadInt(usage, "video_tokens"));
    }

    private static int? ReadInt(JsonNode? node, string propertyName)
    {
        try
        {
            return node?[propertyName]?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeChatEndpoint(string endpoint)
    {
        var url = endpoint.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;
        return url + "/chat/completions";
    }

    private sealed record VisualReasoningProviderChunk(
        VisualReasoningStreamEvent? Event,
        string? RequestId,
        VisualReasoningUsage? Usage);

    private sealed record VisualReasoningUsage(
        int? PromptTokens,
        int? CompletionTokens,
        int? ImageTokens,
        int? VideoTokens);
}
