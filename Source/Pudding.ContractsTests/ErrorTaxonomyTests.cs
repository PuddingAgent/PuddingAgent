using Pudding.Contracts;

namespace Pudding.ContractsTests;

/// <summary>
/// 错误码 ↔ 线名与默认语义是跨进程契约，冻结为快照。
/// 未知线名必须确定性地 fail closed，而不是被当作成功或新码。
/// </summary>
public sealed class ErrorTaxonomyTests
{
    [Fact]
    public void WireNames_AreFrozenSnapshot()
    {
        var actual = DesktopCapabilityErrorCodes.All.Select(DesktopCapabilityErrorCodes.NameOf).ToArray();

        string[] expected =
        [
            "unsupported_capability",
            "not_connected",
            "disconnected",
            "unauthorized",
            "invalid_target",
            "page_version_mismatch",
            "paused",
            "user_takeover",
            "deadline_exceeded",
            "cancelled",
            "outcome_unknown",
            "resource_exhausted",
            "ui_unavailable",
            "invalid_request",
            "internal_error",
        ];

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NameOf_RejectsUnregisteredCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopCapabilityErrorCodes.NameOf((DesktopCapabilityErrorCode)1234));
    }

    [Fact]
    public void TryParse_RoundTripsEveryRegisteredCode()
    {
        foreach (var code in DesktopCapabilityErrorCodes.All)
        {
            Assert.True(DesktopCapabilityErrorCodes.TryParse(DesktopCapabilityErrorCodes.NameOf(code), out var parsed));
            Assert.Equal(code, parsed);
        }

        Assert.False(DesktopCapabilityErrorCodes.TryParse("not_a_code", out _));
        Assert.False(DesktopCapabilityErrorCodes.TryParse(null, out _));
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, DesktopCapabilityErrorCodes.ParseOrInternalError("brand_new_code"));
    }

    [Fact]
    public void DefaultSemantics_AreFrozenSnapshot()
    {
        var actual = DesktopCapabilityErrorCodes.All
            .Select(code => $"{code}|retry={DesktopCapabilityErrorCodes.DefaultRetryable(code)}|side={DesktopCapabilityErrorCodes.DefaultMayHaveSideEffects(code)}")
            .ToArray();

        string[] expected =
        [
            "UnsupportedCapability|retry=False|side=False",
            "NotConnected|retry=True|side=False",
            "Disconnected|retry=True|side=False",
            "Unauthorized|retry=False|side=False",
            "InvalidTarget|retry=False|side=False",
            "PageVersionMismatch|retry=True|side=False",
            "Paused|retry=True|side=False",
            "UserTakeover|retry=True|side=False",
            "DeadlineExceeded|retry=True|side=False",
            "Cancelled|retry=True|side=False",
            "OutcomeUnknown|retry=False|side=True",
            "ResourceExhausted|retry=True|side=False",
            "UiUnavailable|retry=True|side=False",
            "InvalidRequest|retry=False|side=False",
            "InternalError|retry=False|side=True",
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsSafeToRetry_ExcludesSideEffectingErrors()
    {
        Assert.True(DesktopCapabilityError.Disconnected("channel closed").IsSafeToRetry);
        Assert.False(DesktopCapabilityError.OutcomeUnknown("lost result").IsSafeToRetry);
        Assert.False(DesktopCapabilityError.Internal("boom").IsSafeToRetry);
        Assert.False(DesktopCapabilityError.Unauthorized("no").IsSafeToRetry);
    }

    [Fact]
    public void DeadlineAndCancel_CanBeMarkedAsPossiblyExecuted()
    {
        var expiredBeforeStart = DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false);
        Assert.False(expiredBeforeStart.MayHaveSideEffects);
        Assert.True(expiredBeforeStart.Retryable);

        var expiredWhileRunning = DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true);
        Assert.True(expiredWhileRunning.MayHaveSideEffects);
        Assert.False(expiredWhileRunning.IsSafeToRetry);

        var cancelledInFlight = DesktopCapabilityError.Cancelled(mayHaveSideEffects: true);
        Assert.True(cancelledInFlight.MayHaveSideEffects);
        Assert.Equal(DesktopCapabilityErrorCode.Cancelled, cancelledInFlight.Code);
    }

    [Fact]
    public void Message_IsSanitizedAndNeverEmpty()
    {
        var multiLine = new DesktopCapabilityError(DesktopCapabilityErrorCode.Paused, "line1\nline2\ttab");
        Assert.Equal("line1 line2 tab", multiLine.Message);

        var empty = new DesktopCapabilityError(DesktopCapabilityErrorCode.UiUnavailable);
        Assert.Equal("ui_unavailable", empty.Message);

        var longMessage = new DesktopCapabilityError(DesktopCapabilityErrorCode.InternalError, new string('x', 900));
        Assert.Equal(DesktopCapabilityError.MaxMessageLength, longMessage.Message.Length);
    }

    [Fact]
    public void Constructor_RejectsUnregisteredCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopCapabilityError((DesktopCapabilityErrorCode)77, "x"));
    }

    [Fact]
    public void WireCodeAndToString_ExposeNoPayload()
    {
        var error = DesktopCapabilityError.UnsupportedCapability("shell.dialog");
        Assert.Equal("unsupported_capability", error.WireCode);
        Assert.Contains("unsupported_capability", error.ToString(), StringComparison.Ordinal);
    }
}
