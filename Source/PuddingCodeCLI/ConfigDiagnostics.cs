namespace PuddingCodeCLI;

internal sealed record ConfigIssue(
    string Code,
    string Level,
    string Message,
    string? FixHint = null,
    bool Fixable = false);

internal static class ConfigDiagnostics
{
    public static IReadOnlyList<ConfigIssue> Validate(PuddingCliConfig config, bool usingEnv)
    {
        var issues = new List<ConfigIssue>();

        if (usingEnv)
            return issues;

        if (config.Providers.Count == 0)
        {
            issues.Add(new ConfigIssue(
                Code: "NO_PROVIDER",
                Level: "error",
                Message: "No provider configured. Run onboarding or /model add.",
                FixHint: "Run /config fix to generate YAML scaffold, or /model add.",
                Fixable: true));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(config.ActiveProvider))
        {
            issues.Add(new ConfigIssue(
                Code: "ACTIVE_MISSING",
                Level: "warn",
                Message: "ActiveProvider is empty. First provider will be used.",
                FixHint: "Run /config fix to set active provider automatically.",
                Fixable: true));
        }
        else if (!config.Providers.Any(p => p.Id.Equals(config.ActiveProvider, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ConfigIssue(
                Code: "ACTIVE_NOT_FOUND",
                Level: "warn",
                Message: $"ActiveProvider '{config.ActiveProvider}' not found. First provider will be used.",
                FixHint: "Run /config fix to set active provider automatically.",
                Fixable: true));
        }

        foreach (var p in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Endpoint))
                issues.Add(new ConfigIssue(
                    Code: "ENDPOINT_MISSING",
                    Level: "error",
                    Message: $"Provider '{p.Id}' missing endpoint.",
                    FixHint: p.Id.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)
                        ? "Run /config fix to apply default Ollama endpoint."
                        : "Edit provider endpoint in config or YAML.",
                    Fixable: p.Id.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(p.Model))
                issues.Add(new ConfigIssue(
                    Code: "MODEL_MISSING",
                    Level: "error",
                    Message: $"Provider '{p.Id}' missing model.",
                    FixHint: "Run /config fix to infer model from provider id (id/model).",
                    Fixable: true));
            if (p.Billing.Mode != BillingMode.LocalFree && string.IsNullOrWhiteSpace(p.ApiKey))
                issues.Add(new ConfigIssue(
                    Code: "API_KEY_EMPTY",
                    Level: "warn",
                    Message: $"Provider '{p.Id}' has empty API key.",
                    FixHint: "Set api key (or use LocalFree for local endpoints).",
                    Fixable: false));
        }

        return issues;
    }
}
