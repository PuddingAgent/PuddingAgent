using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 自动清理的**纯决策**逻辑（2026-10-27，S1 第一部分）。
///
/// 背景缺陷：<see cref="StorageMaintenanceCoordinator"/> 的 automatic 队列没有周期性生产者，
/// 只有重启恢复会写入，因此自动清理自上线起从未触发（27 天积累 1017 万行，见
/// Docs/14_reports/2026-10-27-存储自动清理空转诊断.md）。
///
/// 本类型只回答「**本轮应当评估哪些数据类、保留多少天、跳过原因是什么**」，
/// 不接触数据库、不创建作业、不删除任何东西。把它独立成纯函数的目的：
/// 让 S2（真正接线到 coordinator）之前，决策规则可以被单测钉死。
/// </summary>
internal static class StorageAutomaticCleanupPolicy
{
    /// <summary>被跳过的原因。顺序即判定优先级，先命中者生效。</summary>
    internal enum SkipReason
    {
        /// <summary>应当参与自动清理。</summary>
        None = 0,
        /// <summary>证据 / 用户创作数据：禁止任何清理。</summary>
        Protected,
        /// <summary>该数据类未开放自动清理。</summary>
        AutomaticDisabled,
        /// <summary>自动清理依赖长期聚合先落地（聚合未实现 → 有意关闭）。</summary>
        RequiresRollup,
        /// <summary>既无策略值也无默认保留天数，无从判断 cutoff。</summary>
        NoRetentionConfigured,
    }

    /// <summary>一个待评估的候选：保留天数与由此得出的 cutoff。</summary>
    internal readonly record struct Candidate(string TargetId, int RetentionDays, DateTimeOffset CutoffUtc);

    /// <summary>单个数据类的决策结果。<see cref="Candidate"/> 仅在 <see cref="Reason"/> 为 None 时有值。</summary>
    internal readonly record struct Decision(string TargetId, SkipReason Reason, Candidate? Candidate)
    {
        internal bool IsCandidate => Reason == SkipReason.None && Candidate is not null;
    }

    /// <summary>按目录顺序评估全部数据类。纯函数：不读时钟、不读配置、无副作用。</summary>
    internal static IReadOnlyList<Decision> Evaluate(
        IEnumerable<StorageDataClassCatalog.StorageDataClassDefinition> definitions,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var decisions = new List<Decision>();

        foreach (var definition in definitions)
        {
            if (definition is null)
                continue;

            // 1. 受保护优先于一切：即使误配了 AutomaticCleanupAllowed 也不得清理。
            if (IsProtected(definition.SafetyLevel))
            {
                decisions.Add(new Decision(definition.TargetId, SkipReason.Protected, null));
                continue;
            }

            // 2. 显式关闭。
            if (!definition.AutomaticCleanupAllowed)
            {
                decisions.Add(new Decision(definition.TargetId, SkipReason.AutomaticDisabled, null));
                continue;
            }

            // 3. 依赖聚合：属于有意设计，不是缺陷（与"缺生产者"要分开看）。
            if (definition.RequiresRollupBeforeAutomatic)
            {
                decisions.Add(new Decision(definition.TargetId, SkipReason.RequiresRollup, null));
                continue;
            }

            // 4. 没有保留天数就无法算 cutoff —— 绝不默认成某个魔法值。
            if (definition.DefaultRetentionDays is not { } configuredDays || configuredDays <= 0)
            {
                decisions.Add(new Decision(definition.TargetId, SkipReason.NoRetentionConfigured, null));
                continue;
            }

            // 5. 夹回该数据类声明可接受的范围（目录里 Min/Max 可能缺省）。
            var days = configuredDays;
            if (definition.MinRetentionDays is { } min && days < min)
                days = min;
            if (definition.MaxRetentionDays is { } max && days > max)
                days = max;

            var cutoff = nowUtc.AddDays(-days);
            decisions.Add(new Decision(
                definition.TargetId,
                SkipReason.None,
                new Candidate(definition.TargetId, days, cutoff)));
        }

        return decisions;
    }

    /// <summary>与 <c>StorageDataClassCatalog.ToDataClassDtos</c> 的 Protected 口径保持一致。</summary>
    private static bool IsProtected(StorageSafetyLevel level) =>
        level is StorageSafetyLevel.Evidence or StorageSafetyLevel.UserData;
}
