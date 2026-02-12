using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Tools;

public sealed class FileTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Name => "file";
    public string Description => "Read or write file contents. Actions: read, write, list.";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "The action to perform: read, write, or list"),
            new("path", "string", "File or directory path"),
            new("content", "string", "Content to write (only for write action)")
        ],
        ["action", "path"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<FileToolArgs>(argumentsJson, s_jsonOptions);
        if (args?.Path is null) return "Error: path is required";

        try
        {
            return args.Action?.ToLower() switch
            {
                "read" => await File.ReadAllTextAsync(args.Path, ct),
                "write" => await WriteFileAsync(args.Path, args.Content ?? "", ct),
                "list" => string.Join("\n", Directory.GetFileSystemEntries(args.Path)),
                _ => $"Unknown action: {args.Action}"
            };
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);
        return $"Written {content.Length} chars to {path}";
    }
}

file record FileToolArgs(string? Action, string? Path, string? Content);
