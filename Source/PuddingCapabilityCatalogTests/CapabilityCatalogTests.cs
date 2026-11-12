using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingCapabilityCatalog;
using PuddingCapabilityCatalog.Contracts;

namespace PuddingCapabilityCatalogTests;

public sealed class FakeCapabilitySource : ICapabilitySource
{
    public int Calls;
    public string? WorkspaceId;
    public string? Kind;
    public string? Cursor;
    public int MaxCount;
    public CapabilityPage? Page;
    public Exception? Failure;

    public ValueTask<CapabilityPage> ListAsync(string workspaceId, string kind, string? cursor, int maxCount,
        CancellationToken ct = default)
    {
        Calls++;
        WorkspaceId = workspaceId;
        Kind = kind;
        Cursor = cursor;
        MaxCount = maxCount;
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Page ?? new CapabilityPage([], null, true, false, "gen-1", null));
    }
}

internal static class CapabilityFixtures
{
    public static CapabilityRecord Tool(string id, bool requiresApproval = false) => new(id, "tool", $"Tool {id}",
        "rev-1", "gen-1", "available", null, requiresApproval, ["read"]);

    public static CapabilityPage Page(params CapabilityRecord[] records) => new([.. records], null, true, false,
        "gen-1", null);
}

[TestClass]
public sealed class CapabilityCatalogTests
{
    private static CapabilityCatalogService Service(FakeCapabilitySource source) => new(source, "ws-1");

    [TestMethod]
    public async Task List_PreservesRevisionGenerationAvailabilityAndOrder()
    {
        var source = new FakeCapabilitySource
        {
            Page = CapabilityFixtures.Page(CapabilityFixtures.Tool("tool-a"), CapabilityFixtures.Tool("tool-b")),
        };
        var page = await Service(source).ListAsync(new("tool"));
        Assert.AreEqual(2, page.Capabilities.Length);
        Assert.AreEqual("gen-1", page.Generation);
        Assert.AreEqual("tool-a", page.Capabilities[0].CapabilityId);
        Assert.AreEqual("rev-1", page.Capabilities[0].Revision);
        Assert.AreEqual("available", page.Capabilities[0].Availability);
        Assert.AreEqual(1, source.Calls);
        Assert.AreEqual("tool", source.Kind);
        Assert.AreEqual(50, source.MaxCount);
        Assert.IsNull(source.Cursor);
    }

    [TestMethod]
    public async Task List_ForbiddenSourcePage_IsNotAnEmptySuccess()
    {
        var source = new FakeCapabilitySource
        {
            Page = new CapabilityPage([], null, Complete: true, Partial: true, "gen-1", CapabilityErrorCodes.Forbidden),
        };
        var page = await Service(source).ListAsync(new("mcp"));
        Assert.AreEqual(0, page.Capabilities.Length);
        Assert.IsTrue(page.Partial, "A forbidden page must not look like a complete empty catalog.");
        Assert.AreEqual(CapabilityErrorCodes.Forbidden, page.MissingReason);
        Assert.AreEqual(1, source.Calls);
    }

    [TestMethod]
    public async Task List_PartialTruncatedPage_KeepsCursorAndCompleteFalse()
    {
        var source = new FakeCapabilitySource
        {
            Page = new CapabilityPage([CapabilityFixtures.Tool("tool-a")], "cursor-2", Complete: false, Partial: true,
                "gen-1", "capability.truncated"),
        };
        var page = await Service(source).ListAsync(new("tool", Cursor: "cursor-1", MaxCount: 1));
        Assert.IsFalse(page.Complete);
        Assert.AreEqual("cursor-2", page.Cursor);
        Assert.IsTrue(page.Partial);
        Assert.AreEqual("cursor-1", source.Cursor);
        Assert.AreEqual(1, source.MaxCount);
    }

    [TestMethod]
    public async Task List_LegitEmptyPage_IsCompleteNotEmptyAsMissing()
    {
        var source = new FakeCapabilitySource { Page = new CapabilityPage([], null, true, false, "gen-1", null) };
        var page = await Service(source).ListAsync(new("context"));
        Assert.AreEqual(0, page.Capabilities.Length);
        Assert.IsTrue(page.Complete);
        Assert.IsFalse(page.Partial);
        Assert.IsNull(page.MissingReason);
    }

    [TestMethod]
    public async Task List_UnknownOrCaseChangedKind_IsRejectedBeforeTheSource()
    {
        var source = new FakeCapabilitySource();
        foreach (var kind in new[] { "Tool", "plugin", "", " " })
        {
            var failure = await Assert.ThrowsExactlyAsync<CapabilityException>(() =>
                Service(source).ListAsync(new(kind)).AsTask());
            Assert.AreEqual(CapabilityErrorCodes.KindUnknown, failure.Code, kind);
        }
        Assert.AreEqual(0, source.Calls, "Kind validation must happen before the source is queried.");
    }

    [TestMethod]
    public async Task List_InvalidBounds_AreRejectedBeforeTheSource()
    {
        var source = new FakeCapabilitySource();
        var sizes = new[] { 0, 101 };
        foreach (var size in sizes)
        {
            var failure = await Assert.ThrowsExactlyAsync<CapabilityException>(() =>
                Service(source).ListAsync(new("tool", MaxCount: size)).AsTask());
            Assert.AreEqual(CapabilityErrorCodes.RequestInvalid, failure.Code);
        }
        var cursor = await Assert.ThrowsExactlyAsync<CapabilityException>(() =>
            Service(source).ListAsync(new("tool", Cursor: new string('c', 129))).AsTask());
        Assert.AreEqual(CapabilityErrorCodes.RequestInvalid, cursor.Code);
        var workspace = Assert.ThrowsExactly<CapabilityException>(() => new CapabilityCatalogService(source, ""));
        Assert.AreEqual(CapabilityErrorCodes.RequestInvalid, workspace.Code);
        Assert.AreEqual(0, source.Calls);
    }

    [TestMethod]
    public async Task List_CatalogEntryIsNotAnExecutableAuthorization()
    {
        var source = new FakeCapabilitySource
        {
            Page = CapabilityFixtures.Page(CapabilityFixtures.Tool("tool-a", requiresApproval: true)),
        };
        var page = await Service(source).ListAsync(new("tool"));
        Assert.IsTrue(page.Capabilities[0].RequiresApproval,
            "A catalog entry still requires approval; listing is not execution.");
        CollectionAssert.AreEqual(new[] { "read" }, page.Capabilities[0].RequiredCapabilities.ToArray());
    }

    [TestMethod]
    public async Task List_SourceFailure_IsTypedAndNotFakedAsEmpty()
    {
        var source = new FakeCapabilitySource { Failure = new InvalidOperationException("backend down") };
        var failure = await Assert.ThrowsExactlyAsync<CapabilityException>(() =>
            Service(source).ListAsync(new("tool")).AsTask());
        Assert.AreEqual(CapabilityErrorCodes.SourceUnavailable, failure.Code);
    }

    [TestMethod]
    public async Task List_MissingGeneration_IsSourceUnavailable()
    {
        var source = new FakeCapabilitySource { Page = new CapabilityPage([], null, true, false, "", null) };
        var failure = await Assert.ThrowsExactlyAsync<CapabilityException>(() =>
            Service(source).ListAsync(new("tool")).AsTask());
        Assert.AreEqual(CapabilityErrorCodes.SourceUnavailable, failure.Code);
    }

    [TestMethod]
    public async Task List_ForwardCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new FakeCapabilitySource
        {
            Failure = new OperationCanceledException(cts.Token),
        };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            Service(source).ListAsync(new("tool"), cts.Token).AsTask());
    }
}
