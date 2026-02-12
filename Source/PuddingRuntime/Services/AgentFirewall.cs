using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services;

/// <summary>
/// Unified agent firewall — 8 gates evaluated in sequence.
///
/// Gate order (early-exit on first deny):
///   1. ModeGate         — runtime execution mode (YOLO / EStop / Safe)
///   2. SessionGate      — session lifecycle state (Faulted / Stopped)
///   3. CapabilityGate   — tool must be in the agent's capability policy
///   4. AuthorizationGate — explicit / implicit runtime authorization
///   5. SandboxGate      — sandbox policy pass-through
///   6. WorkspaceGate    — host file workspace boundary
///   7. ResourceGate     — resource permissions (shell / file-write / network)
///   8. StateGate        — agent state (heartbeat cooldown)
/// </summary>
public sealed class AgentFirewall : IAgentFirewall
{
    private readonly IRuntimeControlService? _runtime;
    private readonly IToolPermissionPolicyService? _policySvc;
    private readonly IPuddingToolRegistry? _toolRegistry;
    private readonly IToolAuthorizationService? _authzSvc;
    private readonly IToolApprovalService? _approvalSvc;
    private readonly SandboxExecutor _sandbox;
    private readonly IAgentExecutionAvailabilityProvider? _availabilityProvider;
    private readonly ILogger<AgentFirewall> _logger;

    public AgentFirewall(
        IRuntimeControlService? runtime = null,
        IToolPermissionPolicyService? policySvc = null,
        IPuddingToolRegistry? toolRegistry = null,
        IToolAuthorizationService? authzSvc = null,
        IToolApprovalService? approvalSvc = null,
        SandboxExecutor? sandbox = null,
        IAgentExecutionAvailabilityProvider? availabilityProvider = null,
        ILogger<AgentFirewall>? logger = null)
    {
        _runtime = runtime!;
        _policySvc = policySvc;
        _toolRegistry = toolRegistry;
        _authzSvc = authzSvc;
        _approvalSvc = approvalSvc;
        _sandbox = sandbox ?? new SandboxExecutor(NullLoggerFactory.Instance.CreateLogger<SandboxExecutor>());
        _availabilityProvider = availabilityProvider;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<AgentFirewall>();
    }

    public async Task<FirewallDecision> EvaluateAsync(FirewallContext ctx, CancellationToken ct)
    {
        // ── Gate 1: ModeGate ──
        var modeResult = EvaluateModeGate(ctx);
        if (!modeResult.Allowed) return modeResult;

        // ── Gate 2: SessionGate ──
        var sessionResult = EvaluateSessionGate(ctx);
        if (!sessionResult.Allowed) return sessionResult;

        // ── Gate 3: CapabilityGate ──
        var capResult = EvaluateCapabilityGate(ctx);
        if (!capResult.Allowed) return capResult;

        // ── Gate 4: AuthorizationGate ──
        var authzResult = await EvaluateAuthorizationGateAsync(ctx, ct);
        if (!authzResult.Allowed) return authzResult;

        // ── Gate 5: SandboxGate ──
        var sandboxResult = EvaluateSandboxGate(ctx);
        if (!sandboxResult.Allowed) return sandboxResult;

        // ── Gate 6: WorkspaceGate ──
        var wsResult = EvaluateWorkspaceGate(ctx);
        if (!wsResult.Allowed) return wsResult;

        // ── Gate 7: ResourceGate ──
        var resResult = EvaluateResourceGate(ctx);
        if (!resResult.Allowed) return resResult;

        // ── Gate 8: StateGate ──
        var stateResult = await EvaluateStateGateAsync(ctx, ct);
        if (!stateResult.Allowed) return stateResult;

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 1: ModeGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateModeGate(FirewallContext ctx)
    {
        // Use _runtime.Mode as the authoritative source — ctx.RuntimeMode is a hint
        // and might be stale if set before a /yolo command took effect.
        var mode = _runtime?.Mode ?? ctx.RuntimeMode;
        return mode switch
        {
            RuntimeExecutionMode.Yolo =>
                // YOLO: one-click pass through all gates
                FirewallDecision.Allow(),

            RuntimeExecutionMode.EmergencyStopping =>
                FirewallDecision.Deny(
                    $"Runtime is emergency stopping. Tool '{ctx.ToolId}' is blocked.",
                    FirewallGate.Mode),

            RuntimeExecutionMode.Safe when IsWriteTool(ctx.ToolId) =>
                FirewallDecision.Deny(
                    $"Runtime is in safe mode. Write tool '{ctx.ToolId}' is blocked.",
                    FirewallGate.Mode),

            // Normal / Safe (read-only) → continue to next gate
            _ => FirewallDecision.Allow(),
        };
    }

    // ────────────────────────────────────────────
    //  Gate 2: SessionGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateSessionGate(FirewallContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.SessionId))
            return FirewallDecision.Allow();

        if (_runtime is null) return FirewallDecision.Allow();
        var status = _runtime.GetStatus(ctx.SessionId);
        var state = status?.Session?.State;

        if (state == SessionState.Faulted)
            return FirewallDecision.Deny(
                $"Session '{ctx.SessionId}' is faulted. Tool calls are blocked until `/resume`.",
                FirewallGate.Session);

        if (state is SessionState.Completed or SessionState.Stopping or SessionState.Stopped or SessionState.Terminated)
            return FirewallDecision.Deny(
                $"Session '{ctx.SessionId}' is {state}. Start a new session.",
                FirewallGate.Session);

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 3: CapabilityGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateCapabilityGate(FirewallContext ctx)
    {
        // YOLO already returned in ModeGate; but double-check
        if (ctx.RuntimeMode == RuntimeExecutionMode.Yolo)
            return FirewallDecision.Allow();

        if (_toolRegistry is null || _policySvc is null) return FirewallDecision.Allow();
        var descriptor = _toolRegistry.GetDescriptor(ctx.ToolId);
        if (descriptor is not null && !_policySvc.CanExposeToAgent(descriptor, ctx.Policy))
            return FirewallDecision.Deny(
                $"Tool '{ctx.ToolId}' is not allowed by the agent's capability policy. " +
                "Do NOT retry blindly — repeated failures trigger session fuse. " +
                "You can request one-time authorization via request_tool_approval, or use an alternative approach.",
                FirewallGate.Capability);

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 4: AuthorizationGate
    // ────────────────────────────────────────────
    private async Task<FirewallDecision> EvaluateAuthorizationGateAsync(
        FirewallContext ctx, CancellationToken ct)
    {
        // YOLO mode skips authorization checks
        if (ctx.RuntimeMode == RuntimeExecutionMode.Yolo)
            return FirewallDecision.Allow();

        if (_authzSvc is null || _toolRegistry is null) return FirewallDecision.Allow();

        var descriptor = _toolRegistry.GetDescriptor(ctx.ToolId);
        if (descriptor is null) return FirewallDecision.Allow();

        if (_policySvc is null || !_policySvc.RequiresRuntimeAuthorization(descriptor))
            return FirewallDecision.Allow();

        var authzCtx = new ToolAuthorizationContext
        {
            WorkspaceId = ctx.WorkspaceId,
            SessionId = ctx.SessionId,
            AgentInstanceId = ctx.AgentInstanceId,
            UserId = ctx.UserId ?? "admin",
            ToolId = ctx.ToolId,
            ArgumentsHash = ToolAuthorizationDefaults.ComputeArgumentsHash(ctx.ArgumentsJson),
        };

        var authorization = await _authzSvc.CheckAsync(authzCtx, descriptor, ct);
        if (authorization.IsAuthorized)
            return FirewallDecision.Allow();

        // Not explicitly authorized — try implicit approval
        if (_approvalSvc is not null)
        {
            var approval = await _approvalSvc.CheckAsync(
                new ToolApprovalExecutionRequest
                {
                    WorkspaceId = ctx.WorkspaceId,
                    SessionId = ctx.SessionId,
                    AgentInstanceId = ctx.AgentInstanceId,
                    UserId = ctx.UserId ?? "admin",
                    ToolId = ctx.ToolId,
                    ActualArgumentsJson = ctx.ArgumentsJson,
                },
                descriptor,
                ct);

            if (approval?.IsApproved == true)
            {
                _logger.LogInformation(
                    "[AgentFirewall] Implicit approval granted tool={ToolId}",
                    ctx.ToolId);
                return FirewallDecision.Allow();
            }
        }

        var error = authorization.Message;
        if (!error.Contains("request_tool_approval", StringComparison.OrdinalIgnoreCase))
            error += " Recommended next step: call request_tool_approval with exact planned arguments.";

        return FirewallDecision.Deny(error, FirewallGate.Authorization);
    }

    // ────────────────────────────────────────────
    //  Gate 5: SandboxGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateSandboxGate(FirewallContext ctx)
    {
        if (!_sandbox.IsAllowed(ctx.ToolId, ctx.Policy, ctx.AgentInstanceId))
            return FirewallDecision.Deny(
                $"Tool '{ctx.ToolId}' is blocked by sandbox policy for agent '{ctx.AgentInstanceId}'.",
                FirewallGate.Sandbox);

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 6: WorkspaceGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateWorkspaceGate(FirewallContext ctx)
    {
        // Only file-write and file-patch tools are subject to workspace checks.
        // file_read uses skipWorkspaceCheck=true by design.
        if (!IsWriteTool(ctx.ToolId))
            return FirewallDecision.Allow();

        // YOLO mode skips workspace boundary
        if (ctx.RuntimeMode == RuntimeExecutionMode.Yolo)
            return FirewallDecision.Allow();

        // Check that the file path(s) in arguments are inside the workspace.
        if (!string.IsNullOrWhiteSpace(ctx.ArgumentsJson))
        {
            var path = ExtractPathFromArgs(ctx.ToolId, ctx.ArgumentsJson);
            if (!string.IsNullOrWhiteSpace(path)
                && !HostFileToolPaths.TryResolveInsideWorkspace(
                    path, out _, out var wsError, skipWorkspaceCheck: false))
            {
                return FirewallDecision.Deny(wsError, FirewallGate.Workspace);
            }
        }

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 7: ResourceGate
    // ────────────────────────────────────────────
    private FirewallDecision EvaluateResourceGate(FirewallContext ctx)
    {
        // YOLO 模式跳过所有资源限制
        if (ctx.RuntimeMode == RuntimeExecutionMode.Yolo)
            return FirewallDecision.Allow();

        if (ctx.Policy is null)
            return FirewallDecision.Allow();

        var toolId = ctx.ToolId;

        // Shell execution requires policy.AllowShellExecution
        if (IsShellTool(toolId) && !ctx.Policy.AllowShellExecution)
            return FirewallDecision.Deny(
                $"Tool '{toolId}' requires shell execution which is not allowed by policy.",
                FirewallGate.Resource);

        // File write tools require policy.AllowFileWrite
        if (IsWriteTool(toolId) && !ctx.Policy.AllowFileWrite)
            return FirewallDecision.Deny(
                $"Tool '{toolId}' requires file write which is not allowed by policy.",
                FirewallGate.Resource);

        // Network tools require policy.AllowNetworkAccess
        if (IsNetworkTool(toolId) && !ctx.Policy.AllowNetworkAccess)
            return FirewallDecision.Deny(
                $"Tool '{toolId}' requires network access which is not allowed by policy.",
                FirewallGate.Resource);

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Gate 8: StateGate
    // ────────────────────────────────────────────
    private async Task<FirewallDecision> EvaluateStateGateAsync(
        FirewallContext ctx, CancellationToken ct)
    {
        // Only heartbeats are subject to cooldown checks.
        // Agent-to-agent messages must always be delivered.
        if (!ctx.IsHeartbeat || _availabilityProvider is null)
            return FirewallDecision.Allow();

        var availability = await _availabilityProvider.GetAsync(
            ctx.WorkspaceId, ctx.AgentInstanceId, ct);

        if (!availability.CanStartMessageDelivery)
        {
            _logger.LogInformation(
                "[AgentFirewall] StateGate blocked heartbeat agent={Agent} status={Status}",
                ctx.AgentInstanceId, availability.Status);
            return FirewallDecision.Deny(
                $"Agent '{ctx.AgentInstanceId}' is not available for heartbeat delivery " +
                $"(status={availability.Status}).",
                FirewallGate.State);
        }

        return FirewallDecision.Allow();
    }

    // ────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────

    private static bool IsWriteTool(string toolId) =>
        toolId.Equals("file_write", StringComparison.OrdinalIgnoreCase)
        || toolId.Equals("file_patch", StringComparison.OrdinalIgnoreCase);

    private static bool IsShellTool(string toolId) =>
        toolId.Equals("shell", StringComparison.OrdinalIgnoreCase)
        || toolId.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase)
        || toolId.StartsWith("terminal_", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkTool(string toolId) =>
        toolId.Equals("http_fetch", StringComparison.OrdinalIgnoreCase)
        || toolId.Equals("anysearch_search", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the primary file path from tool arguments JSON.
    /// Handles both "path" (file_write / file_read) and "patches[0].path" (file_patch).
    /// </summary>
    private static string? ExtractPathFromArgs(string toolId, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            // file_write / file_read:  { "path": "..." }
            if (root.TryGetProperty("path", out var pathEl)
                && pathEl.ValueKind == System.Text.Json.JsonValueKind.String)
                return pathEl.GetString();

            // file_patch: single file  { "path": "...", "operations": [...] }
            if (toolId.Equals("file_patch", StringComparison.OrdinalIgnoreCase))
            {
                // file_patch batch: { "patches": [ { "path": "..." }, ... ] }
                if (root.TryGetProperty("patches", out var patchesEl)
                    && patchesEl.ValueKind == System.Text.Json.JsonValueKind.Array
                    && patchesEl.GetArrayLength() > 0)
                {
                    var firstPatch = patchesEl[0];
                    if (firstPatch.TryGetProperty("path", out var pEl)
                        && pEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        return pEl.GetString();
                }
            }
        }
        catch
        {
            // Best effort path extraction; ignore parse errors here.
        }

        return null;
    }
}
