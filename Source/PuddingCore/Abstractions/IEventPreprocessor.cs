using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 事件预处理器 — 对 Connector 入站的原始事件进行去重和批处理。
/// 策略由各 Connector 配置，默认去重窗口 2s，批处理窗口 5s。
/// </summary>
public interface IEventPreprocessor
{
    /// <summary>
    /// 处理一批原始事件，返回合并后的处理后事件。
    /// </summary>
    Task<ProcessedEvent[]> ProcessAsync(RawEvent[] rawEvents, CancellationToken ct = default);

    /// <summary>
    /// 配置预处理策略。
    /// </summary>
    Task ConfigureAsync(PreprocessorConfig config, CancellationToken ct = default);
}

/// <summary>
/// 预处理策略配置。
/// </summary>
public sealed record PreprocessorConfig
{
    /// <summary>去重窗口（毫秒）。相同 sourceId+eventType 在此窗口内只保留最后一个。</summary>
    public int DedupWindowMs { get; init; } = 2000;

    /// <summary>批处理窗口（毫秒）。相同 sourceId 不同 eventType 打包为批处理事件。</summary>
    public int BatchWindowMs { get; init; } = 5000;

    /// <summary>针对特定 Connector 的覆盖配置。</summary>
    public Dictionary<string, ConnectorPreprocessorConfig>? ConnectorOverrides { get; init; }
}

/// <summary>
/// 单个 Connector 的预处理配置覆盖。
/// </summary>
public sealed record ConnectorPreprocessorConfig
{
    public int? DedupWindowMs { get; init; }
    public int? BatchWindowMs { get; init; }

    /// <summary>是否启用去重（默认 true）</summary>
    public bool DedupEnabled { get; init; } = true;

    /// <summary>是否启用批处理（默认 true）</summary>
    public bool BatchEnabled { get; init; } = true;
}
