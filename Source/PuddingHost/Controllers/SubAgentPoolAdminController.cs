using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;

namespace PuddingHost.Controllers;

/// <summary>
/// 子代理池（团队管理 → 子代理视图）的**只读**数据出口。
///
/// <para>
/// 在此之前池条目只存在于 <c>agent_pool</c> 表，Web 侧没有任何端点能读到它，
/// 于是界面只能显示空列表 —— 有数据和没这个功能，在用户眼里长得一样。
/// 本控制器只补「管理端出口」这一段：
/// <para>
/// <b>读路径直接查库</b>（理由见下方：内存池有就绪门，读接口不该因未预热而报错）。
/// </para>
/// <para>
/// <b>写路径必须经过池实例</b>：<c>SubAgentPool</c> 的内存缓存与 <c>agent_pool</c> 表是真源/缓存关系，
/// 绕过池直接 UPDATE 库会让内存缓存永久偏差（直到下一次重启重建）。
/// 代价是写接口会继承池的就绪门与「条目必须在本进程内存里」这一限制，
/// 因此写失败时如实返回 503/409，不把「不知道」伪装成「成功」。
/// </para>
/// </para>
/// <para>
/// **鉴权与路由**照抄 <see cref="SubAgentDelegationAdminController"/>：
/// 只认登录态 Admin JWT（<c>Roles = "admin"</c>）。池里是「谁在替我干活」的清单，
/// 与委派策略同级，不应各自发明一套闸门。
/// </para>
/// <para>
/// **为什么不注入 <c>SubAgentPool</c> 单例**：它是内存池，带就绪门 —— 未 Ready 时
/// <c>List()</c> 会抛，而「池还在预热」不该表现为一个读接口报错。本端点直接查库，
/// 于是 Core 刚启动、池尚未 Ready 时界面也能如实显示库里的条目。
/// </para>
/// <para>
/// **空池返回 200 + <c>[]</c>，绝不返回 404**：对界面来说「一个条目都没有」和
/// 「这个端点不存在」是两件完全不同的事 —— 前者应显示空状态与「创建」引导，
/// 后者是部署/路由故障。用 404 表达空列表会把后者伪装成前者。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/sub-agent-pool")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class SubAgentPoolAdminController(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ISubAgentPool subAgentPool)
    : ControllerBase
{
    /// <summary>
    /// 池条目列表（**只读**）：已软删的墓碑行不返回；最近使用的排在前面。
    /// <para>
    /// 排序：<c>last_used_at DESC</c>，空值排最后。库列是 NOT NULL，实际空值表现为空串，
    /// 因此两条都要处理；否则空串会因为在 DESC 里「最小」而落到末尾以外的位置
    /// （取决于 SQLite 的排序实现，这属于会随版本变化的细节，不靠它）。
    /// </para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SubAgentPoolEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SubAgentPoolEntryDto>>> Get(
        CancellationToken ct)
    {
        // 短生命周期 context：Web 请求不该与后台 worker 共用一份跟踪状态。
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entries = await db.AgentPool
            .AsNoTracking()
            .Where(e => e.DeletedAt == null)
            .OrderBy(e => e.LastUsedAt == null || e.LastUsedAt == string.Empty)
            .ThenByDescending(e => e.LastUsedAt)
            .Select(e => new SubAgentPoolEntryDto
            {
                PoolId = e.PoolId,
                Name = e.Name,
                Role = e.Role,
                Responsibility = e.Responsibility,
                AvatarKey = e.AvatarKey,
                ModelId = e.ModelId,
                TemplateId = e.TemplateId,
                Status = e.Status,
                TaskCount = e.TaskCount,
                LastUsedAt = e.LastUsedAt,
                LastSuccess = e.LastSuccess,
                SubSessionId = e.SubSessionId,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(entries);
    }

    /// <summary>
    /// 销毁池条目：库迁 <c>dead</c>（写 <c>deleted_at</c>）→ 删内存条目 → 若原本 busy 则级联取消执行循环。
    /// 语义完全透传 <see cref="ISubAgentPool.DestroyAsync"/>，本端点不额外发明行为。
    /// <para>
    /// <b>为什么先 <see cref="ISubAgentPool.GetSnapshotAsync"/>：</b>未从库加载时，
    /// <c>DestroyAsync</c> 会因内存里看不到该条目而返回 false —— 那是「还没读回来」，
    /// 不是「条目不存在」。先等加载把这两种情况分开，才能给出可信的 404/409。
    /// </para>
    /// <para>
    /// <b>一个必须如实说明的限制：</b>池是进程内缓存，本进程看不到别的进程创建的条目。
    /// 因此列表里看得见的条目不一定删得掉；后者返回 409 而非 404，因为「存在但本进程无权动它」
    /// 与「不存在」对调用方是两件不同的事。
    /// </para>
    /// </summary>
    /// <param name="name">池内展示名（未软删时唯一）。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpDelete("{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name_required", detail = "池条目名不能为空。" });
        }

        try
        {
            await subAgentPool.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (PoolNotReadyException ex)
        {
            // 恢复失败不等于没有条目：如实报「池未就绪」，让运维去看启动日志。
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "pool_not_ready", detail = ex.Message });
        }

        var destroyed = await subAgentPool.DestroyAsync(name, ct).ConfigureAwait(false);
        if (destroyed)
        {
            return NoContent();
        }

        // DestroyAsync 的 false 有四种来源且不区分：条目不在本进程内存、库拒绝状态迁移、
        // 条目已 dead、名字拼错。这里不编造细分，只把「可能的原因」交给调用方判断。
        return Conflict(new
        {
            error = "destroy_rejected",
            detail = "无法销毁：条目不在本进程池内、已处于 dead、或库拒绝了状态迁移。" +
                     "若列表里看得见它，说明它由另一进程创建或本进程尚未加载。",
        });
    }

    /// <summary>
    /// 休眠池条目：库迁 <c>sleeping</c>（<b>不</b>写 <c>deleted_at</c>）→ 改内存状态。
    /// <para>
    /// 与 DELETE 的区别要说清楚：休眠后条目仍在池里，下次 <c>ExecuteAsync</c> 仍会复用
    /// 同一 <c>sub_session_id</c>（KV-cache 就靠这个）；销毁则彻底退出，名字可被新条目占用。
    /// </para>
    /// <para>
    /// <b>为什么是 POST 而不是 PATCH：</b>休眠是一次带副作用的状态转换（会取消当前执行令牌），
    /// 不是对属性的幂等修改。用 PATCH 会让调用方以为可以随便重试。
    /// </para>
    /// </summary>
    /// <param name="name">池内展示名（未软删时唯一）。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpPost("{name}/sleep")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Sleep(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name_required", detail = "池条目名不能为空。" });
        }

        try
        {
            await subAgentPool.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (PoolNotReadyException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "pool_not_ready", detail = ex.Message });
        }

        var slept = await subAgentPool.SleepAsync(name, ct).ConfigureAwait(false);
        if (slept)
        {
            return NoContent();
        }

        return Conflict(new
        {
            error = "sleep_rejected",
            detail = "无法休眠：条目不在本进程池内、已 dead、或库拒绝了状态迁移。" +
                     "若列表里看得见它，说明它由另一进程创建或本进程尚未加载。",
        });
    }

    /// <summary>
    /// 修改池条目的展示字段（备注 / 头像键 / 模型路由）。
    /// <para>
    /// <b>这里可以直接写库、不经过池实例</b>，因为这三个字段都不在
    /// <see cref="PooledSubAgent"/> 快照里 —— 内存缓存根本不持有它们，
    /// 「库改了内存没改」不构成分叉。状态类字段（status / task_count /
    /// active_execution_id）恰好相反：它们就在内存里，必须走池。
    /// </para>
    /// <para>
    /// <b>模型路由目前是「声明」而非「生效配置」：</b>执行路径（<c>SubAgentPool.ExecuteAsync</c>）
    /// 用的是派发方当轮解析出的 <c>LlmProfile</c>，<b>不会</b>回读本列。
    /// 所以这里改的是一条可查、可审计的团队约定，不是「下次一定用它跑」。
    /// 要让本列真正生效，需要改路由解析顺序（执行前先查条目再解析），属独立切片。
    /// </para>
    /// <para>
    /// <b>只做部分更新</b>：请求体里没给的字段保持原值，不把 null 当成「清空」。
    /// 要清空得显式传空字符串，这与「没传」是两件事。
    /// </para>
    /// </summary>
    [HttpPatch("{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string name,
        [FromBody] UpdateSubAgentPoolEntryRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name_required", detail = "池条目名不能为空。" });
        }

        if (body.Responsibility is { Length: > 200 })
        {
            return BadRequest(new { error = "responsibility_too_long", detail = "备注最长 200 字。" });
        }

        if (body.AvatarKey is { Length: > 32 })
        {
            return BadRequest(new { error = "avatar_key_too_long", detail = "头像键最长 32 字符。" });
        }

        // 空串是「清空 → 跟随派发方」，不参与格式校验；非空必须是 providerId/modelId。
        if (body.ModelId is { Length: > 0 } modelId)
        {
            if (modelId.Length > 128)
            {
                return BadRequest(new
                {
                    error = "model_id_too_long",
                    detail = "模型路由最长 128 字符（形如 providerId/modelId）。",
                });
            }

            var slash = modelId.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == modelId.Length - 1 ||
                modelId.IndexOf('/', slash + 1) >= 0)
            {
                return BadRequest(new
                {
                    error = "model_id_invalid",
                    detail = "模型路由必须写成 providerId/modelId，例如 deepseek/deepseek-flash。",
                });
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.AgentPool
            .FirstOrDefaultAsync(e => e.Name == name && e.DeletedAt == null, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            // 这里 404 是诚实的：本端点在库里找，不依赖进程内缓存，
            // 所以「找不到」就是真的不存在，而不是「本进程没加载」。
            return NotFound(new { error = "not_found", detail = $"不存在名为 '{name}' 的池条目。" });
        }

        if (body.Responsibility is not null)
        {
            entity.Responsibility = body.Responsibility;
        }

        if (body.AvatarKey is not null)
        {
            entity.AvatarKey = body.AvatarKey;
        }

        if (body.ModelId is not null)
        {
            // 空串归一化成 NULL：「跟随派发方」在库里有且只有一种表示，
            // 否则会出现 NULL 与 '' 两种「都没设」的写法，读回时要各自处理。
            entity.ModelId = body.ModelId.Length == 0 ? null : body.ModelId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>
/// 修改池条目展示字段的请求体。
/// <para>
/// 三个字段都是可选的：「没传」= 保持原值。要清空必须显式传空字符串 ——
/// 把 null 当成「清空」会让一次只改头像的请求顺手抹掉备注。
/// </para>
/// </summary>
public sealed class UpdateSubAgentPoolEntryRequest
{
    /// <summary>一句话备注（自由文本，最长 200 字）。</summary>
    public string? Responsibility { get; init; }

    /// <summary>预设图形键（最长 32 字符）。</summary>
    public string? AvatarKey { get; init; }

    /// <summary>
    /// 模型路由 <c>providerId/modelId</c>（最长 128 字符）；传空字符串表示清空（跟随派发方）。
    /// <para>
    /// 只校验形状，不校验该路由是否在资源池注册：控制器拿不到 LLM 注册表，
    /// 而在这里伪造一个「存在性校验」的自由文本会更糟。真正解析失败会在派发时暴露。
    /// </para>
    /// </summary>
    public string? ModelId { get; init; }
}

/// <summary>
/// 池条目的 HTTP 契约（camelCase 由 ASP.NET 默认序列化策略产出）。
/// <para>
/// 刻意放在 Host 内而不是 <c>Pudding.Api.Contracts</c>：后者是跨进程契约，
/// 本端点只供 Web 前端读取，塞进去会让无关进程也被迫跟着版本化。
/// </para>
/// <para>
/// 时间列一律原样透出 <c>agent_pool</c> 里的 ISO-8601 文本（<see cref="string"/>）：
/// 转成 <see cref="DateTime"/> 会把「库里到底存的是什么」这层信息抹掉，
/// 而诊断时恰恰需要看到原文。
/// </para>
/// <para>
/// <b>与任务书的一处差异：</b>任务书列了 <c>lastSuccessAt</c>，但实体里只有布尔
/// <c>last_success</c>（三态：NULL 从未执行 / 0 上次失败 / 1 上次成功），
/// 库里**没有任何「最近成功时刻」的列**。凭空造一个恒为 null 的时间字段会让界面
/// 一直显示「从未成功」——那是假数据。这里改为如实透出 <c>lastSuccess</c>；
/// 若前端确实需要时间轴，应另开一列 <c>last_success_at</c> 并补写入方，属后续切片。
/// </para>
/// </summary>
public sealed class SubAgentPoolEntryDto
{
    /// <summary>稳定池 id（创建后不变，前端用它做 key 与跳转）。</summary>
    public string PoolId { get; init; } = string.Empty;

    /// <summary>池内展示名（未软删时唯一）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>能力角色分类（可枚举的短标签，可能为空）。</summary>
    public string? Role { get; init; }

    /// <summary>一句话职责（自由文本，可能为空）。</summary>
    public string? Responsibility { get; init; }

    /// <summary>预设图形键；为空时前端按 <c>Name + PoolId</c> 确定性生成头像。</summary>
    public string? AvatarKey { get; init; }

    /// <summary>
    /// 团队约定的模型路由 <c>providerId/modelId</c>；为空表示跟随派发方。
    /// <para>
    /// <b>如实说明：本列目前不被执行路径读取。</b>派发时用的是调用方当轮解析出的
    /// <c>LlmProfile</c>，因此它是一条「声明/审计」信息，不是生效配置 ——
    /// 不写这一句，界面把它当默认模型显示就会骗人。
    /// </para>
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>创建该条目所用的 agent 模板 id。</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>当前状态：idle / busy / sleeping / dead。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>累计承接的任务数。</summary>
    public int TaskCount { get; init; }

    /// <summary>最近使用时间（ISO-8601 文本，排序键）。</summary>
    public string LastUsedAt { get; init; } = string.Empty;

    /// <summary>三态：<c>null</c> = 从未执行过；<c>false</c> = 上次失败；<c>true</c> = 上次成功。</summary>
    public bool? LastSuccess { get; init; }

    /// <summary>复用键：恢复该条目的会话靠它，而不是靠名字。</summary>
    public string SubSessionId { get; init; } = string.Empty;

    /// <summary>条目创建时间（ISO-8601 文本）。</summary>
    public string CreatedAt { get; init; } = string.Empty;
}
