using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Vendor-facing visual reasoning provider. Implementations encapsulate DashScope/OpenAI-compatible
/// SSE payloads, reasoning delta parsing, usage accounting, and provider error mapping.
/// </summary>
public interface IVisualReasoningProvider
{
    VisualReasoningProviderCapabilities Capabilities { get; }

    Task<VisualReasoningResult> AnalyzeAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default);
}
