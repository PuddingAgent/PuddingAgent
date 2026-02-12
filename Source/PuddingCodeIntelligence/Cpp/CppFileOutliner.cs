using System.Text.RegularExpressions;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Cpp;

/// <summary>
/// Lightweight C/C++ file outliner.
/// Extracts namespaces, classes, structs, enums, functions, constructors,
/// destructors, methods, and fields without requiring a compiler database.
/// </summary>
public sealed partial class CppFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions =
        [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx"];

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof",
    };

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_]\w*(?:::\w+)*)\s*\{?")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^\s*(?:template\s*<[^>]+>\s*)?(class|struct)\s+([A-Za-z_]\w*)\b")]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^\s*enum(?:\s+(?:class|struct))?\s+([A-Za-z_]\w*)\b")]
    private static partial Regex EnumRegex();

    [GeneratedRegex(@"^\s*(?:explicit\s+)?(~?[A-Za-z_]\w*)\s*\(([^)]*)\)\s*(?:noexcept\s*)?(?:;|\{|:)")]
    private static partial Regex ConstructorRegex();

    [GeneratedRegex(@"^\s*(?:(?:static|virtual|inline|constexpr|consteval|friend|extern)\s+)*(?<ret>[A-Za-z_][\w:<>,~*&\s]*?)\s+(?<name>operator\s*[^\s(]+|[A-Za-z_]\w*)\s*\((?<params>[^)]*)\)\s*(?:const\s*)?(?:noexcept\s*)?(?:override\s*)?(?:final\s*)?(?:;|\{)")]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^\s*(?:(?:static|mutable|constexpr|const|volatile)\s+)*(?<type>[A-Za-z_][\w:<>,~*&\s]*?)\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]+\])?\s*(?:=[^;]*)?;")]
    private static partial Regex FieldRegex();

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
            return Task.FromResult(new OutlineResult(false, filePath, [], ex.Message));
        }
    }

    internal static IReadOnlyList<OutlineNode> ParseSource(string source)
    {
        var lines = StripComments(source).Split('\n');
        var nodes = new List<OutlineNode>();
        var skipFunctionScan = new HashSet<int>();

        ParseContainers(lines, nodes, skipFunctionScan);
        ParseFunctions(lines, nodes, skipFunctionScan);

        nodes.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return nodes;
    }

    private static void ParseContainers(
        string[] lines,
        List<OutlineNode> nodes,
        HashSet<int> skipFunctionScan)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (ShouldSkipLine(trimmed))
                continue;

            var lineNumber = i + 1;

            var namespaceMatch = NamespaceRegex().Match(lines[i]);
            if (namespaceMatch.Success)
            {
                nodes.Add(new OutlineNode(
                    namespaceMatch.Groups[1].Value,
                    CodeSymbolKind.Namespace,
                    lineNumber,
                    FindClosingBrace(lines, i),
                    Signature: ExtractSignature(lines[i])));
                continue;
            }

            var typeMatch = TypeRegex().Match(lines[i]);
            if (typeMatch.Success && !IsForwardDeclaration(lines[i]))
            {
                var kind = typeMatch.Groups[1].Value == "class"
                    ? CodeSymbolKind.Class
                    : CodeSymbolKind.Struct;
                var name = typeMatch.Groups[2].Value;
                var endLine = FindClosingBrace(lines, i);
                MarkRange(skipFunctionScan, i, endLine);

                var children = ParseTypeMembers(lines, i + 1, endLine, name);
                nodes.Add(new OutlineNode(
                    name,
                    kind,
                    lineNumber,
                    endLine,
                    Signature: ExtractSignature(lines[i]),
                    Children: children.Count > 0 ? children : null));
                continue;
            }

            var enumMatch = EnumRegex().Match(lines[i]);
            if (enumMatch.Success && !IsForwardDeclaration(lines[i]))
            {
                var endLine = FindClosingBrace(lines, i);
                MarkRange(skipFunctionScan, i, endLine);

                nodes.Add(new OutlineNode(
                    enumMatch.Groups[1].Value,
                    CodeSymbolKind.Enum,
                    lineNumber,
                    endLine,
                    Signature: ExtractSignature(lines[i])));
            }
        }
    }

    private static List<OutlineNode> ParseTypeMembers(
        string[] lines,
        int startIndex,
        int endLine,
        string typeName)
    {
        var members = new List<OutlineNode>();
        if (startIndex >= lines.Length || startIndex + 1 >= endLine)
            return members;

        for (var i = startIndex; i < Math.Min(endLine - 1, lines.Length); i++)
        {
            var trimmed = lines[i].Trim();
            if (ShouldSkipLine(trimmed) || IsAccessLabel(trimmed))
                continue;

            var lineNumber = i + 1;

            var constructorMatch = ConstructorRegex().Match(trimmed);
            if (constructorMatch.Success
                && IsConstructorOrDestructor(constructorMatch.Groups[1].Value, typeName))
            {
                var name = constructorMatch.Groups[1].Value;
                members.Add(new OutlineNode(
                    name,
                    name.StartsWith('~') ? CodeSymbolKind.Method : CodeSymbolKind.Constructor,
                    lineNumber,
                    LineHasOpeningBrace(trimmed) ? FindClosingBrace(lines, i) : lineNumber,
                    Signature: $"{name}({constructorMatch.Groups[2].Value.Trim()})"));
                continue;
            }

            if (TryParseFunction(trimmed, lineNumber, lines, i, out var method))
            {
                members.Add(method);
                continue;
            }

            if (!trimmed.Contains('('))
            {
                var fieldMatch = FieldRegex().Match(trimmed);
                if (fieldMatch.Success)
                {
                    var name = fieldMatch.Groups["name"].Value;
                    var type = fieldMatch.Groups["type"].Value.Trim();
                    members.Add(new OutlineNode(
                        name,
                        CodeSymbolKind.Field,
                        lineNumber,
                        lineNumber,
                        Signature: $"{type} {name}"));
                }
            }
        }

        return members;
    }

    private static void ParseFunctions(
        string[] lines,
        List<OutlineNode> nodes,
        HashSet<int> skipFunctionScan)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (skipFunctionScan.Contains(i))
                continue;

            var trimmed = lines[i].Trim();
            if (ShouldSkipLine(trimmed))
                continue;

            if (TryParseFunction(trimmed, i + 1, lines, i, out var function))
            {
                nodes.Add(function);

                if (LineHasOpeningBrace(trimmed))
                {
                    var endLine = function.EndLine;
                    MarkRange(skipFunctionScan, i + 1, endLine);
                    i = Math.Max(i, endLine - 1);
                }
            }
        }
    }

    private static bool TryParseFunction(
        string trimmedLine,
        int lineNumber,
        string[] lines,
        int lineIndex,
        out OutlineNode node)
    {
        node = default!;

        var match = FunctionRegex().Match(trimmedLine);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.Trim();
        if (ControlKeywords.Contains(name))
            return false;

        var returnType = match.Groups["ret"].Value.Trim();
        var parameters = match.Groups["params"].Value.Trim();
        var signature = $"{returnType} {name}({parameters})";
        var endLine = LineHasOpeningBrace(trimmedLine)
            ? FindClosingBrace(lines, lineIndex)
            : lineNumber;

        node = new OutlineNode(
            name,
            CodeSymbolKind.Method,
            lineNumber,
            endLine,
            Signature: signature);
        return true;
    }

    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(
            source,
            @"/\*.*?\*/",
            match => new string('\n', match.Value.Count(c => c == '\n')),
            RegexOptions.Singleline);

        return Regex.Replace(withoutBlockComments, @"//.*$", "", RegexOptions.Multiline);
    }

    private static bool ShouldSkipLine(string trimmed) =>
        string.IsNullOrWhiteSpace(trimmed)
        || trimmed.StartsWith('#')
        || trimmed.StartsWith('*');

    private static bool IsAccessLabel(string trimmed) =>
        trimmed is "public:" or "private:" or "protected:";

    private static bool IsForwardDeclaration(string line) =>
        line.TrimEnd().EndsWith(';') && !line.Contains('{');

    private static bool IsConstructorOrDestructor(string name, string typeName) =>
        string.Equals(name, typeName, StringComparison.Ordinal)
        || string.Equals(name, "~" + typeName, StringComparison.Ordinal);

    private static bool LineHasOpeningBrace(string line) => line.Contains('{');

    private static int FindClosingBrace(string[] lines, int openBraceLine)
    {
        var depth = 0;
        var sawOpenBrace = false;

        for (var i = openBraceLine; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{')
                {
                    sawOpenBrace = true;
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }

            if (sawOpenBrace && depth <= 0)
                return i + 1;
        }

        return openBraceLine + 1;
    }

    private static void MarkRange(HashSet<int> lines, int startIndex, int endLine)
    {
        for (var i = startIndex; i < endLine; i++)
            lines.Add(i);
    }

    private static string ExtractSignature(string line)
    {
        var signature = line.Trim();
        return signature.Length > 120 ? signature[..120] + "..." : signature;
    }
}
