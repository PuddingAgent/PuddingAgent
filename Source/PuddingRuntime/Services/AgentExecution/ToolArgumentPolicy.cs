namespace PuddingRuntime.Services;

/// <summary>
/// 工具参数在派发前的取值策略。
/// </summary>
/// <remarks>
/// 历史上派发前会对整段 <c>ArgumentsJson</c> 调用 <c>IKeyVaultService.InjectAsync</c>，
/// 把参数里出现的 <c>{{vault:NAME}}</c> 就地换成明文。这条路径没有任何消费方：
/// 所有需要凭据的工具（搜索、日历、余额探针、embedding、DeepSeek Web）都从
/// <c>config/*.json</c> 读取并自行调用 <c>CredentialReference.ResolveAsync</c>，
/// 也没有任何工具声明凭据类参数。它的唯一可观测效果是把明文密钥送进
/// <c>file_write</c> / <c>shell</c> 等工具 —— 前者会落盘，后者会进命令行。
/// 因此这里明确表示：工具参数一律原样派发，替换只在工具自己的凭据读取边界内发生。
/// </remarks>
public static class ToolArgumentPolicy
{
    /// <summary>空参数统一成空 JSON 对象，其余原样返回（不做任何凭据展开）。</summary>
    public static string ForDispatch(string? argumentsJson)
        => string.IsNullOrEmpty(argumentsJson) ? "{}" : argumentsJson;
}
