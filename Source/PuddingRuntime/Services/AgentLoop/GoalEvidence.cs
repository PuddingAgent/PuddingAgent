namespace PuddingRuntime.Services.AgentLoop;

/// <summary>完成证据的种类。种类决定"由谁去核验"，不决定可信度。</summary>
public enum GoalEvidenceKind
{
    /// <summary>一条可以被重跑的命令（及其结果）。</summary>
    Command = 0,
    /// <summary>一次测试运行（用例名 + 结果）。</summary>
    Test = 1,
    /// <summary>一个文件/产物（路径 + 摘要，可复核）。</summary>
    File = 2,
    /// <summary>一个外部链接（PR、issue、监控页）。</summary>
    Url = 3,
}

/// <summary>
/// 一条完成证据。
/// <para>
/// 存在的理由：`OpenItems == 0` 只说明"模型认为清单空了"，不说明"事情真的做完了"。
/// 把完成判定绑到**可核验的引用**上，至少能让假完成留下把柄。
/// </para>
/// </summary>
public sealed record GoalEvidence(GoalEvidenceKind Kind, string Reference, string Summary)
{
    /// <summary>证据必须能被别人复核：引用非空、说明非空。</summary>
    public bool IsVerifiable =>
        !string.IsNullOrWhiteSpace(Reference) && !string.IsNullOrWhiteSpace(Summary);
}
