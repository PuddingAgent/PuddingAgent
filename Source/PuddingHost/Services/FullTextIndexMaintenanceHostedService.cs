using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

/// <summary>
/// S5b（2026-09-27）：把组件已交付的**全文索引局部维护循环**接入宿主 ——
/// 「语料变更 → 索引自动跟进」（判据 2 的另一半；供给片只解决启动时预建）。
/// <para>
/// 契约（S5b R1/R4 + D1~D5，硬约束）：
/// <list type="bullet">
/// <item><c>StartAsync</c> **永不阻塞宿主启动**（长活在启动路径之外，照
/// <see cref="IndexPrebuildService"/> 的精神）。</item>
/// <item>**默认关闭**（<c>system.json</c> 无 <c>FullTextIndex:Maintenance</c> 子节 /
/// <c>Enabled=false</c>）⇒ <c>StartAsync</c> **首句**返回：不构造维护组合、
/// 不解析 scope、不碰索引根、不起 watcher / 线程、不记 Error。</item>
/// <item>开启后仍需 **fail-closed**：供给未开启 / 供给配置非法 / 维护配置非法（如
/// <c>QueueCapacity</c> 超上限）⇒ 记 Error 说清原因，然后**什么都不做**
/// （绝不静默取默认值、绝不部分生效）。</item>
/// <item>scope 清单与预算**只有一个真源**（供给节）：本服务经既有
/// <see cref="FullTextIndexSupplyResolver"/> 取得已校验的绝对语料根，再经
/// <see cref="FullTextIndexMaintenanceOptions.ApplySingleSource"/> 把
/// <c>Scopes</c> / <c>WorkspaceRoot</c> / <c>MaxIndexBytes</c> / <c>IndexRootDirectory</c>
/// 填进组件选项 —— 宿主**不新增**第二份 scope 列表或第二个预算字面量。</item>
/// <item>幂等：宿主重复 <c>StartAsync</c> ⇒ 组件侧仍只收到**一次** <c>StartAsync</c>
/// （不重复建 watcher）；重复 <c>StopAsync</c> ⇒ 组件侧仍只收到**一次** <c>StopAsync</c>。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 为什么维护要求供给节也处于开启状态：scope 清单 / 相对基准 / 预算的唯一真源都在供给节，
/// 供给关闭时该节根本不会被解析（解析器返回空动作，连 scope 探针都不调用）。
/// 与其在宿主里另造一套解析，不如**拒绝**：让「配置写错了」可见，而不是猜一份 scope 出来。
/// </para>
/// </summary>
public sealed class FullTextIndexMaintenanceHostedService : IHostedService
{
    /// <summary>
    /// <c>StopAsync</c> 等待在途启动任务收尾的上界（防「停止时启动还没完成」把停止路径挂住）。
    /// 超时只记 Warning 并继续停止，不谎报成功。
    /// </summary>
    public static readonly TimeSpan DefaultStartCompletionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>等待在途启动任务收尾的上界；测试可调小。见 <see cref="DefaultStartCompletionTimeout"/>。</summary>
    public TimeSpan StartCompletionTimeout { get; init; } = DefaultStartCompletionTimeout;

    private readonly IOptions<MaintenanceOptions> _maintenanceOptions;
    private readonly IOptions<FullTextIndexSupplyOptions> _supplyOptions;
    private readonly FullTextIndexOptions _indexOptions;
    private readonly IFullTextIndexMaintenanceCompositionFactory _compositionFactory;
    private readonly ILogger<FullTextIndexMaintenanceHostedService> _logger;

    private readonly object _lifecycle = new();
    private bool _startRequested;
    private bool _stopRequested;
    private IFullTextIndexMaintenanceComposition? _composition;
    private Task? _startTask;

    /// <summary>构造服务。</summary>
    /// <param name="maintenanceOptions">
    /// 维护参数（Data 目录 <c>system.json</c> 的 <c>FullTextIndex:Maintenance</c> 子节，
    /// 直接绑定组件的 <see cref="MaintenanceOptions"/>）。
    /// </param>
    /// <param name="supplyOptions">供给参数（<c>FullTextIndex</c> 节）：scope 清单 / 基准 / 预算的唯一真源。</param>
    /// <param name="indexOptions">索引存储选项（索引根的唯一真源，与查询侧同一个实例）。</param>
    /// <param name="compositionFactory">维护组合的惰性工厂（默认关闭时**永不被调用**）。</param>
    /// <param name="logger">Logger.</param>
    public FullTextIndexMaintenanceHostedService(
        IOptions<MaintenanceOptions> maintenanceOptions,
        IOptions<FullTextIndexSupplyOptions> supplyOptions,
        FullTextIndexOptions indexOptions,
        IFullTextIndexMaintenanceCompositionFactory compositionFactory,
        ILogger<FullTextIndexMaintenanceHostedService> logger)
    {
        _maintenanceOptions = maintenanceOptions;
        _supplyOptions = supplyOptions;
        _indexOptions = indexOptions;
        _compositionFactory = compositionFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 首句门控（S5b I1）：默认关闭 ⇒ 零副作用 —— 不构造维护组合、不解析 scope、
        // 不碰索引根、不起 watcher / 线程，也不记 Error。
        if (!_maintenanceOptions.Value.Enabled)
            return Task.CompletedTask;

        lock (_lifecycle)
        {
            // 幂等：宿主重复启动 ⇒ 请求被合并，组件侧只收到一次 StartAsync。
            if (_startRequested)
                return Task.CompletedTask;

            _startRequested = true;
        }

        // 启动路径之外的异步作业；StartAsync 立即返回（维护器自身还要挂 watcher / 起体检线程）。
        var startTask = Task.Run(() => StartMaintenanceAsync(cancellationToken), cancellationToken);
        lock (_lifecycle)
        {
            _startTask = startTask;
        }

        return Task.CompletedTask;
    }

    private async Task StartMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var supplyOptions = _supplyOptions.Value;

            // ① scope 清单的唯一真源 = 既有供给解析器（不新增第二份列表、不复刻规范化规则）。
            var resolution = FullTextIndexSupplyResolver.Resolve(supplyOptions);
            if (resolution.IsNoOp)
            {
                _logger.LogError(
                    "[FullTextMaintenance] 维护已开启，但全文索引供给未开启（{Section}:Enabled=false）："
                    + "scope 清单 / 基准 / 预算的唯一真源在供给节 ⇒ 不启动维护（fail-closed，不猜 scope）。",
                    FullTextIndexSupplyOptions.SectionName);
                return;
            }

            if (!resolution.Succeeded)
            {
                _logger.LogError(
                    "[FullTextMaintenance] 供给配置校验未通过，不启动维护（fail-closed）：{Reasons}",
                    resolution.Describe());
                return;
            }

            // ② 生效维护选项：四项来自供给 / 查询侧同源，其余旋钮逐字来自维护节绑定值。
            var effectiveOptions = FullTextIndexMaintenanceOptions.ApplySingleSource(
                _maintenanceOptions.Value,
                supplyOptions,
                _indexOptions);

            // ③ fail-closed 校验（组件纯函数；Enabled=true 时连路径与 scope 域一起校验）。
            var validation = MaintenanceOptions.Validate(effectiveOptions);
            if (!validation.IsValid)
            {
                _logger.LogError(
                    "[FullTextMaintenance] 维护配置校验未通过，不启动维护（fail-closed，不取默认值）：{Violations}",
                    DescribeViolations(validation));
                return;
            }

            // ④ 组件装配（组合根；本服务不做组件装配）。
            var composition = _compositionFactory.Create(effectiveOptions, resolution.AcceptedScopes);

            lock (_lifecycle)
            {
                _composition = composition;
            }

            await composition.Maintenance.StartAsync(composition.Scopes, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[FullTextMaintenance] 局部维护已启动：{Count} scope(s) [{Scopes}]；"
                + "queueCapacity={QueueCapacity}, recoveryScanInterval={RecoveryScanInterval}, "
                + "maxIndexBytes={MaxIndexBytes}, indexRoot={IndexRoot}",
                composition.Scopes.Count,
                string.Join(", ", composition.Scopes.Select(static s => s.RootPath)),
                effectiveOptions.QueueCapacity,
                effectiveOptions.RecoveryScanInterval,
                effectiveOptions.MaxIndexBytes,
                effectiveOptions.IndexRootDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正在停止：启动被取消是预期控制流，不当失败。
            _logger.LogWarning("[FullTextMaintenance] 维护启动被取消（宿主正在停止）。");
        }
        catch (Exception ex)
        {
            // 后台路径不得静默吞掉异常：如实记录，且**不影响宿主其余功能**（维护是可选能力）。
            _logger.LogError(ex, "[FullTextMaintenance] 维护启动失败：未启动维护，其余功能不受影响。");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IFullTextIndexMaintenanceComposition? composition;
        Task? startTask;

        lock (_lifecycle)
        {
            // 幂等：宿主重复停止 ⇒ 组件侧只收到一次 StopAsync。
            if (_stopRequested)
                return;

            _stopRequested = true;
            composition = _composition;
            startTask = _startTask;
        }

        // 先等在途启动收尾：否则「启动还没挂上 watcher，停止就已经跑完」会留下残留 watcher。
        if (startTask is not null && !startTask.IsCompleted)
        {
            try
            {
                await startTask.WaitAsync(StartCompletionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "[FullTextMaintenance] 启动任务在收尾上界（{Timeout}）内未结束，仍继续停止维护。",
                    StartCompletionTimeout);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[FullTextMaintenance] 等待启动收尾被取消（宿主正在停止）。");
            }

            // 启动可能刚好完成：补读一次组合。
            lock (_lifecycle)
            {
                composition ??= _composition;
            }
        }

        if (composition is null)
        {
            // 从未装配（关闭 / fail-closed）⇒ 不调用组件 StopAsync：零副作用也包括「停止时不动它」。
            _logger.LogInformation("[FullTextMaintenance] 维护未启动，StopAsync 为空操作（零副作用）。");
            return;
        }

        await composition.Maintenance.StopAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = composition.Maintenance.GetSnapshot();
        _logger.LogInformation(
            "[FullTextMaintenance] 局部维护已停止：running={Running}, appliedBatches={AppliedBatches}, "
            + "failedBatches={FailedBatches}, lastError={LastError}",
            snapshot.Running,
            snapshot.AppliedBatchCount,
            snapshot.FailedBatchCount,
            snapshot.LastError ?? "(none)");
    }

    private static string DescribeViolations(MaintenanceOptionsValidationResult validation)
        => string.Join(
            " | ",
            validation.Violations.Select(static v => $"{v.Option}='{v.Value}' ({v.Message})"));
}
