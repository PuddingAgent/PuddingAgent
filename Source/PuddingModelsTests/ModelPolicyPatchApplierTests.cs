using System.Collections.Immutable;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

/// <summary>
/// PA-06 06A patch application: set/clear/inherit forms are explicit, duplicate or contradictory patches are rejected,
/// and the instance layer is CAS-applied without touching workspace/system layers.
/// </summary>
[TestClass]
public sealed class ModelPolicyPatchApplierTests
{
    [TestMethod]
    public void SetAndClearAndInheritAreAppliedToTheInstanceLayerOnly()
    {
        var snapshot = Snapshot();

        var setResult = ModelPolicyPatchApplier.Apply(snapshot, Command(2,
            new ModelPolicyPatch("Decision", "model-b", Clear: false)), out var afterSet);
        Assert.IsTrue(setResult.AppliesFromNextRun);
        Assert.AreEqual(3L, setResult.PersistedRevision);
        var setLayer = afterSet.Purposes.Single(p => p.Purpose == "Decision").Instance!;
        Assert.AreEqual("model-b", setLayer.ModelId);
        Assert.IsFalse(setLayer.InheritPrimary);
        Assert.AreEqual(PolicySourceKind.Instance, setLayer.Source);
        Assert.IsNotNull(afterSet.Purposes.Single(p => p.Purpose == "Decision").Workspace, "Workspace layer is untouched.");

        ModelPolicyPatchApplier.Apply(afterSet, Command(3, new ModelPolicyPatch("Decision", null, Clear: true)), out var afterClear);
        Assert.IsNull(afterClear.Purposes.Single(p => p.Purpose == "Decision").Instance);

        ModelPolicyPatchApplier.Apply(snapshot, Command(2, new ModelPolicyPatch("Decision", null, Clear: false, InheritPrimary: true)), out var afterInherit);
        var inheritLayer = afterInherit.Purposes.Single(p => p.Purpose == "Decision").Instance!;
        Assert.IsTrue(inheritLayer.InheritPrimary);
        Assert.IsNull(inheritLayer.ModelId);
    }

    [TestMethod]
    public void ContradictoryOrRepeatedPatchesAreRejected()
    {
        var snapshot = Snapshot();
        var contradict = Assert.ThrowsExactly<ModelException>(() => ModelPolicyPatchApplier.Apply(snapshot,
            Command(2, new ModelPolicyPatch("Decision", "model-b", Clear: true)), out _));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, contradict.Code);

        var repeated = Assert.ThrowsExactly<ModelException>(() => ModelPolicyPatchApplier.Apply(snapshot,
            Command(2, new ModelPolicyPatch("Decision", "a", false), new ModelPolicyPatch("Decision", "b", false)), out _));
        Assert.AreEqual(ModelErrorCodes.FieldUnknown, repeated.Code);

        var empty = Assert.ThrowsExactly<ModelException>(() => ModelPolicyPatchApplier.Apply(snapshot,
            Command(2), out _));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, empty.Code);
    }

    [TestMethod]
    public void RevisionConflictAndUnboundedCommandAreRejected()
    {
        var snapshot = Snapshot();
        var conflict = Assert.ThrowsExactly<ModelException>(() => ModelPolicyPatchApplier.Apply(snapshot,
            Command(9, new ModelPolicyPatch("Decision", "model-b", Clear: false)), out _));
        Assert.AreEqual(ModelErrorCodes.ConfigRevisionConflict, conflict.Code);

        var unbounded = Assert.ThrowsExactly<ModelException>(() => ModelPolicyPatchApplier.Apply(snapshot,
            Command(2, new ModelPolicyPatch("Decision", "model-b", Clear: false)) with { ApplyPolicy = "" }, out _));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, unbounded.Code);
    }

    private static AgentConfigurationSnapshot Snapshot() => new(2, 5, [
        new PurposeModelSetting("Decision",
            new("model-a", false, PolicySourceKind.Instance, 2),
            new("model-w", false, PolicySourceKind.Workspace, 4),
            null),
        new PurposeModelSetting("Review", null, null, new("model-s", false, PolicySourceKind.System, 1)),
    ]);

    private static UpdateModelPolicyCommand Command(long expected, params ModelPolicyPatch[] patches) => new("op-1",
        expected, [.. patches], ApplyPolicy: "next-run");
}
