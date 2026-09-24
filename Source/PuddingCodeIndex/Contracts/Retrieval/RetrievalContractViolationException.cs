namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 检索合同的**跨字段不变量**被违反（ADR-089 §8 的硬约束在构造期 fail-closed）。
/// <para>
/// 与 <see cref="ArgumentException"/> 的分工：单个入参本身不合法（空路径、行号 &lt; 1、页大小越界……）
/// 用 <see cref="ArgumentException"/>；<b>多个字段之间的合同</b>
/// （截断必须落盘、0 命中必须有原因、至多一条下一步、降级必须有原因……）用本异常。
/// </para>
/// <para>
/// 之所以要一个专门类型：这些断言正是"空洞否定不可表示"的落点，
/// 契约测试需要对它们逐一取红（见 U4-2a 的 A2/A3/A4/A9/A12）。
/// </para>
/// </summary>
public sealed class RetrievalContractViolationException : InvalidOperationException
{
    /// <summary>以说明文本构造。</summary>
    public RetrievalContractViolationException(string message)
        : base(message)
    {
    }
}
