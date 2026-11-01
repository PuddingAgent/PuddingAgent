namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 工具结果「追加额度」：整条 Hook 变换链共用一个上限，而不是各 Hook 自己定。
/// <para>
/// 背景（2026-11-01）：变换链上已经有两个会往工具输出里追加文本的 Hook
/// （用法引导、交接摘要提醒）。各自都有配额，但**没人管两条合起来多长**。
/// 这类「谁都往里塞、没人管总量」的设计，出问题时表现为上下文被悄悄挤占，
/// 而且很难归因到某个具体 Hook。
/// </para>
/// <para>
/// 语义：以「本轮第一次变换前的原始长度」为基准，累计追加超过
/// <see cref="MaxAppendedChars"/> 就丢弃该次追加、保留已有结果。
/// 只限制**追加**，不限制 Hook 改写（缩短或等长替换不受影响）。
/// </para>
/// </summary>
public static class ToolResultAppendBudget
{
    /// <summary>单次工具结果的累计追加上限（字符）。两个 Hook 各约数百字符，4096 足够宽裕。</summary>
    public const int MaxAppendedChars = 4096;

    /// <summary>相对原始长度是否已超出追加额度。</summary>
    public static bool IsOverBudget(int originalLength, int currentLength) =>
        currentLength > originalLength && currentLength - originalLength > MaxAppendedChars;

    /// <summary>相对原始长度已追加了多少字符（缩短时为 0）。</summary>
    public static int AppendedChars(int originalLength, int currentLength) =>
        Math.Max(0, currentLength - originalLength);
}
