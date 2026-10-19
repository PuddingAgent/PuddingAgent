using Pudding.CapabilityBroker.AspNetCore;
using PuddingHost.Hosting;
using System.Text.Json;
using System.Text;

// ── PuddingAgent Console/DesktopChild Host (thin entry point) ──────
// All composition root logic lives in PuddingHost; Program.cs only delegates to PuddingApplicationHost.
//
// Modes:
//   Default (no flag):   Console dev server
//   --desktop-child:     Child process launched by PuddingDesktop.exe
//
// Calling order: Parse args → CreateBuilder → Build → InitializeAsync →
//                StartAsync → CaptureBoundAddresses → Ready signal

var isDesktopChild = args.Contains("--desktop-child");
if (isDesktopChild)
{
    // Desktop decodes both redirected streams as UTF-8. Set the producer encoding
    // before any startup output or logger captures Console.Out/Console.Error.
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

// ── 启动阶段埋点（诊断「启动耗时」用）：stdout + 系统日志双通道，每阶段一行 ──
var phases = StartupPhaseTracker.Start();
phases.Mark(StartupPhases.ProcessStart);

// Desktop child 的启动租约必须从进程一开始就续租：
// 全量重编后的首次冷启动实测可超过 60s（services-registered 单阶段 65s），而 Desktop 的启动
// 看门狗以「启动超时内没有 Ready 或租约续期」判定失败并杀掉 Core。此前该循环在 Build() 之后
// 才启动，慢启动阶段完全没有续租，会被误杀（表现为「初始化服务」阶段反复失败）。
CancellationTokenSource? startupLeaseCts = null;
Task startupLeaseTask = Task.CompletedTask;
if (isDesktopChild)
{
    startupLeaseCts = new CancellationTokenSource();
    startupLeaseTask = EmitDesktopStartupLeaseAsync(startupLeaseCts.Token);
}

try
{
    var options = isDesktopChild
        ? PuddingHostOptionsFactory.ForDesktopChild(args)
        : PuddingHostOptionsFactory.ForConsole(args);
    phases.Mark(StartupPhases.OptionsResolved);

    // DataRoot 独占租约：Desktop 重启窗口内旧 Core 可能尚未释放文件句柄。这里做有界重试，
    // 避免新 Core 在「另一个实例正在退出」的竞争中以未处理 IOException 崩溃（Windows 事件日志
    // CLR20r3）；超时后打印明确的启动失败信息并返回退出码 2，而不是抛未处理异常。
    PuddingDataRootLease dataRootLease;
    try
    {
        dataRootLease = new PuddingDataRootLease(options.DataRoot);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] 无法取得 DataRoot 独占租约，Core 不会启动：{ex.Message}");
        Console.Error.WriteLine("[Startup] 请结束占用该数据目录的 PuddingAgent 进程后重试。");
        return 2;
    }

    using var dataRootLeaseScope = dataRootLease;
    phases.Mark(StartupPhases.DataRootLease);

    var builder = PuddingApplicationHost.CreateBuilder(args, options, phases);
    var app = PuddingApplicationHost.Build(builder, phases);

    await PuddingApplicationHost.InitializeAsync(app, CancellationToken.None, phases);
    phases.Mark(StartupPhases.Initialized);

    if (!isDesktopChild)
        Console.WriteLine("[Startup] Starting server...");

    await app.StartAsync();
    phases.Mark(StartupPhases.ServerStarted);

    var address = PuddingApplicationHost.CaptureBoundAddresses(app);
    phases.Mark(StartupPhases.Ready);

    if (isDesktopChild)
    {
        // Emit PUDDING_DESKTOP_READY signal on stdout so Desktop can parse it
        // capabilityEndpoint 只在能力通道启用时出现 ⇒ 关闭时这一行与今天逐字一致。
        // 用序列化器而不是手写 JSON：端点描述是 Core→Desktop 唯一的字符串契约，手写容易产生
        // 只有「打开开关」时才暴露的格式漂移。
        var capabilityEndpoint = PuddingApplicationHost.GetCapabilityEndpointDescription(app.Services);
        var readyPayload = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1,
            ["processId"] = Environment.ProcessId,
            ["baseAddress"] = address,
        };

        // **关闭时字段整个缺席**（而不是写了个 null）：那一行必须与加功能之前逐字一致，
        // 否则"回滚 = 把开关置 false"就不再是逐字回滚。
        // 这里不能用序列化器的 WhenWritingNull 来省事——它只作用于**属性**，不作用于字典项；
        // 而字段名必须取自 Core 侧常量（唯一真源），字典是唯一能同时做到这两点的写法。
        if (capabilityEndpoint is not null)
        {
            readyPayload[CapabilityChannelReadySignal.FieldName] = capabilityEndpoint;
        }

        var readyJson = JsonSerializer.Serialize(readyPayload);
        Console.WriteLine($"PUDDING_DESKTOP_READY {readyJson}");

        // Register shutdown endpoint for Desktop
        // (DesktopLifecycleEndpointExtensions handles this)
    }
    else
    {
        Console.WriteLine($"[Startup] Server running at {address} — waiting for shutdown...");
    }

    try
    {
        await app.WaitForShutdownAsync();
    }
    finally
    {
        Serilog.Log.CloseAndFlush();
    }
}
finally
{
    if (startupLeaseCts is not null)
    {
        await startupLeaseCts.CancelAsync();
        try
        {
            await startupLeaseTask;
        }
        catch (OperationCanceledException)
        {
        }
        startupLeaseCts.Dispose();
    }
}

return 0;

static async Task EmitDesktopStartupLeaseAsync(CancellationToken cancellationToken)
{
    var sequence = 0L;
    var startedAt = DateTimeOffset.UtcNow;

    while (!cancellationToken.IsCancellationRequested)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            processId = Environment.ProcessId,
            sequence = Interlocked.Increment(ref sequence),
            phase = "initializing",
            elapsedMilliseconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
        });
        Console.WriteLine($"PUDDING_DESKTOP_STARTING {payload}");

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}

/// <summary>
/// Public partial class required for WebApplicationFactory&lt;Program&gt; integration tests.
/// </summary>
public partial class Program { }
