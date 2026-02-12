using System.Text.RegularExpressions;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Bicep;

/// <summary>
/// Bicep file outliner (regex-based).
/// Extracts resources, parameters, variables, outputs, modules, and types.
/// </summary>
public sealed partial class BicepFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".bicep", ".bicepparam"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // resource Name 'Type@Version' = { ...
    [GeneratedRegex(
        @"^\s*resource\s+(\w+)\s+'([^']+)'\s*(?:=\s*\{)?",
        RegexOptions.Multiline)]
    private static partial Regex ResourceRegex();

    // param Name Type [= default]
    [GeneratedRegex(
        @"^\s*param\s+(\w+)\s+(\S+)",
        RegexOptions.Multiline)]
    private static partial Regex ParamRegex();

    // var Name = ...
    [GeneratedRegex(
        @"^\s*var\s+(\w+)\s*=",
        RegexOptions.Multiline)]
    private static partial Regex VarRegex();

    // output Name Type = ...
    [GeneratedRegex(
        @"^\s*output\s+(\w+)\s+(\S+)\s*=",
        RegexOptions.Multiline)]
    private static partial Regex OutputRegex();

    // module Name 'path' = { ...
    [GeneratedRegex(
        @"^\s*module\s+(\w+)\s+'([^']+)'\s*(?:=\s*\{)?",
        RegexOptions.Multiline)]
    private static partial Regex ModuleRegex();

    // type Name = ...
    [GeneratedRegex(
        @"^\s*type\s+(\w+)\s*=",
        RegexOptions.Multiline)]
    private static partial Regex TypeRegex();

    // import ... as ... from '...'
    [GeneratedRegex(
        @"^\s*import\s+(.+?)\s+as\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex ImportRegex();

    // targetScope = '...'
    [GeneratedRegex(
        @"^\s*targetScope\s*=\s*'(\w+)'",
        RegexOptions.Multiline)]
    private static partial Regex TargetScopeRegex();

    // metadata/using
    [GeneratedRegex(
        @"^\s*(metadata|using)\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex MetadataUsingRegex();

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

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1;

            // Skip comments
            if (trimmed.StartsWith("//")) continue;

            // targetScope
            var scopeMatch = TargetScopeRegex().Match(line);
            if (scopeMatch.Success)
            {
                nodes.Add(new OutlineNode(
                    "targetScope", CodeSymbolKind.Constant, lineNum, lineNum,
                    Signature: $"targetScope = '{scopeMatch.Groups[1].Value}'"));
                continue;
            }

            // resource
            var resourceMatch = ResourceRegex().Match(line);
            if (resourceMatch.Success)
            {
                var name = resourceMatch.Groups[1].Value;
                var type = resourceMatch.Groups[2].Value;
                var endLine = FindClosingBrace(lines, i);

                // Extract short type name (last segment)
                var shortType = type.Contains('/') ? type.Split('/')[^1].Split('@')[0] : type;
                var version = type.Contains('@') ? type.Split('@')[^1] : "";

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Class, lineNum, endLine,
                    Signature: $"resource {name} '{shortType}@{version}'",
                    Modifiers: "resource"));
                continue;
            }

            // module
            var moduleMatch = ModuleRegex().Match(line);
            if (moduleMatch.Success)
            {
                var name = moduleMatch.Groups[1].Value;
                var path = moduleMatch.Groups[2].Value;
                var endLine = FindClosingBrace(lines, i);

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Class, lineNum, endLine,
                    Signature: $"module {name} '{path}'",
                    Modifiers: "module"));
                continue;
            }

            // param
            var paramMatch = ParamRegex().Match(line);
            if (paramMatch.Success)
            {
                var name = paramMatch.Groups[1].Value;
                var type = paramMatch.Groups[2].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Property, lineNum, lineNum,
                    Signature: $"param {name} {type}",
                    Modifiers: "param"));
                continue;
            }

            // var
            var varMatch = VarRegex().Match(line);
            if (varMatch.Success)
            {
                var name = varMatch.Groups[1].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Variable, lineNum, lineNum,
                    Signature: trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed,
                    Modifiers: "var"));
                continue;
            }

            // output
            var outputMatch = OutputRegex().Match(line);
            if (outputMatch.Success)
            {
                var name = outputMatch.Groups[1].Value;
                var type = outputMatch.Groups[2].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Property, lineNum, lineNum,
                    Signature: $"output {name} {type}",
                    Modifiers: "output"));
                continue;
            }

            // type
            var typeMatch = TypeRegex().Match(line);
            if (typeMatch.Success)
            {
                var name = typeMatch.Groups[1].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Type, lineNum, lineNum,
                    Signature: trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed,
                    Modifiers: "type"));
                continue;
            }

            // import
            var importMatch = ImportRegex().Match(line);
            if (importMatch.Success)
            {
                var what = importMatch.Groups[1].Value.Trim();
                var alias = importMatch.Groups[2].Value;
                nodes.Add(new OutlineNode(
                    alias, CodeSymbolKind.Namespace, lineNum, lineNum,
                    Signature: $"import {what} as {alias}"));
                continue;
            }

            // metadata/using
            var metaMatch = MetadataUsingRegex().Match(line);
            if (metaMatch.Success)
            {
                var kind = metaMatch.Groups[1].Value;
                var name = metaMatch.Groups[2].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Constant, lineNum, lineNum,
                    Signature: trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed));
            }
        }

        return nodes;
    }

    private static int FindClosingBrace(string[] lines, int openBraceLine)
    {
        var depth = 0;
        for (var i = openBraceLine; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            if (depth <= 0) return i + 1;
        }
        return lines.Length;
    }
}
