namespace PuddingCode.Models;

/// <summary>蜂群任务状态枚举。</summary>
public enum SwarmTaskStatus
{
    /// <summary>任务已创建，等待分配。</summary>
    Created,

    /// <summary>任务已分配给 Worker。</summary>
    Assigned,

    /// <summary>Worker 正在执行任务。</summary>
    InProgress,

    /// <summary>任务实现完成，等待审阅。</summary>
    PendingReview,

    /// <summary>任务完成，等待测试验证。</summary>
    Testing,

    /// <summary>任务已完成并通过验证。</summary>
    Completed,

    /// <summary>任务被审阅阻断，需返工。</summary>
    Blocked,

    /// <summary>任务执行失败。</summary>
    Failed,

    /// <summary>任务被放弃。</summary>
    Abandoned
}
