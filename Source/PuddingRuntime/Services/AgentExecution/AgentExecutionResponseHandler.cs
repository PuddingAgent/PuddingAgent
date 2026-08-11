using PuddingCode.Models;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent 响应处理：解析 LLM 输出为 AgentLoopResponse，评估 CompletionPolicy，处理终态裁定。
/// 从 AgentExecutionService 提取，消除上帝类（审计 P0 #1）。
/// </summary>
internal sealed class AgentExecutionResponseHandler
{
    private readonly CompletionPolicy _completionPolicy;
    private readonly ExecutionJournal _journal;
    private readonly ILogger _logger;

    public AgentExecutionResponseHandler(
        CompletionPolicy completionPolicy,
        ExecutionJournal journal,
        ILogger logger)
    {
        _completionPolicy = completionPolicy;
        _journal = journal;
        _logger = logger;
    }

    /// <summary>
    /// 解析 LLM 原始输出并评估 CompletionPolicy。
    /// 返回结构化裁定结果，调用方据此分派 DONE/WAIT/FAILED/CANCELLED/Continue 分支。
    /// </summary>
    public AgentExecutionResponseVerdict EvaluateResponse(
        string rawText,
        AgentLoopContext loopCtx,
        bool isCancelled,
        bool isFrozen,
        int round,
        DateTimeOffset turnStart,
        ExpectedOutputCandidateTracker? expectedOutputTracker = null)
    {
        var loopResp = AgentLoopResponse.Parse(rawText);
        var message = loopResp.Message ?? rawText;
        expectedOutputTracker?.Observe(message);

        var verdict = _completionPolicy.Evaluate(
            loopCtx, loopResp, _journal.GetTurns(loopCtx.SessionId),
            isCancelled, isFrozen);

        return new AgentExecutionResponseVerdict
        {
            LoopResponse = loopResp,
            Message = message,
            CompletionVerdict = verdict,
        };
    }
}

/// <summary>
/// Agent 响应裁定结果。
/// </summary>
internal sealed class AgentExecutionResponseVerdict
{
    public AgentLoopResponse LoopResponse { get; init; } = null!;
    public string Message { get; init; } = string.Empty;
    public CompletionVerdict CompletionVerdict { get; init; }
}
