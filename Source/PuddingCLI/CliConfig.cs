using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PuddingCodeCLI;

/// <summary>CLI configuration root — stored at ~/.pudding/config.json</summary>
public sealed class PuddingCliConfig
{
    public string? ActiveProvider { get; set; }
    public List<ProviderEntry> Providers { get; set; } = [];
    public GlobalSubconsciousConfig? GlobalSubconscious { get; set; }
    public MemoryMaintenanceConfig MemoryMaintenance { get; set; } = new();
    public ContextBudgetConfig ContextBudget { get; set; } = new();
    public HookConfig Hooks { get; set; } = new();
    public SwarmRuntimeConfig Swarm { get; set; } = new();
    public List<AgentTemplateEntry> AgentTemplates { get; set; } = [];
    [JsonIgnore]
    public WorkspaceYamlLoadResult? WorkspaceYaml { get; set; }
}

/// <summary>A configured LLM provider (OpenAI-compatible)</summary>
public sealed class ProviderEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public ProviderBillingConfig Billing { get; set; } = new();
}

public sealed class ProviderBillingConfig
{
    public BillingMode Mode { get; set; } = BillingMode.PerToken;
    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
    public decimal RequestUsd { get; set; }
    public decimal SessionUsd { get; set; }
    public decimal MonthlyUsd { get; set; }
    public int IncludedRequestsPerMonth { get; set; }
    public int IncludedSessionsPerMonth { get; set; }
}

public enum BillingMode
{
    PerToken,
    PerRequest,
    PerSession,
    MonthlyFlat,
    LocalFree
}

/// <summary>Global fallback config for subconscious model selection.</summary>
public sealed class GlobalSubconsciousConfig
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public double BudgetRatio { get; set; } = 0.15;
    public bool Visible { get; set; }
}

/// <summary>Per-agent dual model template. v1 supports role-based static mapping.</summary>
public sealed class AgentTemplateEntry
{
    public string Role { get; set; } = "spirit";
    public ModelRefConfig Conscious { get; set; } = new();
    public ModelRefConfig? Subconscious { get; set; }
    public SubconsciousPolicyConfig SubconsciousPolicy { get; set; } = new();
}

public sealed class ModelRefConfig
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class SubconsciousPolicyConfig
{
    public bool Visible { get; set; } = true;
    public string Verbosity { get; set; } = "low";
    public double BudgetRatio { get; set; } = 0.15;
}

/// <summary>Memory maintenance policy for subconscious memory compaction/indexing lifecycle.</summary>
public sealed class MemoryMaintenanceConfig
{
    public int CompactWriteThreshold { get; set; } = 20;
    public int CompactMinEntries { get; set; } = 180;
    public int CompactKeepEntries { get; set; } = 120;
    public bool UseModelSummarization { get; set; }
    public int ModelSummaryDailyTokenBudget { get; set; } = 12000;
    public int ModelSummaryMaxInputChars { get; set; } = 12000;
    public int ModelSummaryMaxOutputChars { get; set; } = 2400;
}

public sealed class ContextBudgetConfig
{
    public int MaxPromptTokens { get; set; } = 24000;
    public int MaxHistoryMessages { get; set; } = 80;
    public int PreserveTailMessages { get; set; } = 8;
    /// <summary>
    /// When true, old history messages are summarised via the subconscious LLM
    /// instead of being silently dropped when the context budget is exceeded.
    /// Requires the subconscious model to be configured.
    /// </summary>
    public bool UseCompression { get; set; } = false;
    /// <summary>Number of messages per compression pass (default: 16).</summary>
    public int CompressionWindowSize { get; set; } = 16;
}

public sealed class HookConfig
{
    public List<string> Enabled { get; set; } = ["metrics"];
    public string AuditLogPath { get; set; } = ".pudding/hooks.log";
    public List<ExternalHookConfig> External { get; set; } = [];
}

public sealed class ExternalHookConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; } = 8000;
}

public sealed class SwarmRuntimeConfig
{
    public string FinalTestCommand { get; set; } = "";
}

/// <summary>Load / save ~/.pudding/config.json with migration from old format</summary>
public static class ConfigManager
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pudding", "config.json");

    public static PuddingCliConfig Load(string path)
        => Load(path, null);

    public static PuddingCliConfig Load(string path, string? workspaceRoot)
    {
        if (!File.Exists(path))
        {
            var empty = new PuddingCliConfig();
            var wsRootNoFile = string.IsNullOrWhiteSpace(workspaceRoot) ? Environment.CurrentDirectory : workspaceRoot;
            empty.WorkspaceYaml = WorkspaceYamlConfigLoader.MergeInto(empty, wsRootNoFile);
            EnsureDefaults(empty);
            return empty;
        }
        try
        {
            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json);

            // Migrate v0.1.0 single-provider format: { apiKey, endpoint, model }
            if (node?["apiKey"] is not null)
            {
                var migrated = new PuddingCliConfig
                {
                    ActiveProvider = "default",
                    Providers =
                    [
                        new ProviderEntry
                        {
                            Id = "default",
                            Name = "Migrated",
                            Endpoint = node["endpoint"]?.GetValue<string>()
                                       ?? "https://api.openai.com/v1/chat/completions",
                            ApiKey = node["apiKey"]!.GetValue<string>(),
                            Model = node["model"]?.GetValue<string>() ?? "gpt-4o"
                        }
                    ],
                    GlobalSubconscious = new GlobalSubconsciousConfig
                    {
                        ProviderId = "default",
                        Model = "gpt-4o-mini",
                        BudgetRatio = 0.15,
                        Visible = false
                    },
                    AgentTemplates =
                    [
                        new AgentTemplateEntry
                        {
                            Role = "spirit",
                            Conscious = new ModelRefConfig { ProviderId = "default", Model = "gpt-4o" },
                            Subconscious = new ModelRefConfig { ProviderId = "default", Model = "gpt-4o-mini" },
                            SubconsciousPolicy = new SubconsciousPolicyConfig
                            {
                                Visible = false,
                                Verbosity = "low",
                                BudgetRatio = 0.15
                            }
                        }
                    ]
                };
                Save(path, migrated);
                return migrated;
            }

            var config = JsonSerializer.Deserialize<PuddingCliConfig>(json, s_jsonOptions)
                         ?? new PuddingCliConfig();

            var wsRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? Environment.CurrentDirectory : workspaceRoot;
            config.WorkspaceYaml = WorkspaceYamlConfigLoader.MergeInto(config, wsRoot);
            EnsureDefaults(config);
            return config;
        }
        catch
        {
            return new PuddingCliConfig();
        }
    }

    public static void Save(string path, PuddingCliConfig config)
    {
        EnsureDefaults(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, s_jsonOptions));
    }

    private static void EnsureDefaults(PuddingCliConfig config)
    {
        foreach (var provider in config.Providers)
        {
            provider.Billing ??= new ProviderBillingConfig();
            if (provider.Billing.Mode == BillingMode.PerToken && IsLocalEndpoint(provider.Endpoint))
                provider.Billing.Mode = BillingMode.LocalFree;
        }

        config.MemoryMaintenance ??= new MemoryMaintenanceConfig();
        config.ContextBudget ??= new ContextBudgetConfig();
        config.Hooks ??= new HookConfig();
        config.Swarm ??= new SwarmRuntimeConfig();
        if (config.MemoryMaintenance.CompactWriteThreshold <= 0)
            config.MemoryMaintenance.CompactWriteThreshold = 20;
        if (config.MemoryMaintenance.CompactMinEntries <= 0)
            config.MemoryMaintenance.CompactMinEntries = 180;
        if (config.MemoryMaintenance.CompactKeepEntries <= 0)
            config.MemoryMaintenance.CompactKeepEntries = 120;
        if (config.MemoryMaintenance.CompactKeepEntries > config.MemoryMaintenance.CompactMinEntries)
            config.MemoryMaintenance.CompactKeepEntries = Math.Max(1, config.MemoryMaintenance.CompactMinEntries / 2);
        if (config.MemoryMaintenance.ModelSummaryDailyTokenBudget <= 0)
            config.MemoryMaintenance.ModelSummaryDailyTokenBudget = 12000;
        if (config.MemoryMaintenance.ModelSummaryMaxInputChars <= 0)
            config.MemoryMaintenance.ModelSummaryMaxInputChars = 12000;
        if (config.MemoryMaintenance.ModelSummaryMaxOutputChars <= 0)
            config.MemoryMaintenance.ModelSummaryMaxOutputChars = 2400;
        if (config.ContextBudget.MaxPromptTokens <= 0)
            config.ContextBudget.MaxPromptTokens = 24000;
        if (config.ContextBudget.MaxHistoryMessages <= 0)
            config.ContextBudget.MaxHistoryMessages = 80;
        if (config.ContextBudget.PreserveTailMessages <= 0)
            config.ContextBudget.PreserveTailMessages = 8;
        if (config.Hooks.Enabled.Count == 0)
            config.Hooks.Enabled.Add("metrics");
        if (string.IsNullOrWhiteSpace(config.Hooks.AuditLogPath))
            config.Hooks.AuditLogPath = ".pudding/hooks.log";
        config.Hooks.External ??= [];
        foreach (var h in config.Hooks.External)
        {
            if (h.TimeoutMs <= 0)
                h.TimeoutMs = 8000;
        }
        if (config.Swarm is null)
            config.Swarm = new SwarmRuntimeConfig();

        if (config.GlobalSubconscious is null)
        {
            var fallbackProvider = config.ActiveProvider
                                   ?? config.Providers.FirstOrDefault()?.Id
                                   ?? "default";
            config.GlobalSubconscious = new GlobalSubconsciousConfig
            {
                ProviderId = fallbackProvider,
                Model = "gpt-4o-mini",
                BudgetRatio = 0.15,
                Visible = false
            };
        }

        var defaultProvider = config.ActiveProvider
                              ?? config.Providers.FirstOrDefault()?.Id
                              ?? "default";
        var defaultModel = config.Providers.FirstOrDefault(p =>
                               p.Id.Equals(defaultProvider, StringComparison.OrdinalIgnoreCase))
                           ?.Model ?? "gpt-4o";

        EnsureAgentTemplateRole(config, "spirit", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "leader", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "worker", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "explore", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "researcher", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "planner", defaultProvider, defaultModel);
        EnsureAgentTemplateRole(config, "reviewer", defaultProvider, defaultModel);
    }

    private static bool IsLocalEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var text = endpoint.ToLowerInvariant();
        return text.Contains("localhost")
               || text.Contains("127.0.0.1")
               || text.Contains("0.0.0.0")
               || text.Contains("ollama");
    }

    private static void EnsureAgentTemplateRole(
        PuddingCliConfig config,
        string role,
        string providerId,
        string model)
    {
        if (config.AgentTemplates.Any(t => t.Role.Equals(role, StringComparison.OrdinalIgnoreCase)))
            return;

        config.AgentTemplates.Add(new AgentTemplateEntry
        {
            Role = role,
            Conscious = new ModelRefConfig { ProviderId = providerId, Model = model },
            Subconscious = new ModelRefConfig
            {
                ProviderId = config.GlobalSubconscious!.ProviderId,
                Model = config.GlobalSubconscious.Model
            },
            SubconsciousPolicy = new SubconsciousPolicyConfig
            {
                Visible = config.GlobalSubconscious.Visible,
                Verbosity = "low",
                BudgetRatio = config.GlobalSubconscious.BudgetRatio
            }
        });
    }
}
