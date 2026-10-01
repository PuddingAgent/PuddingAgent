using Google.Protobuf;
using Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.ProtocolTests;

public sealed class SerializationRoundTripTests
{
    [Fact]
    public void EmptyPayload_ParsesAsNoOneofCase()
    {
        // 映射层依赖这一点做 fail closed：无 payload 的命令必须被拒绝，而不是当成默认值执行。
        var command = CapabilityCommand.Parser.ParseFrom(Array.Empty<byte>());

        Assert.Equal(CapabilityCommand.PayloadOneofCase.None, command.PayloadCase);
        Assert.Equal(string.Empty, command.OperationId);
        Assert.Null(command.Deadline);
    }

    [Fact]
    public void Command_WithNavigatePayload_RoundTrips()
    {
        var deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero));

        var frame = new DesktopFrame
        {
            Hello = new DesktopHello
            {
                DesktopId = "desk-1",
                ProcessInstanceId = "proc-1",
                SupportedVersions = new ProtocolRange { Minimum = 1, Maximum = 1 },
                Capabilities =
                {
                    new CapabilityDeclaration { Capability = "webview.navigate", Version = 1 },
                },
            },
        };

        var parsedHello = DesktopFrame.Parser.ParseFrom(frame.ToByteArray());
        Assert.Equal(DesktopFrame.FrameOneofCase.Hello, parsedHello.FrameCase);
        Assert.Equal("desk-1", parsedHello.Hello.DesktopId);
        Assert.Single(parsedHello.Hello.Capabilities);
        Assert.Equal("webview.navigate", parsedHello.Hello.Capabilities[0].Capability);

        var coreFrame = new CoreFrame
        {
            Command = new CapabilityCommand
            {
                OperationId = "op-1",
                Generation = 7,
                Capability = "webview.navigate",
                TraceId = "trace-9",
                CorrelationId = "call-3",
                Deadline = deadline,
                Navigate = new NavigateCommand
                {
                    Target = new CommandTarget { ContextId = "ctx-1", PageId = "page-2" },
                    Url = "https://example.com/a",
                    ExpectedPageVersion = 4,
                },
            },
        };

        var parsedCommand = CoreFrame.Parser.ParseFrom(coreFrame.ToByteArray());
        Assert.Equal(CoreFrame.FrameOneofCase.Command, parsedCommand.FrameCase);
        Assert.Equal(CapabilityCommand.PayloadOneofCase.Navigate, parsedCommand.Command.PayloadCase);
        Assert.Equal("ctx-1", parsedCommand.Command.Navigate.Target.ContextId);
        Assert.Equal(4, parsedCommand.Command.Navigate.ExpectedPageVersion);
        Assert.Equal(deadline, parsedCommand.Command.Deadline);
        Assert.Equal(deadline.ToDateTimeOffset(), parsedCommand.Command.Deadline.ToDateTimeOffset());
    }

    [Fact]
    public void Result_OutcomeVariants_AreMutuallyExclusive()
    {
        var result = new OperationResult { OperationId = "op-2", Generation = 1 };

        result.Navigate = new NavigateOutcome { Disposition = NavigateDisposition.Accepted, PageVersion = 1 };
        Assert.Equal(OperationResult.OutcomeOneofCase.Navigate, result.OutcomeCase);

        result.ExecuteJavascript = new JavascriptOutcome { Kind = JavascriptValueKind.String, JsonValue = "\"hi\"" };
        Assert.Equal(OperationResult.OutcomeOneofCase.ExecuteJavascript, result.OutcomeCase);

        result.Error = new ErrorOutcome
        {
            Code = "page_version_mismatch",
            Message = "stale",
            Retryable = true,
            MayHaveSideEffects = false,
        };
        Assert.Equal(OperationResult.OutcomeOneofCase.Error, result.OutcomeCase);

        var parsed = OperationResult.Parser.ParseFrom(result.ToByteArray());
        Assert.Equal(OperationResult.OutcomeOneofCase.Error, parsed.OutcomeCase);
        Assert.True(parsed.Error.Retryable);
        Assert.False(parsed.Error.MayHaveSideEffects);
        Assert.Equal("page_version_mismatch", parsed.Error.Code);
    }

    [Fact]
    public void EventsAndHeartbeats_RoundTrip()
    {
        var desktopFrame = new DesktopFrame
        {
            Event = new DesktopEvent
            {
                EventId = "evt-1",
                PageState = new PageStateChanged
                {
                    Target = new CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                    State = "complete",
                    PageVersion = 12,
                    Url = "https://example.com/done",
                },
            },
        };

        var parsedEvent = DesktopFrame.Parser.ParseFrom(desktopFrame.ToByteArray());
        Assert.Equal(DesktopEvent.PayloadOneofCase.PageState, parsedEvent.Event.PayloadCase);
        Assert.Equal(12, parsedEvent.Event.PageState.PageVersion);

        var heartbeat = CoreFrame.Parser.ParseFrom(new CoreFrame { Heartbeat = new Heartbeat { Sequence = 5 } }.ToByteArray());
        Assert.Equal(5, heartbeat.Heartbeat.Sequence);

        var ack = DesktopFrame.Parser.ParseFrom(new DesktopFrame { HeartbeatAck = new HeartbeatAck { Sequence = 5 } }.ToByteArray());
        Assert.Equal(5, ack.HeartbeatAck.Sequence);
    }

    [Fact]
    public void PageStateCommandAndOutcome_RoundTrip()
    {
        var command = new CapabilityCommand
        {
            OperationId = "op-state",
            Generation = 2,
            Capability = "webview.page_state",
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
            GetPageState = new GetPageStateCommand
            {
                Target = new CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
            },
        };

        var parsedCommand = CapabilityCommand.Parser.ParseFrom(command.ToByteArray());
        Assert.Equal(CapabilityCommand.PayloadOneofCase.GetPageState, parsedCommand.PayloadCase);
        Assert.Equal("page-1", parsedCommand.GetPageState.Target.PageId);

        var result = new OperationResult
        {
            OperationId = "op-state",
            Generation = 2,
            PageState = new PageStateOutcome
            {
                Url = "https://example.com/a",
                PageVersion = 12,
                Readiness = "complete",
            },
        };

        var parsedResult = OperationResult.Parser.ParseFrom(result.ToByteArray());
        Assert.Equal(OperationResult.OutcomeOneofCase.PageState, parsedResult.OutcomeCase);
        Assert.Equal("complete", parsedResult.PageState.Readiness);
        Assert.Equal(12, parsedResult.PageState.PageVersion);

        // 设置 error 必须清掉 page_state 分支（oneof 互斥）。
        parsedResult.Error = new ErrorOutcome { Code = "invalid_request" };
        Assert.Equal(OperationResult.OutcomeOneofCase.Error, parsedResult.OutcomeCase);
        Assert.Null(parsedResult.PageState);
    }

    [Fact]
    public void ShellStatusCommandAndOutcome_RoundTrip()
    {
        var command = new CapabilityCommand
        {
            OperationId = "op-status",
            Generation = 3,
            Capability = "shell.status",
            GetShellStatus = new GetShellStatusCommand(),
        };

        var parsedCommand = CapabilityCommand.Parser.ParseFrom(command.ToByteArray());
        Assert.Equal(CapabilityCommand.PayloadOneofCase.GetShellStatus, parsedCommand.PayloadCase);
        Assert.NotNull(parsedCommand.GetShellStatus);

        var result = new OperationResult
        {
            OperationId = "op-status",
            Generation = 3,
            ShellStatus = new ShellStatusOutcome
            {
                WindowState = "hidden_to_tray",
                TrayVisible = true,
                AutomationState = "user_takeover",
                OpenPageCount = 4,
            },
        };

        var parsedResult = OperationResult.Parser.ParseFrom(result.ToByteArray());
        Assert.Equal(OperationResult.OutcomeOneofCase.ShellStatus, parsedResult.OutcomeCase);
        Assert.Equal("hidden_to_tray", parsedResult.ShellStatus.WindowState);
        Assert.True(parsedResult.ShellStatus.TrayVisible);
        Assert.Equal(4u, parsedResult.ShellStatus.OpenPageCount);

        // 设置 error 必须清掉 shell_status 分支（oneof 互斥）。
        parsedResult.Error = new ErrorOutcome { Code = "paused" };
        Assert.Equal(OperationResult.OutcomeOneofCase.Error, parsedResult.OutcomeCase);
        Assert.Null(parsedResult.ShellStatus);
    }

    [Fact]
    public void SnapshotCommandAndOutcome_RoundTrip()
    {
        var command = new CapabilityCommand
        {
            OperationId = "op-snap",
            Generation = 2,
            Capability = "browser.snapshot",
            Snapshot = new SnapshotCommand
            {
                Target = new CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                ExpectedPageVersion = 7,
                Budget = new SnapshotBudget
                {
                    IncludeDom = true,
                    IncludeAccessibilityTree = false,
                    IncludeHtml = false,
                    MaxNodes = 500,
                    MaxTextLength = 4096,
                },
            },
        };

        var parsedCommand = CapabilityCommand.Parser.ParseFrom(command.ToByteArray());
        Assert.Equal(CapabilityCommand.PayloadOneofCase.Snapshot, parsedCommand.PayloadCase);
        Assert.Equal(7, parsedCommand.Snapshot.ExpectedPageVersion);
        Assert.Equal(500, parsedCommand.Snapshot.Budget.MaxNodes);

        var result = new OperationResult
        {
            OperationId = "op-snap",
            Generation = 2,
            Snapshot = new SnapshotOutcome
            {
                DomText = "body>div",
                AccessibilityTree = "document",
                Html = "<div/>",
                Truncated = true,
                NodeCount = 12,
                PageVersion = 7,
            },
        };

        var parsedResult = OperationResult.Parser.ParseFrom(result.ToByteArray());
        Assert.Equal(OperationResult.OutcomeOneofCase.Snapshot, parsedResult.OutcomeCase);
        Assert.True(parsedResult.Snapshot.Truncated);
        Assert.Equal(12, parsedResult.Snapshot.NodeCount);
        Assert.Equal(7, parsedResult.Snapshot.PageVersion);

        // 设置 error 必须清掉 snapshot 分支（oneof 互斥）。
        parsedResult.Error = new ErrorOutcome { Code = "page_version_mismatch" };
        Assert.Equal(OperationResult.OutcomeOneofCase.Error, parsedResult.OutcomeCase);
        Assert.Null(parsedResult.Snapshot);
    }
    [Fact]
    public void JavaScriptResult_CarriesRawJsonFragment()
    {
        var outcome = new JavascriptOutcome { Kind = JavascriptValueKind.Json, JsonValue = "{\"a\":1}", Truncated = true };
        var parsed = JavascriptOutcome.Parser.ParseFrom(outcome.ToByteArray());

        Assert.Equal("{\"a\":1}", parsed.JsonValue);
        Assert.True(parsed.Truncated);
    }
}
