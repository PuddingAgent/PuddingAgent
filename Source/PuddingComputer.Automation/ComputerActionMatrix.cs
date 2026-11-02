using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>
/// 动作矩阵（设计 §5.2）：哪个动作在哪个后端上成立，以及一条容易被写错的文本语义。
/// <para>
/// 为什么要有这个纯函数，而不是在驱动里 if 判断：
/// 「Invoke 只能用 UIA」「Click 只能用 Input」这类约束一旦散落在调用点，
/// 新增一个动作或新增一个后端时必然漏改一处，而漏改的表现是**运行期静默走错路径**。
/// 放在这里可以逐组合断言。
/// </para>
/// </summary>
public static class ComputerActionMatrix
{
    /// <summary>
    /// 该动作能否用该后端执行。
    /// <para>
    /// 注意 <see cref="ComputerAction.Invoke"/> 与 <see cref="ComputerAction.Click"/> 不是同义词：
    /// 前者是"控件语义点击"（UIA，与窗口位置无关），后者是"在这个像素上按一下"（Input，依赖观察）。
    /// 允许用 Input 表达 Invoke 会让上层失去语义信息。
    /// </para>
    /// </summary>
    public static bool IsSupported(ComputerAction action, ComputerBackend backend) => action switch
    {
        ComputerAction.Invoke
            or ComputerAction.SetValue
            or ComputerAction.Select
            or ComputerAction.Toggle
            or ComputerAction.Scroll => backend == ComputerBackend.Uia,

        ComputerAction.Click
            or ComputerAction.DoubleClick
            or ComputerAction.RightClick
            or ComputerAction.Move
            or ComputerAction.TypeText
            or ComputerAction.Press => backend == ComputerBackend.Input,

        // 首版明确不支持：两侧都不成立，调用方应报 unsupported 而不是"尽力而为"。
        ComputerAction.Drag => false,

        _ => false,
    };

    /// <summary>该动作唯一可用的后端；两侧都不支持时返回 null。</summary>
    public static ComputerBackend? SupportedBackend(ComputerAction action) =>
        IsSupported(action, ComputerBackend.Uia) ? ComputerBackend.Uia
        : IsSupported(action, ComputerBackend.Input) ? ComputerBackend.Input
        : null;

    /// <summary>
    /// 空文本的语义：<see cref="ComputerAction.SetValue"/> 传空串是**清空**，
    /// 而 <see cref="ComputerAction.TypeText"/> 传空串是 **no-op**。
    /// <para>
    /// 这个区分值得单独一个方法，因为两者都返回成功：把"插入空串"当成"清空"，
    /// 结果就是该清空的没清空、不该动的被清空，而且测试如果只断言"调用成功"会全绿。
    /// </para>
    /// </summary>
    public static bool EmptyTextClearsContent(ComputerAction action) =>
        action == ComputerAction.SetValue;

    /// <summary>该动作是否会改变目标状态（用于回执里判断"是否需要后置验证"）。</summary>
    public static bool MutatesTarget(ComputerAction action) => action switch
    {
        ComputerAction.Move => false,
        ComputerAction.Drag => false,
        _ => true,
    };
}
