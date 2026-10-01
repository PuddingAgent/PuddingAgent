using Pudding.Contracts;

namespace Pudding.ContractsTests;

public sealed class CapabilityResultTests
{
    [Fact]
    public void Success_CarriesValueAndNoError()
    {
        var result = CapabilityResult<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.True(result.IsInitialized);
        Assert.Equal(42, result.Value);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(42, value);
        Assert.Equal(42, result.ValueOr(0));
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_CarriesErrorAndNoValue()
    {
        var error = DesktopCapabilityError.Unauthorized("nope");
        var result = CapabilityResult<string>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.True(result.IsInitialized);
        Assert.Same(error, result.Error);
        Assert.False(result.TryGetValue(out var value));
        Assert.Null(value);
        Assert.Equal("fallback", result.ValueOr("fallback"));
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void DefaultStruct_IsUninitializedAndFailsLoudly()
    {
        var result = default(CapabilityResult<int>);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.False(result.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_RejectsNullError()
    {
        Assert.Throws<ArgumentNullException>(() => CapabilityResult<int>.Failure(null!));
    }

    [Fact]
    public void Map_PropagatesFailureWithoutInvokingSelector()
    {
        var result = CapabilityResult<int>.Failure(DesktopCapabilityError.Internal("boom"));
        var invoked = false;

        var mapped = result.Map(v =>
        {
            invoked = true;
            return v.ToString();
        });

        Assert.False(invoked);
        Assert.True(mapped.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, mapped.Error.Code);
    }

    [Fact]
    public void Map_TransformsSuccess()
    {
        var mapped = CapabilityResult<int>.Success(2).Map(v => v * 3);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(6, mapped.Value);
    }

    [Fact]
    public void ToString_IsSafeForLogs()
    {
        Assert.Equal("Success(7)", CapabilityResult<int>.Success(7).ToString());
        Assert.Equal("Failure(unauthorized)", CapabilityResult<int>.Failure(DesktopCapabilityError.Unauthorized("x")).ToString());
        Assert.Equal("Failure(uninitialized)", default(CapabilityResult<int>).ToString());
    }
}
