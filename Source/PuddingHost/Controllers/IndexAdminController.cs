using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Services;

namespace PuddingHost.Controllers;

/// <summary>
/// Slice S-A（2026-10-01）：全文索引**状态只读出口**（Web Admin 面板的数据源，面板本身属下一刀 Slice B）。
/// Slice S-A2（同日）：同一出口上**增列**符号 / 代码索引块（<c>codeIndex</c>）。
/// <para>
/// 鉴权与命名空间**逐项照抄** <see cref="StorageAdminController"/>：同样只认登录态 Admin JWT
/// （<c>Roles = "admin"</c>），不接 Desktop ControlToken、不接 External Access Token ——
/// 「看索引状态」与「看存储状态」是同级的运维面，不应各自发明一套闸门。S-A2 未改鉴权、未改路由。
/// </para>
/// <para>
/// 本控制器**只有一个 GET**：纯只读查询，没有任何「重建 / 触发写入」能力
/// （那属于后续切片，且必须走协调器，不得从这里绕）。S-A2 同样只读：
/// 符号索引块只调用查询型 API（注册表列表 / 项目记录列表 / 驱动状态），不打开写事务。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/index")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class IndexAdminController(
    FullTextIndexStatusProbe statusProbe,
    CodeIndexStatusProbe codeIndexProbe) : ControllerBase
{
    /// <summary>
    /// 索引状态快照：**并列两块** —— 全文索引（<c>fullText</c>，字段名与结构自 S-A 起冻结）
    /// 与符号 / 代码索引（<c>codeIndex</c>，S-A2 新增）。
    /// <para>
    /// 全程只读、零副作用：不构造供给组合、不建目录、不写租约、不触发供给、不碰索引库写事务。
    /// 任何单点失败都由各自的探针降级（计数类报 <c>null</c> / 空列表 + 如实原因）并保持 200
    /// （绝不用 500 表示「不知道」）。
    /// </para>
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<IndexAdminStatusSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IndexAdminStatusSnapshot>> GetStatus(CancellationToken cancellationToken)
    {
        // 两块各自由它的探针观测：失败面互不污染（一块降级不影响另一块）。
        var fullText = await statusProbe.BuildAsync(cancellationToken);
        var codeIndex = await codeIndexProbe.BuildAsync(cancellationToken);

        return Ok(new IndexAdminStatusSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            FullText: fullText,
            CodeIndex: codeIndex));
    }
}

/// <summary>
/// 索引状态响应根对象（HTTP 边界的契约；camelCase 由 ASP.NET 默认序列化策略决定）。
/// <para>
/// S-A 时根对象由全文索引探针直接产出（那时只有一个块）；S-A2 起由控制器组装 ——
/// 两个块由两个探针分别观测，根只负责并列，不产生任何新真值。
/// </para>
/// </summary>
/// <param name="GeneratedAtUtc">快照组装时刻（UTC）。</param>
/// <param name="FullText">全文索引块（<b>字段名与结构自 S-A 起冻结</b>：前端已按它实现并上线）。</param>
/// <param name="CodeIndex">符号 / 代码索引块（S-A2 新增；含 D1/D2 的 fail-closed 判定见其成员注释）。</param>
public sealed record IndexAdminStatusSnapshot(
    DateTime GeneratedAtUtc,
    FullTextIndexStatusDetailSnapshot FullText,
    CodeIndexStatusDetailSnapshot CodeIndex);
