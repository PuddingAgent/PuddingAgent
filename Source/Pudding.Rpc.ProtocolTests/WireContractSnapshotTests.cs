using Google.Protobuf.Reflection;
using Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.ProtocolTests;

/// <summary>
/// 线上形状快照：service 名、流方向与字段号一旦发布就不能改（改 = 破坏性变更）。
/// </summary>
public sealed class WireContractSnapshotTests
{
    [Fact]
    public void Service_HasSingleBidirectionalConnectMethod()
    {
        var service = DesktopCapability.Descriptor;

        Assert.Equal("pudding.capability.v1.DesktopCapability", service.FullName);
        var method = Assert.Single(service.Methods);
        Assert.Equal("Connect", method.Name);
        Assert.True(method.IsClientStreaming);
        Assert.True(method.IsServerStreaming);
        Assert.Equal(DesktopFrame.Descriptor.FullName, method.InputType.FullName);
        Assert.Equal(CoreFrame.Descriptor.FullName, method.OutputType.FullName);
    }

    [Fact]
    public void GeneratedStubs_AreBothClientAndServer()
    {
        Assert.NotNull(typeof(DesktopCapability.DesktopCapabilityClient));
        Assert.NotNull(typeof(DesktopCapability.DesktopCapabilityBase));
        Assert.True(typeof(DesktopCapability.DesktopCapabilityBase).IsAbstract);
    }

    [Fact]
    public void FrameFieldNumbers_AreFrozen()
    {
        AssertFieldNumbers(
            DesktopFrame.Descriptor,
            ("hello", 1),
            ("result", 2),
            ("event", 3),
            ("heartbeat_ack", 4));

        AssertFieldNumbers(
            CoreFrame.Descriptor,
            ("hello_ack", 1),
            ("command", 2),
            ("cancel", 3),
            ("heartbeat", 4));

        Assert.Equal(
            ["Hello", "Result", "Event", "HeartbeatAck"],
            Enum.GetNames<DesktopFrame.FrameOneofCase>().Where(name => name != "None").ToArray());

        Assert.Equal(
            ["HelloAck", "Command", "Cancel", "Heartbeat"],
            Enum.GetNames<CoreFrame.FrameOneofCase>().Where(name => name != "None").ToArray());
    }

    [Fact]
    public void HandshakeFieldNumbers_AreFrozen()
    {
        AssertFieldNumbers(
            DesktopHello.Descriptor,
            ("desktop_id", 1),
            ("process_instance_id", 2),
            ("supported_versions", 3),
            ("capabilities", 4),
            ("requested_max_frame_bytes", 5));

        AssertFieldNumbers(
            CoreHelloAck.Descriptor,
            ("connection_id", 1),
            ("generation", 2),
            ("negotiated_version", 3),
            ("capabilities", 4),
            ("limits", 5),
            ("core_instance_id", 6));

        AssertFieldNumbers(
            ChannelLimits.Descriptor,
            ("max_frame_bytes", 1),
            ("max_in_flight_operations", 2),
            ("max_queued_bytes", 3),
            ("heartbeat_interval_ms", 4),
            ("heartbeat_timeout_ms", 5));
    }

    [Fact]
    public void CommandAndResultFieldNumbers_AreFrozen()
    {
        AssertFieldNumbers(
            CapabilityCommand.Descriptor,
            ("operation_id", 1),
            ("generation", 2),
            ("capability", 3),
            ("trace_id", 4),
            ("correlation_id", 5),
            ("deadline", 6),
            ("navigate", 10),
            ("execute_javascript", 11),
            ("show_notification", 12),
            ("get_page_state", 13),
            ("get_shell_status", 14),
            ("snapshot", 15),
            ("locate", 16),
            ("interact", 17),
            ("wait_for", 18),
            ("contexts", 19),
            ("tabs", 20));

        AssertFieldNumbers(
            OperationResult.Descriptor,
            ("operation_id", 1),
            ("generation", 2),
            ("navigate", 10),
            ("execute_javascript", 11),
            ("show_notification", 12),
            ("error", 13),
            ("page_state", 14),
            ("shell_status", 15),
            ("snapshot", 16),
            ("locate", 17),
            ("interact", 18),
            ("wait_for", 19),
            ("contexts", 20),
            ("tabs", 21));

        Assert.Equal(
            ["Navigate", "ExecuteJavascript", "ShowNotification", "GetPageState", "GetShellStatus", "Snapshot", "Locate", "Interact", "WaitFor", "Contexts", "Tabs"],
            Enum.GetNames<CapabilityCommand.PayloadOneofCase>().Where(name => name != "None").ToArray());

        Assert.Equal(
            ["Navigate", "ExecuteJavascript", "ShowNotification", "Error", "PageState", "ShellStatus", "Snapshot", "Locate", "Interact", "WaitFor", "Contexts", "Tabs"],
            Enum.GetNames<OperationResult.OutcomeOneofCase>().Where(name => name != "None").ToArray());

        AssertFieldNumbers(OperationCancel.Descriptor, ("operation_id", 1), ("generation", 2), ("reason", 3));
        AssertFieldNumbers(ErrorOutcome.Descriptor,
            ("code", 1), ("message", 2), ("retryable", 3), ("may_have_side_effects", 4));
        AssertFieldNumbers(ShellStatusOutcome.Descriptor,
            ("window_state", 1), ("tray_visible", 2), ("automation_state", 3), ("open_page_count", 4));
        AssertFieldNumbers(SnapshotOutcome.Descriptor,
            ("dom_text", 1), ("accessibility_tree", 2), ("html", 3), ("truncated", 4), ("node_count", 5), ("page_version", 6));
        AssertFieldNumbers(SnapshotCommand.Descriptor, ("target", 1), ("expected_page_version", 2), ("budget", 3));
        AssertFieldNumbers(LocateCommand.Descriptor, ("target", 1), ("expected_page_version", 2), ("locator", 3), ("max_results", 4));
        AssertFieldNumbers(LocatorSpec.Descriptor,
            ("kind", 1), ("value", 2), ("name", 3), ("exact", 4), ("nth", 5), ("has_text", 6));
        AssertFieldNumbers(ElementRef.Descriptor,
            ("ref", 1), ("tag", 2), ("role", 3), ("name", 4), ("text", 5), ("visible", 6), ("enabled", 7), ("checked", 8), ("page_version", 9));
        AssertFieldNumbers(LocateOutcome.Descriptor, ("elements", 1), ("truncated", 2), ("page_version", 3));
        AssertFieldNumbers(InteractCommand.Descriptor,
            ("target", 1), ("expected_page_version", 2), ("action", 3), ("locator", 4), ("text", 5),
            ("values", 6), ("checked", 7), ("delta_x", 8), ("delta_y", 9));
        AssertFieldNumbers(InteractionOutcome.Descriptor, ("element", 1), ("page", 2));
        AssertFieldNumbers(WaitForCommand.Descriptor,
            ("target", 1), ("expected_page_version", 2), ("condition_kind", 3), ("condition_value", 4), ("timeout_ms", 5));
        AssertFieldNumbers(PageInfo.Descriptor,
            ("context_id", 1), ("page_id", 2), ("page_version", 3), ("title", 4), ("url", 5), ("is_active", 6),
            ("is_agent_target", 7), ("can_go_back", 8), ("can_go_forward", 9), ("is_loading", 10));
        AssertFieldNumbers(ContextInfo.Descriptor, ("context_id", 1), ("trust", 2), ("pages", 3));
        AssertFieldNumbers(ContextsOutcome.Descriptor, ("contexts", 1));
        AssertFieldNumbers(TabsCommand.Descriptor, ("target", 1), ("expected_page_version", 2), ("action", 3));
        AssertFieldNumbers(TabsOutcome.Descriptor, ("action", 1), ("tab_closed", 2), ("page", 3), ("remaining", 4));
        AssertFieldNumbers(WaitOutcome.Descriptor,
            ("timed_out", 1), ("condition_kind", 2), ("condition_value", 3), ("page", 4), ("error", 5));
        AssertFieldNumbers(SnapshotBudget.Descriptor,
            ("include_dom", 1), ("include_accessibility_tree", 2), ("include_html", 3), ("max_nodes", 4), ("max_text_length", 5));
    }

    [Fact]
    public void PayloadOneof_IsClosedWhitelist()
    {
        // 计划 §4：禁止「字符串命令名 + 任意 JSON」演化成万能调用。
        Assert.Equal(11, CapabilityCommand.Descriptor.Oneofs.Single(o => o.Name == "payload").Fields.Count);
        Assert.Equal(12, OperationResult.Descriptor.Oneofs.Single(o => o.Name == "outcome").Fields.Count);
    }

    [Fact]
    public void EnumZeroValues_AreUnspecified()
    {
        Assert.Equal(0, (int)NavigateDisposition.Unspecified);
        Assert.Equal(0, (int)JavascriptValueKind.Unspecified);
        Assert.Equal(0, (int)NotificationPriority.Unspecified);
        Assert.Equal(1, (int)NavigateDisposition.Accepted);
        Assert.Equal(2, (int)NavigateDisposition.Completed);
        Assert.Equal(6, (int)JavascriptValueKind.Json);
    }

    private static void AssertFieldNumbers(MessageDescriptor descriptor, params (string Name, int Number)[] expected)
    {
        foreach (var (name, number) in expected)
        {
            var field = descriptor.FindFieldByName(name);
            Assert.NotNull(field);
            Assert.Equal(number, field!.FieldNumber);
        }
    }
}
