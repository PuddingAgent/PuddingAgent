using System.Diagnostics;
using Serilog;

namespace PuddingHost.Hosting;

/// <summary>一次启动阶段打点：阶段名 + 自进程启动以来的总耗时 + 与上一阶段的增量。</summary>
public readonly record struct StartupPhaseMark(string Phase, long TotalMs, long DeltaMs);

/// <summary>
/// 启动阶段名（唯一真源：调用点与测试都引用这里，避免字符串漂移导致埋点静默失效）。
/// </summary>
public static class StartupPhases
{
    public const string ProcessStart = "process-start";
    public const string OptionsResolved = "options-resolved";
    public const string DataRootLease = "data-root-lease";
    public const string DataRootBootstrapped = "data-root-bootstrapped";
    public const string LoggingReady = "logging-ready";
    public const string ServicesRegistered = "services-registered";
    public const string HostBuilt = "host-built";
    public const string MiddlewareMapped = "middleware-mapped";
    public const string Initialized = "initialized";
    public const string ServerStarted = "server-started";
    public const string Ready = "ready";
}

/// <summary>
/// 启动阶段计时器。
///
/// <para><b>动机（2026-09-30 实测诊断）</b>：Core 启动耗时 21.5 秒，而系统日志文件
/// （<c>D:\data\logs\system\pudding-*.log</c>）的**第一行**出现在进程启动后 17.5 秒
/// ⇒ 日志管线建立之前的那段启动时间**无法从日志归因**。本类在每个阶段边界打点。</para>
///
/// <para><b>输出三份</b>：① stdout（Desktop 捕获；日志管线就绪前它是唯一可用通道）
/// ② Serilog（落系统日志文件，供事后取证）③ 内存 <see cref="Marks"/>（供测试与诊断读取）。</para>
///
/// <para><b>纯逻辑</b>：无文件 IO、无静态可变状态、无线程、不读环境；时钟与输出通道均可注入，
/// 因此可确定性测试。</para>
/// </summary>
public sealed class StartupPhaseTracker
{
    private readonly Func<long> _elapsedMs;
    private readonly List<StartupPhaseMark> _marks = new();
    private long _lastMs;

    public StartupPhaseTracker(Func<long> elapsedMs, Action<string>? sink = null)
    {
        _elapsedMs = elapsedMs ?? throw new ArgumentNullException(nameof(elapsedMs));
        Sink = sink;
    }

    private Action<string>? Sink { get; }

    /// <summary>已打点序列（按打点顺序）。</summary>
    public IReadOnlyList<StartupPhaseMark> Marks => _marks;

    /// <summary>真实秒表 + stdout/Serilog 双通道。</summary>
    public static StartupPhaseTracker Start()
    {
        var stopwatch = Stopwatch.StartNew();
        return new StartupPhaseTracker(
            () => stopwatch.ElapsedMilliseconds,
            line =>
            {
                Console.WriteLine(line);
                Log.Information("{StartupPhaseLine}", line);
            });
    }

    /// <summary>打一个阶段点：返回该点事实，并通过 sink 输出一行。</summary>
    public StartupPhaseMark Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            throw new ArgumentException("phase 不能为空", nameof(phase));

        var total = _elapsedMs();
        // 单调兜底：时钟回拨/计时精度不足时**不得**产生负增量，否则日志会把耗时归因到错误阶段。
        if (total < _lastMs)
            total = _lastMs;

        var mark = new StartupPhaseMark(phase, total, total - _lastMs);
        _lastMs = total;
        _marks.Add(mark);

        Sink?.Invoke(Format(mark));
        return mark;
    }

    /// <summary>行格式（契约：一行一个阶段，含 total 与 delta，便于 grep 与人工归因）。</summary>
    public static string Format(StartupPhaseMark mark) =>
        $"[StartupPhase] {mark.Phase} total={mark.TotalMs}ms delta={mark.DeltaMs}ms";
}
