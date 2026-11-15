namespace PuddingCode.SubAgents;

/// <summary>一次地址解析的结果。</summary>
/// <param name="IsParentAlias">输入是否为「发给父代理」的别名。</param>
/// <param name="Address">可直接使用的目标地址；别名无法解析、或输入为空时为 null。</param>
/// <param name="Error">无法投递时的原因，供工具如实回报给调用方。</param>
public sealed record SubAgentAddressResolution(
    bool IsParentAlias,
    string? Address,
    string? Error)
{
    /// <summary>是否可以按此结果投递。</summary>
    public bool CanSend => Address is not null && Error is null;
}

/// <summary>
/// 子代理用 <c>send_message</c> 回信给父代理时的地址解析。
/// <para>
/// 只识别一个别名 <c>agent:parent</c>，由系统解析成具体父代理地址，而<strong>不</strong>让模型
/// 自己填一个 agent id。理由不是省事：模型若幻觉出一个看起来合法的 agent 地址，消息会发给
/// 错误的对端，而调用方从回执上看不出异常——那比「发不出去」更糟。别名经系统解析后，
/// 目标只有「正确的父代理」一种可能。
/// </para>
/// <para>
/// 父代理地址未知时一律返回错误，<strong>不回退</strong>到「发给任意 agent」或「发给父会话」：
/// 投错主体的消息没人能发现，而如实报错至少能被上层看到。
/// </para>
/// </summary>
public static class SubAgentAddressResolver
{
    /// <summary>「发给父代理」的别名，大小写不敏感。</summary>
    public const string ParentAlias = "agent:parent";

    /// <summary>
    /// 解析 <c>send_message</c> 的 <c>to</c> 参数。
    /// </summary>
    /// <param name="rawTo">调用方原样传入的 <c>to</c>。</param>
    /// <param name="parentAgentId">当前子代理的父代理地址；非子代理执行或信息缺失时为 null。</param>
    /// <returns>解析结果。非别名输入原样返回，不改写调用方意图。</returns>
    public static SubAgentAddressResolution Resolve(string? rawTo, string? parentAgentId)
    {
        var trimmed = rawTo?.Trim() ?? string.Empty;
        if (!string.Equals(trimmed, ParentAlias, StringComparison.OrdinalIgnoreCase))
            return new SubAgentAddressResolution(false, trimmed.Length == 0 ? null : trimmed, null);

        if (string.IsNullOrWhiteSpace(parentAgentId))
            return new SubAgentAddressResolution(
                true,
                null,
                $"发送者不知道父代理地址，无法解析 {ParentAlias}。请改为显式 agent 地址，或不要回信。");

        return new SubAgentAddressResolution(true, $"agent:{parentAgentId.Trim()}", null);
    }
}
