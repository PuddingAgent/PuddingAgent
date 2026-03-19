using Microsoft.AspNetCore.Mvc;
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
                ReasoningContent = result.ReasoningContent
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
}
