using System.Globalization;

namespace Pudding.Contracts;

/// <summary>
/// Desktop 实例身份：按 DesktopHome/DataRoot 隔离的产品实例标识，<b>不是</b>进程 ID。
/// 同一次启动的进程实例 ID 由 <see cref="DesktopProcessInstanceId"/> 表达，两者不可混用。
/// </summary>
public sealed record DesktopInstanceId
{
    public const int MaxLength = 128;

    public DesktopInstanceId(string value) => Value = ContractText.RequireIdentifier(value, MaxLength, "desktopId");

    public string Value { get; }

    public static bool IsValid(string? value) => ContractText.IsIdentifier(value, MaxLength);

    public static DesktopInstanceId Parse(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>单次启动的进程实例 ID；断连重连时用于识别「Core 是否换了实例」。</summary>
public sealed record DesktopProcessInstanceId
{
    public const int MaxLength = 128;

    public DesktopProcessInstanceId(string value) => Value = ContractText.RequireIdentifier(value, MaxLength, "processInstanceId");

    public string Value { get; }

    public static bool IsValid(string? value) => ContractText.IsIdentifier(value, MaxLength);

    public static DesktopProcessInstanceId Parse(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// 单次能力调用的操作 ID。由调用方（Core Broker）产生，Desktop 用它建立待完成表并做幂等复用。
/// 同连接同 ID 的重复命令复用已有任务或终态缓存；相同 ID 不同 payload 必须拒绝。
/// </summary>
public sealed record OperationId
{
    public const int MaxLength = 64;

    public OperationId(string value) => Value = ContractText.RequireIdentifier(value, MaxLength, "operationId");

    public string Value { get; }

    public static bool IsValid(string? value) => ContractText.IsIdentifier(value, MaxLength);

    public static OperationId Parse(string value) => new(value);

    /// <summary>生成一个新的操作 ID（十六进制，满足标识符字符集）。</summary>
    public static OperationId NewId() => new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public override string ToString() => Value;
}

/// <summary>业务关联 ID（例如发起该调用的聊天/工具调用），与操作 ID 不同，可跨多个操作复现同一个业务上下文。</summary>
public sealed record DesktopCorrelationId
{
    public const int MaxLength = 128;

    public DesktopCorrelationId(string value) => Value = ContractText.RequireIdentifier(value, MaxLength, "correlationId");

    public string Value { get; }

    public static bool IsValid(string? value) => ContractText.IsIdentifier(value, MaxLength);

    public static DesktopCorrelationId Parse(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// 连接世代号。由 Core 在握手回执中分配，每次重新握手递增。
/// 旧世代的命令与结果必须失效：Desktop 不能执行旧世代命令，也不能让旧世代结果完成新世代命令。
/// <see cref="None"/> 表示尚未握手成功。
/// </summary>
public readonly record struct ConnectionGeneration(long Value)
{
    public static readonly ConnectionGeneration None = new(0);

    public bool IsLive => Value > 0;

    /// <summary>由 Core 回执的值构造；0 与负数不是合法世代。</summary>
    public static ConnectionGeneration Require(long value) => value > 0
        ? new ConnectionGeneration(value)
        : throw new ArgumentOutOfRangeException(nameof(value), value, "Connection generation must be a positive value assigned by Core.");

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 契约文本校验。标识符限定可见 ASCII（系统产生，不接受用户文本）；
/// 展示文本允许 CJK，只限制长度并剔除控制字符。
/// </summary>
internal static class ContractText
{
    public static bool IsIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var allowed = c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or ':';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    public static string RequireIdentifier(string? value, int maxLength, string parameterName) =>
        IsIdentifier(value, maxLength)
            ? value!
            : throw new ArgumentException(
                $"Value must be 1..{maxLength} characters from [A-Za-z0-9._:-].", parameterName);

    /// <summary>
    /// 展示文本（通知标题/正文等）：允许 CJK，剔除控制字符；
    /// <b>超长显式取红</b>（区别于诊断消息的尽力截断），避免调用方在不知道的情况下丢内容。
    /// </summary>
    public static string RequireDisplayText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Value must be non-empty and at most {maxLength} characters.", parameterName);
        }

        if (value.Trim().Length > maxLength)
        {
            throw new ArgumentException($"Value must be at most {maxLength} characters.", parameterName);
        }

        return NormalizeDisplayText(value, maxLength);
    }

    /// <summary>剔除控制字符、修剪空白并截断到上限；空输入返回空串。</summary>
    public static string NormalizeDisplayText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var capacity = Math.Min(value.Length, maxLength);
        var buffer = new char[capacity];
        var length = 0;
        foreach (var c in value)
        {
            if (length == capacity)
            {
                break;
            }

            buffer[length++] = char.IsControl(c) ? ' ' : c;
        }

        return new string(buffer, 0, length).Trim();
    }
}
