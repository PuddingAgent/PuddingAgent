using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Runtime 节点注册 API——Runtime 节点启动后调用此端点注册自身，并定期发送心跳。
/// 嵌入式宿主节点额外提供：冻结/解冻、能力列表查询、代理调用原生能力。
/// </summary>
[ApiController]
[Route("api/runtime-registry")]
public class RuntimeRegistryController : ControllerBase
{
    private readonly RuntimeRegistryService _registry;
    private readonly InMemoryAuditEventStore _audit;
    private readonly ILogger<RuntimeRegistryController> _logger;

    public RuntimeRegistryController(
        RuntimeRegistryService registry,
        InMemoryAuditEventStore audit,
        ILogger<RuntimeRegistryController> logger)
    {
        _registry = registry;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/runtime-registry/register
    /// Runtime 调用此端点注册/续约心跳。
    /// </summary>
    [HttpPost("register")]
    public ActionResult<RuntimeRegisterResponse> Register([FromBody] RuntimeRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId) || string.IsNullOrWhiteSpace(request.Endpoint))
            return BadRequest(new RuntimeRegisterResponse { Accepted = false, Message = "NodeId and Endpoint required" });

        var result = _registry.Register(request);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/runtime-registry/nodes
    /// 列出所有已注册 Runtime 节点（含状态）。
    /// </summary>
    [HttpGet("nodes")]
    public ActionResult<IReadOnlyList<RuntimeNodeInfo>> GetNodes()
        => Ok(_registry.GetAll());

    /// <summary>
    /// GET /api/runtime-registry/embedded
    /// 列出所有嵌入式宿主节点。
    /// </summary>
    [HttpGet("embedded")]
    public ActionResult<IReadOnlyList<RuntimeNodeInfo>> GetEmbedded()
        => Ok(_registry.GetEmbeddedNodes());

    /// <summary>
    /// GET /api/runtime-registry/{nodeId}/capabilities
    /// 获取指定嵌入节点的原生能力列表。
    /// </summary>
    [HttpGet("{nodeId}/capabilities")]
    public ActionResult<IReadOnlyList<NativeCapabilityDescriptor>> GetCapabilities(string nodeId)
    {
        var caps = _registry.GetNodeCapabilities(nodeId);
        if (caps is null) return NotFound(new { error = $"Node {nodeId} not found" });
        return Ok(caps);
    }

    /// <summary>
    /// POST /api/runtime-registry/{nodeId}/freeze
    /// 冻结嵌入式节点——冻结后该节点拒绝所有原生能力调用。
    /// </summary>
    [HttpPost("{nodeId}/freeze")]
    public async Task<ActionResult> Freeze(string nodeId, [FromBody] EmbeddedNodeFreezeRequest body, CancellationToken ct)
    {
        if (!_registry.FreezeNode(nodeId))
            return NotFound(new { error = $"Node {nodeId} not found" });

        await _audit.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.EmbeddedNodeFrozen,
            Detail = $"Node={nodeId} reason={body.Reason} operator={body.OperatorId ?? "system"}",
        }, ct);

        _logger.LogWarning("[RuntimeRegistry] Node={NodeId} frozen by operator={Op}", nodeId, body.OperatorId);
        return Ok(new { nodeId, frozen = true });
    }

    /// <summary>
    /// POST /api/runtime-registry/{nodeId}/unfreeze
    /// 解除嵌入节点冻结。
    /// </summary>
    [HttpPost("{nodeId}/unfreeze")]
    public async Task<ActionResult> Unfreeze(string nodeId, [FromBody] EmbeddedNodeFreezeRequest body, CancellationToken ct)
    {
        if (!_registry.UnfreezeNode(nodeId))
            return NotFound(new { error = $"Node {nodeId} not found" });

        await _audit.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.EmbeddedNodeUnfrozen,
            Detail = $"Node={nodeId} operator={body.OperatorId ?? "system"}",
        }, ct);

        _logger.LogInformation("[RuntimeRegistry] Node={NodeId} unfrozen by operator={Op}", nodeId, body.OperatorId);
        return Ok(new { nodeId, frozen = false });
    }

    /// <summary>
    /// POST /api/runtime-registry/{nodeId}/invoke-capability
    /// Controller 端入口：执行权限与审批检查后，将原生能力调用代理转发到 Runtime 节点。
    /// </summary>
    [HttpPost("{nodeId}/invoke-capability")]
    public async Task<ActionResult<NativeCapabilityInvokeResult>> InvokeCapability(
        string nodeId,
        [FromBody] NativeCapabilityInvokeRequest request,
        CancellationToken ct)
    {
        // 查询节点
        var nodes = _registry.GetAll();
        var node = nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null)
            return NotFound(new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = $"Node {nodeId} not found" });

        if (node.Status != RuntimeNodeStatus.Online)
            return BadRequest(new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = $"Node {nodeId} is {node.Status}" });

        // 冻结检查
        if (node.IsFrozen)
        {
            await _audit.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.NativeCapabilityDenied,
                SessionId = request.SessionId,
                WorkspaceId = request.WorkspaceId,
                Detail = $"Node={nodeId} cap={request.CapabilityId} reason=NodeFrozen",
            }, ct);
            return StatusCode(403, new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = "节点已冻结，拒绝调用。" });
        }

        // 能力存在性检查
        var caps = _registry.GetNodeCapabilities(nodeId);
        var descriptor = caps?.FirstOrDefault(c => c.CapabilityId == request.CapabilityId);
        if (descriptor is null)
        {
            return BadRequest(new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = $"Capability {request.CapabilityId} not found on node {nodeId}" });
        }

        // 代理转发到 Runtime 节点
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(node.Endpoint) };
            var invokeRequest = request with { NodeId = nodeId };
            var resp = await http.PostAsJsonAsync("/api/native-capability/invoke", invokeRequest, ct);
            NativeCapabilityInvokeResult? result;
            if (resp.IsSuccessStatusCode)
            {
                result = await resp.Content.ReadFromJsonAsync<NativeCapabilityInvokeResult>(ct)
                    ?? new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = "Empty response from runtime" };
            }
            else
            {
                result = new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = $"Runtime returned {resp.StatusCode}" };
            }

            // 写审计
            var auditType = result.IsSuccess ? AuditEventType.NativeCapabilityInvoked : AuditEventType.NativeCapabilityDenied;
            await _audit.RecordAsync(new AuditEventRecord
            {
                EventType = auditType,
                SessionId = request.SessionId,
                WorkspaceId = request.WorkspaceId,
                AgentTemplateId = request.AgentTemplateId,
                Detail = $"Node={nodeId} cap={request.CapabilityId} success={result.IsSuccess}",
            }, ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RuntimeRegistry] Failed to invoke capability={Cap} on node={NodeId}", request.CapabilityId, nodeId);
            await _audit.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.NativeCapabilityDenied,
                SessionId = request.SessionId,
                WorkspaceId = request.WorkspaceId,
                Detail = $"Node={nodeId} cap={request.CapabilityId} error={ex.Message}",
            }, ct);
            return StatusCode(502, new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = $"转发失败：{ex.Message}" });
        }
    }
}
