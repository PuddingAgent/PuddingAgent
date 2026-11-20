using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Services;

namespace PuddingHost.Controllers;

/// <summary>
/// 子代理委派策略（团队管理 → 配置界面）的服务端出口。
///
/// <para>
/// 四个开关决定「谁能创建子代理、能同时跑几个、能不能自己选模型、选不了时用哪个」。
/// 此前它们只能靠手改 <c>data/config/system.json</c> 生效，前端没有任何读写入口 ——
/// 这个控制器就是补那一段。
/// </para>
/// <para>
/// 鉴权与命名空间照抄 <see cref="DiagnosticsLogLevelController"/>：只认登录态 Admin JWT
/// （<c>Roles = "admin"</c>）。这是**写入**（改运行策略 + 落盘），因此不接受匿名调用。
/// </para>
/// <para>
/// **生效时机**：<c>system.json</c> 以 <c>reloadOnChange: true</c> 接入配置链，
/// 而 <c>SubAgentTool</c> 在每次派发时实时读配置 ⇒ 写入后**下一次派发即生效，不需要重启 Core**。
/// 界面不应提示「重启后生效」。
/// </para>
/// <para>
/// **重载是异步的**：PUT 的响应体是「已落盘的目标值」，
/// 但紧接着的 GET 可能仍读到最后一次重载前的快照（实测毫秒级窗口）。
/// 界面应以 PUT 响应为准，不要写完立刻 GET 回读再断言「没生效」。
/// </para>
/// <para>
/// **失败语义**：任一校验不过 ⇒ 400 且不写任何字节；错误明细逐条回给调用方，
/// 而不是第一条就截断（同一个请求里可能同时有两个字段越界）。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/sub-agent-delegation")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class SubAgentDelegationAdminController(SubAgentDelegationSettingsStore settings)
    : ControllerBase
{
    /// <summary>
    /// 当前生效值 + 每项是否来自配置文件（false = 代码默认）。
    /// 界面应据此区分「用户设过」和「系统默认」，两者不能长得一样。
    /// </summary>
    [HttpGet]
    [ProducesResponseType<SubAgentDelegationSnapshot>(StatusCodes.Status200OK)]
    public ActionResult<SubAgentDelegationSnapshot> Get() => settings.Read();

    /// <summary>
    /// 部分更新：请求体里没给的字段保持原值；<c>defaultModelRoute</c> 传空串表示清除。
    /// </summary>
    /// <param name="request">要改的字段。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpPut]
    [ProducesResponseType<SubAgentDelegationSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SubAgentDelegationSnapshot>> Put(
        [FromBody] SubAgentDelegationUpdate request,
        CancellationToken ct)
    {
        if (request is null)
            return Problem(title: "缺少请求体", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var snapshot = await settings.UpdateAsync(request, ct).ConfigureAwait(false);
            return snapshot;
        }
        catch (SubAgentDelegationValidationException ex)
        {
            // fail closed：校验不过就什么都没写；把逐条原因交给调用方。
            return Problem(
                title: "子代理委派配置不合法",
                detail: string.Join("\n", ex.Errors),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
