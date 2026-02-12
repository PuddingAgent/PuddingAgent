using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Lsp;

/// <summary>
/// Placeholder language-server boundary used until concrete language adapters
/// are wired in. It preserves request correlation so callers can safely route
/// responses even for unsupported operations.
/// </summary>
public sealed class NoOpLanguageServerService : ILanguageServerService
{
    public Task<LanguageServerResponse> ExecuteAsync(
        LanguageServerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(LanguageServerResponse.Unsupported(request.Method, request.CorrelationId));
    }
}
