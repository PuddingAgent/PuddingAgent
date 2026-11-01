using PuddingCode.Tasks;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// 待办 → openItems：这是让 `LastRoundMadeProgress` 真正能改变决策的那一环。
/// <para>
/// 计数规则用 <c>CompletedAtUtc</c> 而不是解析 status 文本 —— 文本会变，
/// 结构字段不会。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalOpenItemsReaderTests
{
    private static TodoItemView Item(string slug, bool done) => new()
    {
        Slug = slug,
        Title = slug,
        Status = done ? "done" : "pending",
        OrderIndex = 0,
        CompletedAtUtc = done ? DateTimeOffset.UnixEpoch : null,
    };

    [TestMethod]
    public void Null_list_counts_as_zero()
    {
        Assert.AreEqual(0, GoalOpenItemsReader.CountOpen((IReadOnlyList<TodoItemView>?)null));
    }

    [TestMethod]
    public void Only_unfinished_items_are_counted()
    {
        var items = new[] { Item("a", false), Item("b", true), Item("c", false) };

        Assert.AreEqual(2, GoalOpenItemsReader.CountOpen(items));
    }

    [TestMethod]
    public void All_done_counts_as_zero()
    {
        var items = new[] { Item("a", true), Item("b", true) };

        Assert.AreEqual(0, GoalOpenItemsReader.CountOpen(items));
    }

    [TestMethod]
    public async Task Reader_returns_zero_for_invalid_identity()
    {
        var reader = new GoalOpenItemsReader((_, _, _) => throw new InvalidOperationException("不该被调用"));

        Assert.AreEqual(0, await reader.CountOpenAsync("", "s1"));
        Assert.AreEqual(0, await reader.CountOpenAsync("a1", "  "));
    }

    [TestMethod]
    public async Task Reader_maps_store_result_to_open_count()
    {
        var items = new[] { Item("a", false), Item("b", false), Item("c", true) };
        var reader = new GoalOpenItemsReader((agentId, sessionId, _) =>
        {
            Assert.AreEqual("a1", agentId);
            Assert.AreEqual("s1", sessionId);
            return Task.FromResult<TodoReadResult?>(new TodoReadResult
            {
                ListId = "l1",
                ScopeKind = GoalOpenItemsReader.ScopeSession,
                ScopeId = sessionId,
                Revision = 1,
                Items = items,
                Summary = null!,
            });
        });

        Assert.AreEqual(2, await reader.CountOpenAsync("a1", "s1"));
    }

    [TestMethod]
    public async Task Reader_treats_missing_list_as_zero_open_items()
    {
        var reader = new GoalOpenItemsReader((_, _, _) => Task.FromResult<TodoReadResult?>(null));

        Assert.AreEqual(0, await reader.CountOpenAsync("a1", "s1"));
    }
}
