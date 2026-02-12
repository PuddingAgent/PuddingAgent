namespace PuddingCode.Models;

public sealed record TraceableErrorResponse
{
    public bool Success { get; init; }
    public required string ErrorId { get; init; }
    public string? TraceId { get; init; }
    public string? SessionId { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
