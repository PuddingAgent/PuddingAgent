using System.Text.RegularExpressions;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Yaml;

/// <summary>
/// YAML file outliner (regex-based, no external dependencies).
/// Extracts top-level and nested keys as outline nodes.
/// Useful for appsettings.yaml, docker-compose.yml, GitHub Actions, etc.
/// </summary>
public sealed partial class YamlFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".yml", ".yaml"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // Matches YAML key-value pairs: key: value or key:
    // Also handles keys with special chars when quoted: "key": value
    [GeneratedRegex(@"^(\s*)([\w\-./]+|""[^""]+"")\s*:", RegexOptions.Multiline)]
    private static partial Regex YamlKeyRegex();

    // Matches YAML comment lines
    [GeneratedRegex(@"^\s*#", RegexOptions.Multiline)]
    private static partial Regex CommentLineRegex();

    // Document separator ---
    [GeneratedRegex(@"^---\s*$", RegexOptions.Multiline)]
    private static partial Regex DocumentSeparatorRegex();

    // Multi-line array item: - item
    [GeneratedRegex(@"^(\s*)-\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex ArrayItemRegex();

    public Task<OutlineResult> OutlineAsync(
        string filePath,
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = ParseSource(sourceCode);
            return Task.FromResult(new OutlineResult(true, filePath, nodes));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new OutlineResult(false, filePath, [], ex.Message));
        }
    }

    internal static IReadOnlyList<OutlineNode> ParseSource(string source)
    {
        var lines = source.Split('\n');
        var nodes = new List<OutlineNode>();
        var indentStack = new List<(int indent, string name)>(); // for container tracking

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            // Document separator
            if (DocumentSeparatorRegex().IsMatch(line))
            {
                nodes.Add(new OutlineNode(
                    "---", CodeSymbolKind.Constant, lineNum, lineNum,
                    Signature: "document separator"));
                indentStack.Clear();
                continue;
            }

            // Key-value pair
            var keyMatch = YamlKeyRegex().Match(line);
            if (keyMatch.Success)
            {
                var indent = keyMatch.Groups[1].Value.Length;
                var key = keyMatch.Groups[2].Value.Trim('"');
                var restOfLine = line[(keyMatch.Index + keyMatch.Length)..].Trim();

                // Determine kind
                var kind = CodeSymbolKind.Property;
                if (string.IsNullOrEmpty(restOfLine) || restOfLine == "|")
                {
                    kind = CodeSymbolKind.Class; // container/object or block scalar
                }
                else if (restOfLine.StartsWith('['))
                {
                    kind = CodeSymbolKind.Variable; // inline array
                }

                // Build container path
                var container = BuildContainerPath(indentStack, indent, key);

                // Value preview
                var valuePreview = string.IsNullOrEmpty(restOfLine) || restOfLine == "|"
                    ? "{}"
                    : Truncate(restOfLine, 50);

                var sig = container is null
                    ? $"{key}: {valuePreview}"
                    : $"{container}.{key}: {valuePreview}";

                nodes.Add(new OutlineNode(
                    key, kind, lineNum, lineNum,
                    Signature: sig,
                    Modifiers: $"indent:{indent}",
                    Container: container));

                // Update indent stack
                while (indentStack.Count > 0 && indentStack[^1].indent >= indent)
                    indentStack.RemoveAt(indentStack.Count - 1);

                if (string.IsNullOrEmpty(restOfLine) || restOfLine == "|")
                {
                    indentStack.Add((indent, key));
                }

                continue;
            }

            // Array item
            var arrayMatch = ArrayItemRegex().Match(line);
            if (arrayMatch.Success)
            {
                var indent = arrayMatch.Groups[1].Value.Length;
                var itemValue = arrayMatch.Groups[2].Value.TrimEnd(':');
                // Only add significant array items (not plain strings)
                if (itemValue.StartsWith('{') || itemValue.StartsWith('['))
                {
                    nodes.Add(new OutlineNode(
                        "-", CodeSymbolKind.Variable, lineNum, lineNum,
                        Signature: Truncate(trimmed, 80)));
                }
            }
        }

        return nodes;
    }

    private static string? BuildContainerPath(
        List<(int indent, string name)> stack,
        int currentIndent,
        string currentKey)
    {
        // Find the parent container (last stack entry with indent < current)
        var parts = new List<string>();
        foreach (var (indent, name) in stack)
        {
            if (indent < currentIndent)
                parts.Add(name);
        }

        return parts.Count > 0 ? string.Join('.', parts) : null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
