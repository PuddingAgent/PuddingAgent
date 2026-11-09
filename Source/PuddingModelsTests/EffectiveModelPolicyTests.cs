using Pudding.Api.Contracts;
using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

[TestClass]
public sealed class EffectiveModelPolicyTests
{
    [TestMethod]
    public async Task Resolve_PrefersInstanceThenWorkspaceThenSystem_WithSourceAndRevision()
    {
        var store = new FakeModelPolicyStore();
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.System, "system-model", 1);
        store.Seed("ws-1", "agent-1", "Classification", PolicySourceKind.Workspace, "workspace-model", 2);
        store.Seed("ws-1", "agent-1", "Review", PolicySourceKind.Instance, "review-model", 3);
        var resolver = new ModelPolicyResolver(store);

        var effective = await resolver.ResolveAsync(ModelFixtures.Scope());
        Assert.AreEqual("system", Entry(effective, "Primary").Source);
        Assert.AreEqual(1L, Entry(effective, "Primary").SourceRevision);
        Assert.AreEqual("workspace", Entry(effective, "Classification").Source);
        Assert.AreEqual("instance", Entry(effective, "Review").Source);
        Assert.AreEqual("review-model", Entry(effective, "Review").ModelId);
    }

    [TestMethod]
    public async Task Resolve_MissingPurposeStaysMissing_AndInheritPrimaryOnlyWhenExplicit()
    {
        var store = new FakeModelPolicyStore();
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.Instance, "primary-model", 1);
        var resolver = new ModelPolicyResolver(store);
        var effective = await resolver.ResolveAsync(ModelFixtures.Scope());
        Assert.IsNull(Entry(effective, "Primary").MissingReason);
        Assert.AreEqual(ModelErrorCodes.PolicyMissing, await MissingAsync(resolver, "Review"));

        await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, ModelFixtures.Inherit("Review")));
        var inherited = await resolver.ResolveAsync(ModelFixtures.Scope());
        Assert.AreEqual("primary-model", Entry(inherited, "Review").ModelId);
        Assert.AreEqual("inherited-primary", Entry(inherited, "Review").Source);
    }

    [TestMethod]
    public async Task Resolve_InstanceWinsWhenEveryLayerDeclaresTheSamePurpose()
    {
        var store = new FakeModelPolicyStore();
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.System, "system-model", 1);
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.Workspace, "workspace-model", 2);
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.Instance, "instance-model", 3);
        var resolver = new ModelPolicyResolver(store);
        var entry = Entry(await resolver.ResolveAsync(ModelFixtures.Scope()), "Primary");
        Assert.AreEqual("instance-model", entry.ModelId);
        Assert.AreEqual("instance", entry.Source);
        Assert.AreEqual(3L, entry.SourceRevision);
    }

    [TestMethod]
    public async Task Write_UsesCas_OnlyTheFirstWriterOfARevisionWins()
    {
        var store = new FakeModelPolicyStore();
        var resolver = new ModelPolicyResolver(store);
        await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, ModelFixtures.Set("Primary", "model-a")));

        var conflict = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, ModelFixtures.Set("Primary", "model-b"))));
        Assert.AreEqual(ModelErrorCodes.ConfigRevisionConflict, conflict.Code);
        Assert.AreEqual(1, store.Conflicts);
        var snapshot = store.SnapshotOf("ws-1", "agent-1");
        Assert.AreEqual(1L, snapshot.InstanceRevision);
        Assert.AreEqual("model-a", snapshot.Purposes.Single(p => p.Purpose == "Primary").Instance!.ModelId,
            "The losing writer must not overwrite the winner.");
    }

    [TestMethod]
    public async Task Write_ReceiptAdvancesPersistedRevision_ButAppliesFromNextRun()
    {
        var store = new FakeModelPolicyStore();
        var resolver = new ModelPolicyResolver(store);
        var receipt = await resolver.UpdateAsync(ModelFixtures.Scope(),
            ModelFixtures.Update(0, ModelFixtures.Set("Primary", "model-a"), ModelFixtures.Set("Review", "model-r")));
        Assert.AreEqual(1L, receipt.PersistedRevision);
        Assert.AreEqual(0L, receipt.AppliedRevision);
        Assert.IsTrue(receipt.AppliesFromNextRun);
        Assert.AreEqual(1L, receipt.PolicyGeneration);
        Assert.AreEqual(1L, (await resolver.ResolveAsync(ModelFixtures.Scope())).EffectiveRevision);
    }

    [TestMethod]
    public async Task Write_ClearRemovesTheInstanceOverride_AndFallsBackToOneSource()
    {
        var store = new FakeModelPolicyStore();
        store.Seed("ws-1", "agent-1", "Classification", PolicySourceKind.Workspace, "workspace-model", 2);
        var resolver = new ModelPolicyResolver(store);
        await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, ModelFixtures.Set("Classification", "instance-model")));
        Assert.AreEqual("instance-model", Entry(await resolver.ResolveAsync(ModelFixtures.Scope()), "Classification").ModelId);

        await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(1, ModelFixtures.Clear("Classification")));
        var cleared = Entry(await resolver.ResolveAsync(ModelFixtures.Scope()), "Classification");
        Assert.AreEqual("workspace-model", cleared.ModelId);
        Assert.AreEqual("workspace", cleared.Source);
    }

    [TestMethod]
    public async Task Write_RejectsUnknownOrMalformedFields()
    {
        var store = new FakeModelPolicyStore();
        var resolver = new ModelPolicyResolver(store);
        var unknownPurpose = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, ModelFixtures.Set("Embedding", "x"))));
        Assert.AreEqual(ModelErrorCodes.FieldUnknown, unknownPurpose.Code);
        var badPolicy = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(),
                new("op-1", 0, [ModelFixtures.Set("Primary", "x")], "arbitrary-json")));
        Assert.AreEqual(ModelErrorCodes.FieldUnknown, badPolicy.Code);
        var clearWithModel = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, new ModelPolicyPatch("Primary", "x", Clear: true))));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, clearWithModel.Code);
        var setWithoutModel = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(), ModelFixtures.Update(0, new ModelPolicyPatch("Primary", null, Clear: false))));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, setWithoutModel.Code);
        var duplicate = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.UpdateAsync(ModelFixtures.Scope(),
                ModelFixtures.Update(0, ModelFixtures.Set("Primary", "a"), ModelFixtures.Set("Primary", "b"))));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, duplicate.Code);
        Assert.AreEqual(0, store.Writes, "No malformed write reaches the store.");
    }

    [TestMethod]
    public async Task Write_ChangesOnlyTheTargetAgent_NotTemplatesOrOtherAgents()
    {
        var store = new FakeModelPolicyStore();
        store.Seed("ws-1", "agent-1", "Primary", PolicySourceKind.System, "system-model", 1);
        store.Seed("ws-1", "agent-2", "Primary", PolicySourceKind.System, "system-model", 1);
        var resolver = new ModelPolicyResolver(store);
        await resolver.UpdateAsync(ModelFixtures.Scope("ws-1", "agent-1"),
            ModelFixtures.Update(0, ModelFixtures.Set("Primary", "agent-1-model")));
        Assert.AreEqual("agent-1-model", Entry(await resolver.ResolveAsync(ModelFixtures.Scope("ws-1", "agent-1")), "Primary").ModelId);
        var other = Entry(await resolver.ResolveAsync(ModelFixtures.Scope("ws-1", "agent-2")), "Primary");
        Assert.AreEqual("system-model", other.ModelId, "An instance write must not leak into another agent.");
        Assert.AreEqual(0L, (await resolver.ResolveAsync(ModelFixtures.Scope("ws-1", "agent-2"))).EffectiveRevision);
    }

    [TestMethod]
    public async Task Scope_InAnotherWorkspace_IsRejectedBeforeTheStore()
    {
        var store = new FakeModelPolicyStore();
        var resolver = new ModelPolicyResolver(store);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await resolver.ResolveAsync(new AgentProfileScope("", "agent-1", "actor-1")));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, failure.Code);
    }

    private static async Task<string> MissingAsync(ModelPolicyResolver resolver, string purpose)
        => (await resolver.ResolveAsync(ModelFixtures.Scope())).Purposes.Single(p => p.Purpose == purpose).MissingReason
           ?? throw new AssertFailedException("Expected a missing reason.");

    private static EffectiveModelEntry Entry(EffectiveAgentModelPolicy policy, string purpose)
        => policy.Purposes.Single(p => p.Purpose == purpose);
}
