using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCode.Tools;

public sealed class FileTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ProjectContext? _project;
    private readonly PermissionGuard? _guard;

    public FileTool(ProjectContext? project = null, PermissionGuard? guard = null)
    {
        _project = project;
        _guard = guard;
    }

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
                "read" => await ReadFileAsync(path, ct),
                "write" => await WriteFileAsync(path, args.Content ?? "", ct),
                "list" => ListDirectory(path),
                _ => $"Unknown action: {args.Action}"
            };
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        // Permission check
        if (_guard is not null)
        {
            var perm = _guard.ValidateFileRead(path);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    private async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        // Permission check
        if (_guard is not null)
        {
            var perm = _guard.ValidateFileWrite(path);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);
        return $"Written {content.Length} chars to {path}";
    }

    private string ListDirectory(string path)
    {
        // Permission check
        if (_guard is not null)
        {
            var perm = _guard.ValidateDirectoryList(path);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        return string.Join("\n", Directory.GetFileSystemEntries(path));
    }

    private string ResolvePath(string path) =>
        _project is not null && !Path.IsPathRooted(path)
            ? _project.Resolve(path)
            : path;
}

file record FileToolArgs(string? Action, string? Path, string? Content);
