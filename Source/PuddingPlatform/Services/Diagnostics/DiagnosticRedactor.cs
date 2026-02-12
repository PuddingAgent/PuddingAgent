using PuddingCode.Diagnostics;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 诊断数据脱敏默认实现。
/// 脱敏规则：
///   - RedactText: 截断超过 500 字符的文本，替换为 "…[truncated]"
///   - RedactMetadata: 过滤敏感 key（key/token/secret/authorization/password），value 替换为 "***REDACTED***"
/// </summary>
public class DiagnosticRedactor : IDiagnosticRedactor
{
    private const int MaxTextLength = 500;
    private const string TruncatedSuffix = "…[truncated]";
    private const string RedactedValue = "***REDACTED***";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "api_key",
        "token", "access_token", "refresh_token", "bearer_token",
        "secret", "client_secret", "app_secret",
        "password", "passphrase", "pwd",
        "authorization", "auth",
    };

    /// <inheritdoc/>
    public string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.Length > MaxTextLength)
            return value[..MaxTextLength] + TruncatedSuffix;

        return value;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> RedactMetadata(
        IReadOnlyDictionary<string, string> metadata)
    {
        var result = new Dictionary<string, string>(metadata.Count);

        foreach (var (key, value) in metadata)
        {
            if (IsSensitiveKey(key))
            {
                result[key] = RedactedValue;
            }
            else
            {
                result[key] = RedactText(value);
            }
        }

        return result;
    }

    /// <summary>判断 key 是否属于敏感字段。</summary>
    private static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        foreach (var sensitive in SensitiveKeys)
        {
            if (key.Equals(sensitive, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
