using Pudding.Contracts.Desktop;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// **调用者身份作用域**（设计方案 §3.1 / 下一阶段实施方案 §3.1）。
///
/// 规则（全部是硬约束）：
/// <list type="bullet">
///   <item>身份**只能**来自<b>可信执行上下文</b>：由 <see cref="BrowserAgentToolBase{TArgs}"/> 在每次工具调用开始时
///         用 <see cref="BrowserCallerIdentitySource.TryProject"/> 投影一次；</item>
///   <item>作用域是**调用级**的（AsyncLocal，每层 <c>Push</c> 都返回还原句柄）⇒ 工具是 DI 单例，**不得**把身份放进字段；</item>
///   <item>与<b>审计 Origin</b>分开存放：Origin 用于诊断，本作用域用于**授权**，两者不能互相顶替；</item>
///   <item>投影失败时保留**具体原因**（缺运行身份 / 缺调用者 / 子代理缺父任务），调用方据此拒绝，**不生成默认身份**；</item>
///   <item>不从工具参数、Origin、网页内容或模型自报字段推导（本类型没有接受这些输入的入口）。</item>
/// </list>
/// </summary>
public static class BrowserCallerIdentityScope
{
    private static readonly AsyncLocal<CallState?> CurrentState = new();

    /// <summary>本次调用的调用者身份；不可信/未投影时为 <c>null</c>（**不是**"已授权"）。</summary>
    public static BrowserCallerIdentity? Current => CurrentState.Value?.Identity;

    /// <summary>本次调用**为什么**没有身份（供拒绝结果使用）；成功投影时为 <c>null</c>。</summary>
    public static string? CurrentFailureReason => CurrentState.Value?.FailureReason;

    /// <summary>
    /// 推入一次调用的身份（或失败原因）。返回的句柄在释放时**无条件还原**前值，
    /// 因此正常返回、异常与取消路径都不会残留身份（嵌套调用按栈序还原）。
    /// </summary>
    public static IDisposable Push(BrowserCallerIdentity? identity, string? failureReason)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = new CallState(identity, failureReason);
        return new RestoreScope(previous);
    }

    private sealed record CallState(BrowserCallerIdentity? Identity, string? FailureReason);

    private sealed class RestoreScope(CallState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}

/// <summary>
/// 供 Core 侧消费者（Host 调用上下文工厂等）读取**本次调用**的可信身份。
/// 读取方不得缓存它（工具是单例，身份是调用级事实）。
/// </summary>
public interface IBrowserCallerIdentityAccessor
{
    /// <summary>本次调用的调用者身份；没有可信身份时为 <c>null</c>。</summary>
    BrowserCallerIdentity? Current { get; }

    /// <summary>本次调用缺少身份时的具体原因；有身份时为 <c>null</c>。</summary>
    string? CurrentFailureReason { get; }
}

/// <summary><see cref="IBrowserCallerIdentityAccessor"/> 的默认实现：直接读调用级作用域，本身不保存状态。</summary>
public sealed class BrowserCallerIdentityAccessor : IBrowserCallerIdentityAccessor
{
    public BrowserCallerIdentity? Current => BrowserCallerIdentityScope.Current;

    public string? CurrentFailureReason => BrowserCallerIdentityScope.CurrentFailureReason;
}
