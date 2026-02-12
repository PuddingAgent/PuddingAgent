using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>
/// Default business-facing visual reasoning service. It selects a provider from
/// registered capabilities and enforces request invariants before vendor calls.
/// Artifact authorization and URI resolution should happen before this service is invoked.
/// </summary>
public sealed class DefaultVisualReasoningService(IEnumerable<IVisualReasoningProvider> providers)
    : IVisualReasoningService
{
    private readonly IReadOnlyList<IVisualReasoningProvider> _providers = providers.ToList();

    public Task<VisualReasoningResult> AnalyzeAsync(
        VisualReasoningRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        return SelectProvider(request).AnalyzeAsync(request, ct);
    }

    public async IAsyncEnumerable<VisualReasoningStreamEvent> StreamAsync(
        VisualReasoningRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateRequest(request);
        var provider = SelectProvider(request);

        await foreach (var item in provider.StreamAsync(request, ct))
        {
            yield return item;
        }
    }

    private static void ValidateRequest(VisualReasoningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new InvalidOperationException("Visual reasoning request requires a workspace id.");
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new InvalidOperationException("Visual reasoning request requires a room id.");
        if (string.IsNullOrWhiteSpace(request.ParticipantId))
            throw new InvalidOperationException("Visual reasoning request requires a participant id.");
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new InvalidOperationException("Visual reasoning request requires a session id.");
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new InvalidOperationException("Visual reasoning request requires a model.");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Visual reasoning request requires a prompt.");
        if (request.Inputs.Count == 0)
            throw new InvalidOperationException("Visual reasoning request requires at least one visual input.");

        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.ArtifactId))
                throw new InvalidOperationException("Visual reasoning input requires an artifact id.");
            if (string.IsNullOrWhiteSpace(input.Uri))
            {
                throw new InvalidOperationException(
                    $"Visual reasoning input '{input.ArtifactId}' must have a resolved URI before provider invocation.");
            }
        }
    }

    private IVisualReasoningProvider SelectProvider(VisualReasoningRequest request)
    {
        var requestedProvider = string.IsNullOrWhiteSpace(request.Provider)
            ? VisualReasoningProviders.Unknown
            : request.Provider;

        var candidates = _providers.Where(provider =>
            string.Equals(requestedProvider, VisualReasoningProviders.Unknown, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.Capabilities.Provider, requestedProvider, StringComparison.OrdinalIgnoreCase));

        var provider = candidates.FirstOrDefault(candidate =>
            SupportsModel(candidate.Capabilities, request.Model)
            && SupportsTransport(candidate.Capabilities, request.Transport));

        if (provider is not null)
            return provider;

        throw new InvalidOperationException(
            $"No visual reasoning provider can handle provider='{request.Provider}', model='{request.Model}', transport='{request.Transport}'.");
    }

    private static bool SupportsModel(VisualReasoningProviderCapabilities capabilities, string model)
        => capabilities.SupportedModels.Any(candidate =>
            string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase));

    private static bool SupportsTransport(VisualReasoningProviderCapabilities capabilities, string transport)
        => capabilities.SupportedTransports.Count == 0
           || capabilities.SupportedTransports.Any(candidate =>
               string.Equals(candidate, transport, StringComparison.OrdinalIgnoreCase));
}
