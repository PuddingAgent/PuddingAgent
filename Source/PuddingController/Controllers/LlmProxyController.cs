using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// 内部 LLM 代理 API。
/// Runtime 应通过 Controller 调用此接口，避免直接接触外部 LLM 密钥。
/// </summary>
[ApiController]
[Route("api/internal/llm")]
public sealed class LlmProxyController(
    ControllerLlmProxyService llmProxyService,
    ILogger<LlmProxyController> logger) : ControllerBase
{
    [HttpPost("chat")]
    public async Task<ActionResult<ControllerLlmChatResponse>> Chat(
        [FromBody] ControllerLlmChatRequest request,
        CancellationToken ct)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return BadRequest("messages cannot be empty");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "[LlmProxy] REQUEST ws={Ws} session={Session} template={Template} msgCount={Count} hasLlmConfig={HasConfig}",
            request.WorkspaceId, request.SessionId, request.AgentTemplateId,
            request.Messages.Count, request.LlmConfig is not null);

        try
        {
            var result = await llmProxyService.ChatAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.Messages,
                request.Tools,
                request.LlmConfig,
                ct);

            sw.Stop();
            logger.LogInformation(
                "[LlmProxy] OK ws={Ws} session={Session} contentLen={Len} elapsed={Elapsed}ms",
                request.WorkspaceId, request.SessionId,
                result.Content?.Length ?? 0, sw.ElapsedMilliseconds);

            return Ok(new ControllerLlmChatResponse
            {
                Content = result.Content,
                ToolCalls = result.ToolCalls,
                ReasoningContent = result.ReasoningContent,
                Usage = result.Usage,
            });
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            logger.LogError(
                "[LlmProxy] ERROR (config) ws={Ws} session={Session} elapsed={Elapsed}ms msg={Msg}",
                request.WorkspaceId, request.SessionId, sw.ElapsedMilliseconds, ex.Message);
            return StatusCode(502, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogError(
                "[LlmProxy] ERROR (http) ws={Ws} session={Session} elapsed={Elapsed}ms msg={Msg}",
                request.WorkspaceId, request.SessionId, sw.ElapsedMilliseconds, ex.Message);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    [HttpPost("chat/stream")]
    public async Task ChatStream(
        [FromBody] ControllerLlmChatRequest request,
        CancellationToken ct)
    {
        if (request.Messages is null || request.Messages.Count == 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("messages cannot be empty", ct);
            return;
        }

        ConfigureSseResponse(Response);

        try
        {
            await foreach (var delta in llmProxyService.ChatStreamAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.Messages,
                request.Tools,
                request.LlmConfig,
                ct))
            {
                if (delta.Usage is not null)
                    await WriteSseAsync(Response, "usage", delta.Usage, ct);

                if (delta.ContentDelta is not null
                    || delta.ReasoningDelta is not null
                    || delta.ToolCallIndex is not null
                    || delta.FinishReason is not null)
                {
                    await WriteSseAsync(Response, "delta", delta, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "[LlmProxy] STREAM cancelled ws={Ws} session={Session}",
                request.WorkspaceId, request.SessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[LlmProxy] STREAM error ws={Ws} session={Session}",
                request.WorkspaceId, request.SessionId);
            await WriteSseAsync(Response, "error", new { message = ex.Message }, CancellationToken.None);
        }
    }

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"event: {eventName}\n", ct);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
