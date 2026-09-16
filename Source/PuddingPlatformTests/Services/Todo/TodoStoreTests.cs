using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Todo;

namespace PuddingPlatformTests.Services.Todo;

/// <summary>
/// 设计 2026-09-16 §3/§4/§8（TD-1）：TodoStore 服务端强制约束与验收标准 1-4。
/// <para>
/// §8-1 幂等：同内容重复写入不产生重复项；§8-2 CAS：expected_revision 不符明确报错不静默覆盖；
/// §8-3 约束：两个 in_progress 拒绝、blocked 无 reason 拒绝；§8-4 勾选后 read 与 check 同一数据源。
/// 另覆盖：≤20 项、slug 列表内唯一、全量替换移除缺席项、归档。
/// </para>
/// </summary>
[TestClass]
public sealed class TodoStoreTests
{
    private const string ScopeKind = TodoWireMaps.ScopeGoal;
    private const string ScopeId = "goal-run-1";

    private string _testRoot = null!;
    private PlatformDbContextFactory _dbFactory = null!;
    private TodoStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "todo-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_testRoot, "platform.db")};Default Timeout=10")
            .Options;
        _dbFactory = new PlatformDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new TodoStore(_dbFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_FirstWrite_CreatesListWithDiffAndSummary()
    {
        var result = await _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Title = "看板梳理拆解",
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "audit-board", Title = "审计看板", Status = TodoWireMaps.StatusInProgress },
                new TodoItemInput { Slug = "fix-root-cause", Title = "修根因", Status = TodoWireMaps.StatusPending },
            ],
        });

        Assert.AreEqual(1, result.Revision);
        CollectionAssert.AreEquivalent(new[] { "audit-board", "fix-root-cause" }, result.Diff.Added.ToList());
        Assert.AreEqual(0, result.Diff.Removed.Count);
        Assert.AreEqual(2, result.Summary.Total);
        Assert.AreEqual(1, result.Summary.InProgress);
        Assert.AreEqual("audit-board", result.Summary.CurrentSlug);
    }

    /// <summary>§8-1：todo_write 同内容重复调用 ⇒ 不产生重复项、revision 变化符合预期（幂等）。</summary>
    [TestMethod]
    public async Task WriteAsync_SameContentTwice_NoDuplicateItems()
    {
        var first = await WriteStandardListAsync();

        var second = await _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = first.Revision,
            Items = StandardItems(),
        });

        Assert.AreEqual(first.Revision + 1, second.Revision);
        Assert.AreEqual(0, second.Diff.Added.Count);
        Assert.AreEqual(0, second.Diff.Completed.Count);
        Assert.AreEqual(0, second.Diff.Blocked.Count);
        Assert.AreEqual(0, second.Diff.Removed.Count);

        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(3, read.Items.Count);
        Assert.AreEqual(3, read.Items.Select(i => i.Slug).Distinct().Count());
        CollectionAssert.AreEquivalent(
            new[] { "audit-board", "fix-root-cause", "write-report" },
            read.Items.Select(i => i.Slug).ToList());
    }

    /// <summary>§8-2：expected_revision 不符 ⇒ 明确报错（todo.version_conflict）且不静默覆盖。</summary>
    [TestMethod]
    public async Task WriteAsync_StaleRevision_ThrowsVersionConflictAndKeepsContent()
    {
        await WriteStandardListAsync();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0, // 已存在 ⇒ 不是合法首写
            Items =
            [
                new TodoItemInput { Slug = "hijacked", Title = "静默覆盖探测", Status = TodoWireMaps.StatusPending },
            ],
        }));

        Assert.AreEqual(TodoErrorCode.TodoVersionConflict, ex.ErrorCode);

        // 内容未被覆盖：仍是首次写入的三项。
        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(3, read.Items.Count);
        Assert.IsFalse(read.Items.Any(i => i.Slug == "hijacked"));
    }

    [TestMethod]
    public async Task WriteAsync_MoreThan20Items_ThrowsTooManyItems()
    {
        var items = Enumerable.Range(0, 21)
            .Select(i => new TodoItemInput { Slug = $"s-{i}", Title = $"步骤 {i}", Status = TodoWireMaps.StatusPending })
            .ToList();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items = items,
        }));

        Assert.AreEqual(TodoErrorCode.TodoTooManyItems, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteAsync_TwoInProgress_ThrowsMultipleInProgress()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "a", Title = "A", Status = TodoWireMaps.StatusInProgress },
                new TodoItemInput { Slug = "b", Title = "B", Status = TodoWireMaps.StatusInProgress },
            ],
        }));

        Assert.AreEqual(TodoErrorCode.TodoMultipleInProgress, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteAsync_BlockedWithoutReason_ThrowsBlockedReasonRequired()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "a", Title = "A", Status = TodoWireMaps.StatusBlocked },
            ],
        }));

        Assert.AreEqual(TodoErrorCode.TodoBlockedReasonRequired, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteAsync_DuplicateSlug_ThrowsDuplicateSlug()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "a", Title = "A", Status = TodoWireMaps.StatusPending },
                new TodoItemInput { Slug = "a", Title = "A2", Status = TodoWireMaps.StatusPending },
            ],
        }));

        Assert.AreEqual(TodoErrorCode.TodoDuplicateSlug, ex.ErrorCode);
    }

    /// <summary>全量替换语义：缺席即移除（不累积僵尸项），diff.removed 报告。</summary>
    [TestMethod]
    public async Task WriteAsync_FullReplace_RemovesMissingSlugs()
    {
        await WriteStandardListAsync();

        var result = await _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 1,
            Items =
            [
                new TodoItemInput { Slug = "audit-board", Title = "审计看板", Status = TodoWireMaps.StatusCompleted, EvidenceRef = "abc1234" },
                new TodoItemInput { Slug = "new-step", Title = "新步骤", Status = TodoWireMaps.StatusPending },
            ],
        });

        CollectionAssert.AreEquivalent(new[] { "audit-board" }, result.Diff.Completed.ToList());
        CollectionAssert.AreEquivalent(new[] { "new-step" }, result.Diff.Added.ToList());
        CollectionAssert.AreEquivalent(new[] { "fix-root-cause", "write-report" }, result.Diff.Removed.ToList());

        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        CollectionAssert.AreEquivalent(new[] { "audit-board", "new-step" }, read.Items.Select(i => i.Slug).ToList());
        Assert.AreEqual(1, read.Summary.Completed);
        Assert.IsNull(read.Summary.CurrentSlug);
    }

    /// <summary>§8-3：第二个 in_progress ⇒ 服务端拒绝且给出可读原因（指名现有 in_progress 项）。</summary>
    [TestMethod]
    public async Task CheckAsync_SecondInProgress_ThrowsWithReadableReason()
    {
        await WriteStandardListAsync();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.CheckAsync(new TodoCheckRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "fix-root-cause",
            Status = TodoWireMaps.StatusInProgress,
            ExpectedRevision = 1,
        }));

        Assert.AreEqual(TodoErrorCode.TodoMultipleInProgress, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "audit-board");
    }

    /// <summary>§8-3：blocked 无 blocked_reason ⇒ 拒绝。</summary>
    [TestMethod]
    public async Task CheckAsync_BlockedWithoutReason_Throws()
    {
        await WriteStandardListAsync();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.CheckAsync(new TodoCheckRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusBlocked,
            ExpectedRevision = 1,
        }));

        Assert.AreEqual(TodoErrorCode.TodoBlockedReasonRequired, ex.ErrorCode);
    }

    /// <summary>§8-4：todo_check 勾选完成后，todo_read 与 check 返回同一数据源的同一视图。</summary>
    [TestMethod]
    public async Task CheckAsync_CompleteThenRead_SameSingleSource()
    {
        await WriteStandardListAsync();

        var check = await _store.CheckAsync(new TodoCheckRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusCompleted,
            EvidenceRef = "commit:deadbee",
            ExpectedRevision = 1,
        });

        Assert.AreEqual(2, check.Revision);
        Assert.AreEqual(TodoWireMaps.StatusCompleted, check.Item.Status);
        Assert.AreEqual("commit:deadbee", check.Item.EvidenceRef);
        Assert.IsNotNull(check.Item.CompletedAtUtc);
        Assert.AreEqual(1, check.Summary.Completed);
        Assert.IsNull(check.Summary.CurrentSlug);

        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(read.Revision, check.Revision);
        var readItem = read.Items.Single(i => i.Slug == "audit-board");
        Assert.AreEqual(TodoWireMaps.StatusCompleted, readItem.Status);
        Assert.AreEqual(check.Item.CompletedAtUtc, readItem.CompletedAtUtc);
        Assert.AreEqual(check.Summary.Completed, read.Summary.Completed);
    }

    [TestMethod]
    public async Task CheckAsync_UnknownSlug_ThrowsItemNotFound()
    {
        await WriteStandardListAsync();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.CheckAsync(new TodoCheckRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "no-such-item",
            Status = TodoWireMaps.StatusCompleted,
            ExpectedRevision = 1,
        }));

        Assert.AreEqual(TodoErrorCode.TodoItemNotFound, ex.ErrorCode);
    }

    [TestMethod]
    public async Task CheckAsync_StaleRevision_ThrowsVersionConflict()
    {
        await WriteStandardListAsync();

        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.CheckAsync(new TodoCheckRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusCompleted,
            ExpectedRevision = 99,
        }));

        Assert.AreEqual(TodoErrorCode.TodoVersionConflict, ex.ErrorCode);
        Assert.AreEqual(1, ex.CurrentRevision);
    }

    [TestMethod]
    public async Task ReadAsync_NoList_ReturnsNull()
    {
        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = "never-written" });
        Assert.IsNull(read);
    }

    [TestMethod]
    public async Task ArchiveAsync_SetsArchivedAtAndWriteReactivates()
    {
        await WriteStandardListAsync();

        var archived = await _store.ArchiveAsync(new TodoArchiveRequest { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.AreEqual(2, archived.Revision);
        Assert.AreNotEqual(default, archived.ArchivedAtUtc);

        var read = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(archived.ArchivedAtUtc, read.ArchivedAtUtc);

        // 归档后的再次写入 = 重新激活（临时性语义：归档只是收尾标记）。
        var rewrite = await _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = archived.Revision,
            Items =
            [
                new TodoItemInput { Slug = "fresh", Title = "新一轮", Status = TodoWireMaps.StatusPending },
            ],
        });
        Assert.AreEqual(archived.Revision + 1, rewrite.Revision);

        var read2 = await _store.ReadAsync(new TodoReadQuery { ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read2);
        Assert.IsNull(read2.ArchivedAtUtc);
    }

    [TestMethod]
    public async Task WriteAsync_InvalidScopeKind_ThrowsInvalidScopeKind()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = "board",
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items = [],
        }));

        Assert.AreEqual(TodoErrorCode.TodoInvalidScopeKind, ex.ErrorCode);
    }

    // ── fixtures ────────────────────────────────────────────

    private static List<TodoItemInput> StandardItems() =>
    [
        new TodoItemInput { Slug = "audit-board", Title = "审计看板", Status = TodoWireMaps.StatusInProgress },
        new TodoItemInput { Slug = "fix-root-cause", Title = "修根因", Status = TodoWireMaps.StatusPending },
        new TodoItemInput { Slug = "write-report", Title = "写报告", Status = TodoWireMaps.StatusPending },
    ];

    private async Task<TodoWriteResult> WriteStandardListAsync()
    {
        return await _store.WriteAsync(new TodoWriteRequest
        {
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Title = "标准拆解",
            ExpectedRevision = 0,
            Items = StandardItems(),
        });
    }
}
