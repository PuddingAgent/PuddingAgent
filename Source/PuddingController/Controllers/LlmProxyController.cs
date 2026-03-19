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
public sealed class LlmProxyController(ControllerLlmProxyService llmProxyService) : ControllerBase
{
    [HttpPost("chat")]
    public async Task<ActionResult<ControllerLlmChatResponse>> Chat(
        [FromBody] ControllerLlmChatRequest request,
        CancellationToken ct)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return BadRequest("messages cannot be empty");

        try
        {
            var result = await llmProxyService.ChatAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.Messages,
                request.LlmConfig,
                ct);

            return Ok(new ControllerLlmChatResponse
            {
                Content = result.Content,
                ToolCalls = result.ToolCalls,
                ReasoningContent = result.ReasoningContent
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
