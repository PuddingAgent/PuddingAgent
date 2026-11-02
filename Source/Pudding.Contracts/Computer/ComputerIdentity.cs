namespace Pudding.Contracts.Computer;

/// <summary>
/// 窗口的不透明身份。
/// <para>
/// 刻意**不**暴露 HWND/PID/启动时间：那些是驱动层的实现细节，且 HWND 会被复用
/// （同一数值可能在不同时刻指向不同窗口）。外部只持有这个句柄，由驱动负责映射与校验。
/// </para>
/// <para>
/// 为什么放在 Contracts 而不是 Automation：身份要跨进程（Workflow/Worker/Supervisor）
/// 与跨传输（Named Pipe）传递，属于最底层的值对象，只依赖 BCL。
/// </para>
/// </summary>
public readonly record struct WindowIdentity
{
    /// <summary>不透明标识文本；空值视为编程错误，在构造处立即失败。</summary>
    public string Value { get; }

    public WindowIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Window identity must be non-empty.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// 一次观察（窗口快照 / UIA 子树 / 截图）的世代标识。
/// <para>
/// 观察会失效：写动作、窗口几何变化、显示布局变化、遮挡归属变化都会让旧观察作废。
/// 用独立 ID 而不是时间戳，是因为"是否可信"由登记表判定，不能靠调用方比对时钟。
/// </para>
/// </summary>
public readonly record struct ObservationId
{
    public string Value { get; }

    public ObservationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Observation identity must be non-empty.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}

/// <summary>执行驱动的 Worker 实例身份（进程生命周期内唯一，跨世代不复用）。</summary>
public readonly record struct WorkerId
{
    public string Value { get; }

    public WorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Worker identity must be non-empty.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// 一段桌面写权限的租约身份。
/// <para>
/// 租约把"谁在操作这台机器"显式化：同一时刻只应有一个持有者，
/// 释放/过期后必须重新申请，不能靠旧租约续期。
/// </para>
/// </summary>
public readonly record struct LeaseId
{
    public string Value { get; }

    public LeaseId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Lease identity must be non-empty.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}
