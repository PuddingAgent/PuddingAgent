using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;

namespace PuddingCode.Runtime;

public enum RuntimeExecutionMode
{
    /// <summary>Normal mode — tools require authorization per policy.</summary>
    Normal,
    /// <summary>Safe mode — all tool calls blocked.</summary>
    Safe,
    /// <summary>Emergency stopping — backend is shutting down.</summary>
    EmergencyStopping,
    /// <summary>YOLO mode — all tool permission checks bypassed. Memory-only, lost on restart.</summary>
    Yolo,
}

public enum RuntimeErrorKind
{
    Tool,
    Api,
    Runtime,
}

/// <summary>渐进式熔断预警等级。</summary>
public enum FuseWarningLevel
{
    /// <summary>无预警 — 错误数低于预警阈值。</summary>
    None,
    /// <summary>第一档预警 — 错误数已达到预警阈值，提醒 Agent 注意。</summary>
    Warning,
    /// <summary>第二档预警 — 濒临熔断，下一次错误将触发熔断。</summary>
    Critical,
}

public sealed record RuntimeControlDecision(bool Allowed, string Message)
{
    public static RuntimeControlDecision Allow() => new(true, string.Empty);
    public static RuntimeControlDecision Deny(string message) => new(false, message);
}

public sealed record RuntimeErrorRecord
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required RuntimeErrorKind Kind { get; init; }
    public required string Component { get; init; }
    public required string Message { get; init; }
    public required string Fingerprint { get; init; }
}

public sealed record RuntimeFuseResult
{
    public required bool Triggered { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<RuntimeErrorRecord> RecentErrors { get; init; }
    public required FuseWarningLevel WarningLevel { get; init; }
    public required int WindowErrorCount { get; init; }
    public required int SameFingerprintCount { get; init; }
}

public sealed record RuntimeControlActionResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}

public sealed record RuntimeSessionControlSnapshot
{
    public required string SessionId { get; init; }
    public required SessionState State { get; init; }
    public required int WindowErrorCount { get; init; }
    public required int SameFingerprintCount { get; init; }
    public required int RecentErrorCount { get; init; }
    public required IReadOnlyList<RuntimeErrorRecord> RecentErrors { get; init; }
    public string? FaultSummary { get; init; }
}

public sealed record RuntimeStatusSnapshot
{
    public required RuntimeExecutionMode Mode { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required int ActiveSessions { get; init; }
    public RuntimeSessionControlSnapshot? Session { get; init; }
}

public interface IRuntimeControlService
{
    RuntimeExecutionMode Mode { get; }

    CancellationToken GetSessionCancellationToken(string sessionId);

    RuntimeControlDecision CanAcceptUserMessage(string? sessionId);

    RuntimeControlDecision CanStartAgent(string sessionId);

    RuntimeControlDecision CanInvokeTool(string sessionId, string toolName);

    void MarkSessionRunning(string sessionId);

    void MarkSessionWaitingForTool(string sessionId);

    void MarkSessionCompleted(string sessionId);

    void MarkSessionStopped(string sessionId);

    void MarkProgress(string sessionId);

    RuntimeFuseResult RecordError(
        string sessionId,
        RuntimeErrorKind kind,
        string component,
        string message);

    RuntimeControlActionResult ResetSessionFault(string sessionId);

    RuntimeControlActionResult StopSession(string sessionId, string reason);

    RuntimeControlActionResult StopAll(string reason);

    RuntimeControlActionResult SetMode(RuntimeExecutionMode mode, string reason);

    RuntimeStatusSnapshot GetStatus(string? sessionId = null);
}

public sealed partial class RuntimeControlService : IRuntimeControlService
{
    private const int DefaultMaxErrorsInWindow = 50;
    private const int DefaultWarningThreshold = 30;
    private const int DefaultWindowSeconds = 60;
    private const int MaxRecentErrors = 10;

    private readonly ConcurrentDictionary<string, SessionControlState> _sessions = new(StringComparer.Ordinal);
    private readonly ILogger<RuntimeControlService> _logger;
    private volatile RuntimeExecutionMode _mode = RuntimeExecutionMode.Normal;

    // ── 可配置滑动窗口熔断参数 ──
    private readonly int _maxErrorsInWindow;
    private readonly int _warningThreshold;
    private readonly TimeSpan _errorWindow;

    /// <param name="logger">Optional logger.</param>
    /// <param name="maxErrorsInWindow">Max errors in sliding window before fuse triggers.</param>
    /// <param name="warningThreshold">Error count at which warnings begin.</param>
    /// <param name="windowSeconds">Sliding window duration in seconds.</param>
    public RuntimeControlService(
        ILogger<RuntimeControlService>? logger = null,
        int? maxErrorsInWindow = null,
        int? warningThreshold = null,
        int? windowSeconds = null)
    {
        _logger = logger ?? NullLogger<RuntimeControlService>.Instance;
        _maxErrorsInWindow = maxErrorsInWindow ?? DefaultMaxErrorsInWindow;
        _warningThreshold = warningThreshold ?? DefaultWarningThreshold;
        _errorWindow = TimeSpan.FromSeconds(windowSeconds ?? DefaultWindowSeconds);
    }

    public RuntimeExecutionMode Mode => _mode;

    public CancellationToken GetSessionCancellationToken(string sessionId)
        => GetState(sessionId).Cancellation.Token;

    public RuntimeControlDecision CanAcceptUserMessage(string? sessionId)
    {
        if (_mode == RuntimeExecutionMode.EmergencyStopping)
            return RuntimeControlDecision.Deny("Runtime is emergency stopping. New user messages are rejected.");

        if (_mode == RuntimeExecutionMode.Safe)
            return RuntimeControlDecision.Deny("Runtime is in safe mode. Agent messages are blocked until `/mode normal` is sent.");

        if (_mode == RuntimeExecutionMode.Yolo)
            return RuntimeControlDecision.Allow();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var state = GetState(sessionId);
            if (state.State == SessionState.Faulted)
                return RuntimeControlDecision.Deny(BuildFaultedMessage(sessionId, state));
            // ADR-057-G: Turn Completed ≠ Conversation Completed.
            // Only block on truly terminal states, not per-Turn completion.
            if (state.State is SessionState.Stopping or SessionState.Stopped or SessionState.Terminated)
                return RuntimeControlDecision.Deny($"Session '{sessionId}' is {state.State}. Start a new session or check `/status`.");
        }

        return RuntimeControlDecision.Allow();
    }

    public RuntimeControlDecision CanStartAgent(string sessionId)
    {
        if (_mode == RuntimeExecutionMode.EmergencyStopping)
            return RuntimeControlDecision.Deny("Runtime is emergency stopping. Agent start is blocked.");

        if (_mode == RuntimeExecutionMode.Safe)
            return RuntimeControlDecision.Deny("Runtime is in safe mode. Agent start is blocked until `/mode normal` is sent.");

        if (_mode == RuntimeExecutionMode.Yolo)
            return RuntimeControlDecision.Allow();

        var state = GetState(sessionId);
        if (state.State == SessionState.Faulted)
            return RuntimeControlDecision.Deny(BuildFaultedMessage(sessionId, state));
        // ADR-057-G: A completed Turn does not prevent starting a new Turn on the same Conversation.
        if (state.State is SessionState.Stopping or SessionState.Stopped or SessionState.Terminated)
            return RuntimeControlDecision.Deny($"Session '{sessionId}' is {state.State}. Start a new session to continue.");
        return RuntimeControlDecision.Allow();
    }

    public RuntimeControlDecision CanInvokeTool(string sessionId, string toolName)
    {
        if (_mode == RuntimeExecutionMode.EmergencyStopping)
            return RuntimeControlDecision.Deny("Runtime is emergency stopping. Tool calls are blocked.");

        if (_mode == RuntimeExecutionMode.Safe)
            return RuntimeControlDecision.Deny($"Runtime is in safe mode. Tool '{toolName}' is blocked.");

        if (_mode == RuntimeExecutionMode.Yolo)
            return RuntimeControlDecision.Allow();

        var state = GetState(sessionId);
        return state.State == SessionState.Faulted
            ? RuntimeControlDecision.Deny(BuildFaultedMessage(sessionId, state))
            : RuntimeControlDecision.Allow();
    }

    public void MarkSessionRunning(string sessionId)
        => SetSessionState(sessionId, SessionState.Running);

    public void MarkSessionWaitingForTool(string sessionId)
        => SetSessionState(sessionId, SessionState.WaitingForTool);

    public void MarkSessionCompleted(string sessionId)
        => SetSessionState(sessionId, SessionState.Completed);

    public void MarkSessionStopped(string sessionId)
        => SetSessionState(sessionId, SessionState.Stopped);

    public void MarkProgress(string sessionId)
    {
        var state = GetState(sessionId);
        lock (state.Sync)
        {
            state.RecentErrors.Clear();
        }
    }

    public RuntimeFuseResult RecordError(
        string sessionId,
        RuntimeErrorKind kind,
        string component,
        string message)
    {
        var now = DateTimeOffset.UtcNow;
        var state = GetState(sessionId);
        var fingerprint = Fingerprint(kind, component, message);
        RuntimeFuseResult result;

        lock (state.Sync)
        {
            // ── 滑动窗口：清除过期错误 ──
            while (state.RecentErrors.Count > 0
                   && now - state.RecentErrors.Peek().TimestampUtc > _errorWindow)
                state.RecentErrors.Dequeue();

            var record = new RuntimeErrorRecord
            {
                TimestampUtc = now,
                Kind = kind,
                Component = component,
                Message = message,
                Fingerprint = fingerprint,
            };
            state.RecentErrors.Enqueue(record);
            while (state.RecentErrors.Count > MaxRecentErrors)
                state.RecentErrors.Dequeue();

            // ── 窗口内同类错误计数 ──
            var sameFingerprintCount = state.RecentErrors.Count(e => e.Fingerprint == fingerprint);

            // ── 窗口内总错误计数 ──
            var windowErrorCount = state.RecentErrors.Count;

            // ── 渐进式预警等级 ──
            FuseWarningLevel warningLevel = FuseWarningLevel.None;
            if (state.State != SessionState.Faulted && windowErrorCount >= _warningThreshold)
            {
                warningLevel = windowErrorCount >= _maxErrorsInWindow - 1
                    ? FuseWarningLevel.Critical  // 濒临熔断
                    : FuseWarningLevel.Warning;  // 接近阈值
            }

            // ── 熔断判断：滑动窗口内错误数达到阈值 ──
            var triggered = state.State != SessionState.Faulted
                            && windowErrorCount >= _maxErrorsInWindow;

            if (triggered)
            {
                state.State = SessionState.Faulted;
                state.FaultSummary = BuildFuseSummary(sessionId, state);
                state.Cancellation.Cancel();
                _logger.LogError(
                    "[RuntimeFuse] Triggered session={SessionId} kind={Kind} component={Component} " +
                    "windowErrors={WindowErrors} sameFingerprint={SameFp} windowSec={WindowSec} " +
                    "maxErrors={MaxErrors} fingerprint={Fingerprint}",
                    sessionId, kind, component, windowErrorCount, sameFingerprintCount,
                    _errorWindow.TotalSeconds, _maxErrorsInWindow, fingerprint);
            }

            result = new RuntimeFuseResult
            {
                Triggered = triggered,
                Summary = state.FaultSummary ?? BuildWarningSummary(sessionId, state, warningLevel, sameFingerprintCount, windowErrorCount),
                RecentErrors = state.RecentErrors.ToArray(),
                WarningLevel = warningLevel,
                WindowErrorCount = windowErrorCount,
                SameFingerprintCount = sameFingerprintCount,
            };
        }

        return result;
    }

    /// <summary>
    /// 从 Faulted 熔断状态恢复会话——用户确认可以继续后调用。
    /// 清除错误计数器、重置 CancellationToken、将状态从 Faulted → Recovering → Running。
    /// </summary>
    public RuntimeControlActionResult ResetSessionFault(string sessionId)
    {
        var state = GetState(sessionId);
        lock (state.Sync)
        {
            // 如果会话已经自然结束（Completed/Stopped/Terminated），
            // 熔断早已解除，直接返回成功让 UI 清除残留的 fuse 消息
            if (state.State is SessionState.Completed or SessionState.Stopped or SessionState.Terminated)
            {
                state.RecentErrors.Clear();
                state.FaultSummary = null;
                _logger.LogInformation("[RuntimeControl] Session already terminal — cleared stale fault state session={SessionId} state={State}", sessionId, state.State);
                return new RuntimeControlActionResult
                {
                    Success = true,
                    Message = $"Session '{sessionId}' has already ended ({state.State}). Fuse is no longer active — you can start a new session.",
                };
            }

            if (state.State != SessionState.Faulted)
                return new RuntimeControlActionResult
                {
                    Success = false,
                    Message = $"Session '{sessionId}' is not faulted (current state: {state.State}). Reset is only allowed from Faulted state.",
                };

            state.RecentErrors.Clear();
            state.FaultSummary = null;
            state.Cancellation = new CancellationTokenSource();
            state.State = SessionState.Recovering;
            state.State = SessionState.Running;
        }

        _logger.LogInformation("[RuntimeControl] Session fault reset session={SessionId}", sessionId);
        return new RuntimeControlActionResult
        {
            Success = true,
            Message = $"Session '{sessionId}' has been reset from Faulted to Running. You may now resume the session.",
        };
    }

    public RuntimeControlActionResult StopSession(string sessionId, string reason)
    {
        var state = GetState(sessionId);
        lock (state.Sync)
        {
            state.State = SessionState.Stopping;
            state.Cancellation.Cancel();
            state.State = SessionState.Stopped;
        }

        _logger.LogWarning("[RuntimeControl] Session stopped session={SessionId} reason={Reason}", sessionId, reason);
        return new RuntimeControlActionResult
        {
            Success = true,
            Message = $"Session '{sessionId}' stopped. Reason: {reason}",
        };
    }

    public RuntimeControlActionResult StopAll(string reason)
    {
        var count = 0;
        foreach (var pair in _sessions)
        {
            StopSession(pair.Key, reason);
            count++;
        }

        return new RuntimeControlActionResult
        {
            Success = true,
            Message = $"Stopped {count} active session(s). Reason: {reason}",
        };
    }

    public RuntimeControlActionResult SetMode(RuntimeExecutionMode mode, string reason)
    {
        _mode = mode;
        if (mode is RuntimeExecutionMode.Safe or RuntimeExecutionMode.EmergencyStopping)
        {
            foreach (var pair in _sessions)
            {
                if (pair.Value.State is SessionState.Running or SessionState.WaitingForTool or SessionState.Recovering)
                    pair.Value.Cancellation.Cancel();
            }
        }

        _logger.LogWarning("[RuntimeControl] Mode changed mode={Mode} reason={Reason}", mode, reason);

        var note = mode == RuntimeExecutionMode.Yolo
            ? " (memory-only, lost on restart)"
            : string.Empty;

        return new RuntimeControlActionResult
        {
            Success = true,
            Message = $"Runtime mode is now {mode}{note}. Reason: {reason}",
        };
    }

    public RuntimeStatusSnapshot GetStatus(string? sessionId = null)
        => new()
        {
            Mode = _mode,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ActiveSessions = _sessions.Count,
            Session = string.IsNullOrWhiteSpace(sessionId)
                ? null
                : Snapshot(GetState(sessionId)),
        };

    private SessionControlState GetState(string sessionId)
        => _sessions.GetOrAdd(sessionId, static id => new SessionControlState(id));

    private void SetSessionState(string sessionId, SessionState newState)
    {
        var state = GetState(sessionId);
        lock (state.Sync)
        {
            if (state.State == SessionState.Faulted && newState != SessionState.Recovering)
                return;
            if (newState == SessionState.Running && state.Cancellation.IsCancellationRequested)
                state.Cancellation = new CancellationTokenSource();
            state.State = newState;
        }
    }

    private static RuntimeSessionControlSnapshot Snapshot(SessionControlState state)
    {
        lock (state.Sync)
        {
            var now = DateTimeOffset.UtcNow;
            // 滑动窗口内有效错误
            var windowErrors = state.RecentErrors
                .Where(e => now - e.TimestampUtc <= TimeSpan.FromSeconds(60))
                .ToList();
            var fingerprint = windowErrors.FirstOrDefault()?.Fingerprint;
            var sameFp = fingerprint is null ? 0
                : windowErrors.Count(e => e.Fingerprint == fingerprint);
            return new RuntimeSessionControlSnapshot
            {
                SessionId = state.SessionId,
                State = state.State,
                WindowErrorCount = windowErrors.Count,
                SameFingerprintCount = sameFp,
                RecentErrorCount = state.RecentErrors.Count,
                RecentErrors = windowErrors,
                FaultSummary = state.FaultSummary,
            };
        }
    }

    private static string BuildFaultedMessage(string sessionId, SessionControlState state)
        => (state.FaultSummary ?? $"Session '{sessionId}' is Faulted.")
           + Environment.NewLine
           + "Send /resume to recover this session (clears error counters and allows the session to continue).";

    private static string BuildFuseSummary(string sessionId, SessionControlState state)
    {
        var windowErrors = state.RecentErrors.Count;
        return "Session fuse triggered." + Environment.NewLine +
               $"Session: {sessionId}" + Environment.NewLine +
               $"State: {SessionState.Faulted}" + Environment.NewLine +
               $"Errors in window: {windowErrors}" + Environment.NewLine +
               "Action: stopped agent output, blocked further tool calls." + Environment.NewLine +
               "Recovery: Send /resume to clear error counters and continue this session.";
    }

    private string BuildWarningSummary(
        string sessionId,
        SessionControlState state,
        FuseWarningLevel warningLevel,
        int sameFingerprintCount,
        int windowErrorCount)
    {
        if (warningLevel == FuseWarningLevel.None)
            return $"Session '{sessionId}' has {windowErrorCount} recent error(s) in the last {_errorWindow.TotalSeconds:F0}s.";

        var remaining = _maxErrorsInWindow - windowErrorCount;
        var sameFp = state.RecentErrors.FirstOrDefault()?.Fingerprint;
        var toolInfo = !string.IsNullOrWhiteSpace(sameFp) ? ExtractComponentFromFingerprint(sameFp) : "this tool";

        if (warningLevel == FuseWarningLevel.Critical)
            return $"⛔ FUSE WARNING: {toolInfo} has been rejected {sameFingerprintCount} times. " +
                   $"This is the {windowErrorCount}th error in the last {_errorWindow.TotalSeconds:F0}s. " +
                   $"Only {remaining} more error(s) will trigger session fuse. " +
                   "STOP retrying immediately — call request_tool_approval or try a different approach.";

        return $"⚠️ Note: {toolInfo} has been rejected {sameFingerprintCount} times recently. " +
               $"{windowErrorCount} error(s) in the last {_errorWindow.TotalSeconds:F0}s. " +
               $"If {remaining} more error(s) occur, session fuse will suspend this session. " +
               "Consider: (1) call request_tool_approval to request authorization, or (2) try an alternative tool/approach.";
    }

    /// <summary>从指纹中尝试提取工具/组件名用于更友好的提示。</summary>
    private static string ExtractComponentFromFingerprint(string fingerprint)
    {
        // fingerprint 格式: kind|component|normalized_message 的 SHA256 前16位 hex
        // 直接返回 "the tool" 即可 — 具体工具名从 error 消息中获取
        return "the tool";
    }

    private static string Fingerprint(RuntimeErrorKind kind, string component, string message)
    {
        var normalized = NormalizeError(message);
        var value = $"{kind}|{component.Trim().ToLowerInvariant()}|{normalized}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string NormalizeError(string message)
    {
        var normalized = WhitespaceRegex().Replace(message.Trim().ToLowerInvariant(), " ");
        normalized = GuidRegex().Replace(normalized, "{guid}");
        normalized = NumberRegex().Replace(normalized, "{num}");
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex NumberRegex();

    private sealed class SessionControlState
    {
        public SessionControlState(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public object Sync { get; } = new();
        public CancellationTokenSource Cancellation { get; set; } = new();
        public SessionState State { get; set; } = SessionState.Created;
        public Queue<RuntimeErrorRecord> RecentErrors { get; } = new();
        public string? FaultSummary { get; set; }
    }
}
