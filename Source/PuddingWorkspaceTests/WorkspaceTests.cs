using System.Collections.Immutable;
using PuddingWorkspace;
using PuddingWorkspace.Contracts;

namespace PuddingWorkspaceTests;

[TestClass]
public sealed class WorkspacePathPolicyTests
{
    [TestMethod]
    public void Normalize_RejectsTraversalRootedAndUnsafePaths()
    {
        foreach (var path in new[]
                 {
                     "../escape.txt", "a/../b.txt", "/abs.txt", "C:/data.txt", "a\\b.txt", "a//b.txt",
                     ".", "..", "con.txt", "a. ", "a\u0001b.txt", "a/b/../../c.txt",
                 })
        {
            var failure = Assert.ThrowsExactly<WorkspaceException>(() => WorkspaceFilePolicy.Normalize(path));
            Assert.AreEqual(WorkspaceErrorCodes.PathRejected, failure.Code, path);
        }
    }

    [TestMethod]
    public void Normalize_AcceptsCanonicalNestedChinesePaths()
    {
        Assert.AreEqual("报告/2026/结果.md", WorkspaceFilePolicy.Normalize("报告/2026/结果.md"));
        Assert.AreEqual("a/b.txt", WorkspaceFilePolicy.Normalize("a/b.txt"));
    }

    [TestMethod]
    public void WorkspaceId_RejectsSeparatorsAndTraversal()
    {
        foreach (var id in new[] { "", " ", "ws/1", "ws:1", "..", "a\\b" })
            Assert.AreEqual(WorkspaceErrorCodes.WorkspaceForbidden,
                Assert.ThrowsExactly<WorkspaceException>(() => WorkspaceFilePolicy.EnsureWorkspaceId(id)).Code);
    }
}

[TestClass]
public sealed class WorkspaceCatalogTests
{
    private static WorkspaceCatalog Catalog(FakeWorkspaceSource source, FakeFileStore files) => new(source, files);

    [TestMethod]
    public async Task List_ValidatesBoundsBeforeSource_AndPreservesCursorCompleteness()
    {
        var source = new FakeWorkspaceSource
        {
            Page = new WorkspacePage([WorkspaceFixtures.Entry()], "cursor-2", false),
        };
        var catalog = Catalog(source, new FakeFileStore());
        var page = await catalog.ListAsync(null, 10);
        Assert.AreEqual("cursor-2", page.Cursor);
        Assert.IsFalse(page.Complete);

        Assert.AreEqual(WorkspaceErrorCodes.RequestInvalid,
            (await Assert.ThrowsExactlyAsync<WorkspaceException>(async () => await catalog.ListAsync(null, 0))).Code);
        Assert.AreEqual(WorkspaceErrorCodes.RequestInvalid,
            (await Assert.ThrowsExactlyAsync<WorkspaceException>(async () => await catalog.ListAsync(new string('c', 129), 10))).Code);
        Assert.AreEqual(1, source.Calls, "Invalid bounds must not query the source.");
    }

    [TestMethod]
    public async Task List_SourceFailure_IsTypedUnavailable()
    {
        var source = new FakeWorkspaceSource { Failure = new InvalidOperationException("down") };
        var failure = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(source, new FakeFileStore()).ListAsync(null, 10));
        Assert.AreEqual(WorkspaceErrorCodes.SourceUnavailable, failure.Code);
    }

    [TestMethod]
    public async Task Environment_ReturnsLogicalSnapshotOnly_AndMissingIsTyped()
    {
        var source = new FakeWorkspaceSource { Environment = WorkspaceFixtures.Environment() };
        var snapshot = await Catalog(source, new FakeFileStore()).GetEnvironmentAsync("ws-1");
        Assert.AreEqual("workspace:default", snapshot.LogicalRoot);
        Assert.AreEqual("windows", snapshot.OsFamily);
        Assert.AreEqual("policy-v1", snapshot.PolicyVersion);
        Assert.AreEqual(2, snapshot.Capabilities.Length);
        // The contract type has no environment-variable/secret surface at all.
        Assert.IsFalse(snapshot.GetType().GetProperties().Any(p => p.Name.Contains("Env", StringComparison.Ordinal)));

        source.Environment = null;
        var missing = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(source, new FakeFileStore()).GetEnvironmentAsync("ws-1"));
        Assert.AreEqual(WorkspaceErrorCodes.NotFound, missing.Code);
    }

    [TestMethod]
    public async Task Read_RejectsLinksBeforeIo_AndMissingIsTyped()
    {
        var files = new FakeFileStore { Link = true };
        var linked = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), files).ReadFileAsync("ws-1", "a/b.txt"));
        Assert.AreEqual(WorkspaceErrorCodes.LinkRejected, linked.Code);

        var missing = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), new FakeFileStore()).ReadFileAsync("ws-1", "a/b.txt"));
        Assert.AreEqual(WorkspaceErrorCodes.NotFound, missing.Code);
    }

    [TestMethod]
    public async Task Read_ReturnsContent_AndTraversalNeverReachesTheStore()
    {
        var files = new FakeFileStore
        {
            Content = new WorkspaceFileContent("ws-1", "a/b.txt", 4, "hash", WorkspaceFixtures.Bytes("hello")),
        };
        var content = await Catalog(new FakeWorkspaceSource(), files).ReadFileAsync("ws-1", "a/b.txt");
        Assert.AreEqual(4L, content.Revision);
        Assert.AreEqual("hash", content.ContentHash);

        await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), files).ReadFileAsync("ws-1", "../escape.txt"));
    }

    [TestMethod]
    public async Task Write_ChecksPathHashAndAtomicity()
    {
        var files = new FakeFileStore();
        var catalog = Catalog(new FakeWorkspaceSource(), files);
        var content = WorkspaceFixtures.Bytes("内容");
        var receipt = await catalog.WriteFileAsync("ws-1", "报告/结果.md", "hash-old", content);
        Assert.IsTrue(receipt.Atomic);
        Assert.AreEqual("报告/结果.md", files.WrittenPath);
        Assert.AreEqual("hash-old", files.ExpectedHash);

        Assert.AreEqual(WorkspaceErrorCodes.RequestInvalid,
            (await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
                await catalog.WriteFileAsync("ws-1", "报告/结果.md", "", content))).Code);
        Assert.AreEqual(WorkspaceErrorCodes.RequestInvalid,
            (await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
                await catalog.WriteFileAsync("ws-1", "报告/结果.md", "hash", []))).Code);
    }

    [TestMethod]
    public async Task Write_NonAtomicAdapterOrLink_IsRejected()
    {
        var nonAtomic = new FakeFileStore { Atomic = false };
        var unsupported = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), nonAtomic)
                .WriteFileAsync("ws-1", "a.txt", "hash-old", WorkspaceFixtures.Bytes("x")));
        Assert.AreEqual(WorkspaceErrorCodes.Unsupported, unsupported.Code);

        var linked = new FakeFileStore { Link = true };
        var rejected = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), linked)
                .WriteFileAsync("ws-1", "a.txt", "hash-old", WorkspaceFixtures.Bytes("x")));
        Assert.AreEqual(WorkspaceErrorCodes.LinkRejected, rejected.Code);
    }

    [TestMethod]
    public async Task Write_CasMismatchFromAdapter_IsNotSwallowed()
    {
        var files = new FakeFileStore { Failure = new WorkspaceException(WorkspaceErrorCodes.HashMismatch, "stale") };
        var failure = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(new FakeWorkspaceSource(), files)
                .WriteFileAsync("ws-1", "a.txt", "hash-old", WorkspaceFixtures.Bytes("x")));
        Assert.AreEqual(WorkspaceErrorCodes.HashMismatch, failure.Code);
    }

    [TestMethod]
    public async Task ForbiddenWorkspaceId_IsRejectedBeforeTheSource()
    {
        var source = new FakeWorkspaceSource();
        var failure = await Assert.ThrowsExactlyAsync<WorkspaceException>(async () =>
            await Catalog(source, new FakeFileStore()).GetEnvironmentAsync("../ws"));
        Assert.AreEqual(WorkspaceErrorCodes.WorkspaceForbidden, failure.Code);
        Assert.AreEqual(0, source.Calls);
    }
}
