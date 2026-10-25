namespace PuddingCode.Platform;

/// <summary>
/// Agent 级思考强度的读写端口：<c>/effort</c> 命令与聊天面板下拉共用同一份存储，
/// 从而「飞书里切到 max，回到桌面也显示极致」只有一个真源。
///
/// 为什么是窄接口而不是直接注入 Agent 文件服务：
/// ① 命令处理器只需要「读当前值 / 写一个字段」两件事，注入整个文件服务会把
///    创建、删除、冻结等写能力一起带进一个纯文本命令入口；
/// ② 命令用例可以在不准备真实数据目录的前提下断言行为。
/// </summary>
public interface IAgentReasoningEffortStore
{
    /// <summary>
    /// 读取该 Agent 的显式档位；未配置或已清除时返回 <c>null</c>（= 跟随模型默认）。
    /// </summary>
    Task<string?> GetReasoningEffortAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default);

    /// <summary>
    /// 写入该 Agent 的显式档位，返回写入后的值（<c>null</c> = 已清除，回到模型默认）。
    /// 非法档位抛 <see cref="ArgumentException"/> 而不是静默回落：
    /// 静默会让用户以为改了档位、实际仍按默认跑。
    /// </summary>
    Task<string?> SetReasoningEffortAsync(
        string workspaceId,
        string agentId,
        string? effort,
        CancellationToken ct = default);
}
