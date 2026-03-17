using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntime.Controllers;

/// <summary>
/// 原生能力执行 API——Controller 在完成权限与审批校验后，
/// 将原生能力调用代理转发到此端点，由 NativeCapabilityExecutor 执行具体 bridge。
/// </summary>
[ApiController]
[Route("api/native-capability")]
public sealed class NativeCapabilityController : ControllerBase
{
    private readonly NativeCapabilityExecutor _executor;
    private readonly ILogger<NativeCapabilityController> _logger;

    public NativeCapabilityController(
        NativeCapabilityExecutor executor,
        ILogger<NativeCapabilityController> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/native-capability/invoke
    /// 由 Controller 调用，执行宿主原生能力。
    /// </summary>
    [HttpPost("invoke")]
    public async Task<ActionResult<NativeCapabilityInvokeResult>> Invoke(
        [FromBody] NativeCapabilityInvokeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CapabilityId))
            return BadRequest(new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = "CapabilityId required" });

        var result = await _executor.ExecuteAsync(request, ct);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/native-capability/list
    /// 列出本节点所有已注册原生能力（供 Controller 查询/刷新）。
    /// </summary>
    [HttpGet("list")]
    public ActionResult<IReadOnlyList<NativeCapabilityDescriptor>> List()
        => Ok(_executor.GetAllCapabilities());
}
