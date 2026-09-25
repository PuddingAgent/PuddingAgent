namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 全文索引「供给请求」——调用方<b>只提交原始路径</b>。
/// <para>
/// 绝对化 / 去尾分隔符 / 解析 <c>..</c> / 规范化后去重 / 嵌套检测，全部由协调器
/// （<see cref="IFullTextIndexSupplyCoordinator"/>）承担；调用方<b>不做</b>任何预处理，
/// 也不得自行判断 scope 是否可用。
/// </para>
/// </summary>
/// <param name="RootPaths">原始 scope 路径（通常一个；多 scope 用于一次性规划/启动）。可为相对形式，由协调器逐条拒绝。</param>
/// <param name="BudgetBytes">本次请求的索引体积预算（字节）。null ⇒ 使用协调器注入的默认预算。</param>
/// <param name="RequestedBy">可选请求方标识。A1 只用于诊断，<b>不参与任何判定</b>。</param>
public sealed record SupplyScopeRequest(
    IReadOnlyList<string> RootPaths,
    long? BudgetBytes = null,
    string? RequestedBy = null)
{
    /// <summary>单 scope 便捷构造：等价于 <c>new SupplyScopeRequest(new[] { rootPath }, budgetBytes, requestedBy)</c>。</summary>
    public SupplyScopeRequest(string rootPath, long? budgetBytes = null, string? requestedBy = null)
        : this(new[] { rootPath }, budgetBytes, requestedBy)
    {
    }
}

/// <summary>
/// 规范化之后的 scope（协调器内部与结果里的唯一 scope 标识）。
/// </summary>
/// <param name="ScopeKey">
/// 规范化后的<b>规范键</b>：绝对路径 + 去尾分隔符 + 解析 <c>..</c> + 分隔符统一为 <c>\</c> + 不变文化小写。
/// Windows 优先（文件系统大小写不敏感）⇒ 小写化保证「同一目录只对应一套租约文件 / 一条 job 记录」。
/// </param>
/// <param name="RootPath">规范化后的绝对路径（保留调用方给的大小写），用于读盘。</param>
public sealed record SupplyScope(string ScopeKey, string RootPath);

/// <summary>scope 被拒绝的原因枚举（逐条显式，不允许静默丢弃）。</summary>
public enum SupplyRejectionReason
{
    /// <summary>空串 / 空白串 / 请求里没有任何路径。</summary>
    Empty = 0,

    /// <summary>相对路径（A1 只接受绝对路径；基准解析属宿主配置层，不在本组件猜）。</summary>
    Relative = 1,

    /// <summary>路径在磁盘上不存在。</summary>
    NotFound = 2,

    /// <summary>路径存在但不是目录（是文件）。</summary>
    NotDirectory = 3,

    /// <summary>规范化后与本次请求中另一个 scope 相同。</summary>
    Duplicate = 4,

    /// <summary>与本次请求中另一个 scope 存在父子嵌套关系（任一方向）。</summary>
    Nested = 5,
}

/// <summary>
/// 一条 scope 拒绝记录：值 + 原因枚举 + 可读消息（三者齐全，缺一不可）。
/// </summary>
/// <param name="Value">被拒绝的值：能规范化时给出规范化结果，否则给出原始入参。</param>
/// <param name="Reason">原因枚举。</param>
/// <param name="Message">可读消息（含路径与拒绝理由，供直接展示）。</param>
public sealed record SupplyScopeRejection(string Value, SupplyRejectionReason Reason, string Message);
