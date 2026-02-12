using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.TypeScript;

/// <summary>
/// Regex-based TypeScript/JavaScript file outliner.
/// Extracts classes, interfaces, type aliases, enums, functions, methods,
/// properties, and top-level variable declarations without external dependencies.
/// </summary>
public sealed partial class TypeScriptFileOutliner : IFileOutliner
{
    // ── Supported extensions ──────────────────────────────────────────
    private static readonly string[] Extensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".mjs"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // ── Regex patterns (compiled for performance) ─────────────────────

    // export? (default)? (abstract|declare)? class Name <T> extends B implements I { ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:(abstract|declare)\s+)?class\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex ClassRegex();

    // export? interface Name <T> extends E { ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?interface\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex InterfaceRegex();

    // export? type Name = ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?type\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex TypeAliasRegex();

    // export? (const)? enum Name { ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:(?:const|declare)\s+)?enum\s+(\w+)",
        RegexOptions.Multiline)]
    private static partial Regex EnumRegex();

    // export? function*? Name(...) { ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\*?\s+(\w+)\s*(?:<[^>]*>)?\s*\(([^)]*)\)",
        RegexOptions.Multiline)]
    private static partial Regex FunctionRegex();

    // Arrow function: export? const Name = (...) => ...
    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:(?:const|let|var)\s+)(\w+)\s*(?::\s*[^=]+)?\s*=\s*(?:async\s+)?(?:<[^>]*>)?\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex ArrowFunctionRegex();

    // Method: (public|private|protected|static|async|override|abstract|readonly)* Name(...) { ...
    [GeneratedRegex(
        @"^\s{2,}((?:(?:public|private|protected|static|async|override|abstract|readonly|get|set)\s+)*)(\w+)\s*(?:<[^>]*>)?\s*\(([^)]*)\)(?:\s*:\s*([^\s{]+))?",
        RegexOptions.Multiline)]
    private static partial Regex MethodRegex();

    // Property in class/interface: (modifiers) Name: Type; or Name = ...;
    [GeneratedRegex(
        @"^\s{2,}((?:(?:public|private|protected|static|readonly|abstract|override)\s+)*)(\w+)\s*[?]?\s*(?::\s*([^=;]+?))?\s*(?:=|;)",
        RegexOptions.Multiline)]
    private static partial Regex PropertyRegex();

    // export? const/let/var Name: Type = ... (top-level)
    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:declare\s+)?(?:const|let|var)\s+(\w+)\s*(?::\s*([^\s=]+(?:\s*\|\s*[^\s=]+)*))?",
        RegexOptions.Multiline)]
    private static partial Regex VariableRegex();

    // import ... from '...'
    [GeneratedRegex(
        @"^\s*import\s+.*?from\s+['""]([^'""]+)['""]",
        RegexOptions.Multiline)]
    private static partial Regex ImportRegex();

    // ── Marker keywords for closing braces ────────────────────────────
    private static readonly HashSet<string> MethodKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "static", "async", "override",
        "abstract", "readonly", "get", "set"
    };

    // ── Public API ────────────────────────────────────────────────────

    public async Task<OutlineResult> OutlineAsync(
        string filePath,
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = await Task.Run(
                () => ParseSource(sourceCode),
                cancellationToken);

            return new OutlineResult(true, filePath, nodes);
        }
        catch (Exception ex)
        {
            return new OutlineResult(false, filePath, [], ex.Message);
        }
    }

    // ── Internal parsing ──────────────────────────────────────────────

    internal static IReadOnlyList<OutlineNode> ParseSource(string source)
    {
        var lines = source.Split('\n');
        var nodes = new List<OutlineNode>();
        var topLevelBuffer = new List<int>(); // track top-level variable/function line indices

        // 1. Parse classes, interfaces, type aliases, enums (containers)
        ParseContainers(lines, nodes, topLevelBuffer);

        // 2. Parse top-level functions and variables
        ParseTopLevelDeclarations(lines, nodes, topLevelBuffer);

        // 3. Sort by line number
        nodes.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

        return nodes;
    }

    private static void ParseContainers(string[] lines, List<OutlineNode> nodes, List<int> skipLines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1; // 1-based

            // Skip lines starting with // or inside strings (simple heuristic)
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                continue;

            OutlineNode? node = null;

            // Class
            if (TryMatchAtLineStart(lines, i, ClassRegex(), out var classMatch))
            {
                var modifier = classMatch.Groups[1].Value;
                var name = classMatch.Groups[2].Value;
                var sig = ExtractLineSignature(line);
                var endLine = FindClosingBrace(lines, i);
                var children = ParseMembers(lines, i + 1, endLine);

                node = new OutlineNode(
                    name, CodeSymbolKind.Class, lineNum, endLine,
                    Signature: sig,
                    Modifiers: string.IsNullOrEmpty(modifier) ? null : modifier,
                    Children: children.Count > 0 ? children : null);

                skipLines.Add(i);
            }
            // Interface
            else if (TryMatchAtLineStart(lines, i, InterfaceRegex(), out var ifaceMatch))
            {
                var name = ifaceMatch.Groups[1].Value;
                var sig = ExtractLineSignature(line);
                var endLine = FindClosingBrace(lines, i);
                var children = ParseMembers(lines, i + 1, endLine);

                node = new OutlineNode(
                    name, CodeSymbolKind.Interface, lineNum, endLine,
                    Signature: sig,
                    Children: children.Count > 0 ? children : null);

                skipLines.Add(i);
            }
            // Type alias
            else if (TryMatchAtLineStart(lines, i, TypeAliasRegex(), out var typeMatch))
            {
                var name = typeMatch.Groups[1].Value;
                var sig = ExtractLineSignature(line);

                node = new OutlineNode(
                    name, CodeSymbolKind.Type, lineNum, lineNum,
                    Signature: sig);

                skipLines.Add(i);
            }
            // Enum
            else if (TryMatchAtLineStart(lines, i, EnumRegex(), out var enumMatch))
            {
                var name = enumMatch.Groups[1].Value;
                var sig = ExtractLineSignature(line);
                var endLine = FindClosingBrace(lines, i);

                node = new OutlineNode(
                    name, CodeSymbolKind.Enum, lineNum, endLine,
                    Signature: sig);

                skipLines.Add(i);
            }
            // Top-level function
            else if (TryMatchAtLineStart(lines, i, FunctionRegex(), out var funcMatch))
            {
                var name = funcMatch.Groups[1].Value;
                var parameters = funcMatch.Groups[2].Value.Trim();
                var sig = $"function {name}({parameters})";
                var endLine = FindClosingBrace(lines, i);

                node = new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, endLine,
                    Signature: sig,
                    Modifiers: DetectFunctionModifiers(line));

                skipLines.Add(i);
            }

            if (node is not null)
                nodes.Add(node);
        }
    }

    private static List<OutlineNode> ParseMembers(string[] lines, int startLine, int endLine)
    {
        var members = new List<OutlineNode>();
        if (startLine >= endLine || startLine >= lines.Length) return members;

        for (var i = startLine; i < Math.Min(endLine, lines.Length); i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1;

            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                continue;

            // Method
            var methodMatch = MethodRegex().Match(line);
            if (methodMatch.Success && line.Contains('(') && !line.TrimStart().StartsWith("//"))
            {
                var modifiers = methodMatch.Groups[1].Value.Trim();
                var name = methodMatch.Groups[2].Value;
                var parameters = methodMatch.Groups[3].Value.Trim();
                var returnType = methodMatch.Groups[4].Value.Trim();

                // Filter out control flow keywords
                if (MethodKeywords.Contains(name) ||
                    name is "if" or "else" or "for" or "while" or "switch" or "catch" or "return" or "new" or "throw")
                    continue;

                var sig = string.IsNullOrEmpty(returnType)
                    ? $"{name}({parameters})"
                    : $"{name}({parameters}): {returnType}";

                var methodEndLine = FindClosingBrace(lines, i);

                members.Add(new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, methodEndLine,
                    Signature: sig,
                    Modifiers: string.IsNullOrEmpty(modifiers) ? null : modifiers));
                continue;
            }

            // Property
            var propMatch = PropertyRegex().Match(line);
            if (propMatch.Success && !line.Contains('('))
            {
                var modifiers = propMatch.Groups[1].Value.Trim();
                var name = propMatch.Groups[2].Value;
                var type = propMatch.Groups[3].Value.Trim();

                if (MethodKeywords.Contains(name)) continue;

                var sig = string.IsNullOrEmpty(type) ? name : $"{name}: {type}";

                members.Add(new OutlineNode(
                    name, CodeSymbolKind.Property, lineNum, lineNum,
                    Signature: sig,
                    Modifiers: string.IsNullOrEmpty(modifiers) ? null : modifiers));
            }
        }

        return members;
    }

    private static void ParseTopLevelDeclarations(string[] lines, List<OutlineNode> nodes, List<int> skipLines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (skipLines.Contains(i)) continue;

            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1;

            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                continue;

            // Arrow function
            var arrowMatch = ArrowFunctionRegex().Match(line);
            if (arrowMatch.Success)
            {
                var name = arrowMatch.Groups[1].Value;
                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, lineNum,
                    Signature: $"const {name} = ...",
                    Modifiers: "arrow"));
                continue;
            }

            // Top-level const/let/var
            if (trimmed.StartsWith("export ") || trimmed.StartsWith("const ") ||
                trimmed.StartsWith("let ") || trimmed.StartsWith("var ") ||
                trimmed.StartsWith("declare "))
            {
                // Only top-level (no leading spaces)
                if (!line.StartsWith(" ") && !line.StartsWith("\t"))
                {
                    var varMatch = VariableRegex().Match(line);
                    if (varMatch.Success)
                    {
                        var name = varMatch.Groups[1].Value;
                        var type = varMatch.Groups[2].Value;
                        var sig = string.IsNullOrEmpty(type)
                            ? line.Trim().TrimEnd(';')
                            : $"{name}: {type}";

                        nodes.Add(new OutlineNode(
                            name, CodeSymbolKind.Variable, lineNum, lineNum,
                            Signature: sig));
                    }
                }
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static bool TryMatchAtLineStart(string[] lines, int index, Regex regex, out Match match)
    {
        match = regex.Match(lines[index]);
        return match.Success && match.Index < lines[index].Length / 2;
    }

    private static string ExtractLineSignature(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 120)
            trimmed = trimmed[..120] + "...";
        return trimmed;
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
            if (depth <= 0) return i + 1; // 1-based end line
        }
        return lines.Length; // fallback
    }

    private static string? DetectFunctionModifiers(string line)
    {
        var mods = new List<string>();
        if (line.Contains("export")) mods.Add("export");
        if (line.Contains("async")) mods.Add("async");
        if (line.Contains("default")) mods.Add("default");
        return mods.Count > 0 ? string.Join(" ", mods) : null;
    }
}
