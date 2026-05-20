using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Agents;
using PuddingCode.Runtime;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services;

/// <summary>
/// 工具调用 Facade，统一权限、审计、耗时、错误处理。
/// 第一阶段：适配现有 SkillRuntime，保持行为不变。
/// </summary>
public sealed class ToolInvocationService : IToolInvocationService
{
    private readonly SkillRuntime _skillRuntime;
    private readonly IAgentWorkspaceGuard? _workspaceGuard;
    private readonly ILogger<ToolInvocationService> _logger;

    public ToolInvocationService(
        SkillRuntime skillRuntime,
        IAgentWorkspaceGuard? workspaceGuard = null,
        ILogger<ToolInvocationService>? logger = null)
    {
        _skillRuntime = skillRuntime;
        _workspaceGuard = workspaceGuard;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolInvocationService>.Instance;
    }

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var argsHash = ComputeArgsHash(request.ArgumentsJson);

        _logger.LogDebug(
            "[ToolInvocation] Invoke tool={ToolName} callId={ToolCallId} session={SessionId}",
            request.ToolName, request.ToolCallId, request.SessionId);

        // 权限检查
        var guardDenied = CheckGuardDenied(request);
        if (guardDenied is not null)
        {
            return guardDenied;
        }

        try
        {
            var input = ExtractInputFromJson(request.ArgumentsJson);
            var parameters = ExtractParametersFromJson(request.ArgumentsJson);

            var invokeRequest = new SkillInvokeRequest
            {
                AgentInstanceId = request.AgentInstanceId,
                WorkspaceId = request.WorkspaceId,
                SessionId = request.SessionId,
                Input = input,
                Parameters = parameters,
            };

            var result = await _skillRuntime.InvokeAsync(
                request.ToolName, invokeRequest, policy: null, ct);

            var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

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
            };
        }
        catch (Exception ex)
        {
            var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(ex, "[ToolInvocation] Tool error tool={ToolName} callId={ToolCallId}", request.ToolName, request.ToolCallId);

            return new ToolInvocationResult
            {
                Success = false,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Error = ex.Message,
                DurationMs = durationMs,
                ArgsHash = argsHash,
            };
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
                Error = $"Permission denied: {decision.Reason}",
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

    private static string ExtractInputFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return ExtractInput(root);

            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }
        catch
        {
            return argumentsJson;
        }
    }

    private static string ExtractInput(JsonElement root)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "input" or "command" or "code" or "query" or "text" or "content")
                return prop.Value.GetString() ?? string.Empty;
        }
        return root.GetRawText();
    }

    private static IReadOnlyDictionary<string, string> ExtractParametersFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
