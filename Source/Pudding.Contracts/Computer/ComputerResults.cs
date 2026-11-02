namespace Pudding.Contracts.Computer;

/// <summary>
/// 端口返回结果：成功带值，失败带稳定错误码，且**失败可以携带部分值**。
/// <para>
/// 为什么用 <c>sealed class</c> 而不是 <c>record struct</c>：
/// struct 的 <c>default</c> 会造出一个「既非成功也非失败」的第三态 ——
/// <c>IsSuccess</c> 为 false 但 <c>ErrorCode</c> 为 null，调用方两边都读不到东西。
/// 设计里那句「私有构造器 + Success/Failure 工厂」正是在防这个，而 class 的 <c>default</c> 是 null，
/// 天然没有第三态。
/// </para>
/// <para>
/// <see cref="PartialValue"/> 存在的理由很具体：动作执行到一半失败时（<c>partial_input</c>），
/// 回执必须能把「已提交多少个输入」带回来。丢掉它就等于把"部分生效"退化成"完全失败"，
/// 调用方会因此重发，造成重复副作用。
/// </para>
/// </summary>
public sealed class ComputerResult<T>
{
    private ComputerResult(T? value, string? errorCode, T? partialValue)
    {
        Value = value;
        ErrorCode = errorCode;
        PartialValue = partialValue;
    }

    /// <summary>成功时的值；失败时为 null。</summary>
    public T? Value { get; }

    /// <summary>失败时的稳定错误码（见 <see cref="ComputerErrorCodes"/>）；成功时为 null。</summary>
    public string? ErrorCode { get; }

    /// <summary>失败时可能已产生的部分结果（例如已提交的输入数量）。</summary>
    public T? PartialValue { get; }

    public bool IsSuccess => ErrorCode is null;

    public static ComputerResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(value));
        return new ComputerResult<T>(value, null, default);
    }

    /// <summary>失败。可选携带部分值，用于保留"已做了一半"的事实。</summary>
    public static ComputerResult<T> Failure(string errorCode, T? partialValue = default)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Failure must carry a stable error code.", nameof(errorCode));
        return new ComputerResult<T>(default, errorCode, partialValue);
    }

    /// <summary>取成功值，失败时抛出——只在已确认 <see cref="IsSuccess"/> 后使用。</summary>
    public T RequireValue() => IsSuccess && Value is not null
        ? Value
        : throw new InvalidOperationException($"Result is not successful (errorCode={ErrorCode ?? "<null>"}).");

    public override string ToString() =>
        IsSuccess ? $"Success({Value})" : $"Failure({ErrorCode}, partial={(PartialValue is null ? "no" : "yes")})";
}
