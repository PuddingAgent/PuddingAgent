using System.Text;
using PuddingContextHandoff;

namespace PuddingContextHandoffTests;

[TestClass]
public sealed class HandoffStoreTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-handoff-tests", Guid.NewGuid().ToString("N"));
    private static readonly HandoffScope Scope = new("workspace", "session", "agent");
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task RestartAndRevision_KeepBothExactVersions()
    {
        var store = new HandoffStore(_root);
        var first = await store.SaveAsync(Scope, "未完成：迁移。约束：不部署。证据：报告.md", "run1", "call1");
        var second = await store.SaveAsync(Scope, "迁移已验证；下一步待用户决定部署。", "run2", "call2");
        var restarted = new HandoffStore(_root);
        Assert.AreEqual(second, await restarted.ReadAsync(Scope));
        Assert.AreEqual(first, await restarted.ReadAsync(Scope, first.Id));
        Assert.AreEqual("call1", (await restarted.ReadAsync(Scope, first.Id))!.ToolCallId);
    }

    [TestMethod]
    public async Task ScopeIsolation_DoesNotAllowCrossSessionWorkspaceOrAgentReads()
    {
        var store = new HandoffStore(_root);
        var note = await store.SaveAsync(Scope, "private note");
        foreach (var other in new[] { Scope with { SessionId = "other" }, Scope with { WorkspaceId = "other" }, Scope with { AgentId = "other" } })
        {
            Assert.IsNull(await store.ReadAsync(other));
            Assert.IsNull(await store.ReadAsync(other, note.Id));
        }
    }

    [TestMethod]
    public async Task RejectedOrCancelledWrite_PreservesPreviousCompleteNote()
    {
        var store = new HandoffStore(_root);
        var previous = await store.SaveAsync(Scope, "keep me");
        foreach (var text in new[] { " ", new string('中', 2001), new string('a', 6001) })
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Scope, text));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Scope, "new", ct: new CancellationToken(true)));
        Assert.AreEqual(previous, await store.ReadAsync(Scope));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ByteBudget_AllowsExactUtf8LimitWithoutTruncation()
    {
        var text = new string('中', 2000);
        Assert.AreEqual(HandoffStore.MaxSummaryBytes, Encoding.UTF8.GetByteCount(text));
        var store = new HandoffStore(_root);
        await store.SaveAsync(Scope, text);
        Assert.AreEqual(text, (await store.ReadAsync(Scope))!.Summary);
    }

    [TestMethod]
    public async Task InvalidIdsAndCorruptPointer_AreRejected()
    {
        var store = new HandoffStore(_root);
        await store.SaveAsync(Scope, "note");
        foreach (var id in new[] { "../latest", "", new string('x', 32) })
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(Scope, id));
        var pointer = Directory.GetFiles(_root, "latest", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(pointer, new string('x', 33));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Scope));
    }

    [TestMethod]
    public async Task ParallelSaves_PublishOnlyCompleteRecords()
    {
        var store = new HandoffStore(_root);
        var notes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => store.SaveAsync(Scope, "note " + i)));
        Assert.Contains((await store.ReadAsync(Scope))!.Id, notes.Select(n => n.Id));
        foreach (var note in notes) Assert.AreEqual(note, await store.ReadAsync(Scope, note.Id));
    }

    [TestMethod]
    public async Task CorruptOrMisScopedRecord_FailsClosed()
    {
        var store = new HandoffStore(_root);
        var note = await store.SaveAsync(Scope, "note");
        var path = Directory.GetFiles(_root, note.Id + ".json", SearchOption.AllDirectories).Single();
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("workspace", "different"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Scope));
        await File.WriteAllTextAsync(path, new string('x', 49 * 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Scope));
    }

    [TestMethod]
    public async Task HostScopeIsHashed_NotUsedAsAPath()
    {
        var scope = new HandoffScope("../../outside", "C:\\untrusted", "../agent");
        var store = new HandoffStore(_root);
        var note = await store.SaveAsync(scope, "note");
        Assert.AreEqual(note, await store.ReadAsync(scope));
        Assert.AreEqual(64, Path.GetFileName(Directory.GetDirectories(_root).Single()).Length);
    }
}
