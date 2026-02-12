using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;

namespace PuddingAgent.Controllers;

/// <summary>
/// 网关/连接器诊断接口：用于观察 connector 运行状态与计数，支持故障复现定位。
/// </summary>
[ApiController]
[Route("gateway/diagnostics")]
public sealed class GatewayDiagnosticsController : ControllerBase
{
    [HttpGet("connectors")]
    public async Task<IActionResult> GetConnectorsDiagnostics(
        [FromServices] IEnumerable<IPuddingConnector> connectors,
        CancellationToken cancellationToken)
    {
        var list = new List<object>();
        foreach (var connector in connectors)
        {
            var d = await connector.GetDiagnosticsAsync(cancellationToken);
            list.Add(new
            {
                connectorId = connector.Descriptor.ConnectorId,
                connectorType = connector.Descriptor.ConnectorType,
                protocol = connector.Descriptor.Protocol,
                status = d.Status,
                messagesReceived = d.MessagesReceived,
                messagesSent = d.MessagesSent,
                errors = d.Errors,
                lastReceiveTime = d.LastReceiveTime,
                lastErrorTime = d.LastErrorTime,
                lastError = d.LastError,
            });
        }

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow,
            connectors = list,
        });
    }
}
