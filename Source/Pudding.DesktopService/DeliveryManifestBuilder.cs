namespace Pudding.DesktopService;

/// <summary>
/// 生成交付清单所需的**事实**（由构建/测试/日志汇总而来）。
/// 刻意保持"事实"形态：生成器不做判断，判断留给 <see cref="DeliveryManifestValidator"/>。
/// </summary>
public sealed record DeliveryBuildFacts(
    string? BuildId,
    string? Commit,
    IReadOnlyList<string>? Artifacts,
    IReadOnlyList<DeliveryCheck>? Checks,
    IReadOnlyList<string>? ChangelogDocuments,
    IReadOnlyList<string>? AdditionalKnownUnverified = null,
    IReadOnlyList<string>? RequiredExternalSteps = null,
    int ChangelogFilesTotal = 0,
    int ChangelogFilesScanned = 0,
    IReadOnlyList<string>? RetryNotes = null);

/// <summary>
/// **交付清单生成器**：把事实汇总成清单，并**用同一套校验**自检（不合格就不产出清单）。
///
/// 这是 B7 交付链的最后一块：构建产物 + 各套件测试计数 + 从修改日志提取的未验证项 + 生命周期四步
/// → <see cref="DeliveryManifest"/> → 自校验 → JSON。三条刻意的设计：
/// <list type="number">
///   <item><b>未验证项来自日志</b>（<see cref="KnownUnverifiedExtractor"/>），不靠人回忆；</item>
///   <item><b>不合格就不产出</b>：宁可让调用方拿到具体问题，也不产出一份"看起来能交"的清单；</item>
///   <item><b>校验只有一份</b>：生成器不自己写规则，直接调用 <see cref="DeliveryManifestValidator"/>。</item>
/// </list>
/// </summary>
public static class DeliveryManifestBuilder
{
    /// <summary>尝试生成清单。返回 <c>false</c> 时 <paramref name="problems"/> 给出全部原因。</summary>
    public static bool TryBuild(
        DeliveryBuildFacts facts,
        out DeliveryManifest? manifest,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(facts);
        manifest = null;

        var collected = new List<string>();

        if (string.IsNullOrWhiteSpace(facts.BuildId))
        {
            collected.Add("buildId is required (the external controller must know which build it is deploying)");
        }

        if (string.IsNullOrWhiteSpace(facts.Commit))
        {
            collected.Add("commit is required (a build nobody can trace back is not deliverable)");
        }

        if (facts.Artifacts is not { Count: > 0 })
        {
            collected.Add("at least one artifact is required (nothing to deploy)");
        }

        if (facts.Checks is not { Count: > 0 })
        {
            collected.Add("at least one recorded check is required (an unverified build is not deliverable)");
        }

        // 未验证项：日志提取 + 调用方补充（去重、保序）；提取器保证**永不返回 null**。
        var knownUnverified = new List<string>();
        var extraction = KnownUnverifiedExtractor.ExtractDetailed(facts.ChangelogDocuments ?? []);
        knownUnverified.AddRange(extraction.Items);
        // 提取器**有展示上限**：把实际条数一并带上，清单才能如实说"显示 N / 共 M（已截断）"。
        var knownUnverifiedTotal = extraction.TotalDistinct;
        foreach (var item in facts.AdditionalKnownUnverified ?? [])
        {
            if (!string.IsNullOrWhiteSpace(item) && !knownUnverified.Contains(item.Trim(), StringComparer.Ordinal))
            {
                knownUnverified.Add(item.Trim());
                knownUnverifiedTotal++;
            }
        }

        if (collected.Count > 0)
        {
            problems = collected;
            return false;
        }

        var candidate = new DeliveryManifest(
            facts.BuildId!.Trim(),
            facts.Commit!.Trim(),
            facts.Artifacts!,
            facts.Checks!,
            knownUnverified,
            knownUnverifiedTotal,
            facts.RequiredExternalSteps is { Count: > 0 }
                ? facts.RequiredExternalSteps
                : DeliveryManifest.RequiredLifecycleSteps,
            facts.ChangelogFilesScanned,
            facts.ChangelogFilesTotal,
            facts.RetryNotes);

        var validation = DeliveryManifestValidator.Validate(candidate);
        if (validation.Count > 0)
        {
            problems = validation;
            return false;
        }

        manifest = candidate;
        problems = [];
        return true;
    }
}
