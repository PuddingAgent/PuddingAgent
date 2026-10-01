using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Services;

namespace PuddingHost.Controllers;

/// <summary>
/// Slice S-A（2026-10-01）：全文索引**状态只读出口**（Web Admin 面板的数据源，面板本身属下一刀 Slice B）。
/// <para>
/// 鉴权与命名空间**逐项照抄** <see cref="StorageAdminController"/>：同样只认登录态 Admin JWT
/// （<c>Roles = "admin"</c>），不接 Desktop ControlToken、不接 External Access Token ——
/// 「看索引状态」与「看存储状态」是同级的运维面，不应各自发明一套闸门。
/// </para>
/// <para>
/// 本控制器**只有一个 GET**：纯只读查询，没有任何「重建 / 触发写入」能力
/// （那属于后续切片，且必须走协调器，不得从这里绕）。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/index")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class IndexAdminController(
    FullTextIndexStatusProbe statusProbe) : ControllerBase
{
    /// <summary>
    /// 全文索引状态快照：配置真值（开关 / 预算 / 最小重建间隔）+ 逐 scope 只读观测 +
    /// 供给 job 台账 + 供给组合是否已构造。
    /// <para>
    /// 全程只读、零副作用：不构造供给组合、不建目录、不写租约、不触发供给。
    /// 任何单点失败都降级为 <c>null</c> 并保持 200（绝不用 500 表示「不知道」）。
    /// </para>
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<FullTextIndexStatusSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FullTextIndexStatusSnapshot>> GetStatus(CancellationToken cancellationToken)
        => Ok(await statusProbe.BuildAsync(cancellationToken));
}
