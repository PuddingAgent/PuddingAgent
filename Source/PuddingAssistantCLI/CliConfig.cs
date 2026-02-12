using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>CLI configuration root — stored at ~/.pudding/config.json</summary>
public sealed class PuddingCliConfig
{
    public string? ActiveProvider { get; set; }
    public List<ProviderEntry> Providers { get; set; } = [];
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
    {
        if (!File.Exists(path)) return new PuddingCliConfig();
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
                    ]
                };
                Save(path, migrated);
                return migrated;
            }

            return JsonSerializer.Deserialize<PuddingCliConfig>(json, s_jsonOptions)
                   ?? new PuddingCliConfig();
        }
        catch
        {
            return new PuddingCliConfig();
        }
    }

    public static void Save(string path, PuddingCliConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, s_jsonOptions));
    }
}
