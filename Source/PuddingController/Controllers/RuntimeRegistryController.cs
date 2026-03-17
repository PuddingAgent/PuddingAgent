using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Runtime 节点注册 API——Runtime 节点启动后调用此端点注册自身，并定期发送心跳。
/// </summary>
[ApiController]
[Route("api/runtime-registry")]
public class RuntimeRegistryController : ControllerBase
{
    private readonly RuntimeRegistryService _registry;
    private readonly ILogger<RuntimeRegistryController> _logger;

    public RuntimeRegistryController(
        RuntimeRegistryService registry,
        ILogger<RuntimeRegistryController> logger)
    {
        _registry = registry;
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
    {
        return Ok(_registry.GetAll());
    }
}
