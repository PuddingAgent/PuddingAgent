using System.Diagnostics.CodeAnalysis;

namespace Pudding.Contracts;

/// <summary>
/// 能力调用结果：成功携带类型化输出，失败携带结构化领域错误。
///
/// 之所以不用异常表达业务失败：跨进程/跨流的失败是<b>协议语义</b>（需要区分
/// 「未执行」「可能已产生副作用」「可重试」），异常会丢语义且容易被当成通道故障。
///
/// <c>default(CapabilityResult&lt;T&gt;)</c> 不是有效结果：<see cref="IsSuccess"/> 为 false，
/// 访问 <see cref="Error"/> 会取红，用于抓住「忘记赋值」的代码路径。
/// </summary>
public readonly struct CapabilityResult<T>
{
    private readonly T? _value;
    private readonly DesktopCapabilityError? _error;
    private readonly bool _isSuccess;

    private CapabilityResult(T value)
    {
        _value = value;
        _error = null;
        _isSuccess = true;
    }

    private CapabilityResult(DesktopCapabilityError error)
    {
        _value = default;
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _isSuccess = false;
    }

    public bool IsSuccess => _isSuccess;

    public bool IsFailure => !_isSuccess;

    /// <summary>未初始化（<c>default</c>）时取红，避免把「忘记赋值」读成空错误。</summary>
    public bool IsInitialized => _isSuccess || _error is not null;

    public DesktopCapabilityError Error => _error
        ?? throw new InvalidOperationException(
            "Result is not a failure (or is uninitialized); check IsSuccess/IsInitialized before reading Error.");

    public T Value => _isSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({_error?.WireCode ?? "uninitialized"}); no value is available.");

    public static CapabilityResult<T> Success(T value) => new(value);

    public static CapabilityResult<T> Failure(DesktopCapabilityError error) => new(error);

    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = _value;
        return _isSuccess;
    }

    public T ValueOr(T fallback) => _isSuccess ? _value! : fallback;

    public CapabilityResult<TOther> Map<TOther>(Func<T, TOther> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _isSuccess ? CapabilityResult<TOther>.Success(map(_value!)) : CapabilityResult<TOther>.Failure(Error);
    }

    public override string ToString() =>
        _isSuccess ? $"Success({_value})" : $"Failure({_error?.WireCode ?? "uninitialized"})";
}
