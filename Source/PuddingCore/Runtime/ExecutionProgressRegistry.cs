using System.Collections.Concurrent;

namespace PuddingCode.Runtime;

/// <summary>
/// 运行进度的语义层级。Liveness 仅说明执行链仍有 I/O；Meaningful 表示任务状态向前推进。
/// </summary>
public enum ExecutionProgressKind
{
    Liveness,
    Meaningful,
}

/// <summary>一次运行进度信号。</summary>
public sealed record ExecutionProgressSignal
{
    public required RuntimeExecutionIdentity Identity { get; init; }
    public required ExecutionProgressKind Kind { get; init; }
    public required string Stage { get; init; }
    public string? Fingerprint { get; init; }
}

/// <summary>主 Run 看门狗读取的不可变进度快照。</summary>
public sealed record ExecutionProgressSnapshot
{
    public required string RootRunId { get; init; }
    public required string ConversationId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset LastLivenessAtUtc { get; init; }
    public required DateTimeOffset LastMeaningfulProgressAtUtc { get; init; }
    public required long LivenessSequence { get; init; }
    public required long MeaningfulSequence { get; init; }
    public string? LastStage { get; init; }
}

/// <summary>
/// 进程内运行进度注册表。主 Run 以 Conversation 为单写者；同一 Conversation 下的
/// 子代理通过稳定 ExecutionIdentity 汇入同一个根运行看门狗。
/// </summary>
public interface IExecutionProgressRegistry
{
    void RegisterRoot(string runId, string conversationId);
    void Report(ExecutionProgressSignal signal);
    ExecutionProgressSnapshot? GetSnapshot(string runId);
    void UnregisterRoot(string runId);
}

public sealed class ExecutionProgressRegistry(TimeProvider? timeProvider = null)
    : IExecutionProgressRegistry
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ProgressState> _rootsByRun = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rootRunByConversation = new(StringComparer.Ordinal);

    public void RegisterRoot(string runId, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var now = _timeProvider.GetUtcNow();
        _rootsByRun[runId] = new ProgressState(runId, conversationId, now);
        _rootRunByConversation[conversationId] = runId;
    }

    public void Report(ExecutionProgressSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!_rootRunByConversation.TryGetValue(signal.Identity.ConversationId, out var rootRunId)
            || !_rootsByRun.TryGetValue(rootRunId, out var state))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (state.Sync)
        {
            state.LastLivenessAtUtc = now;
            state.LivenessSequence++;
            state.LastStage = signal.Stage;

            if (signal.Kind != ExecutionProgressKind.Meaningful)
                return;

            var fingerprintKey = $"{signal.Identity.RunId}\u001f{signal.Stage}";
            if (!string.IsNullOrWhiteSpace(signal.Fingerprint)
                && state.MeaningfulFingerprints.TryGetValue(fingerprintKey, out var previous)
                && string.Equals(previous, signal.Fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(signal.Fingerprint))
                state.MeaningfulFingerprints[fingerprintKey] = signal.Fingerprint;

            state.LastMeaningfulProgressAtUtc = now;
            state.MeaningfulSequence++;
        }
    }

    public ExecutionProgressSnapshot? GetSnapshot(string runId)
    {
        if (!_rootsByRun.TryGetValue(runId, out var state))
            return null;

        lock (state.Sync)
        {
            return new ExecutionProgressSnapshot
            {
                RootRunId = state.RootRunId,
                ConversationId = state.ConversationId,
                StartedAtUtc = state.StartedAtUtc,
                LastLivenessAtUtc = state.LastLivenessAtUtc,
                LastMeaningfulProgressAtUtc = state.LastMeaningfulProgressAtUtc,
                LivenessSequence = state.LivenessSequence,
                MeaningfulSequence = state.MeaningfulSequence,
                LastStage = state.LastStage,
            };
        }
    }

    public void UnregisterRoot(string runId)
    {
        if (!_rootsByRun.TryRemove(runId, out var state))
            return;

        _rootRunByConversation.TryRemove(
            new KeyValuePair<string, string>(state.ConversationId, state.RootRunId));
    }

    private sealed class ProgressState(
        string rootRunId,
        string conversationId,
        DateTimeOffset startedAtUtc)
    {
        public object Sync { get; } = new();
        public string RootRunId { get; } = rootRunId;
        public string ConversationId { get; } = conversationId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset LastLivenessAtUtc { get; set; } = startedAtUtc;
        public DateTimeOffset LastMeaningfulProgressAtUtc { get; set; } = startedAtUtc;
        public long LivenessSequence { get; set; }
        public long MeaningfulSequence { get; set; }
        public string? LastStage { get; set; }
        public Dictionary<string, string> MeaningfulFingerprints { get; } = new(StringComparer.Ordinal);
    }
}
