using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Observability;

namespace PuddingHost.Controllers;

/// <summary>
/// 日志级别开关（Debug 按钮的服务端出口）。
///
/// <para>
/// 用途：Desktop Shell 与 Web 界面各有一个 Debug 按钮，调用本接口把运行中的 Core 切到 Debug/Verbose
/// 以便"确认链路是否真的跑通"，用完再切回 Information —— **不需要重启进程**（Pudding 的 Desktop/Core
/// 是常驻进程，重启会打断用户会话）。
/// </para>
/// <para>
/// 鉴权与命名空间照抄 <see cref="IndexAdminController"/>：只认登录态 Admin JWT（<c>Roles = "admin"</c>），
/// 不接 Desktop ControlToken、不接 External Access Token —— 「改日志级别」与「看索引/存储状态」同为运维面。
/// 这是一次**写入**（改运行级别 + 落盘），因此不接受匿名调用。
/// </para>
/// <para>
/// 失败语义：无法识别的级别名 ⇒ **400 且不改变任何状态**（fail closed，绝不猜测）。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/diagnostics/log-level")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class DiagnosticsLogLevelController(PuddingLogLevelStore levels) : ControllerBase
{
    /// <summary>当前级别与可选值（界面据此渲染下拉/按钮状态）。</summary>
    [HttpGet]
    [ProducesResponseType<DiagnosticsLogLevelSnapshot>(StatusCodes.Status200OK)]
    public ActionResult<DiagnosticsLogLevelSnapshot> Get() => Snapshot();

    /// <summary>切换级别；成功即已生效且已落盘（重启后保持）。</summary>
    /// <param name="request">目标级别。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpPut]
    [ProducesResponseType<DiagnosticsLogLevelSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DiagnosticsLogLevelSnapshot>> Set(
        [FromBody] SetDiagnosticsLogLevelRequest request,
        CancellationToken ct)
    {
        if (!await levels.TrySetAsync(request?.Level, ct).ConfigureAwait(false))
        {
            // fail closed：级别名不认识就什么都不改；把可接受的值一并回给调用方。
            return Problem(
                title: "无法识别的日志级别",
                detail: $"level 必须是以下之一：{string.Join(" / ", PuddingLogLevelStore.SupportedLevels)}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Snapshot();
    }

    private DiagnosticsLogLevelSnapshot Snapshot() => new(
        Level: levels.Current.ToString(),
        IsVerbose: levels.IsVerboseEnabled,
        SupportedLevels: PuddingLogLevelStore.SupportedLevels,
        ConfigFile: levels.FilePath);
}

/// <summary>日志级别快照。</summary>
/// <param name="Level">当前生效级别（Serilog 正式名）。</param>
/// <param name="IsVerbose">是否处于会放大日志量的档位（Verbose/Debug）——界面应据此提示"用完记得关"。</param>
/// <param name="SupportedLevels">可切换的级别。</param>
/// <param name="ConfigFile">持久化文件路径（便于用户知道改到哪去了）。</param>
public sealed record DiagnosticsLogLevelSnapshot(
    string Level,
    bool IsVerbose,
    IReadOnlyList<string> SupportedLevels,
    string ConfigFile);

/// <summary>切换请求。</summary>
/// <param name="Level">目标级别名（大小写不敏感，支持 trace/info/warn/err 等别名）。</param>
public sealed record SetDiagnosticsLogLevelRequest(string? Level);
