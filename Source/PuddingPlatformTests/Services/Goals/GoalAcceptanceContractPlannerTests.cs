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
}
