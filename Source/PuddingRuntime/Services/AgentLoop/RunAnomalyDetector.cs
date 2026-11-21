using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 进入检测窗口的单条观测。刻意做成纯值类型：检测器不接触会话、消息或数据库对象，
/// 单测可以直接构造输入，生产侧也只需要把事件映射成这四个字段。
/// 时间戳由调用方注入（检测器自己不看时钟），否则「频率」这一维无法在单测里复现。
/// </summary>
public readonly record struct RunAnomalyObservation(
    RunAnomalyObservationKind Kind,
    string? Content,
    string? ArgumentsJson,
    long ObservedAtUnixMs);

/// <summary>
/// 观测种类。<see cref="RoundBoundary"/> 只占窗口配额、用于划定轮次边界，
/// 不参与 S1/S2 计数——它没有可比对的载荷，硬算只会制造伪重复。
/// </summary>
public enum RunAnomalyObservationKind
{
    Assistant,
    Tool,
    RoundBoundary,
}

/// <summary>检测信号。S1 是复读机的直接证据，S2 是重复动作，S3 是空转/忙等。</summary>
public enum RunAnomalySignal
{
    None,
    RepeatedAssistantOutput,
    RepeatedToolAction,
    FrequencySpike,
}

/// <summary>
/// 检测器给出的动作。<see cref="Pause"/> 不是 kill：它只表示「停下等人裁决」，
/// 上下文保留，父代理可以 resume / sleep / destroy。
/// </summary>
public enum RunAnomalyAction
{
    None,
    Notice,
    Pause,
}

/// <summary>单次判定结果。</summary>
/// <param name="Action">本轮应执行的动作。</param>
/// <param name="Signal">命中的信号；动作非 None 时才有意义。</param>
/// <param name="HitCount">窗口内该信号样本的重复次数（S3 无样本，记 0）。</param>
/// <param name="WindowSize">当前窗口实际条目数（窗口未填满时小于配置值）。</param>
/// <param name="ConsecutiveHitWindows">连续命中的窗口数：达到 NoticeWindows 出 Notice，继续累加出 Pause。</param>
/// <param name="SampleHash">命中样本的归一化 hash（小写 hex 前 16 位），用于日志里定位到底重复了什么。</param>
public readonly record struct RunAnomalyVerdict(
    RunAnomalyAction Action,
    RunAnomalySignal Signal,
    int HitCount,
    int WindowSize,
    int ConsecutiveHitWindows,
    string? SampleHash);

/// <summary>
/// 检测器阈值。默认值对应设计稿第四节：20 条窗口、重复 3 次、连续两窗才暂停。
/// <para>
/// 注意 <see cref="PauseWindows"/> 必须是 2 而不是 1：第一窗口只留痕（notice），
/// 连续第二窗口才暂停。改成 1 就等于恢复「一命中即杀」，长任务里偶发重复会被误杀。
/// </para>
/// </summary>
public sealed record RunAnomalyDetectorOptions
{
    /// <summary>滑动窗口容量（观测条数）。</summary>
    public int WindowSize { get; init; } = 20;

    /// <summary>S1/S2 触发所需的重复次数。</summary>
    public int RepeatThreshold { get; init; } = 3;

    /// <summary>连续命中多少个窗口出 Notice。</summary>
    public int NoticeWindows { get; init; } = 1;

    /// <summary>连续命中多少个窗口出 Pause。</summary>
    public int PauseWindows { get; init; } = 2;

    /// <summary>S3 判定倍数：当前窗口 events/min 超过前两窗口均值多少倍算尖峰。</summary>
    public double FrequencySpikeRatio { get; init; } = 5.0;
}

/// <summary>
/// Run 级异常检测器（纯状态，无 IO、无时钟、无外部依赖）。
/// <para>
/// 为什么要有它：子代理的固定计数器（轮次 600 / 工具 2400）两头都错——正常长任务被误杀，
/// 而真正的复读机因为缓存命中率高反而跑得比正常任务还便宜，计数器完全挡不住。
/// 要看的是「原地打转」，不是「做了多少步」。
/// </para>
/// <para>
/// 语义：每来一条观测判一次（不是等窗口填满才判），判定基于最近 <see cref="RunAnomalyDetectorOptions.WindowSize"/>
/// 条观测的滑动窗口；窗口边界按观测条数划分（每 WindowSize 条收一个窗口），边界处结算
/// 「本窗口是否命中」，未命中则把连续命中计数清零。
/// </para>
/// <para>
/// 防误杀的关键是那条复位规则：偶发一两次相同调用只应留下一个 Notice，随后被干净窗口清账；
/// 没有它，长任务里分散出现三次相同读文件会一路累加成 Pause。
/// </para>
/// </summary>
public sealed class RunAnomalyDetector
{
    /// <summary>
    /// ISO 时间戳（含空格分隔与 ±HH:MM 时区偏移两种写法）。必须剥掉：
    /// 复读机每轮都会带上「现在几点」和新的请求 id，不剥的话同一段话的 hash 永远不同，检测器永不命中。
    /// </summary>
    private static readonly Regex IsoTimestampPattern = new(
        @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 长度 ≥8 的连续十六进制串（UUID 片段、trace id、短 hash）。与时间戳同理：
    /// 它们是「每次都不一样的噪声」，属于唯一化因子而不是内容。
    /// </summary>
    private static readonly Regex HexTokenPattern = new(
        @"[0-9a-fA-F]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>连续空白（含换行、制表）折叠成单个空格，消除排版抖动。</summary>
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RunAnomalyDetectorOptions _options;
    private readonly List<RunAnomalyObservation> _window = new();
    private readonly List<string?> _signatures = new();
    private readonly List<double> _recentWindowRates = new();

    private long _observationIndex;
    private long _windowFirstObservedAtMs;
    private bool _currentWindowHit;
    private RunAnomalySignal _currentWindowSignal;
    private int _currentWindowHitCount;
    private string? _currentWindowSampleHash;
    private int _consecutiveHitWindows;

    public RunAnomalyDetector(RunAnomalyDetectorOptions? options = null)
    {
        _options = options ?? new RunAnomalyDetectorOptions();

        if (_options.WindowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "WindowSize must be at least 1.");
        if (_options.RepeatThreshold < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "RepeatThreshold must be at least 2.");
        if (_options.NoticeWindows < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "NoticeWindows must be at least 1.");
        if (_options.PauseWindows < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "PauseWindows must be at least 1.");
        if (_options.FrequencySpikeRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "FrequencySpikeRatio must be positive.");
    }

    /// <summary>当前的滑动窗口内容（最旧→最新），供诊断与测试断言。</summary>
    public IReadOnlyList<RunAnomalyObservation> SnapshotWindow() => _window.ToArray();

    /// <summary>清空全部历史：目标被编辑、人类插话、或父代理 resume 之后重新计。</summary>
    public void Reset()
    {
        _window.Clear();
        _signatures.Clear();
        _recentWindowRates.Clear();
        _observationIndex = 0;
        _windowFirstObservedAtMs = 0;
        _currentWindowHit = false;
        _currentWindowSignal = RunAnomalySignal.None;
        _currentWindowHitCount = 0;
        _currentWindowSampleHash = null;
        _consecutiveHitWindows = 0;
    }

    /// <summary>
    /// 记一条观测并给出本次判定。判定是「到达即判」而不是「窗口期末才判」——
    /// 否则复读第 3 条时要等到窗口填满才发现，白跑十几轮。
    /// </summary>
    public RunAnomalyVerdict Observe(RunAnomalyObservation observation)
    {
        _observationIndex++;
        var windowSize = _options.WindowSize;

        if ((_observationIndex - 1) % windowSize == 0)
        {
            _windowFirstObservedAtMs = observation.ObservedAtUnixMs;
            _currentWindowHit = false;
            _currentWindowSignal = RunAnomalySignal.None;
            _currentWindowHitCount = 0;
            _currentWindowSampleHash = null;
        }

        var signature = BuildSignature(observation);
        _window.Add(observation);
        _signatures.Add(signature);
        while (_window.Count > windowSize)
        {
            _window.RemoveAt(0);
            _signatures.RemoveAt(0);
        }

        if (signature is not null)
        {
            var repeatCount = CountSignature(signature);
            if (repeatCount >= _options.RepeatThreshold)
            {
                MarkWindowHit(
                    observation.Kind == RunAnomalyObservationKind.Tool
                        ? RunAnomalySignal.RepeatedToolAction
                        : RunAnomalySignal.RepeatedAssistantOutput,
                    repeatCount,
                    signature);
            }
        }

        if (_observationIndex % windowSize == 0)
        {
            var rate = ComputeWindowRate(observation.ObservedAtUnixMs);
            ApplyFrequencySpike(rate);

            // 复位规则（防误杀核心）：一个窗口结束时若没有任何信号命中，连续命中计数归零 ——
            // 长任务里偶发一两次相同调用只留一个 Notice，不得在后续窗口里被累加成 Pause。
            if (!_currentWindowHit) _consecutiveHitWindows = 0;

            RememberWindowRate(rate);
        }

        return BuildVerdict();
    }

    private RunAnomalyVerdict BuildVerdict()
    {
        var action = RunAnomalyAction.None;
        if (_currentWindowHit)
        {
            if (_consecutiveHitWindows >= _options.PauseWindows)
                action = RunAnomalyAction.Pause;
            else if (_consecutiveHitWindows >= _options.NoticeWindows)
                action = RunAnomalyAction.Notice;
        }

        return new RunAnomalyVerdict(
            action,
            _currentWindowHit ? _currentWindowSignal : RunAnomalySignal.None,
            _currentWindowHit ? _currentWindowHitCount : 0,
            _window.Count,
            _consecutiveHitWindows,
            _currentWindowHit ? _currentWindowSampleHash : null);
    }

    /// <summary>
    /// 标记本窗口命中。同一窗口只升一次阶梯（否则窗口内每多一条重复就多顶一级，
    /// 第 4 条重复就变成 Pause，等于变相的一命中即杀）；
    /// 多信号同窗口命中时按 S1 &gt; S2 &gt; S3 的优先级保留最有说服力的那个。
    /// </summary>
    private void MarkWindowHit(RunAnomalySignal signal, int hitCount, string? sampleHash)
    {
        if (!_currentWindowHit)
        {
            _currentWindowHit = true;
            _consecutiveHitWindows++;
            _currentWindowSignal = signal;
            _currentWindowHitCount = hitCount;
            _currentWindowSampleHash = sampleHash;
            return;
        }

        if (SignalRank(signal) < SignalRank(_currentWindowSignal))
        {
            _currentWindowSignal = signal;
            _currentWindowHitCount = hitCount;
            _currentWindowSampleHash = sampleHash;
        }
    }

    private static int SignalRank(RunAnomalySignal signal) => signal switch
    {
        RunAnomalySignal.RepeatedAssistantOutput => 0,
        RunAnomalySignal.RepeatedToolAction => 1,
        RunAnomalySignal.FrequencySpike => 2,
        _ => int.MaxValue,
    };

    private int CountSignature(string signature)
    {
        var count = 0;
        foreach (var candidate in _signatures)
        {
            if (string.Equals(candidate, signature, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    /// <summary>
    /// 本窗口的 events/min。窗口首尾时间戳相同（同一毫秒内灌进来）时无法算频率，
    /// 返回 0 表示「这一窗不构成尖峰证据」，而不是当成无限快。
    /// </summary>
    private double ComputeWindowRate(long windowLastObservedAtMs)
    {
        var durationMs = windowLastObservedAtMs - _windowFirstObservedAtMs;
        if (durationMs <= 0)
            return 0d;

        return _options.WindowSize * 60_000d / durationMs;
    }

    /// <summary>
    /// S3：本窗口频率超过前两窗均值若干倍即为尖峰。只看已结算的窗口（未满的窗口没有稳定频率）；
    /// 只有一窗历史时用这一窗作基线，两窗起用均值。「连续两窗」由连续命中计数表达。
    /// </summary>
    private void ApplyFrequencySpike(double rate)
    {
        if (rate <= 0d || _recentWindowRates.Count == 0)
            return;

        var baseline = _recentWindowRates.Average();
        if (baseline <= 0d)
            return;

        if (rate <= baseline * _options.FrequencySpikeRatio)
            return;

        MarkWindowHit(RunAnomalySignal.FrequencySpike, 0, null);
    }

    private void RememberWindowRate(double rate)
    {
        _recentWindowRates.Add(rate);
        while (_recentWindowRates.Count > 2)
            _recentWindowRates.RemoveAt(0);
    }

    /// <summary>
    /// 归一化 + 哈希。返回 null 表示「这一条不参与重复计数」：
    /// 空内容（纯空白、或剥完噪声后什么都不剩）不算重复，否则两条空消息就能凑出一次假命中。
    /// </summary>
    private static string? BuildSignature(RunAnomalyObservation observation)
    {
        switch (observation.Kind)
        {
            case RunAnomalyObservationKind.Assistant:
            {
                var normalized = Normalize(observation.Content);
                return normalized is null ? null : HashHex(normalized);
            }

            case RunAnomalyObservationKind.Tool:
            {
                var toolName = observation.Content?.Trim();
                if (string.IsNullOrEmpty(toolName))
                    return null;

                var arguments = Normalize(observation.ArgumentsJson) ?? string.Empty;
                return HashHex(toolName + "\n" + arguments);
            }

            default:
                return null;
        }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // 顺序有意义：先剥时间戳（它自己也含 hex 片段，先剥免得被 hex 规则拆成碎片），
        // 再剥 ≥8 位 hex，最后才折叠空白并去首尾。
        var text = IsoTimestampPattern.Replace(raw, " ");
        text = HexTokenPattern.Replace(text, " ");
        text = WhitespacePattern.Replace(text, " ").Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// SHA256 前 16 位小写 hex。不用 string.GetHashCode()：它带进程级随机种子，
    /// 同一段文本在测试进程与生产进程会得到不同值，检测结果无法复现。
    /// </summary>
    private static string HashHex(string normalized)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }
}
