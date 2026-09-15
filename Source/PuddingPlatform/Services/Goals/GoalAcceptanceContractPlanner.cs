using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.1（G92-1）：空合同的**有界规划**步骤。
/// <para>
/// 只在「合同缺失或条件为空」时运行，依据两类显式输入生成 build/test 条件与检查定义：
/// ① objective 中的**显式证据声明**（GoalObjectiveEvidenceParser，目标级验收）；
/// ② 显式配置的受检目标（GoalRuns:CheckProjects，回归门禁）。
/// 不做动态项目发现、不接受模型自由输入、不扩大权限。
/// 两类输入都为空时保持空合同（fail-closed：走有界修复，而不是 vacuous pass）。
/// 已有条件的合同一律不改写（条件变化必须由条件的 Revision 表达，不能由规划器偷偷替换）。
/// </para>
/// </summary>
public sealed class GoalAcceptanceContractPlanner(
    GoalAcceptanceContractStore contractStore,
    IOptions<GoalRunOptions> options,
    ILogger<GoalAcceptanceContractPlanner>? logger = null)
{
    public const string Source = "bounded_planning";

    /// <summary>合同含 objective 证据声明的目标级条件时的合同级 Source 标识（与纯门禁合同可区分）。</summary>
    public const string SourceWithObjectiveEvidence = "bounded_planning:objective_evidence";

    private const string BuildDefinitionRef = "checks/build.md#dotnet-build";
    private const string TestDefinitionRef = "checks/test.md#dotnet-test";

    private readonly GoalRunOptions _options = options.Value;

    /// <summary>合同为空时生成一份真实合同；返回是否新写入/更新了合同。</summary>
    /// <remarks>
    /// <paramref name="objective"/> 只用于解析**显式**证据声明（迭代1）；
    /// 未声明证据时行为与纯门禁规划完全一致，但会落 warning 日志让「未做目标级验收」可见。
    /// </remarks>
    public async Task<bool> EnsureContractAsync(
        string goalRunId,
        int activationEpoch,
        int objectiveVersion,
        string? planFingerprint,
        string? objective = null,
        CancellationToken ct = default)
    {
        var existing = await contractStore.LoadAsync(goalRunId, activationEpoch, objectiveVersion, ct);
        if (existing is not null
            && GoalVerificationPersistence.ReadCriteria(existing.CriteriaJson).Count > 0)
        {
            // 已有条件的合同不重写：条件变化必须显式提升 Revision。
            return false;
        }

        var projects = ResolveProjects();
        var evidenceTargets = GoalObjectiveEvidenceParser.ExtractEvidenceTargets(objective, logger);
        if (projects.Count == 0 && evidenceTargets.Count == 0)
        {
            logger?.LogInformation(
                "[GoalContract] no bounded check targets configured; contract stays empty (repair path) goal={GoalRunId} epoch={Epoch}",
                goalRunId,
                activationEpoch);
            return false;
        }

        var fingerprint = ResolveInputFingerprint(planFingerprint, activationEpoch, objectiveVersion);
        var criteria = new List<GoalCriterion>();
        var checks = new List<GoalCheckSpec>();

        // 目标级条件在前：objective 显式声明的证据目标是「目标达成」的验收对象。
        foreach (var project in evidenceTargets)
        {
            AddEvidencePair(criteria, checks, project, fingerprint);
        }

        // 回归门禁在后：配置级工程检查保持既有 id 与行为，始终作为回归护栏。
        foreach (var project in projects)
        {
            AddBoundedPair(criteria, checks, project, fingerprint);
        }

        if (criteria.Count == 0)
            return false;

        if (evidenceTargets.Count == 0)
        {
            logger?.LogWarning(
                "[GoalContract] objective declares no evidence; contract covers engineering gates only goal={GoalRunId}",
                goalRunId);
        }

        var source = evidenceTargets.Count > 0 ? SourceWithObjectiveEvidence : Source;
        await contractStore.SaveAsync(
            goalRunId,
            activationEpoch,
            objectiveVersion,
            planFingerprint,
            criteria,
            checks,
            source,
            ct);

        logger?.LogInformation(
            "[GoalContract] bounded contract written goal={GoalRunId} epoch={Epoch} criteria={Criteria} checks={Checks} objectiveEvidence={ObjectiveEvidence}",
            goalRunId,
            activationEpoch,
            criteria.Count,
            checks.Count,
            evidenceTargets.Count);
        return true;
    }

    /// <summary>只接受显式配置、且通过注册表安全校验的相对项目路径；去重并保持顺序。</summary>
    private List<string> ResolveProjects()
    {
        var configured = _options.CheckProjects;
        var projects = new List<string>();
        if (configured is null || configured.Length == 0)
            return projects;

        foreach (var raw in configured)
        {
            var project = raw?.Trim();
            if (string.IsNullOrWhiteSpace(project))
                continue;
            if (!GoalCheckDefinitionRegistry.IsSafeTarget(project))
            {
                logger?.LogWarning("[GoalContract] rejecting unsafe check target {Target}", raw);
                continue;
            }

            if (!projects.Contains(project, StringComparer.Ordinal))
                projects.Add(project);
        }

        return projects;
    }

    /// <summary>
    /// 输入指纹取绑定的 Plan 指纹（工作树/计划变化即失效）；缺失时退化为 epoch+objective 作用域值，
    /// 此时不宣称能检测内容变化，但仍是稳定身份（不会把不同轮次的结果混为一谈）。
    /// </summary>
    private static string ResolveInputFingerprint(
        string? planFingerprint,
        int activationEpoch,
        int objectiveVersion)
        => string.IsNullOrWhiteSpace(planFingerprint)
            ? $"epoch:{activationEpoch}:objective:{objectiveVersion}"
            : planFingerprint.Trim();

    private static void AddBoundedPair(
        List<GoalCriterion> criteria,
        List<GoalCheckSpec> checks,
        string project,
        string inputFingerprint)
    {
        AddBoundedCheck(criteria, checks, project, BuildDefinitionRef, inputFingerprint);
        AddBoundedCheck(criteria, checks, project, TestDefinitionRef, inputFingerprint);
    }

    private static void AddBoundedCheck(
        List<GoalCriterion> criteria,
        List<GoalCheckSpec> checks,
        string project,
        string definitionRef,
        string inputFingerprint)
    {
        if (!GoalCheckDefinitionRegistry.TryResolve(definitionRef, out var definition))
            return;

        var criterionId = $"{definition.Kind}:{project}";
        var definitionHash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(definition);

        criteria.Add(new GoalCriterion
        {
            Id = criterionId,
            Revision = 1,
            Requirement = string.Equals(definition.Kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal)
                ? $"回归门禁：{project} 必须构建成功（真实执行，退出码 0）"
                : $"回归门禁：{project} 的测试必须真实执行且全部通过（不接受自我声明）",
            Required = true,
            Kind = definition.Kind,
            DefinitionRef = definition.DefinitionRef,
            DefinitionHash = definitionHash,
            InputRefs = [project],
            ExecutorRole = "core",
            FreshnessPolicy = "input-fingerprint",
            DependencyIds = [],
        });

        checks.Add(new GoalCheckSpec
        {
            CheckId = $"bounded:{definition.Kind}:{project}",
            CriterionId = criterionId,
            CriterionRevision = 1,
            Kind = definition.Kind,
            DefinitionRef = definition.DefinitionRef,
            DefinitionHash = definitionHash,
            InputRefs = [project],
            InputFingerprint = inputFingerprint,
            ExecutorRole = "core",
            ExpectedEvidence = $"受控执行器运行 {definition.CommandTemplate} 并产生真实报告",
            ExpectedTestCount = null,
        });
    }

    /// <summary>目标声明的证据条件：与门禁同一组受控定义，但 id 与文案前缀区分来源。</summary>
    private static void AddEvidencePair(
        List<GoalCriterion> criteria,
        List<GoalCheckSpec> checks,
        string project,
        string inputFingerprint)
    {
        AddEvidenceCheck(criteria, checks, project, BuildDefinitionRef, inputFingerprint);
        AddEvidenceCheck(criteria, checks, project, TestDefinitionRef, inputFingerprint);
    }

    private static void AddEvidenceCheck(
        List<GoalCriterion> criteria,
        List<GoalCheckSpec> checks,
        string project,
        string definitionRef,
        string inputFingerprint)
    {
        if (!GoalCheckDefinitionRegistry.TryResolve(definitionRef, out var definition))
            return;

        var criterionId = $"objective-{definition.Kind}:{project}";
        var definitionHash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(definition);

        criteria.Add(new GoalCriterion
        {
            Id = criterionId,
            Revision = 1,
            Requirement = string.Equals(definition.Kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal)
                ? $"目标声明的证据：{project} 必须构建成功（真实执行，退出码 0）"
                : $"目标声明的证据：{project} 的测试必须真实执行且全部通过（不接受自我声明）",
            Required = true,
            Kind = definition.Kind,
            DefinitionRef = definition.DefinitionRef,
            DefinitionHash = definitionHash,
            InputRefs = [project],
            ExecutorRole = "core",
            FreshnessPolicy = "input-fingerprint",
            DependencyIds = [],
        });

        checks.Add(new GoalCheckSpec
        {
            CheckId = $"objective:{definition.Kind}:{project}",
            CriterionId = criterionId,
            CriterionRevision = 1,
            Kind = definition.Kind,
            DefinitionRef = definition.DefinitionRef,
            DefinitionHash = definitionHash,
            InputRefs = [project],
            InputFingerprint = inputFingerprint,
            ExecutorRole = "core",
            ExpectedEvidence = $"受控执行器运行 {definition.CommandTemplate} 并产生真实报告",
            ExpectedTestCount = null,
        });
    }
}
