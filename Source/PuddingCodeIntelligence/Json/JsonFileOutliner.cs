using System.Text.Json;
using System.Text.RegularExpressions;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Json;

/// <summary>
/// JSON file outliner.
/// Extracts top-level keys and nested object/array keys as outline nodes.
/// Useful for appsettings.json, tsconfig.json, package.json, etc.
/// </summary>
public sealed partial class JsonFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".json", ".jsonc", ".json5"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // Detect JSON with comments (// or /*) — we strip them before parsing
    [GeneratedRegex(@"^\s*//", RegexOptions.Multiline)]
    private static partial Regex LineCommentRegex();

    public Task<OutlineResult> OutlineAsync(
        string filePath,
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = ParseSource(sourceCode, filePath);
            return Task.FromResult(new OutlineResult(true, filePath, nodes));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new OutlineResult(false, filePath, [], ex.Message));
        }
    }

    internal static IReadOnlyList<OutlineNode> ParseSource(string source, string filePath = "")
    {
        // Strip single-line comments for .jsonc support
        if (LineCommentRegex().IsMatch(source))
        {
            source = StripJsonComments(source);
        }

        var lines = source.Split('\n');
        var nodes = new List<OutlineNode>();

        try
        {
            using var doc = JsonDocument.Parse(source, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ExtractObjectKeys(doc.RootElement, lines, nodes, depth: 0, container: null);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                nodes.Add(new OutlineNode(
                    "[]", CodeSymbolKind.Variable, 1, 1,
                    Signature: $"Array[{doc.RootElement.GetArrayLength()}]"));
            }
        }
        catch (JsonException)
        {
            // If parsing fails, try line-by-line key extraction
            ExtractKeysFromLines(lines, nodes);
        }

        return nodes;
    }

    private static void ExtractObjectKeys(
        JsonElement obj,
        string[] lines,
        List<OutlineNode> nodes,
        int depth,
        string? container)
    {
        if (depth > 3) return; // limit depth for outline

        foreach (var prop in obj.EnumerateObject())
        {
            var lineNum = FindKeyLine(lines, prop.Name);
            var kind = prop.Value.ValueKind switch
            {
                JsonValueKind.Object => CodeSymbolKind.Class,
                JsonValueKind.Array => CodeSymbolKind.Variable,
                JsonValueKind.String => CodeSymbolKind.Property,
                JsonValueKind.Number => CodeSymbolKind.Property,
                JsonValueKind.True or JsonValueKind.False => CodeSymbolKind.Property,
                JsonValueKind.Null => CodeSymbolKind.Property,
                _ => CodeSymbolKind.Unknown
            };

            var valuePreview = GetValuePreview(prop.Value);
            var sig = $"\"{prop.Name}\": {valuePreview}";

            var node = new OutlineNode(
                prop.Name, kind, lineNum, lineNum,
                Signature: sig,
                Container: container);

            nodes.Add(node);

            // Recurse into nested objects
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var nestedContainer = container is null ? prop.Name : $"{container}.{prop.Name}";
                ExtractObjectKeys(prop.Value, lines, nodes, depth + 1, nestedContainer);
            }
        }
    }

    private static string GetValuePreview(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"\"{Truncate(value.GetString() ?? "", 40)}\"",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "{...}",
        JsonValueKind.Array => $"[...{value.GetArrayLength()}]",
        _ => "?"
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static int FindKeyLine(string[] lines, string key)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains($"\"{key}\""))
                return i + 1;
        }
        return 1;
    }

    private static void ExtractKeysFromLines(string[] lines, List<OutlineNode> nodes)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, "^\"([^\"]+)\"\\s*:");
            if (match.Success)
            {
                nodes.Add(new OutlineNode(
                    match.Groups[1].Value, CodeSymbolKind.Property, i + 1, i + 1,
                    Signature: trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed));
            }
        }
    }

    private static string StripJsonComments(string source)
    {
        var lines = source.Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//")) continue;
            // Strip inline comments
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            result.Add(idx >= 0 ? line[..idx] : line);
        }
        return string.Join('\n', result);
    }
}
