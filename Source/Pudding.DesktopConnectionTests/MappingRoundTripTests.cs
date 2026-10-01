using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

/// <summary>
/// 领域 ↔ wire 的映射一致性：契约真源（Contracts）与线上真源（proto）不得各自漂移。
/// </summary>
public sealed class MappingRoundTripTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    [Fact]
    public void Hello_UsesContractsProtocolRangeAndDeclaredCapabilities()
    {
        var options = new DesktopConnectionOptions
        {
            DesktopId = DesktopId,
            ProcessInstanceId = new DesktopProcessInstanceId("proc-1"),
            SupportedCapabilities = DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification,
        };

        var frame = DesktopFrameMapping.Hello(
            options, DesktopCapabilities.DeclareFor(options.SupportedCapabilities));

        // 版本区间只有一处真源：Contracts 的常量。proto 侧必须与之一致。
        Assert.Equal((uint)DesktopProtocolVersion.Minimum, frame.Hello.SupportedVersions.Minimum);
        Assert.Equal((uint)DesktopProtocolVersion.Current, frame.Hello.SupportedVersions.Maximum);
        Assert.Equal(
            ["webview.navigate", "shell.notification"],
            frame.Hello.Capabilities.Select(declaration => declaration.Capability).ToArray());
    }

    [Fact]
    public void Decode_NavigateCommand_ProducesDomainRequestAndContext()
    {
        var command = Frames.NavigateCommand(
            "op-1",
            expectedPageVersion: 9,
            traceId: "trace-1",
            correlationId: "call-1",
            deadline: DateTimeOffset.UtcNow.AddSeconds(30)).Command;

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.True(decoded.IsSuccess);
        var value = decoded.Value;
        Assert.Equal("op-1", value.OperationId.Value);
        Assert.Equal(1, value.Generation.Value);
        Assert.Equal(DesktopCapability.WebViewNavigate, value.Capability.Capability);
        Assert.Equal("https://example.com/", value.Request.Navigate!.Url.AbsoluteUri);
        Assert.Equal(9, value.Request.Navigate.ExpectedPageVersion.Value);
        Assert.Equal("ctx-1/page-1", value.Request.Target!.Key);
        Assert.Equal("trace-1", value.TraceId);
        Assert.Equal("call-1", value.Context.CorrelationId!.Value);
        Assert.Equal(TimeSpan.Zero, value.Context.DeadlineUtc.Offset);
        Assert.StartsWith("webview.navigate:", value.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_JavaScriptAndNotification_MapDomainValues()
    {
        var javascript = CoreFrameMapping.Decode(Frames.JavascriptCommand("op-js").Command, DesktopId);
        Assert.True(javascript.IsSuccess);
        Assert.Equal(JavascriptRequest.DefaultMaxResultBytes, javascript.Value.Request.Javascript!.MaxResultBytes);

        var sized = CoreFrameMapping.Decode(
            Frames.JavascriptCommand("op-js", maxResultBytes: 2048).Command, DesktopId);
        Assert.Equal(2048, sized.Value.Request.Javascript!.MaxResultBytes);

        foreach (var (wire, expected) in new (Proto.NotificationPriority Wire, DesktopNotificationPriority Expected)[]
                 {
                     (Proto.NotificationPriority.Unspecified, DesktopNotificationPriority.Normal),
                     (Proto.NotificationPriority.Low, DesktopNotificationPriority.Low),
                     (Proto.NotificationPriority.Normal, DesktopNotificationPriority.Normal),
                     (Proto.NotificationPriority.High, DesktopNotificationPriority.High),
                 })
        {
            var notification = CoreFrameMapping.Decode(
                Frames.NotificationCommand("op-n", priority: wire).Command, DesktopId);
            Assert.Equal(expected, notification.Value.Request.Notification!.Priority);
        }
    }

    [Fact]
    public void Decode_FingerprintIgnoresVolatileFieldsButDetectsPayloadChanges()
    {
        var first = Frames.NavigateCommand("op-1", traceId: "a", deadline: DateTimeOffset.UtcNow.AddSeconds(30)).Command;
        var second = Frames.NavigateCommand("op-1", traceId: "b", deadline: DateTimeOffset.UtcNow.AddSeconds(60)).Command;
        var changed = Frames.NavigateCommand("op-1", url: "https://example.com/other").Command;

        var firstFingerprint = CoreFrameMapping.Decode(first, DesktopId).Value.Fingerprint;

        Assert.Equal(firstFingerprint, CoreFrameMapping.Decode(second, DesktopId).Value.Fingerprint);
        Assert.NotEqual(firstFingerprint, CoreFrameMapping.Decode(changed, DesktopId).Value.Fingerprint);
    }

    [Fact]
    public void Decode_RejectsMalformedInputFailClosed()
    {
        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(null, DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(Frames.Command("bad id", "webview.navigate"), DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(Frames.Command("op-1", "webview.navigate"), DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(
                Frames.Command("op-1", "webview.navigate", generation: 0, deadline: DateTimeOffset.UtcNow.AddSeconds(5)),
                DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(Frames.Command("op-1", "webview.navigate", deadline: DateTimeOffset.UtcNow.AddSeconds(5)), DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.UnsupportedCapability,
            CoreFrameMapping.Decode(
                Frames.Command("op-1", "webview.teleport", deadline: DateTimeOffset.UtcNow.AddSeconds(5)),
                DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidTarget,
            CoreFrameMapping.Decode(Frames.NavigateCommand("op-1", includeTarget: false).Command, DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(Frames.NavigateCommand("op-1", url: "relative/path").Command, DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(
                Frames.JavascriptCommand("op-1", script: new string('x', 256 * 1024 + 1)).Command, DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(
                Frames.JavascriptCommand("op-1", maxResultBytes: JavascriptRequest.MaxResultBytesLimit + 1).Command,
                DesktopId).Error.Code);

        Assert.Equal(
            DesktopCapabilityErrorCode.InvalidRequest,
            CoreFrameMapping.Decode(Frames.NotificationCommand("op-1", title: " ").Command, DesktopId).Error.Code);
    }

    [Fact]
    public void Encode_Result_UsesTheCommandedCapabilityBranch()
    {
        var navigate = DesktopFrameMapping.Result(
            new OperationId("op-1"),
            ConnectionGeneration.Require(3),
            FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityResponse.FromNavigate(
                new NavigateResult(NavigateDisposition.Accepted, new Uri("https://example.com/a"), DesktopPageVersion.Require(2))));

        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.Navigate, navigate.Result.OutcomeCase);
        Assert.Equal(Proto.NavigateDisposition.Accepted, navigate.Result.Navigate.Disposition);
        Assert.Equal(3UL, navigate.Result.Generation);

        var javascript = DesktopFrameMapping.Result(
            new OperationId("op-2"),
            ConnectionGeneration.Require(1),
            FakeExecutor.Descriptor(DesktopCapability.WebViewExecuteJavascript),
            DesktopCapabilityResponse.FromJavascript(new JavascriptResult(JavascriptValueKind.Json, "{\"a\":1}", true)));

        Assert.Equal(Proto.JavascriptValueKind.Json, javascript.Result.ExecuteJavascript.Kind);
        Assert.True(javascript.Result.ExecuteJavascript.Truncated);

        // 执行器返回了与命令能力不匹配的 payload：必须是 internal_error，而不是张冠李戴地编码。
        var mismatched = DesktopFrameMapping.Result(
            new OperationId("op-3"),
            ConnectionGeneration.Require(1),
            FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate),
            DesktopCapabilityResponse.FromNotification(new DesktopNotificationResult(true, "n-1")));

        Assert.Equal("internal_error", mismatched.Result.Error.Code);
    }

    [Fact]
    public void Encode_AllNavigateDispositionsAndJavascriptKinds()
    {
        foreach (var (domain, wire) in new (NavigateDisposition Domain, Proto.NavigateDisposition Wire)[]
                 {
                     (NavigateDisposition.Accepted, Proto.NavigateDisposition.Accepted),
                     (NavigateDisposition.Completed, Proto.NavigateDisposition.Completed),
                 })
        {
            var frame = DesktopFrameMapping.Result(
                new OperationId("op-n"),
                ConnectionGeneration.Require(1),
                FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate),
                DesktopCapabilityResponse.FromNavigate(new NavigateResult(domain, null, DesktopPageVersion.Unknown)));

            Assert.Equal(wire, frame.Result.Navigate.Disposition);
        }

        foreach (var kind in Enum.GetValues<JavascriptValueKind>())
        {
            var frame = DesktopFrameMapping.Result(
                new OperationId("op-js"),
                ConnectionGeneration.Require(1),
                FakeExecutor.Descriptor(DesktopCapability.WebViewExecuteJavascript),
                DesktopCapabilityResponse.FromJavascript(new JavascriptResult(kind, "null", false)));

            Assert.Equal((int)kind + 1, (int)frame.Result.ExecuteJavascript.Kind);
        }
    }

    [Fact]
    public void Encode_ErrorPreservesContractSemanticsForEveryCode()
    {
        foreach (var code in DesktopCapabilityErrorCodes.All)
        {
            var error = new DesktopCapabilityError(code, "消息 with\ncontrol");
            var wire = DesktopFrameMapping.ToWire(error);

            Assert.Equal(DesktopCapabilityErrorCodes.NameOf(code), wire.Code);
            Assert.Equal(error.Retryable, wire.Retryable);
            Assert.Equal(error.MayHaveSideEffects, wire.MayHaveSideEffects);
            Assert.DoesNotContain('\n', wire.Message);

            Assert.True(DesktopCapabilityErrorCodes.TryParse(wire.Code, out var parsed));
            Assert.Equal(code, parsed);
        }
    }

    [Fact]
    public void Decode_InvalidCorrelationIdIsIgnoredButOperationIdIsStrict()
    {
        var decoded = CoreFrameMapping.Decode(
            Frames.NavigateCommand("op-1", correlationId: "not valid!").Command, DesktopId);

        Assert.True(decoded.IsSuccess);
        Assert.Null(decoded.Value.Context.CorrelationId);
    }
}
