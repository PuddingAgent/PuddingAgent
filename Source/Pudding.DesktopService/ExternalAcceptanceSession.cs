namespace Pudding.DesktopService;

/// <summary>一条外部观察的结果。</summary>
public enum SmokeObservationOutcome
{
    /// <summary>未执行（外部控制器没做，或做了没记录）。</summary>
    NotRun,

    /// <summary>观察通过。</summary>
    Passed,

    /// <summary>观察失败（看到的现象与期望不符）。</summary>
    Failed,
}

/// <summary>一条观察记录：对应清单里的项 + 结果 + 证据（看到的现象）。</summary>
public sealed record SmokeObservation(string CheckId, SmokeObservationOutcome Outcome, string? Evidence);

/// <summary>生命周期一步的外部证据（启动/重启/崩溃恢复/退出回收）。</summary>
public sealed record LifecycleEvidence(string Step, bool Passed, string? Evidence);

/// <summary>
/// 外部验收会话的全部输入事实。
/// </summary>
public sealed record ExternalAcceptanceInput(
    DeliveryManifest DeliveredManifest,
    string ObservedBuildId,
    IReadOnlyList<SmokeObservation> Observations,
    IReadOnlyList<LifecycleEvidence> Lifecycle);

/// <summary>
/// **外部验收会话的判定**（B7 的收口）：把「内部交付清单 + 外部 smoke 清单 + 外部观察事实」合成一个结论。
///
/// 它一层层复用已有真源，绝不自己重造规则：
/// <list type="bullet">
///   <item>观察项清单来自 <see cref="BrowserSmokePlan"/>（覆盖 G1–G11 是它的职责）；</item>
///   <item>生命周期四步来自 <see cref="DeliveryManifest.RequiredLifecycleSteps"/>；</item>
///   <item>"谁能作出这个结论"来自 <see cref="DesktopAcceptance"/>（内部主体一律拒绝）。</item>
/// </list>
///
/// 四条**拒绝**是核心：① 有未执行的观察项 ⇒ 不能算通过（"没测"不等于"通过"）；
/// ② 有任何失败 ⇒ 拒绝；③ 观察的构建与交付清单不一致 ⇒ 拒绝（旧构建上的结论不算数）；
/// ④ 生命周期四步缺证据 ⇒ 拒绝。
/// </summary>
public static class ExternalAcceptanceSession
{
    /// <summary>判定是否允许作出 `external-lifecycle-verified` 结论。</summary>
    public static bool TryConclude(
        ExternalAcceptanceInput input,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(input);
        var collected = new List<string>();

        // ④ 生命周期四步：每步都要有证据，且都通过。
        foreach (var step in DeliveryManifest.RequiredLifecycleSteps)
        {
            var evidence = input.Lifecycle?.FirstOrDefault(item =>
                string.Equals(item.Step, step, StringComparison.OrdinalIgnoreCase));

            if (evidence is null)
            {
                collected.Add($"lifecycle step '{step}' has no external evidence");
            }
            else if (!evidence.Passed)
            {
                collected.Add($"lifecycle step '{step}' did not pass");
            }
            else if (string.IsNullOrWhiteSpace(evidence.Evidence))
            {
                // 说"通过了"但没有任何观察记录 —— 无法复核。
                collected.Add($"lifecycle step '{step}' reports success without any recorded evidence");
            }
        }

        // ③ 构建一致性：外部结论必须是在**交付的那一份构建**上得到的。
        if (string.IsNullOrWhiteSpace(input.ObservedBuildId))
        {
            collected.Add("the observations must name the build they were made on");
        }
        else if (!string.Equals(input.ObservedBuildId.Trim(), input.DeliveredManifest.BuildId, StringComparison.Ordinal))
        {
            collected.Add(
                $"the observations were made on build '{input.ObservedBuildId.Trim()}'"
                + $" but the delivered manifest is '{input.DeliveredManifest.BuildId}'");
        }

        // ① 覆盖：清单里的每一项都要有观察记录，且不能是 NotRun。
        var observed = input.Observations ?? [];
        foreach (var check in BrowserSmokePlan.Checks)
        {
            var matches = observed
                .Where(observation => string.Equals(observation.CheckId, check.Id, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
            {
                collected.Add($"smoke check '{check.Id}' ({check.Gate}) was not observed");
                continue;
            }

            if (matches.Length > 1)
            {
                collected.Add($"smoke check '{check.Id}' was recorded {matches.Length} times (ambiguous evidence)");
                continue;
            }

            // ② 未执行 ≠ 通过；失败直接拒绝。
            switch (matches[0].Outcome)
            {
                case SmokeObservationOutcome.NotRun:
                    collected.Add($"smoke check '{check.Id}' ({check.Gate}) was not run");
                    break;
                case SmokeObservationOutcome.Failed:
                    collected.Add($"smoke check '{check.Id}' ({check.Gate}) failed");
                    break;
                case SmokeObservationOutcome.Passed when string.IsNullOrWhiteSpace(matches[0].Evidence):
                    collected.Add($"smoke check '{check.Id}' ({check.Gate}) passed without recorded evidence");
                    break;
            }
        }

        // 清单之外的观察记录：不阻止结论，但必须**显式登记**（否则可能是清单漂移的信号）。
        var knownIds = BrowserSmokePlan.Checks.Select(check => check.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = observed
            .Where(observation => !knownIds.Contains(observation.CheckId))
            .Select(observation => observation.CheckId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            collected.Add("observations reference unknown smoke checks: " + string.Join(", ", unknown));
        }

        // 谁能作出这个结论：只有外部控制器（规则来自 DesktopAcceptance，不在这里重写）。
        if (!DesktopAcceptance.CanClaim(
                new DesktopAcceptance.Evidence(
                    DesktopAcceptance.Stage.ExternalLifecycleVerified,
                    DesktopAcceptance.Claimant.ExternalController,
                    input.ObservedBuildId,
                    Checks: collected.Count == 0 ? ["verdict"] : [],
                    RanOnANewBuild: true,
                    ExercisesLifecycle: true),
                out var claimReason))
        {
            collected.Add(claimReason ?? "the external lifecycle verdict is not allowed");
        }

        problems = collected;
        return collected.Count == 0;
    }

    /// <summary>渲染验收记录（留痕：谁在哪个构建上看到了什么）。</summary>
    public static string ToMarkdown(ExternalAcceptanceInput input, bool concluded)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new System.Text.StringBuilder();
        builder.Append("| 观察项 | 门禁 | 结果 | 证据 |\n|---|---|---|---|\n");

        var observedById = (input.Observations ?? [])
            .GroupBy(observation => observation.CheckId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var check in BrowserSmokePlan.Checks)
        {
            var outcome = observedById.TryGetValue(check.Id, out var observation)
                ? observation.Outcome switch
                {
                    SmokeObservationOutcome.Passed => "通过",
                    SmokeObservationOutcome.Failed => "失败",
                    _ => "未执行",
                }
                : "未执行";

            var evidence = observedById.TryGetValue(check.Id, out var recorded) ? recorded.Evidence ?? "—" : "—";
            builder.Append($"| {check.Id} | {check.Gate} | {outcome} | {evidence} |\n");
        }

        builder.Append("\n| 生命周期 | 结果 | 证据 |\n|---|---|---|\n");
        foreach (var step in DeliveryManifest.RequiredLifecycleSteps)
        {
            var evidence = (input.Lifecycle ?? []).FirstOrDefault(item =>
                string.Equals(item.Step, step, StringComparison.OrdinalIgnoreCase));

            builder.Append(
                $"| {step} | {(evidence is null ? "缺证据" : evidence.Passed ? "通过" : "失败")} | {(evidence?.Evidence ?? "—")} |\n");
        }

        builder.Append($"\n观察构建：`{input.ObservedBuildId}`；交付构建：`{input.DeliveredManifest.BuildId}`；")
            .Append($"结论：**{(concluded ? "external-lifecycle-verified" : "未通过（详见校验问题）")}**\n");

        return builder.ToString();
    }
}
