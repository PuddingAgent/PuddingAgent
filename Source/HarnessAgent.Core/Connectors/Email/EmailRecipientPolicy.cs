namespace HarnessAgent.Core.Connectors.Email;

/// <summary>
/// 收件人白名单。
///
/// <para>邮箱渠道一旦存在，模型就能给任意地址发信——包括把本机内容发到外部。
/// 这和飞书文件投递是同一类风险，但邮箱更危险：飞书的收件人是会话绑定的，
/// 而邮箱的收件人是一个字符串参数，可以被提示词注入直接改写。</para>
///
/// <para>因此判定放在发信的必经之路上，且**默认只允许显式登记的地址**：
/// 没有白名单就不发信，而不是「默认全允许、需要时再收紧」。后者在实践中从不生效。</para>
/// </summary>
public sealed class EmailRecipientPolicy
{
    /// <summary>覆盖默认名单的环境变量（分号或逗号分隔的邮箱地址）。</summary>
    public const string OverrideVariable = "PUDDING_EMAIL_RECIPIENTS";

    /// <summary>默认允许的收件人。改这里等于扩大可发范围，须谨慎。</summary>
    public static IReadOnlyList<string> DefaultRecipients { get; } =
        ["1519481987@qq.com"];

    /// <summary>允许的收件人（已规范化：小写、去空白）。</summary>
    public IReadOnlyList<string> AllowedRecipients { get; }

    public EmailRecipientPolicy(IEnumerable<string> allowedRecipients)
    {
        ArgumentNullException.ThrowIfNull(allowedRecipients);
        var list = new List<string>();
        foreach (var candidate in allowedRecipients)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var normalized = candidate.Trim().ToLowerInvariant();
            if (!list.Contains(normalized, StringComparer.Ordinal))
                list.Add(normalized);
        }

        AllowedRecipients = list;
    }

    /// <summary>
    /// 由覆盖值解析出策略：覆盖值非空时**替换**默认名单（不是追加），
    /// 便于需要精确控制可发范围时不让默认地址悄悄留着。
    /// </summary>
    public static EmailRecipientPolicy Resolve(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
            return new EmailRecipientPolicy(DefaultRecipients);

        var parsed = overrideValue
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .ToArray();
        return parsed.Length == 0
            ? new EmailRecipientPolicy(DefaultRecipients)
            : new EmailRecipientPolicy(parsed);
    }

    /// <summary>判定一个收件地址能否使用。不抛异常。</summary>
    public EmailRecipientDecision Evaluate(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return EmailRecipientDecision.Deny("recipient is required");

        // 换行与回车必须单独拦：它们能把后续内容顶成新的邮件头
        // （Bcc: 注入就是这样做的），而 RFC 5322 的地址本身不含这两个字符。
        if (address.IndexOfAny(['\r', '\n']) >= 0)
            return EmailRecipientDecision.Deny("recipient contains a line break");

        var normalized = address.Trim().ToLowerInvariant();

        var at = normalized.IndexOf('@');
        if (at <= 0 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1)
            return EmailRecipientDecision.Deny("recipient is not a single mail address");

        if (normalized.Contains(' ')
            || normalized.Contains('<')
            || normalized.Contains('>')
            || normalized.Contains(','))
        {
            return EmailRecipientDecision.Deny(
                "recipient must be a bare address without display name or list");
        }

        if (!AllowedRecipients.Contains(normalized, StringComparer.Ordinal))
            return EmailRecipientDecision.Deny("recipient is not in the allow list");

        return EmailRecipientDecision.Allow(normalized);
    }
}

/// <summary>收件判定结果。<see cref="NormalizedAddress"/> 仅在允许时有值。</summary>
public readonly record struct EmailRecipientDecision(
    bool Allowed,
    string? NormalizedAddress,
    string? Reason)
{
    public static EmailRecipientDecision Allow(string address) =>
        new(true, address, null);

    public static EmailRecipientDecision Deny(string reason) =>
        new(false, null, reason);
}
