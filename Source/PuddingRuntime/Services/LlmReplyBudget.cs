namespace PuddingRuntime.Services;

/// <summary>
/// 单轮回复的输出上限（本轮真实申请的 max_tokens）及其窗口预留。
/// 2026-10-23：资源池登记的是「模型最大输出能力」（deepseek-flash = 393,216），此前直接把它
/// 当成预留全额扣减 ⇒ 1,000,000 窗口只剩 605,760 可用输入，39% 被锁在输出侧白等；同时真的把
/// 393,216 当 max_tokens 发出去，服务商按 input+max_tokens 计窗，等于自造超窗。
/// 预留必须等于「本轮真实申请的输出上限」，因此两者共用 <see cref="PerTurnOutputCapTokens"/>。
/// </summary>
internal static class LlmReplyBudget
{
    /// <summary>
    /// 单轮回复上限：≈ 十万量级字符，远超任何单轮答复；产品口径只需改这一处。
    /// 1M 窗口下预留占比 6.4%（旧口径 39.3%）。
    /// </summary>
    internal const int PerTurnOutputCapTokens = 64_000;

    /// <summary>本轮实际申请的 max_tokens：登记值取小者；未登记（null）或 0 原样透传，交给网关默认。</summary>
    internal static int? ResolveRequestTokens(int? registeredMaxOutputTokens)
        => registeredMaxOutputTokens is > 0 ? Math.Min(registeredMaxOutputTokens.Value, PerTurnOutputCapTokens)
            : registeredMaxOutputTokens;

    /// <summary>窗口预留：与 <see cref="ResolveRequestTokens"/> 同口径，未登记/0 视为不预留。</summary>
    internal static int ResolveReservedTokens(int? registeredMaxOutputTokens)
        => Math.Min(Math.Max(0, registeredMaxOutputTokens ?? 0), PerTurnOutputCapTokens);
}
