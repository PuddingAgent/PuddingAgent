using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Todo;

namespace PuddingPlatformTests.Services.Todo;

/// <summary>
/// 设计 2026-09-16 §3/§4/§8（TD-1；TD-1b 对齐用户裁决）：TodoStore 约束分级与 Agent 隔离。
/// <para>
/// §8-1 幂等：同内容重复写入不产生重复项；§8-2 CAS：expected_revision 不符明确报错不静默覆盖；
/// §8-3 约束分级：slug 重复 / CAS 冲突硬拒绝；&gt;20 项 / &gt;1 in_progress / blocked 无 reason
/// ⇒ 接受写入 + 返回 warning（TD-1b 软化）；§8-7 Agent 隔离：Agent A 既读不到也写不到
/// Agent B 的列表（即使 scope_id 相同）。TD-1b：归档语义移除（无 ArchiveAsync，写入不再重激活）。
/// </para>
/// </summary>
[TestClass]
public sealed class TodoStoreTests
{
    private const string ScopeKind = TodoWireMaps.ScopeGoal;
    private const string ScopeId = "goal-run-1";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";

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
            AgentId = AgentA,
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
        Assert.IsNull(result.Warnings);
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
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = first.Revision,
            Items = StandardItems(),
        });

        Assert.AreEqual(first.Revision + 1, second.Revision);
        Assert.IsNull(second.Warnings);
        Assert.AreEqual(0, second.Diff.Added.Count);
        Assert.AreEqual(0, second.Diff.Completed.Count);
        Assert.AreEqual(0, second.Diff.Blocked.Count);
        Assert.AreEqual(0, second.Diff.Removed.Count);

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
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
            AgentId = AgentA,
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
        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(3, read.Items.Count);
        Assert.IsFalse(read.Items.Any(i => i.Slug == "hijacked"));
    }

    /// <summary>§8-3（TD-1b 软化）：&gt;20 项 ⇒ 接受写入 + warning（不再拒绝）。</summary>
    [TestMethod]
    public async Task WriteAsync_MoreThan20Items_SucceedsWithWarning()
    {
        var items = Enumerable.Range(0, 21)
            .Select(i => new TodoItemInput { Slug = $"s-{i}", Title = $"步骤 {i}", Status = TodoWireMaps.StatusPending })
            .ToList();

        var result = await _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items = items,
        });

        // 接受写入（TD-1b：软约束不拦手），但返回稳定前缀的 warning 供 UI 提示。
        Assert.AreEqual(1, result.Revision);
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(
            result.Warnings.Any(w => w.StartsWith(TodoWireMaps.WarnTooManyItems, StringComparison.Ordinal)),
            $"expected a '{TodoWireMaps.WarnTooManyItems}' warning, got: [{string.Join("; ", result.Warnings)}]");

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(21, read.Summary.Total);
    }

    /// <summary>§8-3（TD-1b 软化）：两个 in_progress ⇒ 接受写入 + warning（不再拒绝）。</summary>
    [TestMethod]
    public async Task WriteAsync_TwoInProgress_SucceedsWithWarning()
    {
        var result = await _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "a", Title = "A", Status = TodoWireMaps.StatusInProgress },
                new TodoItemInput { Slug = "b", Title = "B", Status = TodoWireMaps.StatusInProgress },
            ],
        });

        Assert.AreEqual(1, result.Revision);
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(
            result.Warnings.Any(w => w.StartsWith(TodoWireMaps.WarnMultipleInProgress, StringComparison.Ordinal)),
            $"expected a '{TodoWireMaps.WarnMultipleInProgress}' warning, got: [{string.Join("; ", result.Warnings)}]");

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(2, read.Summary.InProgress);
    }

    /// <summary>§8-3（TD-1b 软化）：blocked 无 blocked_reason ⇒ 接受写入 + warning（不再拒绝）。</summary>
    [TestMethod]
    public async Task WriteAsync_BlockedWithoutReason_SucceedsWithWarning()
    {
        var result = await _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "a", Title = "A", Status = TodoWireMaps.StatusBlocked },
            ],
        });

        Assert.AreEqual(1, result.Revision);
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(
            result.Warnings.Any(w => w.StartsWith(TodoWireMaps.WarnBlockedReasonRequired, StringComparison.Ordinal)),
            $"expected a '{TodoWireMaps.WarnBlockedReasonRequired}' warning, got: [{string.Join("; ", result.Warnings)}]");
    }

    /// <summary>硬约束保持：slug 列表内重复 ⇒ 仍硬报错（todo.duplicate_slug）。</summary>
    [TestMethod]
    public async Task WriteAsync_DuplicateSlug_ThrowsDuplicateSlug()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentA,
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
            AgentId = AgentA,
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

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        CollectionAssert.AreEquivalent(new[] { "audit-board", "new-step" }, read.Items.Select(i => i.Slug).ToList());
        Assert.AreEqual(1, read.Summary.Completed);
        Assert.IsNull(read.Summary.CurrentSlug);
    }

    /// <summary>§8-3（TD-1b 软化）：check 到第二个 in_progress ⇒ 接受勾选 + warning（指名现有 in_progress 项）。</summary>
    [TestMethod]
    public async Task CheckAsync_SecondInProgress_SucceedsWithWarning()
    {
        await WriteStandardListAsync();

        var check = await _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "fix-root-cause",
            Status = TodoWireMaps.StatusInProgress,
            ExpectedRevision = 1,
        });

        // 接受写入（不再硬拒），返回 warning 且点名已存在的 in_progress 项。
        Assert.AreEqual(2, check.Revision);
        Assert.IsNotNull(check.Warnings);
        Assert.IsTrue(
            check.Warnings.Any(w => w.StartsWith(TodoWireMaps.WarnMultipleInProgress, StringComparison.Ordinal)
                && w.Contains("audit-board", StringComparison.Ordinal)),
            $"expected a '{TodoWireMaps.WarnMultipleInProgress}' warning naming 'audit-board', got: [{string.Join("; ", check.Warnings)}]");

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(read);
        Assert.AreEqual(2, read.Summary.InProgress);
    }

    /// <summary>
    /// §8-3（TD-1b 软化）：check 到 blocked 且生效 reason（请求值 ?? 项上已有值）为空 ⇒ 接受 + warning；
    /// 请求提供 reason ⇒ 无 warning；项上已有 reason、后续 check 不带 reason ⇒ 沿用旧 reason，不产生 warning。
    /// </summary>
    [TestMethod]
    public async Task CheckAsync_BlockedWithoutReason_SucceedsWithWarning()
    {
        await WriteStandardListAsync();

        // 阶段 1：audit-board 项原本无 reason，check 到 blocked 不带 reason ⇒ warning（写入仍被接受）。
        var first = await _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusBlocked,
            ExpectedRevision = 1,
        });
        Assert.AreEqual(2, first.Revision);
        Assert.IsNotNull(first.Warnings);
        Assert.IsTrue(first.Warnings.Any(w => w.StartsWith(TodoWireMaps.WarnBlockedReasonRequired, StringComparison.Ordinal)));

        // 阶段 2：请求提供 reason ⇒ 无 warning，且 reason 落到项上。
        var second = await _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusBlocked,
            BlockedReason = "上游依赖缺失，等待人工提供凭据",
            ExpectedRevision = 2,
        });
        Assert.AreEqual(3, second.Revision);
        Assert.IsNull(second.Warnings);
        Assert.AreEqual("上游依赖缺失，等待人工提供凭据", second.Item.BlockedReason);

        // 阶段 3：项上已有 reason，再次 check 到 blocked（不带 reason）⇒ 沿用旧 reason，无 warning。
        var third = await _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusBlocked,
            ExpectedRevision = 3,
        });
        Assert.AreEqual(4, third.Revision);
        Assert.IsNull(third.Warnings);
        Assert.AreEqual("上游依赖缺失，等待人工提供凭据", third.Item.BlockedReason);
    }

    /// <summary>§8-4：todo_check 勾选完成后，todo_read 与 check 返回同一数据源的同一视图。</summary>
    [TestMethod]
    public async Task CheckAsync_CompleteThenRead_SameSingleSource()
    {
        await WriteStandardListAsync();

        var check = await _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusCompleted,
            EvidenceRef = "commit:deadbee",
            ExpectedRevision = 1,
        });

        Assert.AreEqual(2, check.Revision);
        Assert.IsNull(check.Warnings);
        Assert.AreEqual(TodoWireMaps.StatusCompleted, check.Item.Status);
        Assert.AreEqual("commit:deadbee", check.Item.EvidenceRef);
        Assert.IsNotNull(check.Item.CompletedAtUtc);
        Assert.AreEqual(1, check.Summary.Completed);
        Assert.IsNull(check.Summary.CurrentSlug);

        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
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
            AgentId = AgentA,
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
            AgentId = AgentA,
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
        var read = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = "never-written" });
        Assert.IsNull(read);
    }

    /// <summary>
    /// §8-7 Agent 隔离（TD-1b 核心）：同一 (scope_kind, scope_id)，Agent B 读不到 A 的列表、
    /// 写不进 A 的列表（CAS 定位不到即明确报错）；B 首写只能创建自己的独立列表，且不影响 A 的数据。
    /// </summary>
    [TestMethod]
    public async Task SameScope_DifferentAgents_AreFullyIsolated()
    {
        var writtenByA = await WriteStandardListAsync();
        Assert.AreEqual(1, writtenByA.Revision);

        // ① 读被拒：B 读同 scope ⇒ 不可见（found=false），拿不到 A 的任何数据。
        var readByB = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentB, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNull(readByB);

        // ② 写被拒：B check A 的列表 ⇒ 列表按 agent 过滤后不存在（TodoListNotFound）。
        var checkEx = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.CheckAsync(new TodoCheckRequest
        {
            AgentId = AgentB,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Slug = "audit-board",
            Status = TodoWireMaps.StatusCompleted,
            ExpectedRevision = 1,
        }));
        Assert.AreEqual(TodoErrorCode.TodoListNotFound, checkEx.ErrorCode);

        // ③ 写被拒：B 以 A 的 revision 写同 scope ⇒ CAS 明确报错（不能以他方版本写入）。
        var casEx = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentB,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = writtenByA.Revision,
            Items =
            [
                new TodoItemInput { Slug = "intruded", Title = "跨 Agent 写入探测", Status = TodoWireMaps.StatusPending },
            ],
        }));
        Assert.AreEqual(TodoErrorCode.TodoVersionConflict, casEx.ErrorCode);

        // ④ B 首写（revision=0）⇒ 创建 B 自己的独立列表（隔离命名空间），与 A 的列表共存。
        var writtenByB = await _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentB,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items =
            [
                new TodoItemInput { Slug = "b-own", Title = "B 自己的拆解", Status = TodoWireMaps.StatusPending },
            ],
        });
        Assert.AreNotEqual(writtenByA.ListId, writtenByB.ListId);

        // ⑤ A 的列表完全不受 B 影响：revision 不变、内容不变、无 intruded 项。
        var readByAAgain = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentA, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(readByAAgain);
        Assert.AreEqual(writtenByA.ListId, readByAAgain.ListId);
        Assert.AreEqual(1, readByAAgain.Revision);
        Assert.AreEqual(3, readByAAgain.Items.Count);
        Assert.IsFalse(readByAAgain.Items.Any(i => i.Slug == "intruded" || i.Slug == "b-own"));

        // ⑥ B 只能看到自己的列表。
        var readByBAgain = await _store.ReadAsync(new TodoReadQuery { AgentId = AgentB, ScopeKind = ScopeKind, ScopeId = ScopeId });
        Assert.IsNotNull(readByBAgain);
        Assert.AreEqual(writtenByB.ListId, readByBAgain.ListId);
        CollectionAssert.AreEquivalent(new[] { "b-own" }, readByBAgain.Items.Select(i => i.Slug).ToList());
    }

    /// <summary>TD-1b 防御：agent_id 缺失/空白 ⇒ 明确报错（身份由工具上下文注入，不允许匿名访问）。</summary>
    [TestMethod]
    public async Task WriteAsync_MissingAgentId_ThrowsInvalidAgent()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = "",
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            ExpectedRevision = 0,
            Items = [],
        }));

        Assert.AreEqual(TodoErrorCode.TodoInvalidAgent, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteAsync_InvalidScopeKind_ThrowsInvalidScopeKind()
    {
        var ex = await Assert.ThrowsExactlyAsync<TodoStoreException>(() => _store.WriteAsync(new TodoWriteRequest
        {
            AgentId = AgentA,
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
            AgentId = AgentA,
            ScopeKind = ScopeKind,
            ScopeId = ScopeId,
            Title = "标准拆解",
            ExpectedRevision = 0,
            Items = StandardItems(),
        });
    }
}
