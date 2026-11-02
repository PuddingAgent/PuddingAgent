namespace Pudding.Contracts.Computer;

// ⚠️ 这些 struct 刻意**不做**构造校验：
//    record struct 的 default 值不会执行构造函数，任何"构造时抛异常"的写法对 default 都无效，
//    只会制造"我验过了"的假安全感。设计 §4.1 把验证放在工厂里，并为每个入口配
//    "default(struct) 不构成有效身份"的断言。此处保持纯值语义。

/// <summary>窗口的不透明标注 ID。HWND 只存在于 <c>WindowsWindowRegistry</c> 的内部表，不出现在这里。</summary>
public readonly record struct ComputerWindowId(string Value);

/// <summary>一次观察（窗口快照 / UIA 子树 / 截图）的世代标识。</summary>
public readonly record struct ComputerObservationId(string Value);

/// <summary>Computer Use 会话标识（与 Agent 的 ChatSession 不同层：一个会话可开多个控制租约）。</summary>
public readonly record struct ComputerSessionId(string Value);

/// <summary>
/// 窗口身份四元组。
/// <para>
/// 为什么要四个字段而不是一个 HWND：**HWND 会被系统复用**。
/// <c>ProcessId + ProcessStartTimeUtcTicks + WindowGeneration</c> 联合起来才能保证
/// 「同一个 WindowId 在两次观察之间仍是同一个窗口」，而不是同一数值换了主人。
/// </para>
/// </summary>
public sealed record ComputerWindowIdentity(
    ComputerWindowId WindowId,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    long WindowGeneration);
