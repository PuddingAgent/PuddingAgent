using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Business-facing visual reasoning service. It owns policy such as model selection, thinking
/// controls, artifact authorization, and how final answers are projected into the interaction OS.
/// </summary>
public interface IVisualReasoningService
{
    Task<VisualReasoningResult> AnalyzeAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default);
}
