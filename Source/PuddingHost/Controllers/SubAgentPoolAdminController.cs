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
/// <b>写路径直接写库，不经过池实例</b>：<c>agent_pool</c> 表是事实源，池的内存条目是跟随它的缓存。
/// 之所以能这么写，是因为本控制器的写只碰展示字段，而这些字段的执行消费者都在使用点重新查库
/// 或每单刷新 —— 角色见 <c>SubAgentPool.RefreshDeclaredRoleLockedAsync</c>，
/// 模型见 <c>ISubAgentPool.GetDeclaredModelIdAsync</c>，模板见 <c>GetDeclaredTemplateIdAsync</c>。
/// 状态类字段（status / task_count / active_execution_id）恰好相反：它们就在内存里且被抢占/收尾
/// 逻辑判定，改它们必须走池。
/// 唯一残留是工具侧 <c>pool_action="list"</c> 读内存快照，会显示旧值直到下次派活或进程重启。
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
                OwnerSessionId = e.OwnerSessionId,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 花费随列表一次带回，而不是让前端对每个成员各发一次 /runs：
        // 「团队里谁最贵」是个排行榜问题，逐条往返会把它变成 N 次请求 + N 次等待。
        // 账本行的 SubAgentId 列存的正是条目的 sub_session_id（写入时同源），
        // 所以按它归集；改名、换模板都不会断开。
        var sessionIds = entries
            .Select(e => e.SubSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (sessionIds.Count > 0)
        {
            var ledgerRows = await db.TokenUsageEvents
                .AsNoTracking()
                .Where(e => e.SubAgentId != null && sessionIds.Contains(e.SubAgentId))
                .Select(e => new { e.SubAgentId, e.TotalTokens, e.TotalCost })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var spendBySession = ledgerRows
                .GroupBy(x => x.SubAgentId!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => SummarizeSpend(g.Select(x => (x.TotalTokens, x.TotalCost)).ToList()),
                    StringComparer.Ordinal);

            foreach (var item in entries)
            {
                if (!string.IsNullOrWhiteSpace(item.SubSessionId)
                    && spendBySession.TryGetValue(item.SubSessionId, out var entrySpend))
                {
                    item.Spend = entrySpend;
                }
            }
        }

        return Ok(entries);
    }

    /// <summary>
    /// 池容量上限（只读）。
    /// <para>
    /// <b>为什么要有一个端点：</b>上限来自配置 <c>SubAgentPool:MaxCapacity</c>（缺省 15），
    /// 平时只出现在启动日志里。达到上限时 <c>CreateAsync</c> 抛
    /// 「Sub-agent pool is full」—— 界面此前看不到这个数字，用户会在建第 16 个成员时
    /// 突然撞墙，且不知道墙在哪。
    /// </para>
    /// <para>
    /// <b>只返回上限，不返回已用数：</b>「会不会满」的真实判据是内存池的条目数，
    /// 而列表读的是库。池尚未加载完时两者不同，用库计数当分子会比不显示更误导。
    /// 已用数就是列表本身的条数，由界面自己数。
    /// </para>
    /// <para>
    /// <b>休眠不释放容量：</b>只有销毁（写 <c>deleted_at</c>）才让条目退出计数。
    /// 这条差异由界面在休眠按钮处说明，端点不重复表达。
    /// </para>
    /// <para>
    /// <b>已知保留字：</b>路由段 <c>capacity</c> 是字面量，优先于详情端点的
    /// <c>{name}</c>。因此名为 <c>capacity</c> 的池条目无法用
    /// <c>GET /api/admin/sub-agent-pool/capacity</c> 取详情 —— 它会被这里接走。
    /// </para>
    /// </summary>
    /// <returns>容量上限。</returns>
    [HttpGet("capacity")]
    [ProducesResponseType<SubAgentPoolCapacityDto>(StatusCodes.Status200OK)]
    public ActionResult<SubAgentPoolCapacityDto> GetCapacity() =>
        Ok(new SubAgentPoolCapacityDto { Capacity = subAgentPool.MaxCapacity });

    /// <summary>
    /// 把一批账本行汇总成界面契约。
    /// <para>
    /// <b>两处调用方（列表与运行历史）必须共用这一个实现：</b>各写一份同义公式，
    /// 同一位成员在两个界面显示不同金额只是时间问题。
    /// </para>
    /// <para>
    /// 未计价 = <c>cost == 0 且 tokens &gt; 0</c>。资源池没登记价格时账本只能写 0，
    /// 与「确实没花钱」在库里长得一样，所以必须单独数出来，而不是折进总额。
    /// </para>
    /// </summary>
    /// <returns>没有任何账本行时返回 <c>null</c>：没有账本不等于花了 0 元。</returns>
    private static SubAgentSpendDto? SummarizeSpend(
        IReadOnlyCollection<(long TotalTokens, decimal TotalCost)> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        return new SubAgentSpendDto
        {
            TotalTokens = rows.Sum(x => x.TotalTokens),
            PricedCostUsd = rows.Sum(x => x.TotalCost),
            PricedCalls = rows.Count(x => x.TotalCost > 0m),
            UnpricedCalls = rows.Count(x => x.TotalCost == 0m && x.TotalTokens > 0),
        };
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name_required", detail = "池条目名不能为空。" });
        }

        IReadOnlyList<PooledSubAgent> snapshot;
        try
        {
            snapshot = await subAgentPool.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (PoolNotReadyException ex)
        {
            // 恢复失败不等于没有条目：如实报「池未就绪」，让运维去看启动日志。
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "pool_not_ready", detail = ex.Message });
        }

        // 快照取自库（真源），因此「快照里没有」= 库里没有这个活着的条目：
        // 要么名字拼错，要么它已被销毁（软删后不再进快照）。
        // 这与 409 是两件事 —— 混成一个会让人去查「归属」，而实际只是打错了一个字。
        var exists = snapshot.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (!exists)
        {
            return NotFound(new
            {
                error = "not_found",
                detail = $"池内没有名为 '{name}' 的条目：名字拼错，或它已被销毁。",
            });
        }

        var destroyed = await subAgentPool.DestroyAsync(name, ct).ConfigureAwait(false);
        if (destroyed)
        {
            return NoContent();
        }

        // 走到这里说明「库里有、本进程销毁不了」。两种可能且不细分：
        // 条目由另一进程创建（本进程内存里没有它），或库拒绝了状态迁移。
        // 两者的处置都是「换进程操作 / 先查库里的实际状态」，与名字打错完全不同。
        return Conflict(new
        {
            error = "destroy_rejected",
            detail = "条目在库中存在，但本进程无法完成销毁：它可能由另一进程创建" +
                     "（本进程内存里没有它），或库拒绝了状态迁移。",
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
    /// 修改池条目的展示字段（备注 / 头像键 / 角色 / 模型路由）。
    /// <para>
    /// <b>这里可以直接写库、不经过池实例</b>：备注、头像键、模型路由都不在
    /// <see cref="PooledSubAgent"/> 快照里，内存缓存根本不持有它们，
    /// 「库改了内存没改」不构成分叉。状态类字段（status / task_count /
    /// active_execution_id）恰好相反：它们就在内存里，必须走池。
    /// </para>
    /// <para>
    /// <b><c>role</c> 每单都会从库刷新：</b><c>ExecuteAsync</c> 在构建执行请求前调用
    /// <c>RefreshDeclaredRoleLockedAsync</c>，后者按 <c>pool_id</c> 读库并覆盖内存值，
    /// 因此面板改完这一列，同一进程的下一单就用新角色。刷新失败时退回缓存值并记 warning，
    /// 不阻断派活。残留的一处陈旧是工具侧 <c>pool_action="list"</c> 读内存快照，
    /// 它在下次派活或进程重启前仍显示旧值。
    /// </para>
    /// <para>
    /// <b>模型路由同样在使用点回读：</b>派活未显式指定 model 时，<c>SubAgentTool</c> 先调
    /// <c>GetDeclaredModelIdAsync</c> 取本列，取到即用它解析路由；显式 model 仍优先，
    /// 本列为空时退回平台默认。所以「改完下次就用它跑」对未显式指定 model 的派活成立，
    /// 对调用方显式指定的派活不成立。
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

        if (body.Role is { Length: > 64 })
        {
            return BadRequest(new { error = "role_too_long", detail = "角色标签最长 64 字符。" });
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

        if (body.Role is not null)
        {
            // 与 model_id 同一口径：空串归一化成 NULL，库里「没设」只有一种表示。
            entity.Role = body.Role.Length == 0 ? null : body.Role;
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

    /// <summary>
    /// 某池条目的运行历史（**只读**，最近的在前）。
    /// <para>
    /// <b>为什么这条端点对「管理」是必需的：</b>此前管理视图只够看骨架 ——
    /// 名字、状态、任务数、最近使用时间。管理者看不到这个成员具体跑过什么、
    /// 每次跑了多久、用了多少轮工具、失败时报的什么错。没有运行历史的团队管理，
    /// 与只看花名册没有区别。
    /// </para>
    /// <para>
    /// <b>关联键是 <c>sub_session_id</c>，不是名字：</b><c>sub_agent_runs</c> 里没有池名，
    /// 而 <c>agent_pool.sub_session_id</c> 正是「复用同一会话」的那个稳定标识
    /// （池条目休眠/唤醒都保持它）。所以历史天然按会话归集，跨池条目改名也不会丢。
    /// </para>
    /// <para>
    /// <b>排序用自增主键而不是 <c>started_at</c> 文本：</b>后者是 ISO-8601 字符串列，
    /// 同偏移量下字典序确实等于时间序，但那是「碰巧成立」而不是契约；
    /// 一旦将来写入方带上非零偏移，排序就会错乱。自增 Id 天然是创建顺序。
    /// </para>
    /// <para>
    /// <b>名字不存在返回 404，条目存在但没有运行历史返回 200 + <c>[]</c>：</b>
    /// 「这个人从没干过活」与「这个人在不在团队里」是两件事，
    /// 合并成一个响应会让界面对新成员显示成错误。
    /// </para>
    /// </summary>
    /// <para>
    /// <b>返回分页信封而不是裸数组：</b>数组无法表达「被截断了」。
    /// 一个跑过 200 次的成员，界面拿到 20 条会显示得像「就这 20 条」，
    /// 于是「总共跑了 20 次」这个错误结论没有任何地方可以纠正它。
    /// <c>total</c> 是全量计数，<c>truncated</c> 是 <c>total &gt; items.Count</c> 的直接结论。
    /// </para>
    /// </summary>
    /// <param name="name">池内展示名（未软删时唯一）。</param>
    /// <param name="limit">最多返回多少条（1..100，越界自动夹取）。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpGet("{name}/runs")]
    [ProducesResponseType<SubAgentRunsPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubAgentRunsPageDto>> GetRuns(
        string name,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { error = "name_required", detail = "池条目名不能为空。" });
        }

        var take = Math.Clamp(limit, 1, 100);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // 只取两列：本端点判定存在性只需要 sub_session_id。
        var entry = await db.AgentPool
            .AsNoTracking()
            .Where(e => e.DeletedAt == null && e.Name == name)
            .Select(e => new { e.SubSessionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return NotFound(new
            {
                error = "not_found",
                detail = $"池内没有名为 '{name}' 的条目：名字拼错，或它已被销毁。",
            });
        }

        if (string.IsNullOrWhiteSpace(entry.SubSessionId))
        {
            // 条目存在但还没有会话标识：等价于「零条历史」，不是错误。
            // 但仍需返回信封形状 —— 让前端只处理一种响应结构，而不是两种。
            return Ok(new SubAgentRunsPageDto());
        }

        // total 是同一谓词下的全量计数：与下面 Take 之前的过滤条件必须逐字一致，
        // 否则 truncated 会凭空为真或为假。
        var total = await db.SubAgentRuns
            .AsNoTracking()
            .CountAsync(r => r.SubSessionId == entry.SubSessionId, ct)
            .ConfigureAwait(false);

        var runs = await db.SubAgentRuns
            .AsNoTracking()
            .Where(r => r.SubSessionId == entry.SubSessionId)
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(r => new SubAgentRunDto
            {
                RunId = r.RunId,
                Status = r.Status,
                TemplateId = r.TemplateId,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                TotalRounds = r.TotalRounds,
                TotalToolCalls = r.TotalToolCalls,
                TotalDurationMs = r.TotalDurationMs,
                ErrorMessage = r.ErrorMessage,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 花费汇总与运行历史是同一批 run 的两面：汇总是「这个成员一共花了多少」，
        // 历史是「每一次花了多少」。归集键用 sub_session_id —— 账本的 SubAgentId 列
        // 存的正是这个值（FileSubAgentRunStore 写入时同源），因此改名/换模板都不会断开。
        //
        // ⚠️ 未计价是**真实存在**的状态，不是边界情况：资源池 pricePer1MTokens 缺失时
        // TokenUsageRecorder 只能把 TotalCost 写成 0，与「真的花了 0 元」在库里长得一样。
        // 因此这里额外数出未计价行数，让界面能说「还有 N 次没有价格」，
        // 而不是把「价格未知」静默汇进总额。
        // 投影两列后在内存聚合，而不是 SQL 端 SUM/COUNT：
        // SQLite 提供程序把 decimal 存为 TEXT，SQL 端对 decimal 求 SUM 不受支持，
        // 而 SUM(CASE WHEN decimal > 0 ...) 会退化为字符串比较（"0.0000" > "0" 误判为真）。
        // 代价是把这个子会话的全部账本行拉回内存；它是一个片区的历史（实测最大百行量级），
        // 且本端点只在管理面板打开时调用。若将来单条目行数过万，应改为
        // 「SQL 端按 double 求和 + 显式精度取舍」，而不是把这里改成不完整采样。
        var ledger = await db.TokenUsageEvents
            .AsNoTracking()
            .Where(e => e.SubAgentId == entry.SubSessionId)
            .Select(e => new { e.TotalTokens, e.TotalCost })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var spend = SummarizeSpend(ledger.Select(x => (x.TotalTokens, x.TotalCost)).ToList());

        return Ok(new SubAgentRunsPageDto
        {
            Items = runs,
            Total = total,
            Truncated = total > runs.Count,
            Spend = spend,
        });
    }
}

/// <summary>
/// 修改池条目展示字段的请求体。
/// <para>
/// 四个字段都是可选的：「没传」= 保持原值。要清空必须显式传空字符串 ——
/// 把 null 当成「清空」会让一次只改头像的请求顺手抹掉备注。
/// </para>
/// </summary>
public sealed class UpdateSubAgentPoolEntryRequest
{
    /// <summary>一句话备注（自由文本，最长 200 字）。</summary>
    public string? Responsibility { get; init; }

    /// <summary>预设图形键（最长 32 字符）。</summary>
    public string? AvatarKey { get; init; }

    /// <summary>能力角色分类短标签（最长 64 字符；仅展示，不参与执行路由）。</summary>
    public string? Role { get; init; }

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
    /// <b>本列在执行路径上生效，但有前提：</b>派活未显式指定 model 时，
    /// <c>SubAgentTool</c> 会先用它解析路由；调用方显式给了 model 则显式值优先。
    /// 所以界面把它显示成「默认模型」是准确的，显示成「强制模型」则不准确。
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

    /// <summary>
    /// 归属父会话 id（创建它的那个会话），可能为空。
    /// <para>
    /// <b>这不只是元数据，是驱动权限：</b><c>SubAgentPool.ExecuteAsync</c> 会用它与
    /// 派发方的 <c>ParentSessionId</c> 比较，不等即抛 <c>PoolOwnershipMismatchException</c>。
    /// 也就是说条目是「会话作用域」的，换一个会话就不认；面板显示它，是为了让
    /// 「为什么在别的对话里用不了这个成员」有据可查，而不是靠猜。
    /// </para>
    /// </summary>
    public string? OwnerSessionId { get; init; }

    /// <summary>条目创建时间（ISO-8601 文本）。</summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>
    /// 该条目全部历史调用的花费汇总；无任何账本行时为 <c>null</c>。
    /// <para>
    /// <b>为什么用 <c>set</c> 而不是 <c>init</c>：</b>聚合需要先知道有哪些 sub_session_id，
    /// 只能在这条查询之后算；本类其余字段都是 <c>init</c>，此处例外是刻意的。
    /// </para>
    /// <para>
    /// <b>null ≠ 零：</b>null 表示从未产生账本记录，零表示有记录但一条都没计价。
    /// </para>
    /// </summary>
    public SubAgentSpendDto? Spend { get; set; }
}

/// <summary>
/// 运行历史的 HTTP 契约：分页信封。
/// <para>
/// <b>为什么不是裸数组：</b>数组只能表达「有什么」，不能表达「还差什么」。
/// 没有 <see cref="Total"/> 与 <see cref="Truncated"/>，界面就只能把截断后的结果
/// 当成全量展示 —— 那是「仪器谎报」最典型的一种：数字没错，含义错了。
/// </para>
/// <para>
/// <b>时间顺序由 <c>Items</c> 自身的顺序表达：</b>最近的在前，由后端
/// <c>ORDER BY Id DESC</c> 保证，前端不应重排（重排会引入第二套口径）。
/// </para>
/// </summary>
public sealed class SubAgentRunsPageDto
{
    /// <summary>本次返回的运行记录，最近的在前。</summary>
    public IReadOnlyList<SubAgentRunDto> Items { get; init; } = Array.Empty<SubAgentRunDto>();

    /// <summary>该条目全量运行记录条数（不受 <c>limit</c> 影响）。</summary>
    public int Total { get; init; }

    /// <summary>是否因 <c>limit</c> 截断；等价于 <c>Total &gt; Items.Count</c>。</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// 该条目全部历史调用的花费汇总；无任何账本行时为 <c>null</c>。
    /// <para>
    /// <b>null ≠ 零：</b>null 表示「这个子会话从未产生过账本记录」，零表示「有记录但一条都没计价」。
    /// 前端据此决定是「不显示汇总」还是「全部未计价」。
    /// </para>
    /// </summary>
    public SubAgentSpendDto? Spend { get; init; }
}

/// <summary>
/// 子代理花费汇总（来自 <c>TokenUsageEvents</c> 账本）。
/// <para>
/// <b>为什么金额与未计价计数必须一起返回：</b>资源池缺价格时
/// <c>TokenUsageRecorder</c> 只能写 <c>TotalCost = 0</c>，与「确实免费」在库里无法区分。
/// 只返回金额的看板会把 85M token 的模型花费显示成 $0（实测：claude-sonnet $49M / claude-opus $24M
/// 全部计为 0）。带上未计价计数，界面才能说「$X，另有 N 次没有价格」。
/// </para>
/// </summary>
public sealed class SubAgentSpendDto
{
    /// <summary>全部账本行的 token 合计（含未计价行）。</summary>
    public long TotalTokens { get; init; }

    /// <summary>已计价行的金额合计（USD）；未计价行贡献 0。</summary>
    public decimal PricedCostUsd { get; init; }

    /// <summary>已计价调用数（<c>TotalCost &gt; 0</c>）。</summary>
    public int PricedCalls { get; init; }

    /// <summary>
    /// 未计价调用数（<c>TotalCost == 0</c> 但 <c>TotalTokens &gt; 0</c>）。
    /// <para>TotalTokens 为 0 的行不算未计价：那是没有可计量用量的失败请求，不是缺价。</para>
    /// </summary>
    public int UnpricedCalls { get; init; }
}

/// <summary>
/// 子代理单次运行的 HTTP 契约（camelCase 由 ASP.NET 默认序列化策略产出）。
/// <para>
/// <b>刻意不返回 <c>archive_path</c>：</b>那是本机绝对路径，界面用不上，
/// 而它在 admin 响应体里出现只会扩大信息面。需要看归档内容时应另开按 runId 读取的端点，
/// 由后端做路径边界校验，而不是让前端拿着路径自己去拼。
/// </para>
/// <para>
/// 时间列与 <see cref="SubAgentPoolEntryDto"/> 同一口径：原样透出库里的 ISO-8601 文本，
/// 不转 <see cref="DateTime"/> —— 诊断时恰恰需要看到「库里到底存的是什么」。
/// </para>
/// </summary>
public sealed class SubAgentRunDto
{
    /// <summary>运行 id（<c>run_yyyyMMdd_HHmmss_hex</c>）。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>运行状态；取值词表见 <c>SubAgentRunEntity.TerminalStatuses</c>。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>该次运行使用的 agent 模板 id。</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>开始时间（ISO-8601 文本）。</summary>
    public string StartedAt { get; init; } = string.Empty;

    /// <summary>结束时间（ISO-8601 文本）；非终态为 null。</summary>
    public string? CompletedAt { get; init; }

    /// <summary>本次运行的 LLM 轮次数。</summary>
    public int TotalRounds { get; init; }

    /// <summary>本次运行的工具调用次数。</summary>
    public int TotalToolCalls { get; init; }

    /// <summary>本次运行总耗时（毫秒）。</summary>
    public long TotalDurationMs { get; init; }

    /// <summary>失败原因；成功为 null。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 池容量上限的 HTTP 契约。
/// <para>
/// 只有一个字段是刻意的：多一个「已用数」就需要选口径（库计数 vs 内存计数），
/// 而两者在池未加载完时并不相等。界面上「已用」直接就是列表长度，
/// 没有第二个真源，也就不存在两个数字对不上的问题。
/// </para>
/// </summary>
public sealed class SubAgentPoolCapacityDto
{
    /// <summary>池内条目数上限；达到后新建会失败。</summary>
    public int Capacity { get; init; }
}
