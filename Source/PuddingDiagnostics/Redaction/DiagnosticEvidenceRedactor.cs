using System.Text.RegularExpressions;

namespace PuddingCode.Diagnostics;

/// <summary>
/// 证据脱敏（可诊断基础设施设计 §5.4）。
/// <para>
/// 设计要点（针对既有 <c>PuddingPlatform.Services.Diagnostics.DiagnosticRedactor</c> 的实测缺口）：
/// ① 键名用**规范化后的子串匹配**，而不是整串相等——<c>llm.apiKey</c>、<c>authorization_header</c>、
/// <c>myToken</c> 都必须命中；
/// ② 值也要过一遍「看起来像密钥」的形态检查（非敏感键里夹带密钥时仍然拦截）；
/// ③ URL 查询串一律剥离（query 里常见 token）。
/// </para>
/// </summary>
public static class DiagnosticEvidenceRedactor
{
    public const string RedactedValue = "***REDACTED***";
    public const string TruncatedSuffix = "…[truncated]";
    public const int MaxValueChars = 512;

    /// <summary>敏感键名的规范化子串（小写、仅字母数字）。</summary>
    private static readonly string[] SensitiveKeyTokens =
    [
        "apikey", "token", "secret", "password", "passphrase", "pwd", "authorization",
        "credential", "cookie", "privatekey", "accesskey", "signature",
    ];

    private static readonly Regex KeyNormalizer = new("[^a-z0-9]", RegexOptions.Compiled);

    private static readonly Regex SecretValue = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,}|jv_live_[A-Za-z0-9_\-]{8,}|ark-[0-9a-f\-]{16,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>键名是否敏感（子串匹配，忽略大小写与非字母数字分隔）。</summary>
    public static bool ShouldRedactKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = KeyNormalizer.Replace(key.ToLowerInvariant(), string.Empty);
        return SensitiveKeyTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>值是否看起来像密钥。</summary>
    public static bool LooksLikeSecret(string? value)
        => !string.IsNullOrEmpty(value) && SecretValue.IsMatch(value);

    /// <summary>脱敏 + 截断 + URL query 剥离；输出永远是单行有界字符串。</summary>
    public static string Sanitize(string? key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (ShouldRedactKey(key) || LooksLikeSecret(value))
            return RedactedValue;

        var sanitized = StripUrlQuery(value);
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ');

        return sanitized.Length <= MaxValueChars
            ? sanitized
            : sanitized[..MaxValueChars] + TruncatedSuffix;
    }

    private static string StripUrlQuery(string value)
    {
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
            return value;

        var queryIndex = value.IndexOf('?', schemeIndex);
        return queryIndex < 0 ? value : value[..queryIndex] + "?<redacted>";
    }
}
