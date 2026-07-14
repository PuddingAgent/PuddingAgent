using PuddingCode.Configuration;

namespace PuddingCode.Abstractions;

/// <summary>
/// Read-only access to the LLM provider configuration.
/// </summary>
public interface ILlmResourcePoolService
{
    Task<PuddingLlmProvidersConfig> LoadAsync(CancellationToken ct = default);
}
