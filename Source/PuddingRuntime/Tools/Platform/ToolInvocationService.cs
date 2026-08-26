using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Agents;
using PuddingCode.Observability;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services;

/// <summary>
/// 工具调用 Facade，统一权限、审计、耗时、错误处理。
/// 统一适配运行时 Tool 注册表，调用方不需要知道工具来自原生 IPuddingTool 还是 legacy IAgentSkill。
/// </summary>
public sealed class ToolInvocationService : IToolInvocationService
{
    private readonly IPuddingToolExecutionService _toolExecutionService;
    private readonly IAgentWorkspaceGuard? _workspaceGuard;
    private readonly IRuntimeControlService? _runtimeControl;
    private readonly IIdleDetector? _idleDetector;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly ILogger<ToolInvocationService> _logger;

    public ToolInvocationService(
        IPuddingToolExecutionService toolExecutionService,
        IAgentWorkspaceGuard? workspaceGuard = null,
        ILogger<ToolInvocationService>? logger = null,
        IRuntimeControlService? runtimeControl = null,
        IIdleDetector? idleDetector = null,
        ITelemetryMetricSink? telemetrySink = null)
    {
        _toolExecutionService = toolExecutionService;
        _workspaceGuard = workspaceGuard;
        _runtimeControl = runtimeControl;
        _idleDetector = idleDetector;
        _telemetrySink = telemetrySink;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolInvocationService>.Instance;
    }

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var compatibility = HarnessToolCompatibilityAdapter.Normalize(
            request.ToolName,
            request.ArgumentsJson);
        if (compatibility.Adapted)
        {
            _logger.LogInformation(
                "[ToolInvocation] Harness compatibility adapted tool={RequestedTool}->{CanonicalTool} callId={ToolCallId}",
                compatibility.RequestedToolName,
                compatibility.ToolName,
                request.ToolCallId);
            await RecordHarnessCompatibilityMetricAsync(
                compatibility,
                request,
                startedAt,
                ct);
            request = request with
            {
                ToolName = compatibility.ToolName,
                ArgumentsJson = compatibility.ArgumentsJson,
            };
        }
        var argsHash = ComputeArgsHash(request.ArgumentsJson);

        _logger.LogDebug(
            "[ToolInvocation] Invoke tool={ToolName} callId={ToolCallId} session={SessionId}",
            request.ToolName, request.ToolCallId, request.SessionId);

        var runtimeDecision = _runtimeControl?.CanInvokeTool(request.SessionId, request.ToolName);
        if (runtimeDecision is { Allowed: false })
        {
            var fuse = _runtimeControl!.RecordError(
                request.SessionId,
                RuntimeErrorKind.Tool,
                request.ToolName,
                runtimeDecision.Message);
            return new ToolInvocationResult
            {
                Success = false,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Error = fuse.Triggered ? fuse.Summary : runtimeDecision.Message,
                DurationMs = 0,
                ArgsHash = argsHash,
            };
        }

        // 权限检查
        var guardDenied = CheckGuardDenied(request);
        if (guardDenied is not null)
        {
            _runtimeControl?.RecordError(
                request.SessionId,
                RuntimeErrorKind.Tool,
                request.ToolName,
                guardDenied.Error ?? "Tool invocation denied.");
            return guardDenied;
        }

        try
        {
            var toolContext = new ToolExecutionContext
            {
                WorkspaceId = request.WorkspaceId,
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentInstanceId,
                ConfigurationAgentInstanceId = request.ConfigurationAgentInstanceId,
                WorkingDirectory = request.WorkingDirectory,
                AgentTemplateId = request.AgentTemplateId,
                Trace = request.Trace,
                ToolCallId = request.ToolCallId,
                ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                DelegationDepth = request.DelegationDepth,
                MaxDelegationDepth = request.MaxDelegationDepth,
                AllowSubDelegation = request.AllowSubDelegation,
                RoleInPlan = request.RoleInPlan,
                CallerLlmSnapshot = request.CallerLlmSnapshot,
                CallerVisionHelperRoute = request.CallerVisionHelperRoute,
                CapabilityPolicy = request.CapabilityPolicy,
                ExecutionIdentity = request.ExecutionIdentity is null
                    ? null
                    : request.ExecutionIdentity with { ToolCallId = request.ToolCallId },
                ActiveTask = request.ActiveTask,
            };

            var result = await _toolExecutionService.ExecuteAsync(
                request.ToolName,
                request.ArgumentsJson,
                toolContext,
                request.CapabilityPolicy,
                ct);
            _idleDetector?.RecordToolCompleted();

            var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            if (result.Success)
            {
                _runtimeControl?.MarkProgress(request.SessionId);
            }
            else
            {
                var fuse = _runtimeControl?.RecordError(
                    request.SessionId,
                    RuntimeErrorKind.Tool,
                    request.ToolName,
                    result.Error ?? "Tool invocation failed.");
                if (fuse is { Triggered: true })
                {
                    return new ToolInvocationResult
                    {
                        Success = false,
                        ToolCallId = request.ToolCallId,
                        ToolName = request.ToolName,
                        Error = fuse.Summary,
                        DurationMs = durationMs,
                        ArgsHash = argsHash,
                        OutputLength = result.Output?.Length ?? 0,
                    };
                }
            }

            return new ToolInvocationResult
            {
                Success = result.Success,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Output = result.Output,
                Error = result.Error,
                DurationMs = durationMs,
                ArgsHash = argsHash,
                OutputLength = result.Output?.Length ?? 0,
                ToolContentParts = result.ToolContentParts,
            };
        }
        catch (Exception ex)
        {
            var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(ex, "[ToolInvocation] Tool error tool={ToolName} callId={ToolCallId}", request.ToolName, request.ToolCallId);
            var fuse = _runtimeControl?.RecordError(
                request.SessionId,
                RuntimeErrorKind.Tool,
                request.ToolName,
                ex.Message);

            return new ToolInvocationResult
            {
                Success = false,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Error = fuse is { Triggered: true } ? fuse.Summary : ex.Message,
                DurationMs = durationMs,
                ArgsHash = argsHash,
            };
        }
    }

    private async Task RecordHarnessCompatibilityMetricAsync(
        HarnessToolInvocation compatibility,
        ToolInvocationRequest request,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        var adaptationKind = (compatibility.ToolNameAdapted, compatibility.ArgumentsAdapted) switch
        {
            (true, true) => "tool_and_arguments",
            (true, false) => "tool_name",
            _ => "arguments",
        };

        try
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = request.Trace ?? RuntimeTraceContext.CreateNew(
                    sessionId: request.SessionId,
                    workspaceId: request.WorkspaceId),
                Source = "runtime",
                Category = TelemetryMetricCategories.Tool,
                Name = "tool.harness_compatibility",
                Status = TelemetryMetricStatuses.Recorded,
                OccurredAtUtc = occurredAtUtc,
                CountValue = 1,
                Unit = "call",
                Summary = $"Harness tool contract adapted to '{compatibility.ToolName}'.",
                Dimensions = new Dictionary<string, string>
                {
                    ["requested_tool"] = compatibility.RequestedToolName,
                    ["canonical_tool"] = compatibility.ToolName,
                    ["tool_name_adapted"] = compatibility.ToolNameAdapted.ToString().ToLowerInvariant(),
                    ["arguments_adapted"] = compatibility.ArgumentsAdapted.ToString().ToLowerInvariant(),
                    ["adaptation_kind"] = adaptationKind,
                    ["adapter_version"] = "1",
                    ["agent_instance_id"] = request.AgentInstanceId,
                    ["agent_template_id"] = request.AgentTemplateId ?? string.Empty,
                },
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Compatibility telemetry is best-effort and must not change tool cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ToolInvocation] Failed to record Harness compatibility telemetry callId={ToolCallId}",
                request.ToolCallId);
        }
    }

    private ToolInvocationResult? CheckGuardDenied(ToolInvocationRequest request)
    {
        if (_workspaceGuard is null)
            return null;

        var decision = _workspaceGuard.CanExecuteTool(request.AgentInstanceId, request.ToolName);
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "[ToolInvocation] Tool denied tool={ToolName} agent={AgentInstanceId} reason={Reason}",
                request.ToolName, request.AgentInstanceId, decision.Reason);

            return new ToolInvocationResult
            {
                Success = false,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Error = $"Permission denied: {decision.Reason}. " +
                        "Do NOT retry blindly — repeated failures trigger session fuse. " +
                        "Call request_tool_approval to request one-time authorization, or try a different approach.",
                DurationMs = 0,
                ArgsHash = ComputeArgsHash(request.ArgumentsJson),
            };
        }

        return null;
    }

    private static string ComputeArgsHash(string argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson));
        return Convert.ToHexStringLower(hash);
    }
}
