using PuddingCode.SubAgents;

namespace PuddingCode.Abstractions;

/// <summary>
/// 子代理运行时诊断服务 — 扫描运行归档并生成聚合统计报告。
/// </summary>
public interface ISubAgentDiagnosticsService
{
    /// <summary>
    /// 根据请求参数扫描子代理运行归档，返回诊断报告。
    /// </summary>
    Task<SubAgentDiagnosticsReport> GetDiagnosticsAsync(
        SubAgentDiagnosticsRequest request,
        CancellationToken ct = default);

    /// <summary>读取单个 run 的 events.jsonl，计算 LLM/Tool/Overhead 耗时分解。</summary>
    Task<SubAgentLatencyBreakdown?> GetRunLatencyBreakdownAsync(
        string runId, CancellationToken ct = default);

    /// <summary>
    /// 生成诊断报告的文本摘要，末尾包含生成时间戳。
    /// </summary>
    Task<string> GetDiagnosticsReportAsync(
        SubAgentDiagnosticsRequest request,
        CancellationToken ct = default);
}
