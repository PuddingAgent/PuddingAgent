using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>可观测性与调试面板。</summary>
public class ObservabilityController : Controller
{
    private readonly PlatformApiClient _api;

    public ObservabilityController(PlatformApiClient api) => _api = api;

    public async Task<IActionResult> Index(string? sessionId, string? messageId, string? workspaceId, CancellationToken ct)
    {
        var summary = await _api.GetDebugSummaryAsync(ct);
        var metrics = await _api.GetDebugMetricsAsync(ct);
        SessionDebugDto? sessionDebug = null;
        MessageDebugDto? messageDebug = null;
        WorkspaceDebugDto? workspaceDebug = null;

        if (!string.IsNullOrWhiteSpace(sessionId))
            sessionDebug = await _api.GetSessionDebugAsync(sessionId, ct);
        if (!string.IsNullOrWhiteSpace(messageId))
            messageDebug = await _api.GetMessageDebugAsync(messageId, ct);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            workspaceDebug = await _api.GetWorkspaceDebugAsync(workspaceId, ct);

        var vm = new ObservabilityViewModel
        {
            SessionId = sessionId,
            MessageId = messageId,
            WorkspaceId = workspaceId,
            Summary = summary,
            Metrics = metrics,
            SessionDebug = sessionDebug,
            MessageDebug = messageDebug,
            WorkspaceDebug = workspaceDebug
        };

        return View(vm);
    }
}

public sealed record ObservabilityViewModel
{
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? WorkspaceId { get; init; }
    public DebugSummaryDto? Summary { get; init; }
    public DebugMetricsDto? Metrics { get; init; }
    public SessionDebugDto? SessionDebug { get; init; }
    public MessageDebugDto? MessageDebug { get; init; }
    public WorkspaceDebugDto? WorkspaceDebug { get; init; }
}
