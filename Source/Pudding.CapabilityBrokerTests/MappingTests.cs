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

    private static DesktopCapabilityDescriptor Descriptor(string name)
    {
        Assert.True(DesktopCapabilities.TryGetByName(name, out var descriptor), $"能力目录里没有 {name}");
        return descriptor;
    }

    [Fact]
    public void EncoderCarriesTheTrustedCallerOntoTheWire()
    {
        // §3.2：能力通道命令携带类型化调用者身份（kind/callerId/taskId/parentTaskId）。
        var caller = new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub", "run-parent");
        var call = new DesktopCallContext(
            new DesktopInstanceId("desk-1"),
            new OperationId("op-carrier"),
            DateTimeOffset.UtcNow.AddSeconds(30),
            correlationId: null,
            browserCaller: caller);

        var command = CoreCommandEncoder.Encode(
            call.OperationId,
            ConnectionGeneration.Require(1),
            Descriptor("browser.contexts"),
            DesktopCapabilityRequest.ForContexts(),
            call,
            traceId: null);

        Assert.NotNull(command.Caller);
        Assert.Equal("sub_agent", command.Caller.Kind);
        Assert.Equal("agent-1", command.Caller.CallerId);
        Assert.Equal("run-sub", command.Caller.TaskId);
        Assert.Equal("run-parent", command.Caller.ParentTaskId);
    }

    [Fact]
    public void EncoderDoesNotInventACallerWhenTheContextHasNone()
    {
        // 没有身份就是**没有**（空字段），不得按 proto 默认值补出任何身份。
        var call = new DesktopCallContext(
            new DesktopInstanceId("desk-1"),
            new OperationId("op-no-caller"),
            DateTimeOffset.UtcNow.AddSeconds(30));

        var command = CoreCommandEncoder.Encode(
            call.OperationId,
            ConnectionGeneration.Require(1),
            Descriptor("browser.contexts"),
            DesktopCapabilityRequest.ForContexts(),
            call,
            traceId: null);

        Assert.Null(command.Caller);
    }

    [Fact]
    public void TheBrowserCapabilityClassifierSeparatesShellCapabilities()
    {
        // §3.2 的边界：浏览器自动化能力需要 caller；Shell 系列不算浏览器操作。
        Assert.True(CoreCommandEncoder.IsBrowserCapability(Descriptor("browser.contexts")));
        Assert.True(CoreCommandEncoder.IsBrowserCapability(Descriptor("webview.navigate")));
        Assert.False(CoreCommandEncoder.IsBrowserCapability(Descriptor("shell.notification")));
        Assert.False(CoreCommandEncoder.IsBrowserCapability(Descriptor("shell.status")));
        Assert.False(CoreCommandEncoder.IsBrowserCapability(Descriptor("shell.clipboard")));
        // 文档预览会把文件内容呈现给用户，但它不是浏览器动作：无页面目标、不需要 caller 投影。
        Assert.False(CoreCommandEncoder.IsBrowserCapability(Descriptor("shell.document_preview")));

        // 控制面（§4.1）**同样**必须有可信 caller —— 否则控制面可匿名申请/撤销授权。
        Assert.True(CoreCommandEncoder.IsBrowserCapability(Descriptor("browser.binding.apply")));
        Assert.True(CoreCommandEncoder.IsBrowserCapability(Descriptor("browser.binding.revoke")));
    }
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

        var documentPreview = CoreCommandEncoder.Encode(
            Operation,
            ConnectionGeneration.Require(1),
            FakeDescriptor(DesktopCapability.ShellDocumentPreview),
            DesktopCapabilityRequest.ForDocumentPreview(
                new DesktopDocumentPreviewRequest("Docs/设计.md", "设计稿")),
            Call,
            null);
        Assert.Equal(Proto.CapabilityCommand.PayloadOneofCase.DocumentPreview, documentPreview.PayloadCase);
        Assert.Equal("Docs/设计.md", documentPreview.DocumentPreview.Path);
        Assert.Equal("设计稿", documentPreview.DocumentPreview.Title);
        // Shell 能力不需要 caller 身份投影：没给就不能凭空造一个。
        Assert.Null(documentPreview.Caller);
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
    public void Decoder_MapsDocumentPreviewIdentityAndStatus()
    {
        var opened = DesktopResultDecoder.Decode(
            new Proto.OperationResult
            {
                OperationId = "op-doc",
                Generation = 1,
                DocumentPreview = new Proto.DocumentPreviewOutcome
                {
                    TabId = "document:Docs/a.md",
                    Path = "Docs/a.md",
                    Status = "opened",
                },
            },
            DesktopCapability.ShellDocumentPreview,
            requestedTarget: null);

        Assert.True(opened.IsSuccess);
        Assert.Equal("document:Docs/a.md", opened.Value.DocumentPreview!.TabId);
        Assert.Equal("Docs/a.md", opened.Value.DocumentPreview.Path);
        Assert.Equal(DesktopDocumentPreviewStatus.Opened, opened.Value.DocumentPreview.Status);

        // 未知 status 折叠为 Accepted：宁可让调用方继续观察标签，也不谎称已经渲染。
        var unknown = DesktopResultDecoder.Decode(
            new Proto.OperationResult
            {
                OperationId = "op-doc",
                Generation = 1,
                DocumentPreview = new Proto.DocumentPreviewOutcome
                {
                    TabId = "t-1",
                    Path = "Docs/a.md",
                    Status = "future_state",
                },
            },
            DesktopCapability.ShellDocumentPreview,
            requestedTarget: null);
        Assert.Equal(DesktopDocumentPreviewStatus.Accepted, unknown.Value.DocumentPreview!.Status);

        // 身份缺失（空 tabId）不得伪装成功：契约构造器会拒，解码器必须折叠成 Internal。
        var missingIdentity = DesktopResultDecoder.Decode(
            new Proto.OperationResult
            {
                OperationId = "op-doc",
                Generation = 1,
                DocumentPreview = new Proto.DocumentPreviewOutcome
                {
                    TabId = string.Empty,
                    Path = "Docs/a.md",
                    Status = "opened",
                },
            },
            DesktopCapability.ShellDocumentPreview,
            requestedTarget: null);
        Assert.True(missingIdentity.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, missingIdentity.Error.Code);
    }

    [Fact]
    public void Decoder_RejectsDocumentPreviewOutcomeForAnotherCapability()
    {
        // 载荷与能力不符必须拒：让错配的结果通过，比报错难查得多。
        var decoded = DesktopResultDecoder.Decode(
            new Proto.OperationResult
            {
                OperationId = "op-doc",
                Generation = 1,
                DocumentPreview = new Proto.DocumentPreviewOutcome
                {
                    TabId = "t-1",
                    Path = "a.md",
                    Status = "opened",
                },
            },
            DesktopCapability.ShellClipboard,
            requestedTarget: null);

        Assert.True(decoded.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, decoded.Error.Code);
    }

    [Fact]
    public void Fingerprint_ForDocumentPreviewTracksThePathAndTitle()
    {
        var descriptor = FakeDescriptor(DesktopCapability.ShellDocumentPreview);

        static string Print(DesktopCapabilityDescriptor descriptor, string path, string? title = null) =>
            CoreCommandEncoder.Fingerprint(
                descriptor,
                DesktopCapabilityRequest.ForDocumentPreview(new DesktopDocumentPreviewRequest(path, title)));

        var baseline = Print(descriptor, "Docs/a.md");
        Assert.Equal(baseline, Print(descriptor, "Docs/a.md"));
        Assert.NotEqual(baseline, Print(descriptor, "Docs/b.md"));
        // 标题只影响显示，但它仍是载荷的一部分：改了就必须算另一次操作。
        Assert.NotEqual(baseline, Print(descriptor, "Docs/a.md", "新标题"));
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
            .Select(DesktopPageReadinessWire.NameOf)
            .ToArray();

        Assert.Equal(["unknown", "loading", "interactive", "complete", "failed"], names);
        Assert.Equal(DesktopPageReadiness.Interactive, DesktopPageReadinessWire.Parse("interactive"));
        Assert.Equal(DesktopPageReadiness.Unknown, DesktopPageReadinessWire.Parse("hibernated"));
        Assert.Equal(DesktopPageReadiness.Unknown, DesktopPageReadinessWire.Parse(null));
    }

    private static DesktopCapabilityDescriptor FakeDescriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);
}
