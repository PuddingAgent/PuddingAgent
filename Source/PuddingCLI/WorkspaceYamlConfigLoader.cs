namespace PuddingCodeCLI;

public sealed record WorkspaceYamlLoadResult(
    bool Enabled,
    string GlobalPath,
    string ProvidersPath,
    int ProviderFiles,
    int ProvidersLoaded,
    int ModelsLoaded,
    IReadOnlyList<string> Warnings);

internal static class WorkspaceYamlConfigLoader
{
    public static WorkspaceYamlLoadResult MergeInto(PuddingCliConfig config, string workspaceRoot)
    {
        var warnings = new List<string>();
        var root = Path.GetFullPath(workspaceRoot);
        var globalPath = FindGlobalConfigPath(root);
        if (string.IsNullOrWhiteSpace(globalPath))
        {
            return new WorkspaceYamlLoadResult(
                Enabled: false,
                GlobalPath: Path.Combine(root, "pudding.yaml"),
                ProvidersPath: Path.Combine(root, "providers"),
                ProviderFiles: 0,
                ProvidersLoaded: 0,
                ModelsLoaded: 0,
                Warnings: warnings);
        }

        WorkspaceYamlConfig global;
        try
        {
            global = ParseGlobal(File.ReadAllLines(globalPath));
        }
        catch (Exception ex)
        {
            warnings.Add($"pudding.yaml parse failed: {ex.Message}");
            return new WorkspaceYamlLoadResult(true, globalPath, Path.Combine(root, "providers"), 0, 0, 0, warnings);
        }

        var providersDir = ResolvePath(root, string.IsNullOrWhiteSpace(global.ProvidersDir) ? "providers" : global.ProvidersDir!);
        var providerFiles = new List<string>();
        if (Directory.Exists(providersDir))
        {
            providerFiles.AddRange(Directory.GetFiles(providersDir, "*.yaml", SearchOption.TopDirectoryOnly));
            providerFiles.AddRange(Directory.GetFiles(providersDir, "*.yml", SearchOption.TopDirectoryOnly));
        }

        var providersLoaded = 0;
        var modelsLoaded = 0;
        foreach (var file in providerFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ProviderYamlConfig provider;
            try
            {
                provider = ParseProvider(File.ReadAllLines(file));
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(file)} parse failed: {ex.Message}");
                continue;
            }

            var providerId = string.IsNullOrWhiteSpace(provider.Id)
                ? Path.GetFileNameWithoutExtension(file)
                : provider.Id.Trim();
            if (string.IsNullOrWhiteSpace(providerId))
            {
                warnings.Add($"{Path.GetFileName(file)} missing provider id.");
                continue;
            }

            var providerName = string.IsNullOrWhiteSpace(provider.Name) ? providerId : provider.Name.Trim();
            var endpoint = provider.Endpoint?.Trim() ?? "";
            var apiKey = ResolveApiKey(provider.ApiKey, provider.ApiKeyEnv);
            var models = provider.Models ?? [];

            if (string.IsNullOrWhiteSpace(endpoint))
                warnings.Add($"{Path.GetFileName(file)} provider '{providerId}' missing endpoint.");
            if (string.IsNullOrWhiteSpace(apiKey) && !IsLocalEndpoint(endpoint))
                warnings.Add($"{Path.GetFileName(file)} provider '{providerId}' missing api key/api_key_env.");

            if (models.Count == 0)
            {
                var fallbackModel = provider.Model?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(fallbackModel))
                {
                    warnings.Add($"{Path.GetFileName(file)} has no models.");
                    continue;
                }

                models = [new ProviderModelYamlConfig { Id = fallbackModel }];
            }

            providersLoaded++;
            foreach (var model in models)
            {
                var modelId = model.Id?.Trim();
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    warnings.Add($"{Path.GetFileName(file)} provider '{providerId}' has empty model id.");
                    continue;
                }

                var billing = BuildBilling(model.Billing, provider.Billing, endpoint);
                var entry = new ProviderEntry
                {
                    Id = $"{providerId}/{modelId}",
                    Name = providerName,
                    Endpoint = endpoint,
                    ApiKey = apiKey,
                    Model = modelId,
                    Temperature = model.Temperature ?? provider.Temperature,
                    MaxTokens = model.MaxTokens ?? provider.MaxTokens,
                    Billing = billing
                };

                Upsert(config.Providers, entry);
                modelsLoaded++;
            }
        }

        if (!string.IsNullOrWhiteSpace(global.ActiveProvider))
        {
            var active = NormalizeActiveProvider(global.ActiveProvider!, config.Providers);
            if (!string.IsNullOrWhiteSpace(active))
                config.ActiveProvider = active;
        }

        if (!string.IsNullOrWhiteSpace(global.SwarmFinalTestCommand))
        {
            config.Swarm ??= new SwarmRuntimeConfig();
            config.Swarm.FinalTestCommand = global.SwarmFinalTestCommand!;
        }

        return new WorkspaceYamlLoadResult(
            Enabled: true,
            GlobalPath: globalPath,
            ProvidersPath: providersDir,
            ProviderFiles: providerFiles.Count,
            ProvidersLoaded: providersLoaded,
            ModelsLoaded: modelsLoaded,
            Warnings: warnings);
    }

    private static WorkspaceYamlConfig ParseGlobal(string[] lines)
    {
        var cfg = new WorkspaceYamlConfig();
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var (k, v) = SplitKeyValue(line);
            switch (k)
            {
                case "providers_dir":
                case "providersdir":
                    cfg.ProvidersDir = v;
                    break;
                case "active_provider":
                case "activeprovider":
                    cfg.ActiveProvider = v;
                    break;
                case "swarm_final_test_command":
                case "swarmfinaltestcommand":
                case "final_test_command":
                    cfg.SwarmFinalTestCommand = v;
                    break;
            }
        }

        return cfg;
    }

    private static ProviderYamlConfig ParseProvider(string[] lines)
    {
        var cfg = new ProviderYamlConfig();
        var mode = Section.None;
        ProviderModelYamlConfig? currentModel = null;

        foreach (var raw in lines)
        {
            var source = StripComment(raw);
            if (string.IsNullOrWhiteSpace(source)) continue;

            var indent = CountIndent(source);
            var line = source.Trim();

            if (indent == 0)
            {
                currentModel = null;
                mode = Section.None;
                var (k, v) = SplitKeyValue(line);
                switch (k)
                {
                    case "id": cfg.Id = v; break;
                    case "name": cfg.Name = v; break;
                    case "endpoint": cfg.Endpoint = v; break;
                    case "api_key": cfg.ApiKey = v; break;
                    case "api_key_env": cfg.ApiKeyEnv = v; break;
                    case "model": cfg.Model = v; break;
                    case "temperature": cfg.Temperature = ParseDouble(v); break;
                    case "max_tokens": cfg.MaxTokens = ParseInt(v); break;
                    case "billing": mode = Section.ProviderBilling; break;
                    case "models":
                        cfg.Models ??= [];
                        mode = Section.Models;
                        break;
                }

                continue;
            }

            if (mode == Section.ProviderBilling && indent >= 2)
            {
                cfg.Billing ??= new BillingYamlConfig();
                ApplyBilling(cfg.Billing, line);
                continue;
            }

            if (mode == Section.Models)
            {
                if (indent == 2 && line.StartsWith("- "))
                {
                    var body = line[2..].Trim();
                    currentModel = new ProviderModelYamlConfig();
                    cfg.Models ??= [];
                    cfg.Models.Add(currentModel);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var (k, v) = SplitKeyValue(body);
                        ApplyModel(currentModel, k, v, ref mode);
                    }
                    continue;
                }

                if (currentModel is not null && indent >= 4)
                {
                    if (mode == Section.ModelBilling)
                    {
                        currentModel.Billing ??= new BillingYamlConfig();
                        ApplyBilling(currentModel.Billing, line);
                    }
                    else
                    {
                        var (k, v) = SplitKeyValue(line);
                        ApplyModel(currentModel, k, v, ref mode);
                    }
                }
            }

            if (mode == Section.ModelBilling && indent <= 2)
                mode = Section.Models;
        }

        return cfg;
    }

    private static void ApplyModel(ProviderModelYamlConfig model, string key, string value, ref Section mode)
    {
        switch (key)
        {
            case "id": model.Id = value; break;
            case "temperature": model.Temperature = ParseDouble(value); break;
            case "max_tokens": model.MaxTokens = ParseInt(value); break;
            case "billing":
                model.Billing ??= new BillingYamlConfig();
                mode = Section.ModelBilling;
                break;
            case "billing_mode":
                model.Billing ??= new BillingYamlConfig();
                model.Billing.Mode = value;
                break;
        }
    }

    private static void ApplyBilling(BillingYamlConfig billing, string line)
    {
        var (k, v) = SplitKeyValue(line);
        switch (k)
        {
            case "mode":
            case "billing_mode":
                billing.Mode = v;
                break;
            case "input_usd_per_million_tokens":
            case "in_usd_per_m":
                billing.InputUsdPerMillionTokens = ParseDecimal(v);
                break;
            case "output_usd_per_million_tokens":
            case "out_usd_per_m":
                billing.OutputUsdPerMillionTokens = ParseDecimal(v);
                break;
            case "request_usd":
                billing.RequestUsd = ParseDecimal(v);
                break;
            case "session_usd":
                billing.SessionUsd = ParseDecimal(v);
                break;
            case "monthly_usd":
                billing.MonthlyUsd = ParseDecimal(v);
                break;
            case "included_requests_per_month":
            case "included_requests":
                billing.IncludedRequestsPerMonth = ParseInt(v);
                break;
            case "included_sessions_per_month":
            case "included_sessions":
                billing.IncludedSessionsPerMonth = ParseInt(v);
                break;
        }
    }

    private static string? FindGlobalConfigPath(string workspaceRoot)
    {
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "pudding.yaml"),
            Path.Combine(workspaceRoot, "pudding.yml")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolvePath(string workspaceRoot, string rawPath)
    {
        var path = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = path[1..].TrimStart('/', '\\');
            path = string.IsNullOrEmpty(rest) ? home : Path.Combine(home, rest);
        }

        path = path.Replace('\\', Path.DirectorySeparatorChar)
                   .Replace('/', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == ' ') count++;
            else break;
        }

        return count;
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return (line.Trim().ToLowerInvariant(), "");
        var k = line[..idx].Trim().ToLowerInvariant();
        var v = line[(idx + 1)..].Trim();
        if ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\'')))
            v = v[1..^1];
        return (k, v);
    }

    private static void Upsert(List<ProviderEntry> providers, ProviderEntry entry)
    {
        var idx = providers.FindIndex(p => p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) providers[idx] = entry;
        else providers.Add(entry);
    }

    private static string ResolveApiKey(string? direct, string? envKey)
    {
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            var fromEnv = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;
        }

        return direct?.Trim() ?? "";
    }

    private static string NormalizeActiveProvider(string value, IReadOnlyList<ProviderEntry> providers)
    {
        var direct = providers.FirstOrDefault(p => p.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct.Id;
        var prefixed = providers.FirstOrDefault(p => p.Id.StartsWith(value + "/", StringComparison.OrdinalIgnoreCase));
        return prefixed?.Id ?? "";
    }

    private static ProviderBillingConfig BuildBilling(BillingYamlConfig? model, BillingYamlConfig? provider, string endpoint)
    {
        var src = model ?? provider;
        if (src is null)
        {
            if (IsLocalEndpoint(endpoint))
                return new ProviderBillingConfig { Mode = BillingMode.LocalFree };
            return new ProviderBillingConfig();
        }

        var mode = src.Mode?.Trim().ToLowerInvariant() switch
        {
            "per_request" => BillingMode.PerRequest,
            "per_session" => BillingMode.PerSession,
            "monthly_flat" => BillingMode.MonthlyFlat,
            "local_free" => BillingMode.LocalFree,
            _ => BillingMode.PerToken
        };

        return new ProviderBillingConfig
        {
            Mode = mode,
            InputUsdPerMillionTokens = src.InputUsdPerMillionTokens ?? 0,
            OutputUsdPerMillionTokens = src.OutputUsdPerMillionTokens ?? 0,
            RequestUsd = src.RequestUsd ?? 0,
            SessionUsd = src.SessionUsd ?? 0,
            MonthlyUsd = src.MonthlyUsd ?? 0,
            IncludedRequestsPerMonth = src.IncludedRequestsPerMonth ?? 0,
            IncludedSessionsPerMonth = src.IncludedSessionsPerMonth ?? 0
        };
    }

    private static bool IsLocalEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var text = endpoint.ToLowerInvariant();
        return text.Contains("localhost") || text.Contains("127.0.0.1") || text.Contains("0.0.0.0") || text.Contains("ollama");
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, out var v) ? v : null;

    private static double? ParseDouble(string value) =>
        double.TryParse(value, out var v) ? v : null;

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, out var v) ? v : null;

    private enum Section
    {
        None,
        ProviderBilling,
        Models,
        ModelBilling
    }

    private sealed class WorkspaceYamlConfig
    {
        public string? ProvidersDir { get; set; }
        public string? ActiveProvider { get; set; }
        public string? SwarmFinalTestCommand { get; set; }
    }

    private sealed class ProviderYamlConfig
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
        public string? ApiKeyEnv { get; set; }
        public string? Model { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public BillingYamlConfig? Billing { get; set; }
        public List<ProviderModelYamlConfig>? Models { get; set; }
    }

    private sealed class ProviderModelYamlConfig
    {
        public string? Id { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public BillingYamlConfig? Billing { get; set; }
    }

    private sealed class BillingYamlConfig
    {
        public string? Mode { get; set; }
        public decimal? InputUsdPerMillionTokens { get; set; }
        public decimal? OutputUsdPerMillionTokens { get; set; }
        public decimal? RequestUsd { get; set; }
        public decimal? SessionUsd { get; set; }
        public decimal? MonthlyUsd { get; set; }
        public int? IncludedRequestsPerMonth { get; set; }
        public int? IncludedSessionsPerMonth { get; set; }
    }
}
