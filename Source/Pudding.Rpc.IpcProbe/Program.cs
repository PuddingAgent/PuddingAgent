using System.Diagnostics;
using System.Text;
using Pudding.CapabilityBroker;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;
using DesktopConnectionClient = Pudding.DesktopConnection.DesktopConnection;

namespace Pudding.Rpc.IpcProbe;

/// <summary>
/// 技术探针（计划 §8 切片 B/C 门禁）。与上一版的区别：服务端不再是探针自带的替身，
/// 而是**真实的 <see cref="CapabilityBroker"/>**（Core 侧适配器），因此本探针同时验证
/// 「两端适配器在真实端点上的互操作」——Core 编码的命令能被 Desktop 解码执行，Desktop 的结果
/// 能被 Core 解码为类型化输出。
///
/// 验证内容：
///   1. Core（Kestrel Named Pipe + 显式 HTTP/2）↔ Desktop（ConnectCallback 拨入）握手协商；
///   2. 认证先于握手（无凭据连接被拒，且不产生任何会话）；
///   3. broker 侧 navigate / page_state / 通知的类型化往返与耗时；
///   4. 4 KiB / 64 KiB / 256 KiB / 1 MiB 单帧往返（消息字节预算的实测起点）；
///   5. 取消的真实往返（Core 发 OperationCancel → Desktop 回 cancelled + 副作用标注）；
///   6. Named Pipe 端点 ACL（SDDL）与 ACL 注入钩子；
///   7. 调试备用传输 Loopback h2c 同样可用。
///
/// 退出码：0 = 全部通过；1 = 有失败项。输出为人类可读报告（无凭据、无业务数据）。
/// </summary>
internal static class Program
{
    private const string ControlToken = "probe-control-token";
    private const string DesktopId = "probe-desktop";
    private const string AuthHeader = "x-pudding-control-token";
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var report = new ProbeReport();
        var pipeName = $"pudding-ipc-probe-{Guid.NewGuid():N}";

        try
        {
            RunEndpointDescriptionRoundTrip(report);

            await using var server = await BrokerProbeServer.StartNamedPipeAsync(pipeName, ControlToken, DesktopId);
            report.Pass(
                "kestrel-named-pipe",
                $"Kestrel HTTP/2 已监听 {server.Endpoint}（服务端 = Pudding.CapabilityBroker）");

            await RunAuthenticationFailureAsync(pipeName, server, report);
            await RunInteropAsync(DesktopChannelTransport.NamedPipe(pipeName), server, report, "named-pipe");
            NamedPipeAclProbe.Run(pipeName, report);

            await using var loopback = await BrokerProbeServer.StartLoopbackAsync(ControlToken, DesktopId);
            report.Pass("kestrel-loopback-h2c", $"Kestrel HTTP/2 已监听 {loopback.Endpoint}");
            await RunInteropAsync(
                DesktopChannelTransport.LoopbackHttp2(loopback.Endpoint), loopback, report, "loopback");
        }
        catch (Exception ex)
        {
            report.Fail("probe-run", $"探针异常终止：{ex.GetType().Name}: {ex.Message}");
        }

        return report.Print();
    }

    /// <summary>
    /// 端点描述的两端一致性（计划 §7）：Core 侧**从配置派生**管道名并发布描述，
    /// Desktop 侧严格解析成传输。描述里不含凭据；不同用户/不同 DataRoot 派生出不同的管道名；
    /// 通道关闭时描述为空（就绪流程里不出现该字段）。
    /// </summary>
    private static void RunEndpointDescriptionRoundTrip(ProbeReport report)
    {
        var configured = new CapabilityChannelConfiguration { Enabled = true };
        var endpoint = configured.Describe("probe-user-scope", "probe-data-root", "core-probe")!;
        var text = endpoint.ToEndpointString();
        var resolved = DesktopChannelTransportResolver.ResolveFromText(text);

        if (resolved.IsSuccess
            && resolved.Value.Kind == DesktopChannelTransportKind.NamedPipe
            && string.Equals(resolved.Value.Address, endpoint.Address, StringComparison.Ordinal))
        {
            report.Pass(
                "endpoint-description",
                $"配置派生端点 → Desktop 解析：{text}（transport={resolved.Value.Kind}）");
        }
        else
        {
            report.Fail("endpoint-description", $"描述往返失败：{text} ⇒ {resolved.Error}");
        }

        // 调试备用传输也必须描述成「实际监听的形态」。
        var loopback = new CapabilityChannelConfiguration
        {
            Enabled = true,
            Transport = CapabilityChannelTransport.LoopbackHttp2,
            LoopbackPort = 5099,
        };
        var loopbackResolved = DesktopChannelTransportResolver.Resolve(
            loopback.Describe("probe-user-scope", "probe-data-root", "core-probe"));
        if (loopbackResolved.IsSuccess
            && loopbackResolved.Value.Kind == DesktopChannelTransportKind.LoopbackHttp2
            && string.Equals(loopbackResolved.Value.Address, "http://127.0.0.1:5099", StringComparison.Ordinal))
        {
            report.Pass("endpoint-transport", "loopback-h2c 配置描述为回环端点（不会指向未监听的管道）");
        }
        else
        {
            report.Fail("endpoint-transport", $"loopback 描述错误：{loopbackResolved.Error}");
        }

        var disabled = new CapabilityChannelConfiguration();
        if (disabled.Describe("probe-user-scope", "probe-data-root", "core-probe") is null)
        {
            report.Pass("endpoint-default-off", "通道未开启时端点描述为空（默认不改动既有行为）");
        }
        else
        {
            report.Fail("endpoint-default-off", "通道未开启却仍发布了端点描述");
        }

        var otherRoot = CapabilityEndpointNaming.NamedPipeEndpoint("probe-user-scope", "another-data-root", "core-probe");
        if (!string.Equals(otherRoot.Address, endpoint.Address, StringComparison.Ordinal))
        {
            report.Pass("endpoint-isolation", "不同 DataRoot 派生出不同管道名（不会串接）");
        }
        else
        {
            report.Fail("endpoint-isolation", "不同 DataRoot 得到同一个管道名");
        }
    }
    private static DesktopConnectionOptions DesktopOptions(DesktopChannelAuthentication? authentication) => new()
    {
        DesktopId = new DesktopInstanceId(DesktopId),
        ProcessInstanceId = new DesktopProcessInstanceId($"probe-{Environment.ProcessId}"),
        SupportedCapabilities = DesktopCapability.WebViewNavigate
            | DesktopCapability.WebViewExecuteJavascript
            | DesktopCapability.WebViewPageState
            | DesktopCapability.ShellNotification
            | DesktopCapability.ShellStatus
            | DesktopCapability.BrowserSnapshot
            | DesktopCapability.BrowserLocate,
        Authentication = authentication,
        HandshakeTimeout = StepTimeout,
        InactivityTimeout = TimeSpan.FromSeconds(30),
    };

    private static GrpcDesktopChannelStreamFactory Factory(
        DesktopChannelTransport transport, DesktopChannelAuthentication? authentication) =>
        new(transport, authentication, MaxMessageBytes);

    private static async Task RunAuthenticationFailureAsync(
        string pipeName, BrokerProbeServer server, ProbeReport report)
    {
        var transport = DesktopChannelTransport.NamedPipe(pipeName);
        await using var desktop = new DesktopConnectionClient(Factory(transport, null), new ProbeExecutor(), DesktopOptions(null));

        var outcome = await desktop.RunAsync().WaitAsync(StepTimeout);

        if (outcome.FinalState == DesktopConnectionState.Faulted
            && outcome.Error?.Code == DesktopCapabilityErrorCode.Unauthorized
            && server.Broker.Sessions.Count == 0)
        {
            report.Pass("auth-before-handshake", "缺少控制令牌的连接被拒（unauthorized），且未产生任何会话");
        }
        else
        {
            report.Fail(
                "auth-before-handshake",
                $"期望 Faulted/Unauthorized 且 0 会话，实际 {outcome.FinalState}/{outcome.Error?.Code}/{server.Broker.Sessions.Count}");
        }
    }

    private static async Task RunInteropAsync(
        DesktopChannelTransport transport, BrokerProbeServer server, ProbeReport report, string label)
    {
        var authentication = DesktopChannelAuthentication.StaticHeader(AuthHeader, ControlToken);
        await using var desktop = new DesktopConnectionClient(
            Factory(transport, authentication), new ProbeExecutor(), DesktopOptions(authentication));

        _ = desktop.RunAsync();
        await WaitAsync(() => desktop.State == DesktopConnectionState.Ready, $"{label}: desktop reaches Ready");
        await WaitAsync(() => desktop.Generation.Value > 0, $"{label}: desktop receives a generation");

        var session = await server.WaitForSessionAsync(StepTimeout);
        report.Pass(
            $"{label}-handshake",
            $"Broker ↔ Desktop 握手：generation={session.Generation.Value} 协商能力={session.NegotiatedCapabilities} "
            + $"inFlight={session.Ack.Limits.MaxInFlightOperations} core={session.Ack.CoreInstanceId}");

        var target = new DesktopPageTarget("ctx-probe", "page-probe");

        // 1) 导航（Core 编码 → Desktop 解码执行 → Desktop 编码结果 → Core 解码为类型化输出）
        var navigateCall = Call("navigate");
        var stopwatch = Stopwatch.StartNew();
        var navigate = await session.NavigateAsync(
            new NavigateRequest(target, new Uri("https://example.com/probe")), navigateCall);
        stopwatch.Stop();

        if (navigate.IsSuccess)
        {
            report.Pass(
                $"{label}-navigate",
                $"命令→结果往返 {stopwatch.Elapsed.TotalMilliseconds:F1} ms：{navigate.Value.CurrentUrl} "
                + $"（disposition={navigate.Value.Disposition}, generation={session.Generation.Value}）");
        }
        else
        {
            report.Fail($"{label}-navigate", $"期望 navigate 结果，实际 {navigate.Error}");
        }

        // 2) 只读页面状态（Core 用它确认导航结果与 PageVersion）
        var state = await session.GetPageStateAsync(target, Call("page-state"));
        if (state.IsSuccess)
        {
            report.Pass(
                $"{label}-page-state",
                $"page_state → {state.Value.Url}（version={state.Value.Version.Value}, readiness={state.Value.Readiness}, "
                + $"target={state.Value.Target.Key}）");
        }
        else
        {
            report.Fail($"{label}-page-state", $"期望 page_state 结果，实际 {state.Error}");
        }

        // 3) 载荷尺寸（消息字节预算的实测起点）
        var payloadsOk = true;
        foreach (var bytes in new[] { 4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024 })
        {
            var measure = Stopwatch.StartNew();
            var javascript = await session.ExecuteJavascriptAsync(
                new JavascriptRequest(target, "return 'probe';", default, bytes), Call($"payload-{bytes}"));
            measure.Stop();

            if (!javascript.IsSuccess)
            {
                payloadsOk = false;
                report.Fail($"payload-{bytes / 1024}KiB", $"期望 javascript 结果，实际 {javascript.Error}");
                continue;
            }

            var payloadBytes = Encoding.UTF8.GetByteCount(javascript.Value.JsonValue ?? string.Empty);
            var mebibytesPerSecond = (payloadBytes / 1024d / 1024d) / measure.Elapsed.TotalSeconds;
            report.Info(
                $"payload {bytes / 1024} KiB: 帧体 {payloadBytes} B，往返 {measure.Elapsed.TotalMilliseconds:F1} ms"
                + $"（单流 ≈{mebibytesPerSecond:F1} MiB/s）");
        }

        if (payloadsOk)
        {
            report.Pass("payload-budget", "4 KiB / 64 KiB / 256 KiB / 1 MiB 单帧往返全部成功");
        }

        // 4) 取消的真实往返：Core 发 OperationCancel → Desktop 回 cancelled + 副作用标注
        var cancelCall = Call("cancel");
        var hanging = session.ExecuteJavascriptAsync(
            new JavascriptRequest(target, "hang();", default, 1024), cancelCall);
        await Task.Delay(200);

        var cancelStopwatch = Stopwatch.StartNew();
        var cancelSent = await session.CancelAsync(cancelCall.OperationId);
        var cancelled = await hanging.WaitAsync(StepTimeout);
        cancelStopwatch.Stop();

        if (cancelSent
            && cancelled.Error?.Code == DesktopCapabilityErrorCode.Cancelled
            && cancelled.Error.MayHaveSideEffects)
        {
            report.Pass(
                "cancel",
                $"取消往返 {cancelStopwatch.Elapsed.TotalMilliseconds:F1} ms：cancelled + may_have_side_effects=true");
        }
        else
        {
            report.Fail(
                "cancel",
                $"期望 cancelled+副作用标注，实际 sent={cancelSent}/{cancelled.Error?.Code}/{cancelled.Error?.MayHaveSideEffects}");
        }

        // 5) 通知（无页面目标的 Shell 能力）
        var notification = await session.ShowNotificationAsync(
            new DesktopNotificationRequest("探针", "Core 经能力通道下发通知"), Call("notification"));
        if (notification.IsSuccess && notification.Value.Shown)
        {
            report.Pass("notification", "Shell 通知类型化往返成功");
        }
        else
        {
            report.Fail("notification", $"期望通知成功，实际 {notification.Error}");
        }

        // 5) 只读 Shell 状态（切片 E 第一项：无参数能力）
        var status = await session.GetShellStatusAsync(Call("shell-status"));
        if (status.IsSuccess)
        {
            report.Pass(
                $"{label}-shell-status",
                $"shell_status → {status.Value}（无参数能力，Core 据此决定是否派发变更类操作）");
        }
        else
        {
            report.Fail($"{label}-shell-status", $"期望 shell_status 结果，实际 {status.Error}");
        }

        // 6) 页面快照（切片 D 首个能力）：Ref 只在返回的 PageVersion 内有效。
        var snapshot = await session.SnapshotAsync(
            new BrowserSnapshotRequest(
                target,
                DesktopPageVersion.Unknown,
                new DesktopSnapshotOptions(includeDom: true, includeAccessibilityTree: true, maxNodes: 500)),
            Call("snapshot"));
        if (snapshot.IsSuccess && snapshot.Value.NodeCount > 0 && snapshot.Value.PageVersion.Value > 0)
        {
            report.Pass(
                $"{label}-snapshot",
                $"browser.snapshot → nodes={snapshot.Value.NodeCount} page_version={snapshot.Value.PageVersion.Value} "
                + $"truncated={snapshot.Value.Truncated}");
        }
        else
        {
            report.Fail($"{label}-snapshot", $"期望快照结果，实际 {snapshot.Error}");
        }
        // 7) 元素定位（切片 D）：css 定位命中 + checked 三态过线 + Ref 必须携带来源版本。
        var locate = await session.LocateAsync(
            new BrowserLocateRequest(
                target,
                new DesktopLocator(DesktopLocatorKind.Css, "button"),
                DesktopPageVersion.Unknown,
                maxResults: 10),
            Call("locate"));

        var triStateOk = locate.IsSuccess
            && locate.Value.Elements.Count == 3
            && locate.Value.Elements[0].IsChecked == true
            && locate.Value.Elements[1].IsChecked == false
            && locate.Value.Elements[2].IsChecked is null;

        if (triStateOk && locate.Value.Locator.Kind == DesktopLocatorKind.Css)
        {
            report.Pass(
                $"{label}-locate",
                $"browser.locate → {locate.Value.Elements.Count} 元素（checked 三态 true/false/未知 全部原样过线）"
                + $" locator={locate.Value.Locator} page_version={locate.Value.PageVersion.Value}");
        }
        else
        {
            report.Fail($"{label}-locate", $"期望 3 个元素且 checked 三态完整，实际 {(locate.IsFailure ? locate.Error.ToString() : locate.Value.ToString())}");
        }

        // 边界约束（机器可检）：凭 Ref 定位却不说明来源版本必须被拒绝，而不是由接收方猜测。
        var refWithoutVersionRejected = false;
        try
        {
            _ = new BrowserLocateRequest(target, new DesktopLocator(DesktopLocatorKind.Ref, "e1"));
        }
        catch (ArgumentException)
        {
            refWithoutVersionRejected = true;
        }

        if (refWithoutVersionRejected)
        {
            report.Pass("locate-ref-requires-version", "Ref 定位未携带来源 PageVersion 时在契约层即被拒绝");
        }
        else
        {
            report.Fail("locate-ref-requires-version", "Ref 定位缺少来源版本却被接受（引用失效无法判定）");
        }
        // 会话结束时 Broker 侧应当清空注册表（不残留陈旧会话）。
        await desktop.DisposeAsync();
        await WaitAsync(() => server.Broker.Sessions.Count == 0, $"{label}: broker session removed after disconnect");
        report.Pass($"{label}-disconnect", "Desktop 断开后 Broker 注册表清空（pending 已收尾）");
    }

    private static DesktopCallContext Call(string tag) =>
        new(
            new DesktopInstanceId(DesktopId),
            new OperationId($"probe-{tag}-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow.AddSeconds(30));

    private static async Task WaitAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"probe: {because}");
            }

            await Task.Delay(10);
        }
    }
}
