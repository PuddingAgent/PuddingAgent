using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

// ────────────────────────────────────────────────────────────────────────
// 响应形状（D2）。字段名即契约；camelCase 由 ASP.NET 默认序列化策略决定。
// ⚠️ 本切片**只做全文索引块**：这里不得出现 codeIndex（符号索引属下一刀 S-A2）。
// ────────────────────────────────────────────────────────────────────────

/// <summary>全文索引状态响应根对象。</summary>
/// <param name="GeneratedAtUtc">快照生成时刻（UTC）。</param>
/// <param name="FullText">全文索引块（本切片唯一的块）。</param>
public sealed record FullTextIndexStatusSnapshot(
    DateTime GeneratedAtUtc,
    FullTextIndexStatusDetailSnapshot FullText);

/// <summary>全文索引块的配置真值 + 观测真值（每一项都可追溯到单一真源，见各属性注释）。</summary>
/// <param name="Configured">有效配置里是否存在 <c>FullTextIndex</c> 节。</param>
/// <param name="Enabled"><c>FullTextIndex:Enabled</c> 真值。</param>
/// <param name="IndexRoot">索引根目录（<c>FullTextIndexOptions.IndexRootDirectory</c>，宿主编译期绑定）。</param>
/// <param name="IndexRootExists">索引根目录当前是否存在（只读探测）。</param>
/// <param name="WorkspaceRoot">相对 scope 的解析基准（配置值）。</param>
/// <param name="MaxIndexBytes">单个索引库体积上限（配置值）。</param>
/// <param name="MinRebuildInterval">最小重建间隔（配置值）。</param>
/// <param name="AcceptedScopes">经 <c>FullTextIndexSupplyResolver.Resolve</c> 的**受理结果**。</param>
/// <param name="RejectedReasons">逐条拒绝原因（空数组表示无拒绝）。</param>
/// <param name="CompositionCreated">供给组合是否**已经**被构造（= accessor 的 Current 非空）。</param>
/// <param name="Maintenance">S5b 的局部维护循环开关（如实上报，见 D4）。</param>
/// <param name="Scopes">逐 scope 观测（只读）。</param>
/// <param name="Jobs">供给 job 台账（逐字取自 <c>ListStatusAsync</c>）。</param>
/// <param name="JobsReason">台账为空时的**如实原因**；有 job 时为 <c>null</c>。</param>
public sealed record FullTextIndexStatusDetailSnapshot(
    bool Configured,
    bool Enabled,
    string IndexRoot,
    bool IndexRootExists,
    string? WorkspaceRoot,
    long MaxIndexBytes,
    TimeSpan MinRebuildInterval,
    IReadOnlyList<string> AcceptedScopes,
    IReadOnlyList<string> RejectedReasons,
    bool CompositionCreated,
    FullTextIndexMaintenanceStatusSnapshot Maintenance,
    IReadOnlyList<FullTextIndexScopeStatusSnapshot> Scopes,
    IReadOnlyList<FullTextIndexJobStatusSnapshot> Jobs,
    string? JobsReason);

/// <summary>
/// 局部维护循环（<c>FullTextIndex:Maintenance</c>，S5b）的如实上报。
/// <para>
/// R5「未知 ≠ 新鲜」：配置节缺失 ⇒ <see cref="Configured"/> = false 且 <see cref="Enabled"/>
/// 取绑定类型的**声明默认值**；配置节存在但宿主侧**没有**该子节的绑定器 ⇒ 生效值**不可知** ⇒
/// <see cref="Enabled"/> = <c>null</c>（不得拿默认值冒充生效值）。
/// </para>
/// </summary>
/// <param name="Configured">有效配置里是否存在该子节。</param>
/// <param name="Enabled">生效开关；不可知为 <c>null</c>。</param>
/// <param name="Note">人可读说明（为什么是这个值）。</param>
public sealed record FullTextIndexMaintenanceStatusSnapshot(bool Configured, bool? Enabled, string Note);

/// <summary>
/// 单个 scope 的只读观测。
/// <para>
/// **降级口径（R5）**：路径解析不出来、或目录读不出来 ⇒ 整组目录字段一律 <c>null</c>
/// （含 <see cref="IndexDirectory"/>）—— 宁可「不知道」，不产出半真半假的组合；
/// 路径与内容都读到时才给真值。目录**解析得到但不存在**属于「读到了事实」：
/// <see cref="IndexDirectory"/> 有值、<see cref="IndexDirectoryExists"/> = false，计数类为 <c>null</c>。
/// </para>
/// </summary>
/// <param name="ScopePath">scope 的规范化绝对路径（配置受理结果）。</param>
/// <param name="ScopeExists">scope 目录当前是否存在。</param>
/// <param name="HasIndex">组件的 <c>HasIndex</c>（与 build/search 同口径）；探测失败为 <c>null</c>。</param>
/// <param name="IndexDirectory">索引目录（唯一真源：<c>IFullTextIndexRootedEngine.ResolveIndexDirectory</c>）。</param>
/// <param name="IndexDirectoryExists">索引目录是否存在（读不出来为 <c>null</c>）。</param>
/// <param name="IndexEntryCount">索引目录条目数（文件 + 子目录，递归）；未知为 <c>null</c>。</param>
/// <param name="IndexBytes">索引目录字节合计（递归）；未知为 <c>null</c>。</param>
/// <param name="IndexDirectoryLastWriteUtc">索引目录最后写入时间（UTC）；未知为 <c>null</c>。</param>
public sealed record FullTextIndexScopeStatusSnapshot(
    string ScopePath,
    bool ScopeExists,
    bool? HasIndex,
    string? IndexDirectory,
    bool? IndexDirectoryExists,
    int? IndexEntryCount,
    long? IndexBytes,
    DateTime? IndexDirectoryLastWriteUtc);

/// <summary>供给 job 的可观测状态（逐字取自 <c>IFullTextIndexSupplyCoordinator.ListStatusAsync</c>，不加工、不推断）。</summary>
/// <param name="JobId">job 标识（同 scope 的幂等合并共享同一个）。</param>
/// <param name="State">状态机状态（组件枚举名，逐字）。</param>
/// <param name="Phase">阶段（组件字符串枚举，逐字）。</param>
/// <param name="StartedAt">进入状态机的时刻（UTC）。</param>
/// <param name="FinishedAt">终态时刻（UTC）；非终态为 <c>null</c>。</param>
/// <param name="Message">最近一次可读说明（组件原文）。</param>
/// <param name="IndexedFileCount">清点到的可索引文件数（组件口径：<c>DiscoveredFileCount</c>）。</param>
/// <param name="TotalBytes">清点到的语料字节数（组件口径：<c>DiscoveredBytes</c>）。</param>
/// <param name="ElapsedMs">终态耗时（毫秒）；非终态为 <c>null</c>（不拿「现在」推算，避免随时间漂移的假事实）。</param>
public sealed record FullTextIndexJobStatusSnapshot(
    string JobId,
    string State,
    string Phase,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Message,
    int IndexedFileCount,
    long TotalBytes,
    long? ElapsedMs);

/// <summary>
/// 台账为空时的**如实原因**（I4：绝不伪造空台账）。
/// <para>
/// <c>composition-not-created</c> 是冻结规格点名的取值；另两个（组合已存在但台账为空 / 台账读取失败）
/// 是规格未覆盖的边界，由本实现补齐并如实登记（见切片报告 BLOCKERS 第 3 条）。
/// </para>
/// </summary>
public static class FullTextIndexStatusJobReasons
{
    /// <summary>供给组合**从未被构造**（例如默认关闭）—— 因此进程内不存在任何 job。</summary>
    public const string CompositionNotCreated = "composition-not-created";

    /// <summary>组合已存在（台账可读），但里面确实一条 job 都没有。</summary>
    public const string NoJobsRecorded = "no-jobs-recorded";

    /// <summary>组合已存在但台账读取失败（异常降级：HTTP 仍 200，如实报「读不到」而不是「空台账」）。</summary>
    public const string LedgerReadFailed = "ledger-read-failed";
}

/// <summary>
/// Slice S-A：全文索引**状态只读出口**（<c>GET /api/admin/index/status</c> 的数据来源）。
/// <para>
/// 三条硬约束：
/// <list type="number">
/// <item><b>零副作用</b>（R1）：只用「读」类 API（<c>Directory.Exists</c> / <c>GetFiles</c> /
/// <c>GetDirectories</c> / <c>GetLastWriteTimeUtc</c> / <c>File.Exists</c>）；不建目录、不写租约、
/// 不触发供给。</item>
/// <item><b>不构造组合</b>（R2）：只读 <see cref="IFullTextIndexSupplyAccessor.Current"/>，
/// **绝不**调用 <c>GetOrCreate</c> —— 于是「默认关闭」时查询本身不会把协调器 / 暂存构建器 /
/// 索引根「顺手」造出来。</item>
/// <item><b>不新增第二份真源</b>（R3）：索引目录一律问
/// <c>IFullTextIndexRootedEngine.ResolveIndexDirectory</c>；配置真值一律读数——
/// 配置来自 <c>IOptionsMonitor</c>（I6 要求换值即时可见），索引根来自 <c>FullTextIndexOptions</c> 单例。</item>
/// </list>
/// </para>
/// <para>
/// **降级不崩**（R5 / I8）：任何单点失败都被局部捕获并降级为 <c>null</c>，端点保持 200 ——
/// 「不知道」必须是可表达的，绝不能变成 500 或整块省略。
/// </para>
/// </summary>
public sealed class FullTextIndexStatusProbe
{
    /// <summary>维护循环配置子节的**节名**（仅在 <c>FullTextIndex</c> 节内定位用，不做绑定）。</summary>
    private const string MaintenanceSectionName = "Maintenance";

    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<FullTextIndexSupplyOptions> _supplyOptions;
    private readonly FullTextIndexOptions _indexOptions;
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly IFullTextIndexRootedEngine? _rootedEngine;
    private readonly IFullTextIndexSupplyAccessor _supplyAccessor;
    private readonly ILogger<FullTextIndexStatusProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="configuration">
    /// 有效配置（含 Data 目录 <c>system.json</c>）：用于判断「配置节是否存在」
    /// —— 绑定结果本身无法区分「节不存在」与「节存在但值等于默认」。
    /// </param>
    /// <param name="supplyOptions">供给参数（每次查询取 <c>CurrentValue</c>，因此配置改动能被看见）。</param>
    /// <param name="indexOptions">索引选项（索引根的单一真源）。</param>
    /// <param name="searchEngine">
    /// 全文索引引擎（生产 = live Lucene 实例）：<c>HasIndex</c> 与 build/search 同口径。
    /// </param>
    /// <param name="supplyAccessor">供给组合的共享访问器（只读 <c>Current</c>）。</param>
    /// <param name="logger">Logger.</param>
    public FullTextIndexStatusProbe(
        IConfiguration configuration,
        IOptionsMonitor<FullTextIndexSupplyOptions> supplyOptions,
        FullTextIndexOptions indexOptions,
        IFullTextSearchEngine searchEngine,
        IFullTextIndexSupplyAccessor supplyAccessor,
        ILogger<FullTextIndexStatusProbe> logger)
    {
        _configuration = configuration;
        _supplyOptions = supplyOptions;
        _indexOptions = indexOptions;
        _searchEngine = searchEngine;
        _supplyAccessor = supplyAccessor;
        _logger = logger;

        // 组合根的既有要求（Runtime.cs 的工厂注册处已显式断言）：引擎必须同时是 rooted 引擎。
        // 这里**不抛**：状态出口不能因为接缝缺失而 500，缺了就把「索引目录」如实降级为 null。
        _rootedEngine = searchEngine as IFullTextIndexRootedEngine;
    }

    /// <summary>构建一次状态快照（全程只读）。</summary>
    /// <param name="ct">取消令牌（仅用于台账读取；取消不改变「只读」性质）。</param>
    public async Task<FullTextIndexStatusSnapshot> BuildAsync(CancellationToken ct = default)
    {
        var options = _supplyOptions.CurrentValue;
        var resolution = FullTextIndexSupplyResolver.Resolve(options);

        // R2：只读 Current。这是「默认关闭 ⇒ 零副作用」的落点 —— 查询不得构造组合。
        var composition = _supplyAccessor.Current;

        var scopes = resolution.AcceptedScopes.Select(ObserveScope).ToList();
        var (jobs, jobsReason) = await ReadJobLedgerAsync(composition, ct).ConfigureAwait(false);

        var indexRoot = _indexOptions.IndexRootDirectory;

        return new FullTextIndexStatusSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            FullText: new FullTextIndexStatusDetailSnapshot(
                Configured: IsSectionConfigured(FullTextIndexSupplyOptions.SectionName),
                Enabled: options.Enabled,
                IndexRoot: indexRoot,
                IndexRootExists: Directory.Exists(indexRoot),
                WorkspaceRoot: options.WorkspaceRoot,
                MaxIndexBytes: options.MaxIndexBytes,
                MinRebuildInterval: options.MinRebuildInterval,
                AcceptedScopes: resolution.AcceptedScopes,
                RejectedReasons: DescribeRejections(resolution),
                CompositionCreated: composition is not null,
                Maintenance: ObserveMaintenance(),
                Scopes: scopes,
                Jobs: jobs,
                JobsReason: jobsReason));
    }

    // ── 配置真值（只读） ──────────────────────────────────────────────

    private bool IsSectionConfigured(string sectionName)
        => _configuration.GetSection(sectionName).Exists();

    /// <summary>
    /// D4：局部维护循环开关如实上报。
    /// <para>
    /// 本切片**不新增配置绑定**（规格明令）。因此：配置节缺失 ⇒ 走绑定类型的声明默认值
    /// （单一真源：<see cref="MaintenanceOptions.Enabled"/>，宿主不复刻字面量）；
    /// 配置节存在但宿主侧无绑定器 ⇒ 生效值不可知 ⇒ <c>null</c>（R5）。
    /// </para>
    /// </summary>
    private FullTextIndexMaintenanceStatusSnapshot ObserveMaintenance()
    {
        var sectionPath = $"{FullTextIndexSupplyOptions.SectionName}:{MaintenanceSectionName}";
        if (IsSectionConfigured(sectionPath))
        {
            return new FullTextIndexMaintenanceStatusSnapshot(
                Configured: true,
                Enabled: null,
                Note: "配置节存在，但宿主侧未发现该子节的绑定器：生效值不可知，如实报 null（R5：未知 ≠ 默认）。");
        }

        return new FullTextIndexMaintenanceStatusSnapshot(
            Configured: false,
            Enabled: new MaintenanceOptions().Enabled,
            Note: "配置节未提供，走默认（MaintenanceOptions.Enabled 的声明默认值）。");
    }

    private static IReadOnlyList<string> DescribeRejections(FullTextIndexSupplyResolution resolution)
    {
        if (resolution.Rejections.Count == 0 && resolution.RejectedScopes.Count == 0)
            return [];

        var reasons = new List<string>(resolution.Rejections.Count + resolution.RejectedScopes.Count);
        reasons.AddRange(resolution.Rejections.Select(static r => $"{r.ParameterName}: {r.Message}"));
        reasons.AddRange(resolution.RejectedScopes.Select(static r => $"Scopes['{r.Scope}']: {r.Message}"));
        return reasons;
    }

    // ── job 台账（只读；未构造时如实说明原因） ──────────────────────────

    private async Task<(IReadOnlyList<FullTextIndexJobStatusSnapshot> Jobs, string? Reason)> ReadJobLedgerAsync(
        IFullTextIndexSupplyComposition? composition,
        CancellationToken ct)
    {
        if (composition is null)
            return ([], FullTextIndexStatusJobReasons.CompositionNotCreated);

        try
        {
            var statuses = await composition.Coordinator.ListStatusAsync(ct).ConfigureAwait(false);
            var jobs = statuses.Select(MapJob).ToList();

            return jobs.Count == 0
                ? (jobs, FullTextIndexStatusJobReasons.NoJobsRecorded)
                : (jobs, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 台账读不出来 ≠ 台账为空（R5）：如实降级，端点仍 200。
            _logger.LogWarning(ex, "[IndexStatus] Reading the supply job ledger failed; reporting it as unreadable.");
            return ([], FullTextIndexStatusJobReasons.LedgerReadFailed);
        }
    }

    private static FullTextIndexJobStatusSnapshot MapJob(SupplyJobStatus status) => new(
        JobId: status.JobId,
        State: status.State.ToString(),
        Phase: status.Phase,
        StartedAt: status.StartedAt.UtcDateTime,
        FinishedAt: status.FinishedAt?.UtcDateTime,
        Message: status.Message,
        IndexedFileCount: status.DiscoveredFileCount,
        TotalBytes: status.DiscoveredBytes,
        // 只在终态给时长：非终态若用「现在 - 开始」推算，同一次观测会随时间漂移，不是快照事实。
        ElapsedMs: status.FinishedAt is { } finished
            ? (long)(finished - status.StartedAt).TotalMilliseconds
            : null);

    // ── 逐 scope 只读观测 ────────────────────────────────────────────

    private FullTextIndexScopeStatusSnapshot ObserveScope(string scopePath)
    {
        var scopeExists = Directory.Exists(scopePath);

        bool? hasIndex = null;
        try
        {
            hasIndex = _searchEngine.HasIndex(scopePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "[IndexStatus] HasIndex failed for {Scope}; reporting unknown.", scopePath);
        }

        string? indexDirectory = null;
        try
        {
            indexDirectory = _rootedEngine?.ResolveIndexDirectory(scopePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "[IndexStatus] Resolving the index directory for {Scope} failed; reporting unknown.",
                scopePath);
        }

        if (indexDirectory is null)
            return UnknownDirectory(scopePath, scopeExists, hasIndex);

        var observed = TryObserveDirectory(indexDirectory);
        if (observed is not { } directory)
        {
            // 目录读不出来（含「路径被文件占住」）⇒ 整组目录字段降级为 null（含 indexDirectory，R5/I8）。
            _logger.LogDebug(
                "[IndexStatus] Index directory for {Scope} could not be read; reporting unknown.",
                scopePath);
            return UnknownDirectory(scopePath, scopeExists, hasIndex);
        }

        return new FullTextIndexScopeStatusSnapshot(
            ScopePath: scopePath,
            ScopeExists: scopeExists,
            HasIndex: hasIndex,
            IndexDirectory: indexDirectory,
            IndexDirectoryExists: directory.Exists,
            IndexEntryCount: directory.EntryCount,
            IndexBytes: directory.Bytes,
            IndexDirectoryLastWriteUtc: directory.LastWriteUtc);
    }

    private static FullTextIndexScopeStatusSnapshot UnknownDirectory(
        string scopePath,
        bool scopeExists,
        bool? hasIndex) =>
        new(
            ScopePath: scopePath,
            ScopeExists: scopeExists,
            HasIndex: hasIndex,
            IndexDirectory: null,
            IndexDirectoryExists: null,
            IndexEntryCount: null,
            IndexBytes: null,
            IndexDirectoryLastWriteUtc: null);

    /// <summary>目录观察结果（只读）。计数类只在目录**确实存在且可读**时才有值。</summary>
    private readonly record struct DirectoryObservation(
        bool Exists,
        int? EntryCount,
        long? Bytes,
        DateTime? LastWriteUtc);

    /// <summary>
    /// 只读观察一个目录：存在性 + 递归条目数 + 字节数 + 最后写入时间。
    /// <para>
    /// 语义（与 CLI <c>status</c> 同口径，只读）：
    /// 目录存在 ⇒ <c>Exists=true</c> 且给出计数；
    /// 目录**不存在** ⇒ <c>Exists=false</c> 且计数为 <c>null</c>（「没有」不等于「0 字节」）；
    /// 目录**读不出来**（含路径被文件占住 / 无权限 / 路径非法）⇒ <c>null</c>（未知）。
    /// </para>
    /// </summary>
    private static DirectoryObservation? TryObserveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                var entries = 0;
                long bytes = 0;

                var pending = new Stack<string>();
                pending.Push(directory);

                while (pending.Count > 0)
                {
                    var current = pending.Pop();

                    string[] files;
                    string[] subDirectories;
                    try
                    {
                        files = Directory.GetFiles(current);
                        subDirectories = Directory.GetDirectories(current);
                    }
                    catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                    {
                        // 局部化：单个子目录读不了不放弃整次观察（与 CLI status 同策略）。
                        continue;
                    }

                    foreach (var file in files)
                    {
                        entries++;
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
                        {
                            // 文件在观察间隙消失 / 不可读：条目数照计，字节数不计（不做猜测）。
                        }
                    }

                    foreach (var subDirectory in subDirectories)
                    {
                        entries++;
                        pending.Push(subDirectory);
                    }
                }

                var lastWrite = Directory.GetLastWriteTimeUtc(directory);

                return new DirectoryObservation(
                    Exists: true,
                    EntryCount: entries,
                    Bytes: bytes,
                    // 时间戳读不到时 API 返回 DateTime.MinValue ⇒ 如实转为 null（R5：未知 ≠ 新鲜）。
                    LastWriteUtc: lastWrite == DateTime.MinValue ? null : lastWrite);
            }

            // 索引路径被一个**文件**占住：这不是「目录不存在」，而是「读不出来」⇒ null。
            return File.Exists(directory) ? null : new DirectoryObservation(false, null, null, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
