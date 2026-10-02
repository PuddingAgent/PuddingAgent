using System.Collections.Concurrent;

namespace HarnessAgent.Core.Provider;

/// <summary>
/// Dynamic provider registry — models Pi Agent's provider registration API.
/// Providers can be added, removed, and queried at runtime.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, IModelProvider> _providers = new();
    private readonly ConcurrentDictionary<string, (string ProviderId, ModelDescriptor Model)> _models = new();

    /// <summary>Register a provider and its models.</summary>
    public void Register(IModelProvider provider)
    {
        if (!_providers.TryAdd(provider.ProviderId, provider))
            throw new InvalidOperationException($"Provider '{provider.ProviderId}' is already registered.");

        foreach (var model in provider.Models)
            _models.TryAdd(model.ModelId, (provider.ProviderId, model));
    }

    /// <summary>Unregister a provider and all its models.</summary>
    public bool Unregister(string providerId)
    {
        if (!_providers.TryRemove(providerId, out _))
            return false;

        var keys = _models.Where(kv => kv.Value.ProviderId == providerId).Select(kv => kv.Key).ToList();
        foreach (var key in keys)
            _models.TryRemove(key, out _);
        return true;
    }

    /// <summary>Resolve a model by id across all providers.</summary>
    public (IModelProvider Provider, ModelDescriptor Model)? ResolveModel(string modelId)
    {
        if (!_models.TryGetValue(modelId, out var entry))
            return null;

        var (providerId, model) = entry;
        return _providers.TryGetValue(providerId, out var provider)
            ? (provider, model)
            : null;
    }

    /// <summary>List all registered models.</summary>
    public IReadOnlyList<ModelDescriptor> ListModels() =>
        _models.Values.Select(v => v.Model).ToList();

    /// <summary>List models matching capability tags.</summary>
    public IReadOnlyList<ModelDescriptor> ListModelsByCapability(string tag) =>
        _models.Values
            .Where(v => v.Model.CapabilityTags.Contains(tag) && !v.Model.IsDeprecated)
            .Select(v => v.Model)
            .ToList();

    /// <summary>All registered provider ids.</summary>
    public IReadOnlyCollection<string> ProviderIds => _providers.Keys.ToList();
}
