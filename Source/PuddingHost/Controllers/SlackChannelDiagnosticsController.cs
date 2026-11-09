using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Services;
using PuddingCode.Platform;
using PuddingSlack;

namespace PuddingAgent.Controllers;

[Authorize]
[ApiController]
[Route("api/slack/channels/{channelId}")]
public sealed class SlackChannelDiagnosticsController(ConnectorHost host) : ControllerBase
{
    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(string channelId, CancellationToken ct)
    {
        var result = await host.GetConnectorDiagnosticsAsync(SlackChannelMetadata.ConnectorId(channelId), ct);
        return result is null ? NotFound(new { code = "connector_not_registered" }) : Ok(result);
    }

    [HttpGet("deliveries/{operationId}")]
    public async Task<IActionResult> Receipt(string channelId, string operationId, CancellationToken ct)
    {
        if (operationId.Length is < 1 or > 128) return BadRequest(new { code = "invalid_operation_id" });
        try
        {
            var result = await host.OperateAsync(SlackChannelMetadata.ConnectorId(channelId), "delivery_receipt",
                new Dictionary<string, string> { ["operationId"] = operationId }, ct);
            if (!result.Success || result.Data is null) return NotFound(new { code = "receipt_not_found" });
            var receipt = JsonSerializer.Deserialize<SlackSendReceipt>(result.Data)!;
            return Ok(new { status = receipt.Status.ToString().ToLowerInvariant(), receipt.Code, receipt.ChannelId,
                receipt.MessageTs, receipt.RetryAt, providerAccepted = receipt.Status == SlackSendStatus.Sent });
        }
        catch (InvalidOperationException) { return Conflict(new { code = "connector_unavailable" }); }
    }
}
