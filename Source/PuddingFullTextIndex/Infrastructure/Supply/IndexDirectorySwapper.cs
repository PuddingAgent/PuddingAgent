namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 索引目录「移动 = 同卷重命名」的原语（A2a R3）。
/// <para>
/// 抽成接缝的两个**必要性**（不是为了抽象而抽象）：
/// ① 切换第 3 步（staging → live）的失败路径必须在真实文件系统上**可复现地注入**，
/// 否则「回滚」断言只能靠人工操作验证（A4 要求注入失败并证明 live 仍是旧索引）；
/// ② 可以在测试中固定断言切换的**步骤顺序**（先失效 reader → 移走旧 live → 移入新 live → 回滚方向）。
/// </para>
/// <para>
/// 默认实现就是 <see cref="Directory.Move"/>；staging / trash / live 三者同卷（见
/// <see cref="SupplyIndexDirectoryLayout"/>），因此它确实是重命名语义。
/// </para>
/// </summary>
internal interface IIndexDirectorySwapper
{
    /// <summary>移动目录（同卷重命名）。失败必须抛异常（调用方据此走回滚）。</summary>
    void Move(string sourceDirectory, string destinationDirectory);
}

/// <summary>默认实现：<see cref="Directory.Move(string, string)"/>。</summary>
internal sealed class DirectoryMoveSwapper : IIndexDirectorySwapper
{
    internal static DirectoryMoveSwapper Instance { get; } = new();

    private DirectoryMoveSwapper()
    {
    }

    public void Move(string sourceDirectory, string destinationDirectory) =>
        Directory.Move(sourceDirectory, destinationDirectory);
}
