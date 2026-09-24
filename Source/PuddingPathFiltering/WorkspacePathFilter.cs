namespace PuddingPathFiltering;

/// <summary>
/// 消费者唯一入口（ADR-089 U4-4 D1/D4）：把「名字级噪声目录」与「.gitignore 忽略栈」合成一个判定。
/// 索引侧与工具侧都只通过本类型做排除决策，因此两侧的排除语义由<b>同一份定义</b>派生。
/// <para>纯逻辑：无 I/O。<see cref="GitIgnoreFileLoader"/> 负责读盘并把栈交给本类型。</para>
/// </summary>
public sealed class WorkspacePathFilter
{
    /// <summary>只按名字级噪声名单过滤（无 .gitignore 信息）。</summary>
    public static WorkspacePathFilter NoiseOnly { get; } = new(IgnoreStack.Empty);

    /// <summary>用一个已构造的忽略栈创建过滤器；<c>null</c> 等价于 <see cref="IgnoreStack.Empty"/>。</summary>
    public WorkspacePathFilter(IgnoreStack? gitIgnoreStack = null)
    {
        GitIgnore = gitIgnoreStack ?? IgnoreStack.Empty;
    }

    /// <summary>施加的 .gitignore 忽略栈（可能为空栈）。</summary>
    public IgnoreStack GitIgnore { get; }

    /// <summary>
    /// 是否排除（相对同一个根的路径）。先看名字级噪声目录，再看 .gitignore。
    /// </summary>
    public bool IsExcluded(string? relativePath, bool isDirectory)
    {
        if (PathNoiseRules.IsNoisePath(relativePath))
            return true;

        return GitIgnore.Count > 0 && GitIgnore.IsIgnored(relativePath ?? string.Empty, isDirectory);
    }

    /// <summary>等同 <see cref="IsExcluded"/>，附带命中原因（诊断 / 与 <c>git check-ignore -v</c> 对照）。</summary>
    public bool IsExcluded(string? relativePath, bool isDirectory, out string? reason)
    {
        if (PathNoiseRules.IsNoisePath(relativePath))
        {
            reason = "noise name: " + relativePath;
            return true;
        }

        if (GitIgnore.Count > 0 && GitIgnore.IsIgnored(relativePath ?? string.Empty, isDirectory, out var gitReason))
        {
            reason = gitReason;
            return true;
        }

        reason = null;
        return false;
    }
}
