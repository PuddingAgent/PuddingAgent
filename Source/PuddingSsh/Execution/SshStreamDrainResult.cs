namespace PuddingSsh.Execution;

/// <summary>
/// 单条流的排空事实（ADR-096 D4）：**正常 EOF、读取失败、被中止是三个不同事实**，
/// 不允许把读异常当成 EOF 后继续报成功。
/// </summary>
internal readonly record struct SshStreamDrainResult(bool ReachedEnd, bool ReadFailed, bool Aborted)
{
    /// <summary>尚未开始排空（例如提交前就失败了）。</summary>
    public static readonly SshStreamDrainResult NotStarted = new(false, false, false);

    public static readonly SshStreamDrainResult EndedResult = new(true, false, false);

    public static readonly SshStreamDrainResult ReadFailedResult = new(false, true, false);

    public static readonly SshStreamDrainResult AbortedResult = new(false, false, true);
}
