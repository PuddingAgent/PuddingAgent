using PuddingCode.Configuration;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06 06A manifest policy mapping: the instance layer maps to/from the frozen contracts, CAS patches round-trip
/// (set/clear/inherit), a stale revision conflicts before writing, and duplicate purposes are rejected.
/// </summary>
[TestClass]
public sealed class AgentModelPolicyMapperTests
{
    [TestMethod]
    public void ManifestPolicy_MapsInstanceLayerOnly()
    {
        var policy = new AgentModelPolicy(3, 7,
        [
            new("Review", "model-review"),
            new("Classification", null, InheritPrimary: true),
        ]);
        var snapshot = AgentModelPolicyMapper.ToSnapshot(policy);
        Assert.AreEqual(3L, snapshot.InstanceRevision);
        Assert.AreEqual(7L, snapshot.PolicyGeneration);
        var review = snapshot.Purposes.Single(p => p.Purpose == "Review").Instance!;
        Assert.AreEqual("model-review", review.ModelId);
        Assert.AreEqual(PolicySourceKind.Instance, review.Source);
        var classification = snapshot.Purposes.Single(p => p.Purpose == "Classification").Instance!;
        Assert.IsTrue(classification.InheritPrimary);
        Assert.IsNull(classification.ModelId);
        Assert.IsNull(snapshot.Purposes.Single(p => p.Purpose == "Review").Workspace);
    }

    [TestMethod]
    public void CasPatches_RoundTripSetClearAndInherit()
    {
        var policy = new AgentModelPolicy();
        var set = AgentModelPolicyMapper.Apply(policy, Command(0, new ModelPolicyPatch("Review", "model-review", false)),
            out var setResult);
        Assert.AreEqual(1L, setResult.PersistedRevision);
        Assert.AreEqual(1L, set.Revision);
        Assert.AreEqual("model-review", set.Purposes!.Single().ModelId);

        var cleared = AgentModelPolicyMapper.Apply(set, Command(1, new ModelPolicyPatch("Review", null, Clear: true)), out var clearResult);
        Assert.AreEqual(2L, clearResult.PersistedRevision);
        Assert.AreEqual(0, cleared.Purposes!.Count);

        var inherited = AgentModelPolicyMapper.Apply(cleared,
            Command(2, new ModelPolicyPatch("Review", null, Clear: false, InheritPrimary: true)), out _);
        Assert.IsTrue(inherited.Purposes!.Single().InheritPrimary);
        Assert.IsNull(inherited.Purposes!.Single().ModelId);
    }

    [TestMethod]
    public void StaleRevision_ConflictsBeforeWrite()
    {
        var policy = new AgentModelPolicy(5, 1, [new("Review", "model-review")]);
        var failure = Assert.ThrowsExactly<ModelException>(() =>
            AgentModelPolicyMapper.Apply(policy, Command(4, new ModelPolicyPatch("Review", "model-other", false)), out _));
        Assert.AreEqual(ModelErrorCodes.ConfigRevisionConflict, failure.Code);
    }

    [TestMethod]
    public void DuplicatePurposes_AreRejected()
    {
        var failure = Assert.ThrowsExactly<ModelException>(() =>
            AgentModelPolicyMapper.Apply(new AgentModelPolicy(), Command(0,
                new ModelPolicyPatch("Review", "a", false), new ModelPolicyPatch("Review", "b", false)), out _));
        Assert.AreEqual(ModelErrorCodes.FieldUnknown, failure.Code);
    }

    private static UpdateModelPolicyCommand Command(long expected, params ModelPolicyPatch[] patches)
        => new("op-1", expected, [.. patches], ApplyPolicy: "next-run");
}
