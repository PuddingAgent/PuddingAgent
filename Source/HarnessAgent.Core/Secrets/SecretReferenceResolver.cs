using System.Text.RegularExpressions;

namespace HarnessAgent.Core.Secrets;

/// <summary>
/// 解析配置里的密钥引用：<c>${secret:llm.deepseek.apiKey}</c>。
///
/// <para>只有"引用"这一种写法能让配置不存明文，所以这里有两条刻意从严的语义：</para>
/// <list type="number">
/// <item><b>引用必须占满整个值</b>。不做局部插值——<c>prefix-${secret:k}-suffix</c> 会被拒绝，
/// 而不是被当成普通字符串传下去。局部插值会让日志、异常消息里出现半截密钥。</item>
/// <item><b>看起来像引用但格式不对，一律拒绝</b>，绝不把字面量原样返回。
/// 否则这个值会以"一个不合法的凭据"的形式流到下游，报出来的错跟真正的病因无关。</item>
/// </list>
///
/// <para>非引用值原样通过，所以这个解析器可以先上线、再逐个迁移配置——
/// 不会因为"还没迁完"而打断现有行为。</para>
/// </summary>
public sealed partial class SecretReferenceResolver
{
    /// <summary>引用前缀。</summary>
    public const string Prefix = "${secret:";

    private readonly SecretStore _store;

    public SecretReferenceResolver(SecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    [GeneratedRegex(@"^\$\{secret:([^}]*)\}$")]
    private static partial Regex WholeValueReference { get; }

    /// <summary>解析一个配置值。非引用值原样返回。</summary>
    public async Task<SecretResolution> ResolveAsync(
        string? rawValue,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return SecretResolution.Plain(rawValue ?? string.Empty);

        var trimmed = rawValue.Trim();
        if (!trimmed.Contains(Prefix, StringComparison.Ordinal))
            return SecretResolution.Plain(rawValue);

        var match = WholeValueReference.Match(trimmed);
        if (!match.Success)
        {
            return SecretResolution.DenyReference(
                "a secret reference must occupy the whole value, e.g. ${secret:llm.provider.apiKey}");
        }

        var key = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(key))
            return SecretResolution.DenyReference("secret reference is missing a key");

        var read = await _store.TryGetAsync(key, ct);
        return read.Found
            ? SecretResolution.Resolved(read.Value!)
            : SecretResolution.DenyReference(
                $"secret '{key}' could not be resolved: {read.Reason}");
    }
}

/// <summary>
/// 解析结果。
/// <para><see cref="IsReference"/> 为 false 时表示"这不是引用"，<see cref="Value"/> 就是原值。</para>
/// <para><see cref="Found"/> 为 false 时表示"是引用但解不开"，此时<strong>没有可用的值</strong>。</para>
/// </summary>
public readonly record struct SecretResolution(
    bool IsReference,
    bool Found,
    string? Value,
    string? Reason)
{
    public static SecretResolution Plain(string value) => new(false, true, value, null);

    public static SecretResolution Resolved(string value) => new(true, true, value, null);

    public static SecretResolution DenyReference(string reason) => new(true, false, null, reason);

    /// <summary>
    /// 取出值，或在不可用时抛出。<see cref="SecretResolution.DenyReference"/> 的结果
    /// 在这里必须变成异常——静默返回 null 会让调用方拿着空凭据去连服务器。
    /// </summary>
    public string RequireValue() =>
        Found && Value is not null
            ? Value
            : throw new InvalidOperationException(Reason ?? "secret could not be resolved");
}
