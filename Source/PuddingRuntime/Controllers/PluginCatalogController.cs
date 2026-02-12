using Microsoft.AspNetCore.Mvc;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;

namespace PuddingRuntime.Controllers;

/// <summary>
/// Plugin catalog API for Phase 1 manifest-only loading.
/// Runtime owns plugin discovery because it owns tool execution; the API only reports the current
/// catalog snapshot and never loads DLLs or mutates the tool registry from the controller.
/// </summary>
[ApiController]
[Route("api/plugins")]
public sealed class PluginCatalogController : ControllerBase
{
    private readonly PluginManifestCatalog _catalog;
    private readonly PluginPackageInstaller _installer;
    private readonly PluginDiagnosticsReader _diagnostics;

    public PluginCatalogController(
        PluginManifestCatalog catalog,
        PluginPackageInstaller installer,
        PluginDiagnosticsReader diagnostics)
    {
        _catalog = catalog;
        _installer = installer;
        _diagnostics = diagnostics;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<PluginCatalogItemDto>> List()
        => Ok(_catalog.ListPlugins().Select(MapPlugin).ToList());

    [HttpGet("diagnostics")]
    public ActionResult<IReadOnlyList<PluginDiagnosticEventDto>> ListDiagnostics(
        [FromQuery] string? pluginId,
        [FromQuery] int limit = 50)
        => Ok(_diagnostics
            .ListRecent(new PluginDiagnosticsQuery(pluginId, limit))
            .Select(MapDiagnosticEvent)
            .ToList());

    [HttpPost("reload")]
    public ActionResult<PluginCatalogReloadResultDto> ReloadAll()
    {
        _catalog.Reload();
        var plugins = _catalog.ListPlugins();

        return Ok(new PluginCatalogReloadResultDto(
            PluginCount: plugins.Count,
            Message: "Plugin catalog reloaded. Manifest-only tools are visible in the runtime catalog; DLL execution is not enabled in Phase 1."));
    }

    [HttpGet("{pluginId}")]
    public ActionResult<PluginCatalogItemDto> Get(string pluginId)
    {
        var plugin = _catalog.ListPlugins()
            .FirstOrDefault(p => p.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

        return plugin is null
            ? NotFound()
            : Ok(MapPlugin(plugin));
    }

    [HttpPost("{pluginId}/reload")]
    public ActionResult<PluginReloadResultDto> Reload(string pluginId)
    {
        _catalog.Reload();
        var plugin = _catalog.ListPlugins()
            .FirstOrDefault(p => p.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return NotFound();

        return Ok(new PluginReloadResultDto(
            PluginId: plugin.PluginId,
            RequiresRestart: false,
            Message: "Plugin manifest reloaded. Manifest-only tools are visible in the runtime catalog; DLL execution is not enabled in Phase 1."));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    public async Task<ActionResult<PluginPackageInstallResultDto>> Upload(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file.Length <= 0)
            return BadRequest(new { error = "Plugin package file is empty." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _installer.InstallAsync(stream, file.FileName, ct);
            _catalog.Reload();
            return Ok(new PluginPackageInstallResultDto(
                PluginId: result.PluginId,
                Name: result.Name,
                Version: result.Version,
                RequiresRestart: result.RequiresRestart,
                Message: result.Message));
        }
        catch (PluginPackageValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static PluginCatalogItemDto MapPlugin(PluginCatalogEntry entry)
        => new(
            PluginId: entry.PluginId,
            Name: entry.Name,
            Version: entry.Version,
            Status: entry.Status.ToString(),
            StatusReason: entry.StatusReason,
            ToolCount: entry.Tools.Count,
            Tools: entry.Tools.Select(MapTool).ToList());

    private static PluginToolItemDto MapTool(ToolDescriptor descriptor)
        => new(
            ToolId: descriptor.ToolId,
            Name: descriptor.Name,
            Description: descriptor.Description,
            Category: descriptor.Category.ToString(),
            PermissionLevel: descriptor.PermissionLevel.ToString(),
            Safety: descriptor.Safety.ToString(),
            RuntimeStatus: descriptor.RuntimeStatus,
            IsEnabledByDefault: descriptor.IsEnabledByDefault,
            SortOrder: descriptor.SortOrder,
            Parameters: descriptor.Parameters);

    private static PluginDiagnosticEventDto MapDiagnosticEvent(PluginDiagnosticEvent evt)
        => new(
            EventId: evt.EventId,
            OccurredAtUtc: evt.OccurredAtUtc,
            EventType: evt.EventType,
            PluginId: evt.PluginId,
            PluginVersion: evt.PluginVersion,
            Status: evt.Status,
            Message: evt.Message,
            DurationMs: evt.DurationMs,
            Details: evt.Details);
}

public sealed record PluginCatalogItemDto(
    string PluginId,
    string Name,
    string Version,
    string Status,
    string StatusReason,
    int ToolCount,
    IReadOnlyList<PluginToolItemDto> Tools);

public sealed record PluginToolItemDto(
    string ToolId,
    string Name,
    string Description,
    string Category,
    string PermissionLevel,
    string Safety,
    string RuntimeStatus,
    bool IsEnabledByDefault,
    int SortOrder,
    ToolParameterSchema Parameters);

public sealed record PluginReloadResultDto(
    string PluginId,
    bool RequiresRestart,
    string Message);

public sealed record PluginCatalogReloadResultDto(
    int PluginCount,
    string Message);

public sealed record PluginPackageInstallResultDto(
    string PluginId,
    string Name,
    string Version,
    bool RequiresRestart,
    string Message);

public sealed record PluginDiagnosticEventDto(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string? PluginId,
    string? PluginVersion,
    string? Status,
    string? Message,
    long? DurationMs,
    IReadOnlyDictionary<string, string> Details);
