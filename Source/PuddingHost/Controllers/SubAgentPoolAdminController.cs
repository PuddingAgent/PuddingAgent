using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingHost.Controllers;

/// <summary>
/// 子代理池（团队管理 → 子代理视图）的**只读**数据出口。
///
/// <para>
/// 在此之前池条目只存在于 <c>agent_pool</c> 表，Web 侧没有任何端点能读到它，
/// 于是界面只能显示空列表 —— 有数据和没这个功能，在用户眼里长得一样。
/// 本控制器只补「读」这一段：创建 / 唤醒 / 销毁属于后续切片，
/// 因此这里**一个写方法都没有**，也不接受任何请求体。
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
public sealed class SubAgentPoolAdminController(IDbContextFactory<PlatformDbContext> dbFactory)
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

    /// <summary>默认模型路由 <c>providerId/modelId</c>；为空表示跟随派发方。</summary>
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
