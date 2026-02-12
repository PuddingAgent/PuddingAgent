using System.Text.RegularExpressions;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Python;

/// <summary>
/// Lightweight Python file outliner.
/// Extracts classes, methods, functions, async functions, class attributes,
/// and top-level variables without requiring a Python runtime.
/// </summary>
public sealed partial class PythonFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".py", ".pyw", ".pyi"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    [GeneratedRegex(@"^\s*class\s+([A-Za-z_]\w*)\s*(?:\(([^)]*)\))?\s*:")]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"^\s*(async\s+)?def\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*(?:->\s*([^:]+))?\s*:")]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^\s*([A-Za-z_]\w*)\s*(?::\s*[^=]+)?\s*=")]
    private static partial Regex AssignmentRegex();

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
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var nodes = new List<OutlineNode>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripInlineComment(lines[i]);
            var trimmed = line.Trim();
            if (ShouldSkipLine(trimmed) || GetIndent(line) != 0)
                continue;

            var lineNumber = i + 1;

            var classMatch = ClassRegex().Match(line);
            if (classMatch.Success)
            {
                var endLine = FindBlockEnd(lines, i, 0);
                var children = ParseClassMembers(lines, i + 1, endLine, 0);

                nodes.Add(new OutlineNode(
                    classMatch.Groups[1].Value,
                    CodeSymbolKind.Class,
                    lineNumber,
                    endLine,
                    Signature: ExtractSignature(line),
                    Children: children.Count > 0 ? children : null));
                continue;
            }

            if (TryParseFunction(line, lineNumber, lines, i, out var function))
            {
                nodes.Add(function);
                i = Math.Max(i, function.EndLine - 1);
                continue;
            }

            var assignmentMatch = AssignmentRegex().Match(line);
            if (assignmentMatch.Success)
            {
                var name = assignmentMatch.Groups[1].Value;
                nodes.Add(new OutlineNode(
                    name,
                    IsConstantName(name) ? CodeSymbolKind.Constant : CodeSymbolKind.Variable,
                    lineNumber,
                    lineNumber,
                    Signature: ExtractSignature(line)));
            }
        }

        return nodes;
    }

    private static List<OutlineNode> ParseClassMembers(
        string[] lines,
        int startIndex,
        int endLine,
        int classIndent)
    {
        var members = new List<OutlineNode>();
        var memberIndent = FindFirstChildIndent(lines, startIndex, endLine, classIndent);
        if (memberIndent is null)
            return members;

        for (var i = startIndex; i < Math.Min(endLine, lines.Length); i++)
        {
            var line = StripInlineComment(lines[i]);
            var trimmed = line.Trim();
            if (ShouldSkipLine(trimmed) || trimmed.StartsWith('@'))
                continue;

            if (GetIndent(line) != memberIndent)
                continue;

            var lineNumber = i + 1;

            if (TryParseFunction(line, lineNumber, lines, i, out var method))
            {
                members.Add(method with { Kind = CodeSymbolKind.Method });
                i = Math.Max(i, method.EndLine - 1);
                continue;
            }

            var assignmentMatch = AssignmentRegex().Match(line);
            if (assignmentMatch.Success)
            {
                var name = assignmentMatch.Groups[1].Value;
                members.Add(new OutlineNode(
                    name,
                    IsConstantName(name) ? CodeSymbolKind.Constant : CodeSymbolKind.Property,
                    lineNumber,
                    lineNumber,
                    Signature: ExtractSignature(line)));
            }
        }

        return members;
    }

    private static bool TryParseFunction(
        string line,
        int lineNumber,
        string[] lines,
        int lineIndex,
        out OutlineNode node)
    {
        node = default!;

        var match = FunctionRegex().Match(line);
        if (!match.Success)
            return false;

        var isAsync = !string.IsNullOrEmpty(match.Groups[1].Value);
        var name = match.Groups[2].Value;
        var parameters = match.Groups[3].Value.Trim();
        var returnType = match.Groups[4].Value.Trim();
        var signature = $"{(isAsync ? "async " : "")}def {name}({parameters})";
        if (!string.IsNullOrEmpty(returnType))
            signature += $" -> {returnType}";

        node = new OutlineNode(
            name,
            CodeSymbolKind.Method,
            lineNumber,
            FindBlockEnd(lines, lineIndex, GetIndent(line)),
            Signature: signature,
            Modifiers: isAsync ? "async" : null);

        return true;
    }

    private static int FindBlockEnd(string[] lines, int declarationIndex, int declarationIndent)
    {
        for (var i = declarationIndex + 1; i < lines.Length; i++)
        {
            var trimmed = StripInlineComment(lines[i]).Trim();
            if (ShouldSkipLine(trimmed) || trimmed.StartsWith('@'))
                continue;

            if (GetIndent(lines[i]) <= declarationIndent)
                return i;
        }

        return lines.Length;
    }

    private static int? FindFirstChildIndent(
        string[] lines,
        int startIndex,
        int endLine,
        int parentIndent)
    {
        for (var i = startIndex; i < Math.Min(endLine, lines.Length); i++)
        {
            var trimmed = StripInlineComment(lines[i]).Trim();
            if (ShouldSkipLine(trimmed) || trimmed.StartsWith('@'))
                continue;

            var indent = GetIndent(lines[i]);
            if (indent > parentIndent)
                return indent;
        }

        return null;
    }

    private static int GetIndent(string line)
    {
        var indent = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
                indent++;
            else if (ch == '\t')
                indent += 4;
            else
                break;
        }

        return indent;
    }

    private static string StripInlineComment(string line)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            var escaped = i > 0 && line[i - 1] == '\\';

            if (ch == '\'' && !inDouble && !escaped)
                inSingle = !inSingle;
            else if (ch == '"' && !inSingle && !escaped)
                inDouble = !inDouble;
            else if (ch == '#' && !inSingle && !inDouble)
                return line[..i];
        }

        return line;
    }

    private static bool ShouldSkipLine(string trimmed) =>
        string.IsNullOrWhiteSpace(trimmed)
        || trimmed.StartsWith('#')
        || trimmed is "pass" or "...";

    private static bool IsConstantName(string name) =>
        name.Length > 0 && name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');

    private static string ExtractSignature(string line)
    {
        var signature = line.Trim();
        return signature.Length > 120 ? signature[..120] + "..." : signature;
    }
}
