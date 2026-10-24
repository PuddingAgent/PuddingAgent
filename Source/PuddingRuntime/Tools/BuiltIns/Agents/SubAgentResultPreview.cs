namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 子代理结果的展示裁剪。
/// <para>
/// 实测 <c>query_sub_agents</c> 的 status 输出曾单次达到 <b>257,101 字符</b>（一次调用约等于
/// 八万 token），原因就是把子代理返回的完整结果原样拼进诊断文本。需要全文的正确做法是走结果
/// 消息或落盘文件，而不是在状态查询里重放一遍。
/// </para>
/// </summary>
public static class SubAgentResultPreview
{
    /// <summary>状态输出里保留的结果字符上限。取值足以看清总结与结论，又不至于把上下文吃掉。</summary>
    public const int MaxPreviewChars = 4000;

    /// <summary>超过上限时保留头部并显式标注总长，避免读者误以为结果只到这里。</summary>
    public static string Condense(string? result)
    {
        if (string.IsNullOrEmpty(result))
            return string.Empty;

        if (result.Length <= MaxPreviewChars)
            return result;

        return result[..MaxPreviewChars]
            + $"\n…[结果已截断：共 {result.Length:N0} 字符，此处仅显示前 {MaxPreviewChars:N0} 字符]";
    }
}
