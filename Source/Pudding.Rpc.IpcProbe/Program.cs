using System.Diagnostics;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using DesktopConnectionClient = Pudding.DesktopConnection.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.Rpc.IpcProbe;

/// <summary>
/// 技术探针（计划 §8 切片 B「完成 IPC 技术探针，不接产品 UI」）。
///
/// 验证内容（全部走真实端点，不用假流）：
///   1. Core 侧 Kestrel 用 Named Pipe + 显式 HTTP/2 托管 gRPC，Desktop 用 ConnectCallback 拨入；
///   2. 认证先于握手（无凭据连接被拒）；
///   3. 握手 → navigate 命令 → 类型化结果的真实往返与耗时；
///   4. 4 KiB / 64 KiB / 256 KiB / 1 MiB 单帧往返（消息字节预算的实测起点）；
///   5. 取消在真实端点上及时生效并如实标注副作用；
///   6. Named Pipe 端点 ACL（SDDL）与 ACL 注入钩子（NamedPipeTransportOptions.CreatePipe）；
///   7. 调试备用传输 Loopback h2c 同样可用。
///
/// 退出码：0 = 全部通过；1 = 有失败项。输出为人类可读报告（无凭据、无业务数据）。
/// </summary>
internal static class Program
{
    private const string ControlToken = "probe-control-token";
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var report = new ProbeReport();
        var pipeName = $"pudding-ipc-probe-{Guid.NewGuid():N}";

        try
        {
            await using var server = await ProbeServer.StartNamedPipeAsync(pipeName, ControlToken);
            report.Pass("kestrel-named-pipe", $"Kestrel HTTP/2 已监听 {server.Endpoint}");

            await RunAuthenticationFailureAsync(pipeName, report);
            await RunRoundTripAsync(DesktopChannelTransport.NamedPipe(pipeName), server.Service, report, "named-pipe");
            await RunPayloadSizesAsync(DesktopChannelTransport.NamedPipe(pipeName), server.Service, report);
            await RunCancelAsync(DesktopChannelTransport.NamedPipe(pipeName), server.Service, report);
            NamedPipeAclProbe.Run(pipeName, report);

            await using var loopback = await ProbeServer.StartLoopbackAsync(ControlToken);
            report.Pass("kestrel-loopback-h2c", $"Kestrel HTTP/2 已监听 {loopback.Endpoint}");
            await RunRoundTripAsync(
                DesktopChannelTransport.LoopbackHttp2(loopback.Endpoint), loopback.Service, report, "loopback");
        }
        catch (Exception ex)
        {
            report.Fail("probe-run", $"探针异常终止：{ex.GetType().Name}: {ex.Message}");
        }

        return report.Print();
    }

    private static DesktopConnectionOptions Options(DesktopChannelAuthentication? authentication) => new()
    {
        DesktopId = new DesktopInstanceId("probe-desktop"),
        ProcessInstanceId = new DesktopProcessInstanceId($"probe-{Environment.ProcessId}"),
        SupportedCapabilities = DesktopCapability.WebViewNavigate
            | DesktopCapability.WebViewExecuteJavascript
            | DesktopCapability.WebViewPageState
            | DesktopCapability.ShellNotification,
        Authentication = authentication,
        HandshakeTimeout = StepTimeout,
        InactivityTimeout = TimeSpan.FromSeconds(30),
    };

    private static GrpcDesktopChannelStreamFactory Factory(
        DesktopChannelTransport transport, DesktopChannelAuthentication? authentication) =>
        new(transport, authentication, MaxMessageBytes);

    private static async Task RunAuthenticationFailureAsync(string pipeName, ProbeReport report)
    {
        var transport = DesktopChannelTransport.NamedPipe(pipeName);
        await using var connection = new DesktopConnectionClient(Factory(transport, null), new ProbeExecutor(), Options(null));

        var outcome = await connection.RunAsync().WaitAsync(StepTimeout);

        if (outcome.FinalState == DesktopConnectionState.Faulted
            && outcome.Error?.Code == DesktopCapabilityErrorCode.Unauthorized)
        {
            report.Pass("auth-before-handshake", "缺少控制令牌的连接被拒（unauthorized），未完成握手");
        }
        else
        {
            report.Fail(
                "auth-before-handshake",
                $"期望 Faulted/Unauthorized，实际 {outcome.FinalState}/{outcome.Error?.Code}");
        }
    }

    private static async Task<DesktopConnectionClient> ConnectAsync(
        DesktopChannelTransport transport, ProbeCapabilityService service, ProbeReport report, string label)
    {
        var authentication = DesktopChannelAuthentication.StaticHeader(ProbeCapabilityService.AuthHeader, ControlToken);
        var connection = new DesktopConnectionClient(Factory(transport, authentication), new ProbeExecutor(), Options(authentication));
        _ = connection.RunAsync();

        await WaitAsync(
            () => connection.State == DesktopConnectionState.Ready, $"{label}: connection reaches Ready");
        await WaitAsync(
            () => connection.Generation.Value > 0, $"{label}: handshake assigns a generation");

        var hello = await service.WaitForHelloAsync(StepTimeout);

        report.Pass(
            $"{label}-handshake",
            $"hello_ack 协商成功：desktop={hello.DesktopId} generation={connection.Generation.Value} "
            + $"version={connection.NegotiatedVersion} capabilities={hello.Capabilities.Count} "
            + $"inFlightLimit={connection.EffectiveMaxInFlight} queuedBytes={connection.EffectiveMaxQueuedBytes}");

        return connection;
    }

    private static async Task RunRoundTripAsync(
        DesktopChannelTransport transport, ProbeCapabilityService service, ProbeReport report, string label)
    {
        await using var connection = await ConnectAsync(transport, service, report, label);

        var operationId = $"probe-{label}-navigate";
        var stopwatch = Stopwatch.StartNew();
        service.Enqueue(ProbeFrames.Navigate(operationId, (ulong)connection.Generation.Value));
        var result = await service.WaitForResultAsync(operationId, StepTimeout);
        stopwatch.Stop();

        if (result.OutcomeCase != Proto.OperationResult.OutcomeOneofCase.Navigate)
        {
            report.Fail($"{label}-navigate", $"期望 navigate 结果，实际 {result.OutcomeCase}/{result.Error?.Code}");
            return;
        }

        report.Pass(
            $"{label}-navigate",
            $"命令→结果往返 {stopwatch.Elapsed.TotalMilliseconds:F1} ms：{result.Navigate.CurrentUrl} "
            + $"（disposition={result.Navigate.Disposition}, generation={result.Generation}）");

        // 只读页面状态：Core 用它确认导航结果与 PageVersion，再决定后续交互。
        var stateOperationId = $"probe-{label}-page-state";
        service.Enqueue(ProbeFrames.PageState(stateOperationId, (ulong)connection.Generation.Value));
        var state = await service.WaitForResultAsync(stateOperationId, StepTimeout);

        if (state.OutcomeCase == Proto.OperationResult.OutcomeOneofCase.PageState)
        {
            report.Pass(
                $"{label}-page-state",
                $"page_state → {state.PageState.Url}（version={state.PageState.PageVersion}, readiness={state.PageState.Readiness}）");
        }
        else
        {
            report.Fail($"{label}-page-state", $"期望 page_state 结果，实际 {state.OutcomeCase}/{state.Error?.Code}");
        }
    }

    private static async Task RunPayloadSizesAsync(
        DesktopChannelTransport transport, ProbeCapabilityService service, ProbeReport report)
    {
        await using var connection = await ConnectAsync(transport, service, report, "payload");
        var allSucceeded = true;

        foreach (var bytes in new[] { 4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024 })
        {
            var operationId = $"probe-payload-{bytes}";
            var stopwatch = Stopwatch.StartNew();
            service.Enqueue(ProbeFrames.Javascript(operationId, (ulong)connection.Generation.Value, bytes));
            var result = await service.WaitForResultAsync(operationId, StepTimeout);
            stopwatch.Stop();

            if (result.OutcomeCase != Proto.OperationResult.OutcomeOneofCase.ExecuteJavascript)
            {
                allSucceeded = false;
                report.Fail($"payload-{bytes / 1024}KiB", $"期望 javascript 结果，实际 {result.OutcomeCase}/{result.Error?.Code}");
                continue;
            }

            var payloadBytes = Encoding.UTF8.GetByteCount(result.ExecuteJavascript.JsonValue);
            var mebibytesPerSecond = (payloadBytes / 1024d / 1024d) / stopwatch.Elapsed.TotalSeconds;
            report.Info(
                $"payload {bytes / 1024} KiB: 帧体 {payloadBytes} B，往返 {stopwatch.Elapsed.TotalMilliseconds:F1} ms"
                + $"（单流 ≈{mebibytesPerSecond:F1} MiB/s）");
        }

        if (allSucceeded)
        {
            report.Pass("payload-budget", "4 KiB / 64 KiB / 256 KiB / 1 MiB 单帧往返全部成功（消息字节预算的实测起点）");
        }
    }

    private static async Task RunCancelAsync(
        DesktopChannelTransport transport, ProbeCapabilityService service, ProbeReport report)
    {
        await using var connection = await ConnectAsync(transport, service, report, "cancel");

        var operationId = "probe-cancel";
        service.Enqueue(ProbeFrames.HangingJavascript(operationId, (ulong)connection.Generation.Value));
        await Task.Delay(200);

        var stopwatch = Stopwatch.StartNew();
        service.Enqueue(ProbeFrames.Cancel(operationId, (ulong)connection.Generation.Value));
        var result = await service.WaitForResultAsync(operationId, StepTimeout);
        stopwatch.Stop();

        if (result.Error?.Code == DesktopCapabilityErrorCodes.NameOf(DesktopCapabilityErrorCode.Cancelled)
            && result.Error.MayHaveSideEffects)
        {
            report.Pass(
                "cancel",
                $"取消在 {stopwatch.Elapsed.TotalMilliseconds:F1} ms 内生效：cancelled + may_have_side_effects=true");
        }
        else
        {
            report.Fail(
                "cancel",
                $"期望 cancelled+副作用标注，实际 {result.OutcomeCase}/{result.Error?.Code}/{result.Error?.MayHaveSideEffects}");
        }
    }

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

internal static class ProbeFrames
{
    public static Proto.CoreFrame Navigate(string operationId, ulong generation) =>
        Command(new Proto.CapabilityCommand
        {
            OperationId = operationId,
            Generation = generation,
            Capability = "webview.navigate",
            Deadline = Deadline(),
            Navigate = new Proto.NavigateCommand
            {
                Target = new Proto.CommandTarget { ContextId = "ctx-probe", PageId = "page-probe" },
                Url = "https://example.com/probe",
            },
        });

    public static Proto.CoreFrame Javascript(
        string operationId, ulong generation, int maxResultBytes, string script = "return 'probe';") =>
        Command(new Proto.CapabilityCommand
        {
            OperationId = operationId,
            Generation = generation,
            Capability = "webview.execute_javascript",
            Deadline = Deadline(),
            ExecuteJavascript = new Proto.ExecuteJavascriptCommand
            {
                Target = new Proto.CommandTarget { ContextId = "ctx-probe", PageId = "page-probe" },
                Script = script,
                MaxResultBytes = (uint)maxResultBytes,
            },
        });

    public static Proto.CoreFrame HangingJavascript(string operationId, ulong generation) =>
        Javascript(operationId, generation, 1024, "hang();");

    public static Proto.CoreFrame PageState(string operationId, ulong generation) =>
        Command(new Proto.CapabilityCommand
        {
            OperationId = operationId,
            Generation = generation,
            Capability = "webview.page_state",
            Deadline = Deadline(),
            GetPageState = new Proto.GetPageStateCommand
            {
                Target = new Proto.CommandTarget { ContextId = "ctx-probe", PageId = "page-probe" },
            },
        });

    public static Proto.CoreFrame Cancel(string operationId, ulong generation) =>
        new()
        {
            Cancel = new Proto.OperationCancel { OperationId = operationId, Generation = generation },
        };

    private static Proto.CoreFrame Command(Proto.CapabilityCommand command) => new() { Command = command };

    private static Timestamp Deadline() => Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30));
}
