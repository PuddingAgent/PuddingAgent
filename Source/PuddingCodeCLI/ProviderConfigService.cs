namespace PuddingCodeCLI;

internal static class ProviderConfigService
{
    public static bool TrySetActive(PuddingCliConfig config, string id, out ProviderEntry? target, out string error)
    {
        error = string.Empty;
        target = config.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            error = $"Provider \"{id}\" not found.";
            return false;
        }

        config.ActiveProvider = target.Id;
        return true;
    }

    public static bool TryAdd(PuddingCliConfig config, ProviderEntry entry, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            error = "Provider id is required.";
            return false;
        }

        if (config.Providers.Any(p => p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Provider \"{entry.Id}\" already exists.";
            return false;
        }

        config.Providers.Add(entry);
        if (string.IsNullOrWhiteSpace(config.ActiveProvider))
            config.ActiveProvider = entry.Id;
        return true;
    }

    public static bool TryRemove(
        PuddingCliConfig config,
        string id,
        out ProviderEntry? removed,
        out string? switchedToProviderId,
        out string error)
    {
        removed = null;
        switchedToProviderId = null;
        error = string.Empty;

        var idx = config.Providers.FindIndex(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            error = $"Provider \"{id}\" not found.";
            return false;
        }

        if (config.Providers.Count == 1)
        {
            error = "Cannot remove the only provider.";
            return false;
        }

        removed = config.Providers[idx];
        config.Providers.RemoveAt(idx);

        if (config.ActiveProvider?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
        {
            config.ActiveProvider = config.Providers[0].Id;
            switchedToProviderId = config.ActiveProvider;
        }

        return true;
    }
}
