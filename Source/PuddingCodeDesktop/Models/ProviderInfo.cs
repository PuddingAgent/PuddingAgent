using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PuddingCodeDesktop.Models;

/// <summary>Provider entry loaded from ~/.pudding/config.json (shared with CLI)</summary>
public sealed class ProviderInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }

    public override string ToString() => $"{Id} ({Model})";
}

/// <summary>Desktop config loaded from ~/.pudding/config.json</summary>
public sealed class DesktopConfig
{
    public string? ActiveProvider { get; set; }
    public List<ProviderInfo> Providers { get; set; } = [];
}

/// <summary>Read ~/.pudding/config.json — compatible with CLI's format</summary>
public static class DesktopConfigLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pudding", "config.json");

    public static DesktopConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new DesktopConfig();

        try
        {
            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json);
            if (node is null) return new DesktopConfig();

            // Old single-provider format
            if (node["apiKey"] is not null)
            {
                return new DesktopConfig
                {
                    ActiveProvider = "default",
                    Providers =
                    [
                        new ProviderInfo
                        {
                            Id = "default",
                            Name = "Default",
                            Endpoint = node["endpoint"]?.GetValue<string>() ?? "https://api.openai.com/v1",
                            ApiKey = node["apiKey"]!.GetValue<string>(),
                            Model = node["model"]?.GetValue<string>() ?? "gpt-4o"
                        }
                    ]
                };
            }

            return JsonSerializer.Deserialize<DesktopConfig>(json, s_jsonOptions)
                   ?? new DesktopConfig();
        }
        catch
        {
            return new DesktopConfig();
        }
    }
}
