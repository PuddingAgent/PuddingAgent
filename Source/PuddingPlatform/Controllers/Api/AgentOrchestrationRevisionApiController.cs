using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Authoring surface for immutable graph revisions (S1). Validation is side-effect free; saving a
/// revision performs a head compare-and-swap and never trusts client-authored audit fields. Route
/// graphId must always match the payload graphId.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orchestrations")]
public sealed class AgentOrchestrationRevisionApiController(
    AgentOrchestrationAuthoringService authoringService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();
    private static readonly Regex ElementSegmentRegex = new(
        @"^(?<kind>nodes|edges|triggers|inputs)\[(?<token>[^\]]+)\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex BareCollectionRegex = new(
        @"^(?<kind>nodes|edges|triggers|inputs)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PortSuffixRegex = new(
        @"\.inputs\.(?<port>[A-Za-z0-9._-]+)$",
        RegexOptions.CultureInvariant);

    /// <summary>Validates a draft without persisting anything.</summary>
    [HttpPost("graphs/{graphId}/validate")]
    public async Task<ActionResult<AgentOrchestrationDraftValidationResultDto>> ValidateDraftJson(
        string graphId,
        [FromBody] JsonElement body,
        CancellationToken ct = default)
    {
        if (!TryDeserializeBody(body, out AgentOrchestrationDraftValidateRequest? request))
            return InvalidOrchestrationBody();

        return await ValidateDraft(graphId, request!, ct);
    }

    /// <summary>Typed validation core kept separate from MVC body binding for deterministic tests.</summary>
    [NonAction]
    public async Task<ActionResult<AgentOrchestrationDraftValidationResultDto>> ValidateDraft(
        string graphId,
        AgentOrchestrationDraftValidateRequest request,
        CancellationToken ct = default)
    {
        if (request is null || !IdEquals(graphId, request.GraphId))
        {
            return BadRequest(new
            {
                code = "orchestration.revision_route_graph_mismatch",
                message = "Route graphId must match the request graphId."
            });
        }

        var result = await authoringService.ValidateAsync(request, ct);
        return new JsonResult(new AgentOrchestrationDraftValidationResultDto
        {
            IsValid = result.IsValid,
            NormalizedDefinition = result.NormalizedDefinition,
            Issues = result.Issues.Select(ProjectIssue).ToArray(),
            TopologicalNodeIds = result.TopologicalNodeIds
        }, JsonOptions);
    }

    /// <summary>
    /// Appends the next immutable revision. Returns 201 with the server-authored revision, 409 with
    /// current head facts on CAS conflict, 422 with diagnostics when the draft fails to compile, and
    /// 404 when the graph does not exist.
    /// </summary>
    [Authorize(Roles = "admin")]
    [HttpPut("graphs/{graphId}/revisions")]
    public async Task<ActionResult<AgentOrchestrationGraphDefinition>> PutRevisionJson(
        string graphId,
        [FromBody] JsonElement body,
        CancellationToken ct = default)
    {
        if (!TryDeserializeBody(body, out AgentOrchestrationRevisionWriteRequest? request))
            return InvalidOrchestrationBody();

        return await PutRevision(graphId, request!, ct);
    }

    /// <summary>Typed Revision command core; audit/CAS behavior is identical for HTTP and tests.</summary>
    [NonAction]
    public async Task<ActionResult<AgentOrchestrationGraphDefinition>> PutRevision(
        string graphId,
        AgentOrchestrationRevisionWriteRequest request,
        CancellationToken ct = default)
    {
        if (request?.Definition is null || !IdEquals(graphId, request.Definition.GraphId))
        {
            return BadRequest(new
            {
                code = "orchestration.revision_route_graph_mismatch",
                message = "Route graphId must match the payload definition graphId."
            });
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "admin-ui";
        var result = await authoringService.CreateRevisionAsync(
            new AgentOrchestrationRevisionCreateRequest
            {
                GraphId = graphId.Trim(),
                ExpectedCurrentRevision = request.ExpectedCurrentRevision,
                Definition = request.Definition
            },
            actorId,
            ct);
        if (result.Success)
        {
            return new JsonResult(result.Value, JsonOptions)
            {
                StatusCode = StatusCodes.Status201Created
            };
        }

        var payload = new
        {
            code = result.ErrorCode ?? "orchestration.revision_command_failed",
            message = result.ErrorMessage ?? "Revision command failed.",
            currentRevision = result.CurrentVersion,
            currentRevisionId = result.CurrentRevisionId,
            issues = result.Issues.Select(ProjectIssue).ToArray()
        };
        return result.Status switch
        {
            AgentOrchestrationStoreStatus.NotFound => NotFound(payload),
            AgentOrchestrationStoreStatus.Conflict => Conflict(payload),
            AgentOrchestrationStoreStatus.InvalidState
                when result.ErrorCode == "orchestration.definition_invalid" => UnprocessableEntity(payload),
            _ => BadRequest(payload)
        };
    }

    /// <summary>
    /// Projects a core issue onto the stable API contract. The compiler keeps Code/Message/Path;
    /// the API adds severity/elementType/elementId/portId so the editor can locate the canvas element.
    /// Values carried by the core issue win; path-derived element facts are the fallback so older
    /// issue producers (e.g. graph.cycle_detected on "edges") still project something useful.
    /// </summary>
    private static AgentOrchestrationValidationIssueDto ProjectIssue(AgentOrchestrationValidationIssue issue)
    {
        var (elementType, elementId, portId) = ProjectElement(issue.Path);
        return new AgentOrchestrationValidationIssueDto
        {
            Code = issue.Code,
            Message = issue.Message,
            Path = issue.Path,
            Severity = issue.Severity,
            ElementType = issue.ElementType ?? elementType,
            ElementId = issue.ElementId ?? elementId,
            PortId = issue.PortId ?? portId
        };
    }

    private static (string? ElementType, string? ElementId, string? PortId) ProjectElement(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, null, null);

        string? elementType = null;
        string? elementId = null;
        var segment = ElementSegmentRegex.Match(path);
        if (segment.Success)
        {
            elementType = MapElementKind(segment.Groups["kind"].Value);
            var token = segment.Groups["token"].Value;
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                elementId = token;
        }
        else
        {
            var bare = BareCollectionRegex.Match(path);
            if (bare.Success)
                elementType = MapElementKind(bare.Groups["kind"].Value);
        }

        var port = PortSuffixRegex.Match(path);
        var portId = port.Success ? port.Groups["port"].Value : null;
        return (elementType, elementId, portId);
    }

    private static string MapElementKind(string kind)
        => kind switch
        {
            "nodes" => "node",
            "edges" => "edge",
            "triggers" => "trigger",
            "inputs" => "input",
            var other => other
        };

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryDeserializeBody<T>(JsonElement body, out T? request)
        where T : class
    {
        request = null;
        if (body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        try
        {
            request = body.Deserialize<T>(JsonOptions);
            return request is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private BadRequestObjectResult InvalidOrchestrationBody()
        => BadRequest(new
        {
            code = "orchestration.request_json_invalid",
            message = "Request body does not match the pudding.agent-orchestration/v2 JSON contract."
        });
}

public sealed record AgentOrchestrationDraftValidationResultDto
{
    public required bool IsValid { get; init; }
    public AgentOrchestrationGraphDefinition? NormalizedDefinition { get; init; }
    public IReadOnlyList<AgentOrchestrationValidationIssueDto> Issues { get; init; }
        = Array.Empty<AgentOrchestrationValidationIssueDto>();
    public IReadOnlyList<string> TopologicalNodeIds { get; init; }
        = Array.Empty<string>();
}

public sealed record AgentOrchestrationValidationIssueDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
    public string Severity { get; init; } = "error";
    public string? ElementType { get; init; }
    public string? ElementId { get; init; }
    public string? PortId { get; init; }
}
