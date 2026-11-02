namespace Pudding.Contracts.Computer;

/// <summary>
/// 授权范围。**每一项都是一个独立的能力**，不是等级。
/// <para>
/// 关键区分：<see cref="CaptureWindow"/>（截某个已授权窗口）与 <see cref="CaptureDisplay"/>
/// （截整块显示器）是两个范围 —— **窗口授权不隐含整屏授权**。
/// 把两者合成一个枚举值，等于让"允许你看这个窗口"自动变成"允许你看整个屏幕"。
/// </para>
/// </summary>
[Flags]
public enum ComputerScope
{
    None = 0,

    /// <summary>枚举窗口（列出可选目标）。首次选择目标需要它，之后可复用已有授权。</summary>
    Enumerate = 1,

    /// <summary>读取已授权窗口的 UIA 子树。</summary>
    ReadTree = 2,

    /// <summary>截取已授权窗口的画面（含被遮挡的部分）。</summary>
    CaptureWindow = 4,

    /// <summary>截取整块显示器。**必须单独授予**，不能由窗口授权推出。</summary>
    CaptureDisplay = 8,

    /// <summary>通过 UIA 控件模式写入（Invoke / SetValue / Selection 等）。</summary>
    UiaWrite = 16,

    /// <summary>合成原生输入（鼠标/键盘）。比 UiaWrite 更底层、更不可控。</summary>
    NativeInput = 32,

    /// <summary>窗口管理：激活、移动、关闭等。</summary>
    WindowManage = 64,
}

/// <summary>
/// 一次授予：哪个主体、对哪些资源、能做什么、到什么时候为止。
/// <para>
/// 身份比较使用 <c>workspace + 会话 + Agent + run</c> 的稳定组合，
/// **不用每次调用都变的 ToolCallId** 作为 grant owner（否则同一任务的重试会被当成新主体）。
/// </para>
/// <para>
/// 继承规则：子代理**默认不继承**任何权限；显式授予时仍必须是父授权的**子集**并绑定该子代理；
/// 撤销父授权必须**级联**撤销。窗口的进程启动身份一旦变化，旧授权不再适用。
/// </para>
/// <para>
/// <see cref="IncludeOwnedDialogs"/> 来自用户勾选「包含该窗口打开的对话框」。
/// 只有 OS owner 链能追到授权根窗口、且进程启动身份匹配、当前桌面一致的对话框才动态登记为派生目标；
/// <b>不得</b>按标题（如「另存为」）或同名进程推断授权。
/// </para>
/// </summary>
public sealed record ComputerGrant(
    string GrantId,
    ComputerPrincipal Grantee,
    IReadOnlyList<ComputerWindowId> Windows,
    IReadOnlyList<string> Displays,
    ComputerScope Scope,
    bool IncludeOwnedDialogs,
    DateTimeOffset ExpiresAtUtc,
    long GrantRevision,
    string? ParentGrantId);
