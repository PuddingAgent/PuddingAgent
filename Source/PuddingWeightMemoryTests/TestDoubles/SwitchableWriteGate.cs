using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 可开合写入准入闸门：先用真实容量登记章节，再关闸验证降级路径。
/// <para>
/// 用"关闸"而不是"用一个永远拒绝的闸门"是必要的：关闸前必须先建立分区，否则预测在
/// "分区不存在"这一步就返回空结果，根本走不到饱和降级分支——那样测试会绿得毫无意义。
/// </para>
/// </summary>
internal sealed class SwitchableWriteGate : IWriteAdmissionGate
{
    internal SwitchableWriteGate(int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>关闸后：<see cref="TryEnter"/> 恒失败、<see cref="IsSaturated"/> 恒为真。</summary>
    internal bool Closed { get; set; }

    public int PendingWrites => Closed ? Capacity : 0;

    public bool IsSaturated => Closed;

    public bool TryEnter() => !Closed;

    public void Exit()
    {
        if (Closed)
            throw new InvalidOperationException("闸门关闭时不应有写入在占用槽位。");
    }
}
