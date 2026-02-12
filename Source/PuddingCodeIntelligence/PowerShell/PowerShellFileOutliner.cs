using System.Text.RegularExpressions;

using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.PowerShell;

/// <summary>
/// PowerShell file outliner (regex-based).
/// Extracts functions, cmdlets, workflows, and class definitions.
/// </summary>
public sealed partial class PowerShellFileOutliner : IFileOutliner
{
    private static readonly string[] Extensions = [".ps1", ".psm1", ".psd1"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    // function Name { ... } or function Name(...) { ... }
    [GeneratedRegex(
        @"^\s*(?:function|filter)\s+(\S+?)(?:\s*\(([^)]*)\))?\s*(?:\{|\n\s*\{)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FunctionRegex();

    // function Name { ... } (alternate compact form)
    [GeneratedRegex(
        @"^\s*(?:function|filter)\s+(\w[\w-]*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FunctionNameRegex();

    // [CmdletBinding()] ... class Name { ... }
    [GeneratedRegex(
        @"^\s*class\s+(\w+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ClassRegex();

    // workflow Name { ... }
    [GeneratedRegex(
        @"^\s*workflow\s+(\w[\w-]*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex WorkflowRegex();

    // Enum Name { ... }
    [GeneratedRegex(
        @"^\s*enum\s+(\w+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EnumRegex();

    // $VariableName = ... (top-level assignments)
    [GeneratedRegex(
        @"^\s*(?:\[.*?\]\s*)?(\$\w+)\s*=",
        RegexOptions.Multiline)]
    private static partial Regex VariableRegex();

    // #Requires -Version / #Requires -Modules
    [GeneratedRegex(
        @"^\s*#Requires\s+(-\w+)\s+(.+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RequiresRegex();

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
        var processedLines = new HashSet<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1;

            // Skip comments
            if (trimmed.StartsWith('#') && !trimmed.StartsWith("#Requires"))
                continue;

            // #Requires
            var requiresMatch = RequiresRegex().Match(line);
            if (requiresMatch.Success && !processedLines.Contains(lineNum))
            {
                processedLines.Add(lineNum);
                nodes.Add(new OutlineNode(
                    $"#Requires {requiresMatch.Groups[1].Value}",
                    CodeSymbolKind.Constant, lineNum, lineNum,
                    Signature: line.Trim()));
                continue;
            }

            // Class
            var classMatch = ClassRegex().Match(line);
            if (classMatch.Success && !processedLines.Contains(lineNum))
            {
                processedLines.Add(lineNum);
                var name = classMatch.Groups[1].Value;
                var endLine = FindClosingBrace(lines, i);
                var children = ParseClassMembers(lines, i + 1, endLine);

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Class, lineNum, endLine,
                    Signature: $"class {name}",
                    Children: children.Count > 0 ? children : null));
                continue;
            }

            // Enum
            var enumMatch = EnumRegex().Match(line);
            if (enumMatch.Success && !processedLines.Contains(lineNum))
            {
                processedLines.Add(lineNum);
                var name = enumMatch.Groups[1].Value;
                var endLine = FindClosingBrace(lines, i);

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Enum, lineNum, endLine,
                    Signature: $"enum {name}"));
                continue;
            }

            // Workflow
            var workflowMatch = WorkflowRegex().Match(line);
            if (workflowMatch.Success && !processedLines.Contains(lineNum))
            {
                processedLines.Add(lineNum);
                var name = workflowMatch.Groups[1].Value;
                var endLine = FindClosingBrace(lines, i);

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, endLine,
                    Signature: $"workflow {name}"));
                continue;
            }

            // Function (more flexible matching)
            var funcMatch = FunctionNameRegex().Match(line);
            if (funcMatch.Success && !processedLines.Contains(lineNum))
            {
                processedLines.Add(lineNum);
                var name = funcMatch.Groups[1].Value;
                var endLine = FindClosingBrace(lines, i);

                // Extract parameters if on same/next lines
                var paramStr = ExtractParameters(lines, i);

                nodes.Add(new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, endLine,
                    Signature: $"function {name}({paramStr})"));
                continue;
            }

            // Top-level variable
            if (!line.StartsWith(' ') && !line.StartsWith('\t'))
            {
                var varMatch = VariableRegex().Match(line);
                if (varMatch.Success && !processedLines.Contains(lineNum))
                {
                    processedLines.Add(lineNum);
                    var name = varMatch.Groups[1].Value;
                    nodes.Add(new OutlineNode(
                        name, CodeSymbolKind.Variable, lineNum, lineNum,
                        Signature: line.Trim().Length > 80 ? line.Trim()[..80] + "..." : line.Trim()));
                }
            }
        }

        return nodes;
    }

    private static List<OutlineNode> ParseClassMembers(string[] lines, int startLine, int endLine)
    {
        var members = new List<OutlineNode>();
        if (startLine >= endLine || startLine >= lines.Length) return members;

        for (var i = startLine; i < Math.Min(endLine, lines.Length); i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var lineNum = i + 1;

            if (trimmed.StartsWith('#')) continue;

            // Method: [ReturnType] MethodName(...) { or hidden [ReturnType] MethodName(...)
            var methodMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^(?:(?:hidden|static)\s+)*(?:\[.*?\]\s+)?(\w+)\s*(?:\(([^)]*)\))?\s*\{");
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups[1].Value;
                var parameters = methodMatch.Groups[2].Value.Trim();
                var methodEndLine = FindClosingBrace(lines, i);

                members.Add(new OutlineNode(
                    name, CodeSymbolKind.Method, lineNum, methodEndLine,
                    Signature: $"{name}({parameters})"));
                continue;
            }

            // Property: [Type] $Name = ...
            var propMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"(?:\[.*?\]\s+)?(\$\w+)\s*=");
            if (propMatch.Success)
            {
                var name = propMatch.Groups[1].Value;
                members.Add(new OutlineNode(
                    name, CodeSymbolKind.Property, lineNum, lineNum,
                    Signature: trimmed.Length > 60 ? trimmed[..60] + "..." : trimmed));
            }
        }

        return members;
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

    private static string ExtractParameters(string[] lines, int lineIndex)
    {
        var line = lines[lineIndex];
        // Try to find parameters on the same line
        var openParen = line.IndexOf('(');
        if (openParen >= 0)
        {
            var closeParen = line.IndexOf(')', openParen);
            if (closeParen >= 0)
                return line[(openParen + 1)..closeParen].Trim();
            // Multi-line parameters
            var paramStart = line[(openParen + 1)..].Trim();
            if (lineIndex + 1 < lines.Length)
            {
                var nextLine = lines[lineIndex + 1].Trim();
                var closeIdx = nextLine.IndexOf(')');
                if (closeIdx >= 0)
                    return (paramStart + " " + nextLine[..closeIdx]).Trim();
            }
        }
        return "";
    }
}
