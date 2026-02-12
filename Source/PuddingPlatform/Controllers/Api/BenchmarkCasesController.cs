using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Hermes benchmark case catalog API.
/// List responses intentionally omit prompt text; details return only the selected task prompt.
/// </summary>
[Authorize]
[ApiController]
[Route("api/benchmark-cases")]
public sealed class BenchmarkCasesController(
    BenchmarkCaseCatalogService catalog,
    BenchmarkWorkspaceSeedService seedService,
    BenchmarkRunService runService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BenchmarkCaseSummaryDto>>> List(CancellationToken ct)
    {
        var cases = await catalog.ListAsync(ct);
        return Ok(cases);
    }

    [HttpGet("{caseId}")]
    public async Task<ActionResult<BenchmarkCaseDetailDto>> Get(string caseId, CancellationToken ct)
    {
        var item = await catalog.GetAsync(caseId, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{caseId}/prepare")]
    public async Task<ActionResult<BenchmarkPrepareResultDto>> Prepare(
        string caseId,
        [FromBody] BenchmarkPrepareRequestDto request,
        CancellationToken ct)
    {
        var item = await catalog.GetConfigAsync(caseId, ct);
        if (item is null)
            return NotFound();

        var seed = await seedService.PrepareAsync(item, request.WorkspaceId, ct);
        var run = await runService.CreateAsync(item, request.WorkspaceId, request.SessionId, seed, ct);
        return Ok(new BenchmarkPrepareResultDto
        {
            RunId = run.RunId,
            CaseId = item.Id,
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            Seed = seed,
        });
    }
}

public sealed record BenchmarkPrepareRequestDto
{
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; init; }
}

public sealed record BenchmarkPrepareResultDto
{
    public required string RunId { get; init; }
    public required string CaseId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public required BenchmarkSeedResultDto Seed { get; init; }
}
