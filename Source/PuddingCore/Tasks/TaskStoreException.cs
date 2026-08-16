namespace PuddingCode.Tasks;

/// <summary>Task Store 操作失败的契约异常（CAS/状态/规则冲突）。</summary>
public sealed class TaskStoreException : Exception
{
    public TaskErrorCode ErrorCode { get; }
    public string? TaskId { get; }
    public int? ExpectedVersion { get; }
    public int? ActualVersion { get; }

    public TaskStoreException(TaskErrorCode code, string message,
        string? taskId = null, int? expectedVersion = null, int? actualVersion = null)
        : base(message)
    {
        ErrorCode = code;
        TaskId = taskId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
