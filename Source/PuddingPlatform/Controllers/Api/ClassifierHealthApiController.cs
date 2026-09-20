using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Classification;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 分类器健康只读 API（安全分类器方案 v2 §8.2 / §10 D6 / 切片 S6b-1，供 S6b-2 前端故障明显提示）。
/// <para>
/// <b>服务端权威</b>：直接透传 <see cref="IClassifierHealthReporter.Snapshot()"/> 的进程内快照，
/// 不由前端推断、不做缓存改写、不产生任何网络调用。健康面未接线（宿主未注册该抽象）时返回
/// <c>configured=false</c> 的<b>明确未知态</b>（HTTP 200），绝不 500、绝不假装健康。
/// </para>
/// <para>
/// <b>契约边界（§8.2 2026-09-21 裁定）</b>：本端点只消费 <c>Snapshot()</c>；
/// per-key 连续 deferred 计数与退避档属 Agent 侧诊断（<c>classifier_status</c> 工具），不进本 API。
/// 脱敏由健康面自身保证（detail 不含密钥）；本端点仅透出健康面白名单字段，不附加任何配置原文。
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/classifier-health")]
public sealed class ClassifierHealthApiController : ControllerBase
{
    [HttpGet]
    public IActionResult GetHealth()
    {
        // 可选依赖：健康面未注册（宿主未接线）时显式表达未知态，不让 DI 解析失败变 500。
        // 与 SessionEventsController 的 GetService<T>() 可选依赖范式一致。
        var reporter = HttpContext.RequestServices.GetService<IClassifierHealthReporter>();
        if (reporter is null)
        {
            return Ok(new
            {
                configured = false,
                classifiers = Array.Empty<object>(),
            });
        }

        IReadOnlyList<ClassifierStatus> snapshot = reporter.Snapshot();
        return Ok(new
        {
            configured = true,
            classifiers = snapshot.Select(status => new
            {
                classifierId = status.ClassifierId,
                // 枚举转稳定小写字符串（unknown/healthy/degraded/unavailable），wire 不暴露数值。
                health = status.Health.ToString().ToLowerInvariant(),
                detail = status.Detail,
                consecutiveFailures = status.ConsecutiveFailures,
                lastCheckedAtUtc = status.LastCheckedAtUtc,
                lastLatencyMs = status.LastLatencyMs,
            }),
        });
    }
}
