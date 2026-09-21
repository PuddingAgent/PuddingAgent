using PuddingCode.Abstractions;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 预算裁决的**执行结果**（与 <see cref="SkillAdmissionActions"/> 同词表，不新建动作概念）。
/// <para>
/// 调用方据此更新自己的计数与日志；<see cref="Action"/> 是**实际发生**的动作，
/// 可能与送进来的裁决不同（置换失败会回滚成 <c>defer</c>）。
/// </para>
/// </summary>
public sealed record SkillPortfolioExecutionOutcome
{
    /// <summary>实际发生的动作。</summary>
    public required string Action { get; init; }

    /// <summary>新建成功的技能 id；未建成为 <c>null</c>。</summary>
    public string? SkillId { get; init; }

    /// <summary>被置换的技能 id（置换路径才有值，回滚后仍保留以便追责/回滚）。</summary>
    public string? DisplacedSkillId { get; init; }

    /// <summary>置换是否因"候选择建不出来"而回滚（true ⇒ 被置换者已被恢复启用）。</summary>
    public bool DisplacementRolledBack { get; init; }
}

/// <summary>
/// 预算裁决的**执行器**：把 <see cref="SkillPortfolioAdmissionJudge"/> 给出的动作落到技能仓上。
/// <para>
/// <b>为什么单独一层</b>：判定器是纯函数（可穷举单测），但"置换＝禁用价值最低者"这一条验收标准
/// 考的是**副作用**——禁用、再建、失败回滚。把副作用收进本类型后，它可以用探针式假仓穷举验证
/// （包括"非置换路径零写盘"与"回滚"），而不必启动整个 orchestrator。
/// </para>
/// <para>
/// <b>两条硬约束</b>：
/// ① 只有 <c>create</c> 与 <c>displace</c> 会写盘，<c>merge</c>/<c>skip</c>/<c>defer</c> 一律零副作用；
/// ② 置换要么"禁用 + 建成"、要么"回滚恢复"，**不允许出现净减一个技能的中间态**。
/// </para>
/// </summary>
/// <param name="skillStore">技能仓（唯一的写盘出口）。</param>
public sealed class SkillPortfolioAdmissionExecutor(IAgentSkillEvolutionStore skillStore)
{
    private readonly IAgentSkillEvolutionStore _skillStore =
        skillStore ?? throw new ArgumentNullException(nameof(skillStore));

    /// <summary>
    /// 执行一次裁决。
    /// </summary>
    /// <param name="agentInstanceId">技能归属的 Agent 实例。</param>
    /// <param name="admission">待执行的裁决（来自判定器，或未接线时的既有去重结论）。</param>
    /// <param name="materializeCandidateAsync">物化候选的回调；返回新技能 id，失败返回 <c>null</c>。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SkillPortfolioExecutionOutcome> ExecuteAsync(
        string agentInstanceId,
        SkillAdmissionResult admission,
        Func<Task<string?>> materializeCandidateAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(materializeCandidateAsync);

        if (string.Equals(admission.Action, SkillAdmissionActions.Create, StringComparison.Ordinal))
        {
            var skillId = await materializeCandidateAsync();
            return new SkillPortfolioExecutionOutcome
            {
                Action = SkillAdmissionActions.Create,
                SkillId = skillId,
            };
        }

        if (!string.Equals(admission.Action, SkillAdmissionActions.Displace, StringComparison.Ordinal))
        {
            // merge / skip / defer 都是零副作用裁决：执行器绝不因此碰技能仓。
            return new SkillPortfolioExecutionOutcome { Action = admission.Action };
        }

        if (string.IsNullOrWhiteSpace(admission.TargetSkillId))
        {
            // fail-closed：拿不到置换目标就不许"先建后算"，且**一次写盘都不许有**。
            return new SkillPortfolioExecutionOutcome { Action = SkillAdmissionActions.Defer };
        }

        var displacedSkillId = admission.TargetSkillId;
        await _skillStore.SetEnabledAsync(agentInstanceId, displacedSkillId, enabled: false, ct);

        string? replacementSkillId;
        try
        {
            replacementSkillId = await materializeCandidateAsync();
        }
        catch
        {
            // 回滚后原样抛出：调用方仍须看到失败，但技能仓不得停在"少一个技能"的状态。
            await _skillStore.SetEnabledAsync(agentInstanceId, displacedSkillId, enabled: true, ct);
            throw;
        }

        if (replacementSkillId is not null)
        {
            return new SkillPortfolioExecutionOutcome
            {
                Action = SkillAdmissionActions.Displace,
                SkillId = replacementSkillId,
                DisplacedSkillId = displacedSkillId,
            };
        }

        await _skillStore.SetEnabledAsync(agentInstanceId, displacedSkillId, enabled: true, ct);
        return new SkillPortfolioExecutionOutcome
        {
            Action = SkillAdmissionActions.Defer,
            DisplacedSkillId = displacedSkillId,
            DisplacementRolledBack = true,
        };
    }
}
