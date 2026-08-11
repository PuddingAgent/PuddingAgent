using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Admin-only Graph lifecycle surface. Creation always enters through the trusted compiler;
/// deletion is CAS-guarded and refuses to erase any graph that owns durable run history.
/// </summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/orchestrations")]
public sealed class AgentOrchestrationManagementApiController(IAgentOrchestrationStore store)
    : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();
    private static readonly Regex GraphIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant);

    [HttpPost("graphs")]
    public async Task<ActionResult<AgentOrchestrationGraphDefinition>> CreateGraph(
        [FromBody] AgentOrchestrationGraphCreateRequest request,
        CancellationToken ct = default)
    {
        var validationError = ValidateCreateRequest(request);
        if (validationError is not null)
            return BadRequest(validationError);

        var graphId = request.GraphId.Trim();
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "admin-ui";
        var templateId = string.IsNullOrWhiteSpace(request.TemplateId)
            ? "blank"
            : request.TemplateId.Trim();
        var isImageGeneration = string.Equals(templateId, "image-generation", StringComparison.OrdinalIgnoreCase);
        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = graphId,
            RevisionId = $"{graphId}/r001",
            Revision = 1,
            WorkspaceId = request.WorkspaceId.Trim(),
            RootSessionId = request.RootSessionId.Trim(),
            CreatedByAgentId = actorId,
            Objective = request.Objective.Trim(),
            RequiresExplicitActivation = true,
            MaxConcurrency = request.MaxConcurrency,
            Inputs = isImageGeneration
                ?
                [
                    new AgentOrchestrationGraphInput
                    {
                        InputId = "prompt",
                        Contract = new AgentOrchestrationDataContract
                        {
                            DataType = AgentOrchestrationDataTypes.Content,
                            MediaTypes = ["text/plain"],
                            Cardinality = AgentOrchestrationPortCardinality.One,
                            Deliveries = [AgentOrchestrationValueDelivery.Inline]
                        },
                        RequiredAtActivation = true
                    }
                ]
                : [],
            Nodes = isImageGeneration
                ?
                [
                    new AgentOrchestrationNodeDefinition
                    {
                        NodeId = "image-generate",
                        Kind = AgentOrchestrationNodeKind.Tool,
                        Title = "生成图片",
                        Objective = "根据 Prompt 生成一张图片并保存为工作区 Artifact。",
                        Component = new AgentOrchestrationComponentReference
                        {
                            ComponentType = AgentOrchestrationComponentTypes.ImageGenerate,
                            Version = "1"
                        },
                        Executor = new AgentOrchestrationExecutorBinding
                        {
                            Kind = AgentOrchestrationExecutorKind.Tool,
                            ToolId = "generate_image"
                        },
                        GraphInputBindings =
                        [
                            new AgentOrchestrationGraphInputBinding
                            {
                                InputId = "prompt",
                                TargetPortId = "prompt"
                            }
                        ],
                        ExpectedOutputContract = AgentOrchestrationDataTypes.Artifact,
                        Configuration = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["mode"] = JsonSerializer.SerializeToElement("default"),
                            ["size"] = JsonSerializer.SerializeToElement("2K"),
                            ["watermark"] = JsonSerializer.SerializeToElement(true),
                            ["outputFormat"] = JsonSerializer.SerializeToElement("png")
                        },
                        PermissionMode = AgentOrchestrationPermissionMode.ReadOnly,
                        FailureBehavior = AgentOrchestrationFailureBehavior.FailRun,
                        MaxAttempts = 1,
                        TimeoutSeconds = 240
                    },
                    new AgentOrchestrationNodeDefinition
                    {
                        NodeId = "image-preview",
                        Kind = AgentOrchestrationNodeKind.Tool,
                        Title = "展示图片",
                        Objective = "接收上游图片 Artifact，在编排节点中展示并透传输出。",
                        Component = new AgentOrchestrationComponentReference
                        {
                            ComponentType = AgentOrchestrationComponentTypes.ImagePreview,
                            Version = "1"
                        },
                        Executor = new AgentOrchestrationExecutorBinding
                        {
                            Kind = AgentOrchestrationExecutorKind.Tool,
                            ToolId = "preview_image"
                        },
                        ExpectedOutputContract = AgentOrchestrationDataTypes.Artifact,
                        PermissionMode = AgentOrchestrationPermissionMode.ReadOnly,
                        FailureBehavior = AgentOrchestrationFailureBehavior.FailRun,
                        MaxAttempts = 1,
                        TimeoutSeconds = 30
                    }
                ]
                :
                [
                    new AgentOrchestrationNodeDefinition
                    {
                        NodeId = "start",
                        Kind = AgentOrchestrationNodeKind.HumanInput,
                        Title = "Start",
                        Objective = "Collect the initial input required by this orchestration.",
                        Component = new AgentOrchestrationComponentReference
                        {
                            ComponentType = AgentOrchestrationComponentTypes.HumanInput,
                            Version = "1"
                        },
                        ExpectedOutputContract = AgentOrchestrationDataTypes.Content,
                        PermissionMode = AgentOrchestrationPermissionMode.ReadOnly,
                        FailureBehavior = AgentOrchestrationFailureBehavior.AwaitDecision,
                        MaxAttempts = 1
                    }
                ],
            Edges = isImageGeneration
                ?
                [
                    new AgentOrchestrationEdgeDefinition
                    {
                        EdgeId = "image-generate-to-preview",
                        FromNodeId = "image-generate",
                        ToNodeId = "image-preview",
                        Kind = AgentOrchestrationEdgeKind.Data,
                        Condition = AgentOrchestrationEdgeCondition.OnSuccess,
                        Bindings =
                        [
                            new AgentOrchestrationDataBinding
                            {
                                SourcePortId = "images",
                                TargetPortId = "images",
                                Aggregation = AgentOrchestrationDataAggregation.Append
                            }
                        ]
                    }
                ]
                : [],
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["createdFrom"] = "admin-orchestration-editor",
                ["templateId"] = templateId
            }
        };
        var result = await store.SaveRevisionAsync(
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = definition,
                ExpectedCurrentRevision = 0
            },
            ct);
        if (result.Success)
        {
            return new JsonResult(result.Value, JsonOptions)
            {
                StatusCode = StatusCodes.Status201Created
            };
        }

        return ToErrorResult(result);
    }

    [HttpDelete("graphs/{graphId}")]
    public async Task<ActionResult<AgentOrchestrationGraphDeleteReceipt>> DeleteGraph(
        string graphId,
        [FromQuery] int expectedCurrentRevision,
        CancellationToken ct = default)
    {
        var result = await store.DeleteGraphAsync(
            new AgentOrchestrationGraphDeleteRequest
            {
                GraphId = graphId,
                ExpectedCurrentRevision = expectedCurrentRevision
            },
            ct);
        if (result.Success)
            return new JsonResult(result.Value, JsonOptions);

        return ToErrorResult(result);
    }

    private static object? ValidateCreateRequest(AgentOrchestrationGraphCreateRequest? request)
    {
        if (request is null)
            return new { code = "orchestration.create_request_required", message = "Request is required." };
        if (string.IsNullOrWhiteSpace(request.GraphId) || !GraphIdPattern.IsMatch(request.GraphId.Trim()))
        {
            return new
            {
                code = "orchestration.graph_id_invalid",
                message = "GraphId must start with a letter or digit and contain only letters, digits, '.', '_' or '-'."
            };
        }
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return new { code = "orchestration.workspace_id_required", message = "WorkspaceId is required." };
        if (string.IsNullOrWhiteSpace(request.RootSessionId))
            return new { code = "orchestration.root_session_id_required", message = "RootSessionId is required." };
        if (string.IsNullOrWhiteSpace(request.Objective))
            return new { code = "orchestration.objective_required", message = "Objective is required." };
        if (request.MaxConcurrency is < 1 or > 64)
            return new { code = "orchestration.max_concurrency_invalid", message = "MaxConcurrency must be between 1 and 64." };
        if (!string.IsNullOrWhiteSpace(request.TemplateId) &&
            !string.Equals(request.TemplateId.Trim(), "blank", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.TemplateId.Trim(), "image-generation", StringComparison.OrdinalIgnoreCase))
        {
            return new { code = "orchestration.template_invalid", message = "TemplateId must be blank or image-generation." };
        }
        return null;
    }

    private ActionResult ToErrorResult<T>(AgentOrchestrationStoreResult<T> result)
        where T : class
    {
        var payload = new
        {
            code = result.ErrorCode ?? "orchestration.command_failed",
            message = result.ErrorMessage ?? "Orchestration command failed.",
            currentRevision = result.CurrentVersion
        };
        return result.Status switch
        {
            AgentOrchestrationStoreStatus.NotFound => NotFound(payload),
            AgentOrchestrationStoreStatus.Conflict => Conflict(payload),
            AgentOrchestrationStoreStatus.InvalidState
                when result.ErrorCode == "orchestration.graph_has_runs" => Conflict(payload),
            _ => BadRequest(payload)
        };
    }
}

public sealed record AgentOrchestrationGraphCreateRequest
{
    public required string GraphId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootSessionId { get; init; }
    public required string Objective { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public string TemplateId { get; init; } = "blank";
}
