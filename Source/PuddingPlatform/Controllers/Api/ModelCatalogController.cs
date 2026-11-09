using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>PA-06B authorized model catalog: listing exposes only capability/price-source fields; resolve is actor-bound.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/models")]
public sealed class ModelCatalogController(ModelCatalogApiFactory factory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string workspaceId, [FromQuery] string? cursor = null,
        [FromQuery] int maxCount = 50, CancellationToken ct = default)
    {
        try
        {
            var api = factory.ForManagementUser(User);
            return Ok(await api.ListAsync(new ModelQuery(workspaceId, cursor, maxCount), ct));
        }
        catch (ModelCatalogException ex) { return Error(ex); }
    }

    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromBody] ModelSelectionRequest request, CancellationToken ct = default)
    {
        try
        {
            var api = factory.ForManagementUser(User);
            return Ok(await api.ResolveAsync(request, ct));
        }
        catch (ModelCatalogException ex) { return Error(ex); }
    }

    private IActionResult Error(ModelCatalogException ex) => StatusCode(ex.Code switch
    {
        ModelErrorCodes.ScopeForbidden => 403,
        ModelErrorCodes.PolicyMissing or ModelErrorCodes.CapabilityMissing => 404,
        ModelErrorCodes.RequestInvalid => 400,
        _ => 503,
    }, new { code = ex.Code, message = ex.Message });
}
