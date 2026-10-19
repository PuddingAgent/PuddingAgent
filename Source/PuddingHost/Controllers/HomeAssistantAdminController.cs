using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.HomeAssistant;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Diagnostics;
using PuddingHomeAssistant.Policy;
using PuddingHomeAssistant.Rest;

namespace PuddingHost.Controllers;

/// <summary>
/// Home Assistant 管理面：实例配置 CRUD、连通性测试、域权限目录。
///
/// <para>
/// 鉴权与命名空间照抄 <see cref="IndexAdminController"/>：只认登录态 Admin JWT（<c>Roles = "admin"</c>），
/// 不接 Desktop ControlToken、不接 External Access Token —— 「改凭据」与「看索引/存储状态」同为运维面。
/// </para>
/// <para>安全不变量：</para>
/// <list type="bullet">
/// <item><b>永不回传完整令牌</b>：只给 <c>hasToken</c> 与 <c>tokenPreview</c>（前 6 后 4）；</item>
/// <item><b>更新时 token 可省略</b>：为空表示保留原值，避免要求用户重复输入；</item>
/// <item><b>永久黑名单域不可写入 optIn</b>：请求里出现即 400（fail closed，静默忽略会让用户误以为已生效）；</item>
/// <item><b>agentId 不得含路径分隔符或 <c>..</c></b>：配置路径由它拼接，必须挡住目录穿越。</item>
/// </list>
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/home-assistant")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class HomeAssistantAdminController(
    HaInstanceStore store,
    HaClientFactory factory,
    ILogger<HomeAssistantAdminController> logger) : ControllerBase
{
    /// <summary>三组域清单：默认允许 / 可选放开 / 永久拒绝（前端据此渲染勾选框与置灰项）。</summary>
    [HttpGet("domains")]
    [ProducesResponseType<HaDomainCatalog>(StatusCodes.Status200OK)]
    public ActionResult<HaDomainCatalog> GetDomains() => new HaDomainCatalog(
        HaDomainPolicy.DefaultAllowedDomains(),
        HaDomainPolicy.OptInCandidateDomains(),
        HaDomainPolicy.AlwaysBlockedDomains());

    /// <summary>列出某 Agent 已登记的实例（不含令牌）。</summary>
    [HttpGet("agents/{agentId}/instances")]
    [ProducesResponseType<IReadOnlyList<HaInstanceAdminView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<HaInstanceAdminView>> List(string agentId)
    {
        if (!IsSafeAgentId(agentId))
        {
            return InvalidAgentId();
        }

        return store.Load(agentId).Select(ToView).ToArray();
    }

    /// <summary>新增或更新实例。`token` 省略/为空 = 保留原值。</summary>
    [HttpPut("agents/{agentId}/instances/{alias}")]
    [ProducesResponseType<HaInstanceAdminView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HaInstanceAdminView>> Upsert(
        string agentId,
        string alias,
        [FromBody] HaInstanceUpsertRequest request,
        CancellationToken ct)
    {
        if (!IsSafeAgentId(agentId))
        {
            return InvalidAgentId();
        }

        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 32 ||
            alias.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
        {
            return Problem(
                title: "非法的实例别名",
                detail: "alias 只允许字母、数字、下划线和连字符，长度 1-32。",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Uri.TryCreate(request?.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return Problem(
                title: "非法的 baseUrl",
                detail: "必须是绝对的 http/https 地址，例如 http://192.168.100.10:8123/。",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var blocked = HaDomainPolicy.AlwaysBlockedDomains().ToHashSet(StringComparer.Ordinal);
        var optIn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in request?.OptInDomains ?? [])
        {
            if (!HaDomainPolicy.TryNormalize(raw, out var normalized))
            {
                return Problem(
                    title: "非法的域标识",
                    detail: $"域 '{raw}' 只允许小写字母、数字和下划线。",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (blocked.Contains(normalized))
            {
                // fail closed：永久黑名单域写进配置会让用户误以为已放开，必须当场拒绝。
                return Problem(
                    title: "该域永久禁止",
                    detail: $"域 '{normalized}' 属于永久黑名单（可在 HA 内执行任意逻辑或触碰宿主机），不能通过配置放开。",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            optIn.Add(normalized);
        }

        var existing = store.Load(agentId).ToList();
        var index = existing.FindIndex(x => string.Equals(x.Alias, alias, StringComparison.Ordinal));
        var previous = index >= 0 ? existing[index] : null;

        var token = string.IsNullOrWhiteSpace(request?.Token) ? previous?.Token : request!.Token!.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Problem(
                title: "缺少令牌",
                detail: "新建实例必须提供 Home Assistant 长期访问令牌（更新时可省略以保留原值）。",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var instance = new HaInstance
        {
            Alias = alias,
            BaseUrl = baseUri,
            Token = token,
            OptInDomains = optIn,
        };

        if (index >= 0)
        {
            existing[index] = instance;
        }
        else
        {
            existing.Add(instance);
        }

        await store.SaveAsync(agentId, existing, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Home Assistant 实例已保存: agent={AgentId} alias={Alias} optIn={OptIn}",
            agentId, alias, string.Join(',', optIn));

        return ToView(instance);
    }

    /// <summary>删除实例。不存在时返回 404（不静默成功）。</summary>
    [HttpDelete("agents/{agentId}/instances/{alias}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string agentId, string alias, CancellationToken ct)
    {
        if (!IsSafeAgentId(agentId))
        {
            return InvalidAgentId();
        }

        var existing = store.Load(agentId).ToList();
        var removed = existing.RemoveAll(x => string.Equals(x.Alias, alias, StringComparison.Ordinal));
        if (removed == 0)
        {
            return NotFound();
        }

        await store.SaveAsync(agentId, existing, ct).ConfigureAwait(false);
        logger.LogInformation("Home Assistant 实例已删除: agent={AgentId} alias={Alias}", agentId, alias);
        return NoContent();
    }

    /// <summary>连通性测试：成功返回版本与时区；失败返回稳定错误码（HTTP 200 + ok=false，便于前端渲染）。</summary>
    [HttpPost("agents/{agentId}/instances/{alias}/test")]
    [ProducesResponseType<HaConnectionTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HaConnectionTestResult>> Test(
        string agentId,
        string alias,
        CancellationToken ct)
    {
        if (!IsSafeAgentId(agentId))
        {
            return InvalidAgentId();
        }

        var instance = store.Find(agentId, alias);
        if (instance is null)
        {
            return NotFound();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var client = factory.Create();
            var status = await client.GetStatusAsync(instance, cts.Token).ConfigureAwait(false);
            var config = await client.GetConfigAsync(instance, cts.Token).ConfigureAwait(false);
            return new HaConnectionTestResult(true, status.Message, config.Version, config.TimeZone, null, null);
        }
        catch (HaClientException ex)
        {
            logger.LogWarning("Home Assistant 连通性测试失败: agent={AgentId} alias={Alias} code={Code}", agentId, alias, ex.Code);
            return new HaConnectionTestResult(false, null, null, null, ex.Code, ex.Message);
        }
    }

    private static bool IsSafeAgentId(string? agentId) =>
        !string.IsNullOrWhiteSpace(agentId) &&
        agentId.Length <= 128 &&
        agentId.IndexOfAny(['/', '\\', '\0']) < 0 &&
        !agentId.Contains("..", StringComparison.Ordinal);

    private ObjectResult InvalidAgentId() => Problem(
        title: "非法的 agentId",
        detail: "agentId 不能为空，且不得包含路径分隔符或 '..'。",
        statusCode: StatusCodes.Status400BadRequest);

    private static HaInstanceAdminView ToView(HaInstance instance) => new(
        instance.Alias,
        instance.BaseUrl.ToString(),
        !string.IsNullOrEmpty(instance.Token),
        HaInstanceStore.PreviewToken(instance.Token),
        instance.OptInDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray());
}

/// <summary>Admin 视图中的实例（**不含完整令牌**）。</summary>
public sealed record HaInstanceAdminView(
    string Alias,
    string BaseUrl,
    bool HasToken,
    string? TokenPreview,
    IReadOnlyList<string> OptInDomains);

/// <summary>新增/更新实例的请求。`Token` 为空表示保留原值。</summary>
public sealed record HaInstanceUpsertRequest(
    string? BaseUrl,
    string? Token,
    IReadOnlyList<string>? OptInDomains);

/// <summary>域权限目录。</summary>
public sealed record HaDomainCatalog(
    IReadOnlyList<string> DefaultAllowed,
    IReadOnlyList<string> OptInCandidates,
    IReadOnlyList<string> AlwaysBlocked);

/// <summary>连通性测试结果。</summary>
public sealed record HaConnectionTestResult(
    bool Ok,
    string? ApiMessage,
    string? Version,
    string? TimeZone,
    string? ErrorCode,
    string? Message);
