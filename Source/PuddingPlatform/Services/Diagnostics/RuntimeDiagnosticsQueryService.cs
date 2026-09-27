using PuddingCode.Diagnostics;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 运行诊断查询应用操作——把「时间线查询 + 脱敏」从 DiagnosticsTimelineController 原位下沉。
///
/// 与审计下沉同样的理由：脱敏原先只发生在控制器里，任何直接调用 RuntimeTimelineQueryService 的管理面
/// （例如原生客户端）拿到的是**未脱敏**的 Summary/Error/Metadata，而诊断文本里可能带 key/token/secret。
/// 现在脱敏与查询在同一个应用操作里完成，HTTP 与原生得到同一份已脱敏结果；脱敏策略也只有一处。
/// </summary>
public sealed class RuntimeDiagnosticsQueryService(
    RuntimeTimelineQueryService timeline,
    IDiagnosticRedactor redactor)
{
    public async Task<PagedTimelineResultDto> QueryTimelineAsync(
        RuntimeTimelineQueryDto query, CancellationToken ct = default)
    {
        var result = await timeline.QueryTimelineAsync(query, ct);
        return new PagedTimelineResultDto
        {
            Items = result.Items.Select(Redact).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
        };
    }

    public async Task<IReadOnlyList<RuntimeTimelineItemDto>> GetSessionTimelineAsync(
        string sessionId, CancellationToken ct = default)
        => (await timeline.GetSessionTimelineAsync(sessionId, ct)).Select(Redact).ToList();

    public Task<IReadOnlyList<RuntimeComponentHealthDto>> GetComponentHealthAsync(CancellationToken ct = default)
        => timeline.GetComponentHealthAsync(ct);

    /// <summary>Redaction policy lives here so every surface gets the same treatment.</summary>
    public RuntimeTimelineItemDto Redact(RuntimeTimelineItemDto item) => item with
    {
        Summary = redactor.RedactText(item.Summary),
        Error = redactor.RedactText(item.Error),
        Metadata = redactor.RedactMetadata(item.Metadata),
    };
}
