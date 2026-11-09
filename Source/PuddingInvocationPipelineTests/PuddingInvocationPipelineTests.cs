using System.Collections.Immutable;
using PuddingInvocationPipeline;
using PuddingInvocationPipeline.Contracts;

namespace PuddingInvocationPipelineTests;

public sealed class FakeExtensionSource(ExtensionSnapshot? snapshot = null, Exception? failure = null)
    : IExtensionSource
{
    public ExtensionSnapshot? Snapshot { get; set; } = snapshot;
    public Exception? Failure { get; set; } = failure;

    public ValueTask<ExtensionSnapshot> LoadAsync(string point, CancellationToken ct = default)
        => Failure is not null
            ? throw Failure
            : ValueTask.FromResult(Snapshot!);
}

internal static class ExtensionFixtures
{
    public const string Point = ExtensionPoints.ToolResult;

    public static ExtensionDescriptor Extension(string id, ExtensionKind kind, int priority = 0,
        string pluginId = "plugin-a", long generation = 3, string point = Point,
        params string[] grants) => new(id, pluginId, generation, point, kind, priority, "1.0", [.. grants]);

    public static ExtensionSnapshot Snapshot(params ExtensionDescriptor[] descriptors) => new([.. descriptors], 3);

    public static ExtensionContext Context(params string[] granted) => new("ws-1", "agent-1", "session-1", Point,
        "call-1", "files.write", [.. granted]);
}

[TestClass]
public sealed class ExtensionRegistryTests
{
    private static readonly ExtensionRegistry Registry = new();

    [TestMethod]
    public async Task Load_FiltersToTheRequestedPoint_AndReturnsImmutableSnapshot()
    {
        var source = new FakeExtensionSource(ExtensionFixtures.Snapshot(
            ExtensionFixtures.Extension("ext-a", ExtensionKind.Observer),
            ExtensionFixtures.Extension("ext-b", ExtensionKind.Guard, point: ExtensionPoints.ToolBefore)));
        var snapshot = await Registry.LoadAsync(source, ExtensionFixtures.Point);
        Assert.AreEqual(1, snapshot.Extensions.Length);
        Assert.AreEqual("ext-a", snapshot.Extensions[0].ExtensionId);
        Assert.AreEqual(3L, snapshot.Generation);
    }

    [TestMethod]
    public async Task Load_RejectsDuplicatesInvalidDescriptorsAndStaleGeneration()
    {
        var duplicate = new FakeExtensionSource(ExtensionFixtures.Snapshot(
            ExtensionFixtures.Extension("ext-a", ExtensionKind.Observer),
            ExtensionFixtures.Extension("ext-a", ExtensionKind.Advice, pluginId: "plugin-b")));
        var dup = await Assert.ThrowsExactlyAsync<ExtensionException>(async () =>
            await Registry.LoadAsync(duplicate, ExtensionFixtures.Point));
        Assert.AreEqual(ExtensionErrorCodes.Duplicate, dup.Code);

        var invalid = new FakeExtensionSource(ExtensionFixtures.Snapshot(
            ExtensionFixtures.Extension("ext-a", ExtensionKind.Observer, priority: 2000)));
        var bad = await Assert.ThrowsExactlyAsync<ExtensionException>(async () =>
            await Registry.LoadAsync(invalid, ExtensionFixtures.Point));
        Assert.AreEqual(ExtensionErrorCodes.DescriptorInvalid, bad.Code);

        var stale = await Assert.ThrowsExactlyAsync<ExtensionException>(async () =>
            await Registry.LoadAsync(new FakeExtensionSource(ExtensionFixtures.Snapshot()), ExtensionFixtures.Point,
                observedGeneration: 2));
        Assert.AreEqual(ExtensionErrorCodes.GenerationStale, stale.Code);
    }

    [TestMethod]
    public async Task Load_UnknownPointAndUnavailableSourceAreTyped()
    {
        var unknown = await Assert.ThrowsExactlyAsync<ExtensionException>(async () =>
            await Registry.LoadAsync(new FakeExtensionSource(ExtensionFixtures.Snapshot()), "tool.unknown"));
        Assert.AreEqual(ExtensionErrorCodes.PointUnknown, unknown.Code);

        var unavailable = await Assert.ThrowsExactlyAsync<ExtensionException>(async () =>
            await Registry.LoadAsync(new FakeExtensionSource(failure: new InvalidOperationException("down")),
                ExtensionFixtures.Point));
        Assert.AreEqual(ExtensionErrorCodes.SourceUnavailable, unavailable.Code);
    }
}

[TestClass]
public sealed class ExtensionPipelineTests
{
    private static readonly ExtensionPipeline Pipeline = new();

    [TestMethod]
    public void Plan_OrdersGuardsThenTransformsThenAdviceThenObservers()
    {
        var snapshot = ExtensionFixtures.Snapshot(
            ExtensionFixtures.Extension("observer-1", ExtensionKind.Observer, priority: 100),
            ExtensionFixtures.Extension("advice-1", ExtensionKind.Advice, priority: 100),
            ExtensionFixtures.Extension("transform-1", ExtensionKind.Transform, priority: 0),
            ExtensionFixtures.Extension("guard-1", ExtensionKind.Guard, priority: -10),
            ExtensionFixtures.Extension("guard-2", ExtensionKind.Guard, priority: 10),
            ExtensionFixtures.Extension("advice-2", ExtensionKind.Advice, priority: 0));
        var plan = Pipeline.Plan(ExtensionFixtures.Context(), snapshot, observedGeneration: 3);
        CollectionAssert.AreEqual(
            new[] { "guard-2", "guard-1", "transform-1", "advice-1", "advice-2", "observer-1" },
            plan.Ordered.Select(d => d.ExtensionId).ToArray());
        Assert.AreEqual(0, plan.Skipped.Length);
    }

    [TestMethod]
    public void Plan_SkipsUngrantedExtensions_WithoutSilentRewrite()
    {
        var snapshot = ExtensionFixtures.Snapshot(
            ExtensionFixtures.Extension("ext-open", ExtensionKind.Observer),
            ExtensionFixtures.Extension("ext-write", ExtensionKind.Transform, grants: "files.write"),
            ExtensionFixtures.Extension("ext-two", ExtensionKind.Advice, grants: ["files.write", "audit.write"]));
        var plan = Pipeline.Plan(ExtensionFixtures.Context("files.write"), snapshot, observedGeneration: 3);
        CollectionAssert.AreEqual(new[] { "ext-write", "ext-open" }, plan.Ordered.Select(d => d.ExtensionId).ToArray());
        CollectionAssert.AreEqual(new[] { "ext-two" }, plan.Skipped.Select(d => d.ExtensionId).ToArray());
    }

    [TestMethod]
    public void Plan_RejectsAStaleGeneration()
    {
        var failure = Assert.ThrowsExactly<ExtensionException>(() =>
            Pipeline.Plan(ExtensionFixtures.Context(), ExtensionFixtures.Snapshot(), observedGeneration: 2));
        Assert.AreEqual(ExtensionErrorCodes.GenerationStale, failure.Code);
        Assert.ThrowsExactly<ExtensionException>(() =>
            ExtensionRegistry.EnsureGeneration(4, 5));
    }

    [TestMethod]
    public void ValidateTransform_RejectsProtectedFieldChanges()
    {
        var transform = ExtensionFixtures.Extension("transform-1", ExtensionKind.Transform);
        var before = new Dictionary<string, string>
        {
            ["toolCallId"] = "call-1",
            ["authorizationDecision"] = "allowed",
            ["exitCode"] = "1",
            ["budgetOwner"] = "goal-1",
            ["failureCode"] = "check.failed",
            ["text"] = "old",
        };
        foreach (var field in ProtectedToolFields.All)
        {
            var after = new Dictionary<string, string>(before) { [field] = "tampered" };
            var failure = Assert.ThrowsExactly<ExtensionException>(() =>
                ExtensionPipeline.ValidateTransform(transform, before, after, wasFailure: true, nowSuccess: false));
            Assert.AreEqual(ExtensionErrorCodes.TransformForbidden, failure.Code, field);
        }
    }

    [TestMethod]
    public void ValidateTransform_CannotTurnFailureIntoSuccess_AndOnlyTransformsMayRun()
    {
        var transform = ExtensionFixtures.Extension("transform-1", ExtensionKind.Transform);
        var before = new Dictionary<string, string> { ["text"] = "failed", ["exitCode"] = "1" };
        var after = new Dictionary<string, string> { ["text"] = "failed (retried)", ["exitCode"] = "1" };
        ExtensionPipeline.ValidateTransform(transform, before, after, wasFailure: true, nowSuccess: false);

        var flip = Assert.ThrowsExactly<ExtensionException>(() =>
            ExtensionPipeline.ValidateTransform(transform, before, after, wasFailure: true, nowSuccess: true));
        Assert.AreEqual(ExtensionErrorCodes.TransformForbidden, flip.Code);

        var observer = ExtensionFixtures.Extension("observer-1", ExtensionKind.Observer);
        var notTransform = Assert.ThrowsExactly<ExtensionException>(() =>
            ExtensionPipeline.ValidateTransform(observer, before, after, wasFailure: false, nowSuccess: false));
        Assert.AreEqual(ExtensionErrorCodes.TransformDenied, notTransform.Code);
    }

    [TestMethod]
    public void ValidateTransform_AllowsNonProtectedFieldChanges()
    {
        var transform = ExtensionFixtures.Extension("transform-1", ExtensionKind.Transform);
        var before = new Dictionary<string, string> { ["text"] = "old", ["exitCode"] = "0" };
        var after = new Dictionary<string, string> { ["text"] = "new", ["exitCode"] = "0" };
        ExtensionPipeline.ValidateTransform(transform, before, after, wasFailure: false, nowSuccess: false);
        Assert.IsTrue(true);
    }
}
