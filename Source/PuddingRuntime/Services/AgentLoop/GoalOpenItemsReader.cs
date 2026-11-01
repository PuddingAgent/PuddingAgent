using PuddingCode.Tasks;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 把「当前会话的待办表」变成 goal 决策器需要的 openItems。
/// <para>
/// 这是方案 2 判据 ① 的前置：没有真实的待办来源，`openItems` 只能硬编码 0，
/// 而 `LastRoundMadeProgress` 就永远不会改变决策结果 —— 决策等于还是靠猜。
/// </para>
/// <para>
/// 刻意不直接依赖 <c>ITodoStore</c> 做单元测试：用一个委托包住真实读取，
/// 测试注入桩函数即可，不必伪造整个存储接口。
/// </para>
/// </summary>
public sealed class GoalOpenItemsReader
{
    private readonly Func<string, string, CancellationToken, Task<TodoReadResult?>> _read;

    /// <summary>生产用法：绑定真实 Todo 存储。</summary>
    public GoalOpenItemsReader(ITodoStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _read = (agentId, sessionId, ct) => store.ReadAsync(
            new TodoReadQuery
            {
                AgentId = agentId,
                ScopeKind = ScopeSession,
                ScopeId = sessionId,
            },
            ct);
    }

    /// <summary>测试用法：注入读取桩。</summary>
    internal GoalOpenItemsReader(Func<string, string, CancellationToken, Task<TodoReadResult?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>待办表按会话作用域存放（与 todo_read 的 scope_kind=session 一致）。</summary>
    public const string ScopeSession = "session";

    /// <summary>
    /// 统计未完成项。列表不存在（返回 null）时按 0 处理 —— 没写拆解不等于有活没干。
    /// </summary>
    public async Task<int> CountOpenAsync(string agentId, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(sessionId))
            return 0;

        var result = await _read(agentId, sessionId, ct).ConfigureAwait(false);
        return CountOpen(result);
    }

    /// <summary>纯计数：以 CompletedAtUtc 判断是否完成，不解析 status 文本。</summary>
    public static int CountOpen(TodoReadResult? result) =>
        CountOpen(result?.Items);

    /// <summary>同一计数规则的列表重载，便于只构造条目就能测。</summary>
    public static int CountOpen(IReadOnlyList<TodoItemView>? items) =>
        items is null ? 0 : items.Count(item => item.CompletedAtUtc is null);
}
