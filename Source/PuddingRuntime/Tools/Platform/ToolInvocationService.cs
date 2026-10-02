using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Agents;
using PuddingCode.Core;
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
        var argumentError = HarnessToolCompatibilityAdapter.GetArgumentValidationError(request.ArgumentsJson);
        if (argumentError is not null)
        {
            return new ToolInvocationResult
            {
                Success = false,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Error = argumentError,
                ArgsHash = ComputeArgsHash(request.ArgumentsJson),
                DurationMs = 0,
            };
        }
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
        // 第一阶段（观察期）：评估并**记录**权限结论；判定行为与以前逐字相同。
        var permissionEvidence = EvaluatePermissionEvidence(request, out var guardDenied);
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
                PermissionEvidence = permissionEvidence,
                ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                DelegationDepth = request.DelegationDepth,
                MaxDelegationDepth = request.MaxDelegationDepth,
                AllowSubDelegation = request.AllowSubDelegation,
                RoleInPlan = request.RoleInPlan,
                CallerLlmSnapshot = request.CallerLlmSnapshot,
                CapabilityPolicy = request.CapabilityPolicy,
                ExecutionIdentity = request.ExecutionIdentity is null
                    ? null
                    : request.ExecutionIdentity with { ToolCallId = request.ToolCallId },
                UsageBudget = request.UsageBudget,
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
            else if (!(result.ExitCode == 428
                       || string.Equals(result.Status, VisionErrorCodes.RequestLimitExceeded, StringComparison.Ordinal)
                       || string.Equals(result.Status, ToolResultStatuses.HumanDecisionRequired, StringComparison.Ordinal)
                       || string.Equals(result.Status, ToolResultStatuses.DependencyWait, StringComparison.Ordinal)))
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
                Status = result.Status,
                ExitCode = result.ExitCode,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Output = result.Output,
                Error = result.Error,
                DurationMs = durationMs,
                ArgsHash = argsHash,
                OutputLength = result.Output?.Length ?? 0,
                ToolContentParts = result.ToolContentParts,
                DelegatedUsage = result.DelegatedUsage,
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

    /// <summary>
    /// 评估本次调用的权限并**记录证据**（权限证据链第一阶段：只记录，不改变任何判定）。
    ///
    /// <para>
    /// 判定语义与重构前**逐字相同**：guard 拒绝 ⇒ 返回同形的失败结果（<paramref name="denied"/> 非空），
    /// 调用方照旧立即返回；guard 允许 ⇒ 携带 <c>allowed</c> 证据继续执行。
    /// </para>
    /// <para>
    /// **未配置 guard 时返回 <c>null</c>（= 未评估）**，不得写成 <c>not-required</c> ——
    /// 那会谎称"策略判定无需审批"。缺失必须是可表达的状态（<see cref="ToolPermissionEvidence.IsAbsent"/>）。
    /// </para>
    /// </summary>
    /// <param name="request">本次工具调用请求。</param>
    /// <param name="denied">guard 拒绝时的失败结果；未拒绝为 <c>null</c>。</param>
    /// <returns>权限证据；未评估时为 <c>null</c>。</returns>
    private ToolPermissionEvidence? EvaluatePermissionEvidence(
        ToolInvocationRequest request,
        out ToolInvocationResult? denied)
    {
        denied = null;
        if (_workspaceGuard is null)
        {
            return null;
        }

        var decision = _workspaceGuard.CanExecuteTool(request.AgentInstanceId, request.ToolName);
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "[ToolInvocation] Tool denied tool={ToolName} agent={AgentInstanceId} reason={Reason}",
                request.ToolName, request.AgentInstanceId, decision.Reason);

            denied = new ToolInvocationResult
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
            return new ToolPermissionEvidence(request.ToolCallId, "denied", Source: "workspace-guard");
        }

        return new ToolPermissionEvidence(request.ToolCallId, "allowed", Source: "workspace-guard");
    }

    private static string ComputeArgsHash(string argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson));
        return Convert.ToHexStringLower(hash);
    }
}
