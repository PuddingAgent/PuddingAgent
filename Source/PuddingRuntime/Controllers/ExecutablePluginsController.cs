using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingRuntime.Services.Plugins;

namespace PuddingRuntime.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[Route("api/tool-plugins")]
public sealed class ExecutablePluginsController(ExecutablePluginService plugins) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new { catalog = plugins.Packages.Scan(), activations = plugins.Manager.List(),
        revision = plugins.Revision, workerInstalled = System.IO.File.Exists(ExecutablePluginService.WorkerPath),
        sdkDirectory = ExecutablePluginService.SdkDirectory, coreProcessId = Environment.ProcessId });

    [HttpGet("diagnostics")]
    public IActionResult Diagnostics() => Ok(plugins.Manager.Diagnostics());

    [HttpPost("import")]
    [RequestSizeLimit(65L * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct)
    {
        try { await using var stream = file.OpenReadStream(); return Ok(await plugins.Packages.ImportAsync(stream, ct)); }
        catch (PluginException ex) { return BadRequest(ex.Failure); }
    }

    [HttpPost("load")]
    public async Task<IActionResult> Load(PluginLoadRequest request, CancellationToken ct)
    {
        try
        {
            var result = await plugins.Manager.LoadAsync(request.OperationId, plugins.Find(request.ContentHash), request.Scope,
                new(request.ExecuteApproved, request.ApprovedCapabilities.ToHashSet(StringComparer.Ordinal), request.EnableHooks), request.ExpectedRevision, ct);
            return Ok(result);
        }
        catch (PluginException ex) { return StatusCode(ex.Failure.Code == "plugin.revision_conflict" ? 409 : 400, ex.Failure); }
    }

    [HttpPost("unload")]
    public async Task<IActionResult> Unload(PluginUnloadRequest request, CancellationToken ct)
    {
        try { return Ok(await plugins.Manager.UnloadAsync(request.ActivationId, request.Scope, request.ExpectedRevision, ct)); }
        catch (PluginException ex) { return StatusCode(ex.Failure.Code == "plugin.revision_conflict" ? 409 : 400, ex.Failure); }
    }
}
public sealed record PluginLoadRequest(string ContentHash, string OperationId, PluginScope Scope,
    bool ExecuteApproved, string[] ApprovedCapabilities, bool EnableHooks, long? ExpectedRevision);
public sealed record PluginUnloadRequest(string ActivationId, PluginScope Scope, long? ExpectedRevision);
