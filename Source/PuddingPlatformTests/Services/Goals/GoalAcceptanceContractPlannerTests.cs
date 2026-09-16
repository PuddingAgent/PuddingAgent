using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// ADR-092 §5.1（G92-1）：空合同只能由「有界规划」派生 —— 只依据显式配置的受检目标、只使用
/// 版本化注册表里的定义、不做动态发现；未配置则保持空合同（fail-closed，走有界修复而
/// 不是 vacuous pass）；已有条件的合同一律不重写。
/// </summary>
[TestClass]
public sealed class GoalAcceptanceContractPlannerTests
{
    private const string Project = "Source/PuddingPlatformTests/PuddingPlatformTests.csproj";

    private static GoalAcceptanceContractPlanner NewPlanner(
        IDbContextFactory<PlatformDbContext> factory,
        params string[] projects)
        => new(
            new GoalAcceptanceContractStore(factory),
            Options.Create(new GoalRunOptions { CheckProjects = projects }));

    [TestMethod]
    public async Task NoConfiguredTarget_LeavesContractEmpty()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, "   ", string.Empty);

        Assert.IsFalse(await planner.EnsureContractAsync("goal-empty", 1, 1, "fp-1"));
        Assert.IsNull(await new GoalAcceptanceContractStore(factory).LoadAsync("goal-empty", 1, 1));
    }

    [TestMethod]
    public async Task ConfiguredTarget_WritesBuildAndTestCriteriaUsingRegistryHashes()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project, Project);

        Assert.IsTrue(await planner.EnsureContractAsync("goal-1", 2, 3, "plan-fp-1"));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-1", 2, 3);
        Assert.IsNotNull(contract);
        Assert.AreEqual(1, contract!.ContractVersion);
        Assert.AreEqual("plan-fp-1", contract.PlanFingerprint);
        Assert.AreEqual(GoalAcceptanceContractPlanner.Source, contract.Source);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);

        // 同一个项目重复配置只生成一对条件/检查。
        Assert.AreEqual(2, criteria.Count);
        Assert.AreEqual(2, checks.Count);

        foreach (var criterion in criteria)
        {
            Assert.IsTrue(criterion.Required, $"criterion must be required: {criterion.Id}");
            Assert.AreEqual(1, criterion.Revision);
            Assert.AreEqual(1, criterion.InputRefs.Count);
            Assert.AreEqual(Project, criterion.InputRefs[0]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(criterion.Requirement));
        }

        foreach (var check in checks)
        {
            Assert.IsTrue(
                GoalCheckDefinitionRegistry.TryGetDefinitionHash(check.DefinitionRef, out var expectedHash),
                $"definition must be registered: {check.DefinitionRef}");
            Assert.AreEqual(expectedHash, check.DefinitionHash);
            Assert.AreEqual("plan-fp-1", check.InputFingerprint);
            Assert.AreEqual(Project, check.InputRefs[0]);
            Assert.AreEqual(1, check.CriterionRevision);

            // 检查必须绑定到同名条件的同一 revision（否则身份校验会判 check_identity_mismatch）。
            GoalCriterion? bound = null;
            foreach (var candidate in criteria)
            {
                if (string.Equals(candidate.Id, check.CriterionId, StringComparison.Ordinal))
                {
                    bound = candidate;
                    break;
                }
            }

            Assert.IsNotNull(bound, $"check must bind an existing criterion: {check.CheckId}");
            Assert.AreEqual(bound!.Revision, check.CriterionRevision);
        }

        var hasBuild = false;
        var hasTest = false;
        foreach (var criterion in criteria)
        {
            hasBuild |= string.Equals(criterion.Kind, GoalVerificationSpecKinds.Build, StringComparison.Ordinal);
            hasTest |= string.Equals(criterion.Kind, GoalVerificationSpecKinds.Test, StringComparison.Ordinal);
        }

        Assert.IsTrue(hasBuild, "bounded contract must contain a build criterion");
        Assert.IsTrue(hasTest, "bounded contract must contain a test criterion");
    }

    [TestMethod]
    public async Task UnsafeTargets_AreSkippedAndProduceNoContract()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(
            factory,
            "../escape.csproj",
            "C:/abs/abs.csproj",
            "Source/a.csproj && echo pwned",
            "Source/not-a-project.txt");

        Assert.IsFalse(await planner.EnsureContractAsync("goal-unsafe", 1, 1, "fp-1"));
        Assert.IsNull(await new GoalAcceptanceContractStore(factory).LoadAsync("goal-unsafe", 1, 1));
    }

    [TestMethod]
    public async Task ExistingContractWithCriteria_IsNotRewritten()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var store = new GoalAcceptanceContractStore(factory);
        var existingCriterion = new GoalCriterion
        {
            Id = "human:criterion",
            Revision = 7,
            Requirement = "人工声明的验收条件",
            Required = true,
            Kind = GoalVerificationSpecKinds.Semantic,
            DefinitionRef = "checks/manual.md#review",
            DefinitionHash = "hash-manual",
            InputRefs = [Project],
            ExecutorRole = "human",
            FreshnessPolicy = "input-fingerprint",
            DependencyIds = [],
        };
        var saved = await store.SaveAsync("goal-keep", 1, 1, "fp-keep", [existingCriterion], []);
        Assert.AreEqual(1, saved.ContractVersion);

        var planner = NewPlanner(factory, Project);
        Assert.IsFalse(await planner.EnsureContractAsync("goal-keep", 1, 1, "fp-keep"));

        var reloaded = await store.LoadAsync("goal-keep", 1, 1);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(1, reloaded!.ContractVersion);
        Assert.AreEqual("human:criterion", GoalVerificationPersistence.ReadCriteria(reloaded.CriteriaJson)[0].Id);
        Assert.AreEqual(0, GoalVerificationPersistence.ReadChecks(reloaded.ChecksJson).Count);
    }

    [TestMethod]
    public async Task MissingPlanFingerprint_DegradesToEpochScopedFingerprint()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project);

        Assert.IsTrue(await planner.EnsureContractAsync("goal-nofp", 4, 9, null));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-nofp", 4, 9);
        Assert.IsNotNull(contract);

        var checks = GoalVerificationPersistence.ReadChecks(contract!.ChecksJson);
        Assert.AreEqual(2, checks.Count);

        foreach (var check in checks)
            Assert.AreEqual("epoch:4:objective:9", check.InputFingerprint);
    }

    [TestMethod]
    public async Task ObjectiveEvidence_GeneratesObjectiveCriteria_AlongsideGates()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project);
        var objective = "完成目标；证据: " + Project;

        Assert.IsTrue(await planner.EnsureContractAsync("goal-obj", 1, 1, "fp-obj", objective));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-obj", 1, 1);
        Assert.IsNotNull(contract);
        Assert.AreEqual(GoalAcceptanceContractPlanner.SourceWithObjectiveEvidence, contract!.Source);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);

        // 目标级：build + test 各一条，id 与检查均带 objective 前缀。
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "objective-build:" + Project, StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "objective-test:" + Project, StringComparison.Ordinal)));
        Assert.IsTrue(checks.Any(c => string.Equals(c.CheckId, "objective:build:" + Project, StringComparison.Ordinal)));
        Assert.IsTrue(checks.Any(c => string.Equals(c.CheckId, "objective:test:" + Project, StringComparison.Ordinal)));

        // 门禁保留：既有 bounded id 仍存在，回归护栏不被目标级条件取代。
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "build:" + Project, StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "test:" + Project, StringComparison.Ordinal)));
        Assert.AreEqual(4, criteria.Count);
        Assert.AreEqual(4, checks.Count);

        // 两组要求文案可区分。
        Assert.IsTrue(criteria.First(c => c.Id.StartsWith("objective-", StringComparison.Ordinal))
            .Requirement.StartsWith("目标声明的证据", StringComparison.Ordinal));
        Assert.IsTrue(criteria.First(c => !c.Id.StartsWith("objective-", StringComparison.Ordinal))
            .Requirement.StartsWith("回归门禁", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DifferentObjectives_ProduceDifferentCriteriaIds()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var store = new GoalAcceptanceContractStore(factory);
        var planner = NewPlanner(factory, Project);

        // 同一门禁配置下，两个不同 objective（声明不同项目）必须得到不同判据集合
        // —— 这是把「合同 generic 化」缺口锁死的核心对照。
        var objectiveA = "目标 A；证据: Source/A.csproj";
        var objectiveB = "目标 B；证据: Source/B.csproj";

        Assert.IsTrue(await planner.EnsureContractAsync("goal-cmp-a", 1, 1, "fp-a", objectiveA));
        Assert.IsTrue(await planner.EnsureContractAsync("goal-cmp-b", 1, 1, "fp-b", objectiveB));

        var contractA = await store.LoadAsync("goal-cmp-a", 1, 1);
        var contractB = await store.LoadAsync("goal-cmp-b", 1, 1);
        Assert.IsNotNull(contractA);
        Assert.IsNotNull(contractB);

        var idsA = GoalVerificationPersistence.ReadCriteria(contractA!.CriteriaJson).Select(c => c.Id).ToList();
        var idsB = GoalVerificationPersistence.ReadCriteria(contractB!.CriteriaJson).Select(c => c.Id).ToList();

        CollectionAssert.AreNotEquivalent(idsA, idsB);
        Assert.IsTrue(idsA.Contains("objective-build:Source/A.csproj"));
        Assert.IsFalse(idsB.Contains("objective-build:Source/A.csproj"));
        Assert.IsTrue(idsB.Contains("objective-build:Source/B.csproj"));
        Assert.IsFalse(idsA.Contains("objective-build:Source/B.csproj"));
    }

    [TestMethod]
    public async Task ObjectiveWithoutEvidenceDeclaration_NoObjectiveCriteria()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project);
        var objective = "把所有失败测试修好（没有任何证据声明）";

        Assert.IsTrue(await planner.EnsureContractAsync("goal-noev", 1, 1, "fp-noev", objective));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-noev", 1, 1);
        Assert.IsNotNull(contract);
        // 未声明证据：行为与纯门禁规划一致，合同级 Source 也保持不变。
        Assert.AreEqual(GoalAcceptanceContractPlanner.Source, contract!.Source);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        Assert.AreEqual(2, criteria.Count);
        Assert.IsFalse(criteria.Any(c => c.Id.StartsWith("objective-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnsafeEvidenceDeclarations_StayOutOfContract()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        // 无门禁配置：不安全的证据声明（..逃逸/绝对路径）绝不能单独撑起合同（fail-closed 保持）。
        var planner = NewPlanner(factory);
        var objective = "证据: ..\\escape.csproj, C:/abs/abs.csproj";

        Assert.IsFalse(await planner.EnsureContractAsync("goal-unsafev", 1, 1, "fp-unsafev", objective));
        Assert.IsNull(await new GoalAcceptanceContractStore(factory).LoadAsync("goal-unsafev", 1, 1));
    }

    [TestMethod]
    public async Task MixedEvidenceDeclarations_OnlySafeTargetsBecomeCriteria()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project);
        var objective = "证据: ../escape.csproj、C:/abs/x.csproj、" + Project;

        Assert.IsTrue(await planner.EnsureContractAsync("goal-mixed", 1, 1, "fp-mixed", objective));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-mixed", 1, 1);
        Assert.IsNotNull(contract);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var objectiveCriteria = criteria.Where(c => c.Id.StartsWith("objective-", StringComparison.Ordinal)).ToList();

        // 只有通过安全校验的声明才生成目标级条件。
        Assert.AreEqual(2, objectiveCriteria.Count);
        Assert.IsTrue(objectiveCriteria.All(c => c.InputRefs.Count == 1 && string.Equals(c.InputRefs[0], Project, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FileEvidenceDeclaration_GeneratesReadOnlyFileEvidenceCriterion()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        // 文件证据（非项目扩展名）派生只读核验的 file-evidence 条件，不产生 build/test。
        var planner = NewPlanner(factory);
        var objective = "输出报告；证据: Docs/report.md";

        Assert.IsTrue(await planner.EnsureContractAsync("goal-filev", 1, 1, "fp-filev", objective));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-filev", 1, 1);
        Assert.IsNotNull(contract);
        Assert.AreEqual(GoalAcceptanceContractPlanner.SourceWithObjectiveEvidence, contract!.Source);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);
        var checks = GoalVerificationPersistence.ReadChecks(contract.ChecksJson);

        Assert.AreEqual(1, criteria.Count);
        Assert.AreEqual(1, checks.Count);

        var criterion = criteria[0];
        Assert.AreEqual($"objective-{GoalVerificationSpecKinds.FileEvidence}:Docs/report.md", criterion.Id);
        Assert.AreEqual(GoalVerificationSpecKinds.FileEvidence, criterion.Kind);
        Assert.AreEqual(GoalCheckDefinitionRegistry.FileEvidenceRef, criterion.DefinitionRef);
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryGetDefinitionHash(criterion.DefinitionRef, out var expectedHash));
        Assert.AreEqual(expectedHash, criterion.DefinitionHash);
        Assert.IsTrue(criterion.Required);
        Assert.AreEqual(1, criterion.InputRefs.Count);
        Assert.AreEqual("Docs/report.md", criterion.InputRefs[0]);

        var check = checks[0];
        Assert.AreEqual($"objective:{GoalVerificationSpecKinds.FileEvidence}:Docs/report.md", check.CheckId);
        Assert.AreEqual(criterion.Id, check.CriterionId);
        Assert.AreEqual(GoalVerificationSpecKinds.FileEvidence, check.Kind);
        Assert.AreEqual(expectedHash, check.DefinitionHash);
        Assert.IsNull(check.ExpectedTestCount);
    }

    [TestMethod]
    public async Task UnsafeFileEvidenceDeclarations_StayOutOfContract()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        // 绝对路径 / ..逃逸 / 盘符 / 通配符 / 尾目录分隔符：全部拒绝，合同保持空（fail-closed）。
        var planner = NewPlanner(factory);
        var objective = "证据: ../escape.md, C:/abs/abs.md, a*b.md, dir/, note?.txt";

        Assert.IsFalse(await planner.EnsureContractAsync("goal-unsafef", 1, 1, "fp-unsafef", objective));
        Assert.IsNull(await new GoalAcceptanceContractStore(factory).LoadAsync("goal-unsafef", 1, 1));
    }

    [TestMethod]
    public async Task MixedProjectAndFileEvidence_ProduceBothCriterionKinds()
    {
        var (connection, factory) = await GoalWritePathHarness.CreateAsync();
        await using var _ = connection;

        var planner = NewPlanner(factory, Project);
        var objective = "证据: Source/A.csproj、Docs/summary.md";

        Assert.IsTrue(await planner.EnsureContractAsync("goal-mixv", 1, 1, "fp-mixv", objective));

        var contract = await new GoalAcceptanceContractStore(factory).LoadAsync("goal-mixv", 1, 1);
        Assert.IsNotNull(contract);

        var criteria = GoalVerificationPersistence.ReadCriteria(contract.CriteriaJson);

        // 项目证据 → objective-build/objective-test；文件证据 → objective-file-evidence；门禁 → build/test。
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "objective-build:Source/A.csproj", StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "objective-test:Source/A.csproj", StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "objective-file-evidence:Docs/summary.md", StringComparison.Ordinal)
            && string.Equals(c.Kind, GoalVerificationSpecKinds.FileEvidence, StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "build:" + Project, StringComparison.Ordinal)));
        Assert.IsTrue(criteria.Any(c => string.Equals(c.Id, "test:" + Project, StringComparison.Ordinal)));
        Assert.AreEqual(5, criteria.Count);
    }
}
