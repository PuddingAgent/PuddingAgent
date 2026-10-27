namespace PuddingCode.Platform;

/// <summary>Safe, shared failure facts for the HTTP response and durable lifecycle event.</summary>
public sealed record CompactionFailure(
    string ErrorCode,
    string Message,
    string Phase,
    string CompactionId,
    string? TraceId,
    bool SummaryCommitted,
    string ErrorId,
    string? UpstreamErrorId = null,
    int? NativeErrorCode = null,
    int? WriteAttempts = null);

public sealed class CompactionRequestException(CompactionFailure failure, Exception inner)
    : Exception(failure.Message, inner)
{
    public CompactionFailure Failure { get; } = failure;
}
