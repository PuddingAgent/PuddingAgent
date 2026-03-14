using System.Text;
using Spectre.Console;

namespace PuddingCodeCLI;

internal static class MarkdownConsoleRenderer
{
    public static void Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var codeLang = string.Empty;
        var codeBuffer = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw ?? string.Empty;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    inCode = true;
                    codeLang = line.Length > 3 ? line[3..].Trim() : string.Empty;
                    codeBuffer.Clear();
                }
                else
                {
                    FlushCodeBlock(codeBuffer.ToString(), codeLang);
                    inCode = false;
                    codeLang = string.Empty;
                    codeBuffer.Clear();
                }
                continue;
            }

            if (inCode)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                AnsiConsole.WriteLine();
                continue;
            }

            if (line.StartsWith("# "))
            {
                AnsiConsole.MarkupLine($"[bold yellow]{line[2..].EscapeMarkup()}[/]");
                continue;
            }

            if (line.StartsWith("## "))
            {
                AnsiConsole.MarkupLine($"[bold]{line[3..].EscapeMarkup()}[/]");
                continue;
            }

            if (line.StartsWith("### "))
            {
                AnsiConsole.MarkupLine($"[underline]{line[4..].EscapeMarkup()}[/]");
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var content = line[2..].TrimStart();
                AnsiConsole.MarkupLine($"[grey]•[/] {content.EscapeMarkup()}");
                continue;
            }

            if (IsOrderedList(line, out var ordered))
            {
                AnsiConsole.MarkupLine(ordered.EscapeMarkup());
                continue;
            }

            if (line.StartsWith("> "))
            {
                AnsiConsole.MarkupLine($"[italic grey]{line[2..].EscapeMarkup()}[/]");
                continue;
            }

            AnsiConsole.MarkupLine(line.EscapeMarkup());
        }

        if (inCode && codeBuffer.Length > 0)
            FlushCodeBlock(codeBuffer.ToString(), codeLang);
    }

    private static void FlushCodeBlock(string code, string codeLang)
    {
        var header = string.IsNullOrWhiteSpace(codeLang) ? "Code" : $"Code ({codeLang})";
        var panel = new Panel(code.EscapeMarkup().TrimEnd('\r', '\n'))
        {
            Header = new PanelHeader(header),
            Border = BoxBorder.Rounded
        };
        panel.BorderColor(Color.Grey);
        AnsiConsole.Write(panel);
    }

    private static bool IsOrderedList(string line, out string normalized)
    {
        normalized = line;
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0 || i + 1 >= line.Length) return false;
        if (line[i] != '.' || line[i + 1] != ' ') return false;
        normalized = line;
        return true;
    }
}
