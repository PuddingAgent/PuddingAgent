using PuddingBrowser.Automation;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// **浏览器子代理任务的委派信封工厂**（设计方案 §9 / 门禁 G10）。
///
/// 为什么放在工具层：委派由 Core 的 <c>spawn_sub_agent</c> 发起，但它必须给出**浏览器工具清单**——
/// 而"浏览器有哪七个工具、哪些是读、哪些是写"是**本程序集的代码事实**（<see cref="BrowserAgentToolIds"/>）。
/// 让 Core 侧自己拼工具名，就会出现"名字写错/漏了 navigate 或 screenshot"这类**编译期发现不了**的错误
/// （设计明确写了：注入列表必须包含任务实际需要的 navigate/screenshot）。
///
/// 因此这里给出一个工厂：调用方只描述任务（目标、范围、页面 grant、期望结果、预算、截止时间），
/// 工具清单由本工厂按**代码事实**填好，再交给 <see cref="BrowserDelegation.TryValidate"/> 自检。
/// </summary>
public static class BrowserDelegationTasks
{
    /// <summary>
    /// 构造一个浏览器子代理任务的委派信封。
    /// 只读任务要求 <c>browser_snapshot</c>；可写任务额外要求 <c>browser_navigate</c> 与
    /// <c>browser_screenshot</c>（后者是"写入后如实观察结果"的凭据要求）。
    /// </summary>
    public static BrowserDelegationEnvelope Create(
        string goal,
        BrowserDelegationScope delegatedScope,
        string pageGrantId,
        string expectedResult,
        TimeSpan deadline,
        int resultBudgetBytes = BrowserDelegation.DefaultResultBudgetBytes,
        IReadOnlyList<string>? extraRequiredTools = null)
    {
        ArgumentNullException.ThrowIfNull(delegatedScope);

        var required = new List<string> { BrowserAgentToolIds.Snapshot };
        if (delegatedScope.CanWrite)
        {
            required.Add(BrowserAgentToolIds.Navigate);
            required.Add(BrowserAgentToolIds.Interact);
            // 设计：写入后要**如实观察结果** ⇒ 截图是凭据要求的一部分。
            required.Add(BrowserAgentToolIds.Screenshot);
        }

        if (extraRequiredTools is { Count: > 0 })
        {
            foreach (var tool in extraRequiredTools)
            {
                if (!required.Contains(tool, StringComparer.Ordinal))
                {
                    required.Add(tool);
                }
            }
        }

        // 注入列表 = 必需工具 ∪ 浏览器工具全集（子代理可以少用，但不能"没有可用工具"）。
        var injected = BrowserAgentToolIds.All
            .Concat(required)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new BrowserDelegationEnvelope(
            goal,
            delegatedScope,
            pageGrantId,
            expectedResult,
            resultBudgetBytes,
            injected,
            required,
            deadline);
    }

    /// <summary>
    /// 构造并**立即自检**：不合格就给出原因（调用方不该把不合格信封交给 Runtime）。
    /// </summary>
    public static bool TryCreate(
        string goal,
        BrowserDelegationScope delegatedScope,
        BrowserDelegationScope parentScope,
        string pageGrantId,
        string expectedResult,
        TimeSpan deadline,
        out BrowserDelegationEnvelope? envelope,
        out IReadOnlyList<string> problems,
        int resultBudgetBytes = BrowserDelegation.DefaultResultBudgetBytes,
        IReadOnlyList<string>? extraRequiredTools = null)
    {
        var candidate = Create(
            goal,
            delegatedScope,
            pageGrantId,
            expectedResult,
            deadline,
            resultBudgetBytes,
            extraRequiredTools);

        if (BrowserDelegation.TryValidate(candidate, parentScope, out problems))
        {
            envelope = candidate;
            return true;
        }

        envelope = null;
        return false;
    }
}
