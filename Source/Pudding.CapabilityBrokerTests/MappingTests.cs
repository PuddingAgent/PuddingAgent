using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

/// <summary>Core 侧的编码/解码/指纹：与 Desktop 侧互为逆映射，两端各自有往返测试。</summary>
public sealed class MappingTests
{
    private static readonly OperationId Operation = new("op-1");

    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desk-1"), Operation, DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public void Encoder_ProducesTheExpectedPayloadPerCapability()
    {
        var navigate = CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(3),
            FakeDescriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityRequest.ForNavigate(new NavigateRequest(
                BrokerHarness.Target, new Uri("https://example.com/a"), DesktopPageVersion.Require(2))),
            Call,
            "trace-1");

        Assert.Equal(Proto.CapabilityCommand.PayloadOneofCase.Navigate, navigate.PayloadCase);
        Assert.Equal("op-1", navigate.OperationId);
        Assert.Equal(3UL, navigate.Generation);
        Assert.Equal("trace-1", navigate.TraceId);
        Assert.Equal("ctx-1", navigate.Navigate.Target.ContextId);
        Assert.Equal(2, navigate.Navigate.ExpectedPageVersion);
        Assert.Equal(Call.DeadlineUtc, navigate.Deadline.ToDateTimeOffset());

        var javascript = CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(1),
            FakeDescriptor(DesktopCapability.WebViewExecuteJavascript),
            DesktopCapabilityRequest.ForJavascript(new JavascriptRequest(BrokerHarness.Target, "return 1;", default, 4096)),
            Call,
            null);
        Assert.Equal(Proto.CapabilityCommand.PayloadOneofCase.ExecuteJavascript, javascript.PayloadCase);
        Assert.Equal(4096u, javascript.ExecuteJavascript.MaxResultBytes);

        var notification = CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(1),
            FakeDescriptor(DesktopCapability.ShellNotification),
            DesktopCapabilityRequest.ForNotification(new DesktopNotificationRequest("标题", "内容", DesktopNotificationPriority.High)),
            Call,
            null);
        Assert.Equal(Proto.CapabilityCommand.PayloadOneofCase.ShowNotification, notification.PayloadCase);
        Assert.Equal(Proto.NotificationPriority.High, notification.ShowNotification.Priority);

        var pageState = CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(1),
            FakeDescriptor(DesktopCapability.WebViewPageState),
            DesktopCapabilityRequest.ForPageState(BrokerHarness.Target),
            Call,
            null);
        Assert.Equal(Proto.CapabilityCommand.PayloadOneofCase.GetPageState, pageState.PayloadCase);
        Assert.Equal("page-1", pageState.GetPageState.Target.PageId);
    }

    [Fact]
    public void Encoder_RejectsRequestWithoutMatchingPayload()
    {
        Assert.Throws<ArgumentException>(() => CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(1),
            FakeDescriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityRequest.ForNotification(BrokerHarness.Notification()),
            Call,
            null));
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameRequestAndSensitiveToPayloadChanges()
    {
        var descriptor = FakeDescriptor(DesktopCapability.WebViewNavigate);
        var baseline = Fingerprint("https://example.com/a");

        Assert.Equal(baseline, Fingerprint("https://example.com/a"));
        Assert.NotEqual(baseline, Fingerprint("https://example.com/b"));

        // 不同的 deadline 不改变指纹（重试时 deadline 必然不同，但 payload 相同）。
        var laterCall = new DesktopCallContext(
            new DesktopInstanceId("desk-1"), Operation, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.NotEqual(Call.DeadlineUtc, laterCall.DeadlineUtc);
        Assert.Equal(baseline, Fingerprint("https://example.com/a"));

        static string Fingerprint(string url) => CoreCommandEncoder.Fingerprint(
            FakeDescriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityRequest.ForNavigate(BrokerHarness.Navigate(url)));
    }

    [Fact]
    public void Decoder_MapsEveryErrorCodeWithItsSemantics()
    {
        foreach (var code in DesktopCapabilityErrorCodes.All)
        {
            var outcome = new Proto.OperationResult
            {
                OperationId = "op-1",
                Generation = 1,
                Error = new Proto.ErrorOutcome
                {
                    Code = DesktopCapabilityErrorCodes.NameOf(code),
                    Message = "msg",
                    Retryable = true,
                    MayHaveSideEffects = true,
                },
            };

            var decoded = DesktopResultDecoder.Decode(outcome, DesktopCapability.WebViewNavigate, BrokerHarness.Target);

            Assert.True(decoded.IsFailure);
            Assert.Equal(code, decoded.Error.Code);
            Assert.True(decoded.Error.Retryable);
            Assert.True(decoded.Error.MayHaveSideEffects);
        }
    }

    [Fact]
    public void Decoder_RejectsMismatchedOutcomeAndEmptyOutcome()
    {
        var mismatched = DesktopResultDecoder.Decode(
            DesktopFrames.NavigateOk("op-1", 1), DesktopCapability.WebViewExecuteJavascript, BrokerHarness.Target);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, mismatched.Error.Code);

        var empty = DesktopResultDecoder.Decode(new Proto.OperationResult { OperationId = "op-1" },
            DesktopCapability.WebViewNavigate, BrokerHarness.Target);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, empty.Error.Code);

        var unspecifiedDisposition = new Proto.OperationResult
        {
            OperationId = "op-1",
            Navigate = new Proto.NavigateOutcome { Disposition = Proto.NavigateDisposition.Unspecified },
        };
        Assert.Equal(
            DesktopCapabilityErrorCode.InternalError,
            DesktopResultDecoder.Decode(unspecifiedDisposition, DesktopCapability.WebViewNavigate, BrokerHarness.Target).Error.Code);
    }

    [Fact]
    public void Decoder_RequiresTheRequestedTargetForPageStateResults()
    {
        var decoded = DesktopResultDecoder.Decode(
            DesktopFrames.PageStateOk("op-1", 1), DesktopCapability.WebViewPageState, requestedTarget: null);

        Assert.Equal(DesktopCapabilityErrorCode.InternalError, decoded.Error.Code);
    }

    [Fact]
    public void Readiness_NamesAreFrozenAndUnknownFoldsToUnknown()
    {
        var names = Enum.GetValues<DesktopPageReadiness>()
            .Select(PageReadinessWire.NameOf)
            .ToArray();

        Assert.Equal(["unknown", "loading", "interactive", "complete", "failed"], names);
        Assert.Equal(DesktopPageReadiness.Interactive, PageReadinessWire.Parse("interactive"));
        Assert.Equal(DesktopPageReadiness.Unknown, PageReadinessWire.Parse("hibernated"));
        Assert.Equal(DesktopPageReadiness.Unknown, PageReadinessWire.Parse(null));
    }

    private static DesktopCapabilityDescriptor FakeDescriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);
}
