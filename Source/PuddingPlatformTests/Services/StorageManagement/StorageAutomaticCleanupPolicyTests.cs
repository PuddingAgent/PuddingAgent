using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 自动清理决策纯函数的回归（2026-10-27，S1 第一部分）。
///
/// 这些断言存在的意义：自动清理一旦接线就会真的删数据，所以在接线之前必须把
/// 「谁该被清、谁必须被跳过、跳过原因是什么」钉死。缺了这些，S2 接线时任何一处
/// 判定写反都会静默删掉证据类或用户数据。
/// </summary>
[TestClass]
public sealed class StorageAutomaticCleanupPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Evaluate_NormalTarget_ProducesCandidateWithCutoffFromRetentionDays()
    {
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("runtime", StorageSafetyLevel.Disposable, retentionDays: 14)],
            Now);

        Assert.AreEqual(1, decisions.Count);
        var decision = decisions[0];
        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.None, decision.Reason);
        Assert.IsTrue(decision.IsCandidate);
        Assert.AreEqual(14, decision.Candidate!.Value.RetentionDays);
        Assert.AreEqual(Now.AddDays(-14), decision.Candidate!.Value.CutoffUtc);
    }

    [TestMethod]
    public void Evaluate_EvidenceSafetyLevel_IsProtected_EvenWhenAutomaticCleanupIsAllowed()
    {
        // 关键回归：受保护必须优先于 AutomaticCleanupAllowed —— 误配也不得清理。
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("conversation", StorageSafetyLevel.Evidence, retentionDays: 7, automaticAllowed: true)],
            Now);

        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.Protected, decisions[0].Reason);
        Assert.IsFalse(decisions[0].IsCandidate);
    }

    [TestMethod]
    public void Evaluate_UserDataSafetyLevel_IsProtected()
    {
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("notes", StorageSafetyLevel.UserData, retentionDays: 7)],
            Now);

        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.Protected, decisions[0].Reason);
    }

    [TestMethod]
    public void Evaluate_AutomaticCleanupDisabled_IsSkipped()
    {
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("manual_only", StorageSafetyLevel.Disposable, retentionDays: 7, automaticAllowed: false)],
            Now);

        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.AutomaticDisabled, decisions[0].Reason);
    }

    [TestMethod]
    public void Evaluate_RequiresRollup_IsSkippedAsDesigned_NotAsCandidate()
    {
        // TelemetryRaw / ContextLayerRaw 的真实形态：允许自动清理但要求聚合先落地。
        // 这一条必须与"缺生产者"区分开 —— 它是有意关闭，不是缺陷。
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("telemetry", StorageSafetyLevel.Disposable, retentionDays: 14, requiresRollup: true)],
            Now);

        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.RequiresRollup, decisions[0].Reason);
    }

    [TestMethod]
    public void Evaluate_WithoutRetentionDays_IsSkippedInsteadOfGuessing()
    {
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("no_retention", StorageSafetyLevel.Disposable, retentionDays: null)],
            Now);

        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.NoRetentionConfigured, decisions[0].Reason);
    }

    [TestMethod]
    public void Evaluate_ClampsRetentionIntoDeclaredRange()
    {
        var below = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("low", StorageSafetyLevel.Disposable, retentionDays: 0, min: 3, max: 30)],
            Now);
        Assert.AreEqual(StorageAutomaticCleanupPolicy.SkipReason.NoRetentionConfigured, below[0].Reason,
            "0 天不是合法保留期，应跳过而不是夹成最小值");

        var clampedUp = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("low2", StorageSafetyLevel.Disposable, retentionDays: 1, min: 3, max: 30)],
            Now);
        Assert.AreEqual(3, clampedUp[0].Candidate!.Value.RetentionDays, "低于下限应夹到下限");

        var clampedDown = StorageAutomaticCleanupPolicy.Evaluate(
            [Definition("high", StorageSafetyLevel.Disposable, retentionDays: 3650, min: 3, max: 30)],
            Now);
        Assert.AreEqual(30, clampedDown[0].Candidate!.Value.RetentionDays, "高于上限应夹到上限");
    }

    [TestMethod]
    public void Evaluate_PreservesCatalogOrder_AndSkipsNulls()
    {
        var decisions = StorageAutomaticCleanupPolicy.Evaluate(
            [
                Definition("a", StorageSafetyLevel.Disposable, retentionDays: 7),
                null!,
                Definition("b", StorageSafetyLevel.Evidence, retentionDays: 7),
            ],
            Now);

        Assert.AreEqual(2, decisions.Count);
        Assert.AreEqual("a", decisions[0].TargetId);
        Assert.AreEqual("b", decisions[1].TargetId);
    }

    private static StorageDataClassCatalog.StorageDataClassDefinition Definition(
        string targetId,
        StorageSafetyLevel safetyLevel,
        int? retentionDays,
        bool automaticAllowed = true,
        bool requiresRollup = false,
        int? min = 1,
        int? max = 365) => new()
        {
            TargetId = targetId,
            DisplayName = targetId,
            Description = targetId,
            SafetyLevel = safetyLevel,
            DefaultRetentionDays = retentionDays,
            MinRetentionDays = min,
            MaxRetentionDays = max,
            AutomaticCleanupAllowed = automaticAllowed,
            RequiresRollupBeforeAutomatic = requiresRollup,
        };
}
