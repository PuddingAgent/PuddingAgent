using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §5.1（G92-1）：空合同的**有界规划**步骤。
/// <para>
/// 只在「合同缺失或条件为空」时运行，且只依据显式配置的受检目标（GoalRuns:CheckProjects）生成
/// build/test 条件与检查定义：不做动态项目发现、不接受模型自由输入、不扩大权限。
/// 未配置受检目标时保持空合同（fail-closed：走有界修复，而不是 vacuous pass）。
/// 已有条件的合同一律不改写（条件变化必须由条件的 Revision 表达，不能由规划器偷偷替换）。
/// </para>
/// </summary>
public sealed class GoalAcceptanceContractPlanner(
    GoalAcceptanceContractStore contractStore,
    IOptions<GoalRunOptions> options,
    ILogger<GoalAcceptanceContractPlanner>? logger = null)
{
    public const string Source = "bounded_planning";

    private const string BuildDefinitionRef = "checks/build.md#dotnet-build";
    private const string TestDefinitionRef = "checks/test.md#dotnet-test";

    private readonly GoalRunOptions _options = options.Value;

    /// <summary>合同为空时生成一份真实合同；返回是否新写入/更新了合同。</summary>
    public async Task<bool> EnsureContractAsync(
        string goalRunId,
        int activationEpoch,
        int objectiveVersion,
        string? planFingerprint,
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
        if (projects.Count == 0)
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

        foreach (var project in projects)
        {
            AddBoundedPair(criteria, checks, project, fingerprint);
        }

        if (criteria.Count == 0)
            return false;

        await contractStore.SaveAsync(
            goalRunId,
            activationEpoch,
            objectiveVersion,
            planFingerprint,
            criteria,
            checks,
            Source,
            ct);

        logger?.LogInformation(
            "[GoalContract] bounded contract written goal={GoalRunId} epoch={Epoch} criteria={Criteria} checks={Checks}",
            goalRunId,
            activationEpoch,
            criteria.Count,
            checks.Count);
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
                ? $"{project} 必须构建成功（真实执行，退出码 0）"
                : $"{project} 的测试必须真实执行且全部通过（不接受自我声明）",
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
}
