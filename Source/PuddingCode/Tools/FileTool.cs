using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Tools;

public sealed class FileTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ProjectContext? _project;

    public FileTool(ProjectContext? project = null) => _project = project;

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

        var path = ResolvePath(args.Path);

        try
        {
            return args.Action?.ToLower() switch
            {
                "read" => await File.ReadAllTextAsync(path, ct),
                "write" => await WriteFileAsync(path, args.Content ?? "", ct),
                "list" => string.Join("\n", Directory.GetFileSystemEntries(path)),
                _ => $"Unknown action: {args.Action}"
            };
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private string ResolvePath(string path) =>
        _project is not null && !Path.IsPathRooted(path)
            ? _project.Resolve(path)
            : path;

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
