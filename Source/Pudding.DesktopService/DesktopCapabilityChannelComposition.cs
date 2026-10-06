using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// Desktop 侧能力通道的**组合根**（平台无关部分）：把「读配置 → 判定 → 构造 → 启动 → 停止」
/// 收成一次可测试的调用，Shell 只剩「提供 UI 实现、调一次、退出时释放」。
///
/// 为什么组合根必须放在可测试的地方：它是**唯一会改变产品行为**的一步
/// （规格 §6.3），也是最容易出「看起来配好了、其实没起来」的地方——
/// 本轮就抓到过一例（就绪端点描述不可解析 ⇒ 启用后通道永不启动，而默认关闭的路径全绿）。
/// 因此这里把判定与构造都放在能脱 UI 测试的组件里，Shell 里不留判断逻辑。
///
/// 三条不可动摇的语义：
/// ① <b>关闭 = 什么都不做</b>：不构造、不启动，继续走既有 WebSocket Bridge；返回 <c>Success(null)</c>。回滚即回到这里。
/// ② <b>启用却起不来 = 明确失败</b>，绝不静默留在旧 Bridge：那正是「配置没生效」类故障最难查的形态。
/// ③ <b>不做跨传输回退</b>：解析不了端点就不启动（宁可暂时不启用，也不把同一次操作执行两次）。
/// </summary>
public sealed class DesktopCapabilityChannelComposition : IAsyncDisposable
{
    private readonly DesktopCapabilityHost _host;
    private readonly DesktopService _service;
    private readonly GrpcDesktopChannelStreamFactory _streamFactory;
    private readonly Action<string>? _log;
    private int _disposed;

    private DesktopCapabilityChannelComposition(
        DesktopCapabilityHost host,
        DesktopService service,
        GrpcDesktopChannelStreamFactory streamFactory,
        Action<string>? log)
    {
        _host = host;
        _service = service;
        _streamFactory = streamFactory;
        _log = log;
    }

    /// <summary>能力宿主（状态/世代/重连计数从这里读）。</summary>
    public DesktopCapabilityHost Host => _host;

    /// <summary>执行器接缝：Shell 退出时应调用 <see cref="DesktopService.Close"/>，让排队中的 UI 调用以 UiUnavailable 结束。</summary>
    public DesktopService Service => _service;

    public bool IsRunning => _host.IsRunning;

    /// <summary>
    /// 缺省的服务策略：允许集合 = <b>代码事实</b>声明的能力集合（实现即声明，不从配置放宽）；
    /// Shell 调用方可信级别缺省 <see cref="DesktopContextTrust.Untrusted"/> —— fail closed：
    /// 在 Core 侧授权链路给出可信调用方身份之前，对话框/Picker/剪贴板一律不开放（手册 §3）。
    /// </summary>
    public static DesktopServiceOptions CreateDefaultServiceOptions(
        DesktopContextTrust shellCallerTrust = DesktopContextTrust.Untrusted,
        DesktopContextTrust browserContextCallerTrust = DesktopContextTrust.AgentAuthorized) => new()
        {
            AllowedCapabilities = DesktopCapabilityChannelSettings.DeclaredCapabilities,
            ShellCallerTrust = shellCallerTrust,
            // 浏览器上下文作用域单独给一份来源：它们是 Agent 工具层的调用，不是 Shell UI 动作
            // （外部审查 P1-1：沿用 Shell 信任会让 browser_context list/create/close 全被默认拒绝）。
            BrowserContextCallerTrust = browserContextCallerTrust,
        };

    /// <summary>
    /// 判定并（在启用且可用时）启动能力通道。
    /// 返回值：<c>Success(null)</c> = 关闭态（正常，继续用旧 Bridge）；
    /// <c>Success(composition)</c> = 已启动；<c>Failure</c> = 启用了但起不来（调用方应当显式处理/上报）。
    /// </summary>
    public static async Task<CapabilityResult<DesktopCapabilityChannelComposition?>> StartAsync(
        DesktopCapabilityChannelSettings? settings,
        string? endpointDescription,
        DesktopChannelAuthentication? authentication,
        DesktopProcessInstanceId processInstanceId,
        IDesktopUiDispatcher dispatcher,
        IDesktopUiSurface surface,
        DesktopTargetRegistry pageTargets,
        DesktopServiceOptions? serviceOptions = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(pageTargets);

        var preflight = DesktopCapabilityChannelPreflight.Evaluate(
            settings, processInstanceId, authentication, endpointDescription);

        // 判定结果一律记日志（理由可诊断），但**不含**任何页面内容或凭据。
        log?.Invoke(preflight.Summary);
        foreach (var note in preflight.Notes)
        {
            log?.Invoke(note);
        }

        if (settings is null || !settings.Enabled)
        {
            // ① 关闭是正常状态：不构造、不启动。回滚 = 把开关置 false。
            return CapabilityResult<DesktopCapabilityChannelComposition?>.Success(null);
        }

        if (!preflight.ShouldStart)
        {
            // ② 启用了却起不来必须**可见**：只留一条日志会让「配置没生效」查不出来。
            return CapabilityResult<DesktopCapabilityChannelComposition?>.Failure(
                DesktopCapabilityError.InvalidRequest(preflight.Summary));
        }

        // ③ 传输只解析一次，且不做跨传输回退（预检已判定可解析，这里失败即构造问题）。
        var transport = settings.ResolveTransportFromDescription(endpointDescription);
        if (transport.IsFailure)
        {
            return CapabilityResult<DesktopCapabilityChannelComposition?>.Failure(transport.Error);
        }

        var streamFactory = new GrpcDesktopChannelStreamFactory(transport.Value, authentication);
        var service = new DesktopService(
            dispatcher, surface, pageTargets, serviceOptions ?? CreateDefaultServiceOptions());

        // 顺序与「关闭时不构造」的判据由 DesktopCapabilityHostFactory 与组合顺序测试钉住：
        // 关闭 ⇒ 明确失败且不构造宿主；启用 ⇒ 只构造不启动（启动名额留给这里）。
        var host = DesktopCapabilityHostFactory.Create(
            settings, processInstanceId, authentication, streamFactory, service);

        if (host.IsFailure)
        {
            await DisposeQuietlyAsync(streamFactory, service).ConfigureAwait(false);
            return CapabilityResult<DesktopCapabilityChannelComposition?>.Failure(host.Error);
        }

        try
        {
            await host.Value.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是调用方的意图：清理后照原样传播，不折成失败。
            await DisposeQuietlyAsync(streamFactory, service).ConfigureAwait(false);
            await host.Value.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // 启动期异常不得带出内部细节（可能含端点/路径）：只带异常类型名。
            await DisposeQuietlyAsync(streamFactory, service).ConfigureAwait(false);
            await host.Value.DisposeAsync().ConfigureAwait(false);
            return CapabilityResult<DesktopCapabilityChannelComposition?>.Failure(
                DesktopCapabilityError.Internal($"capability channel failed to start ({ex.GetType().Name})"));
        }

        return CapabilityResult<DesktopCapabilityChannelComposition?>.Success(
            new DesktopCapabilityChannelComposition(host.Value, service, streamFactory, log));
    }

    /// <summary>
    /// 释放：停止监督循环 → 关掉传输 → 关闭服务。**可重复调用、不抛异常**
    /// （它运行在窗口退出路径上，抛异常会把退出流程也带坏）。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_host.IsRunning)
            {
                await _host.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"capability channel stop failed ({ex.GetType().Name})");
        }

        await DisposeQuietlyAsync(_streamFactory, _service).ConfigureAwait(false);

        try
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"capability channel dispose failed ({ex.GetType().Name})");
        }
    }

    private static async ValueTask DisposeQuietlyAsync(
        GrpcDesktopChannelStreamFactory streamFactory,
        DesktopService service)
    {
        try
        {
            await streamFactory.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 关闭路径不抛：传输已不可用与已释放等价。
        }

        try
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }
    }
}
