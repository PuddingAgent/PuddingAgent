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
    private readonly ICentralLockManager? _lockManager;
    private readonly string _ownerAgentId;
    private readonly string _ownerAgentName;
    private readonly string _ownerRole;

    public FileTool(
        ProjectContext? project = null,
        PermissionGuard? guard = null,
        ICentralLockManager? lockManager = null,
        string ownerAgentId = "spirit",
        string ownerAgentName = "Spirit",
        string ownerRole = "spirit")
    {
        _project = project;
        _guard = guard;
        _lockManager = lockManager;
        _ownerAgentId = ownerAgentId;
        _ownerAgentName = ownerAgentName;
        _ownerRole = ownerRole;
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
        if (_lockManager is not null)
        {
            var access = await _lockManager.CheckAccessAsync(_ownerAgentId, path, ct);
            if (!access.Allowed)
            {
                var lockId = access.ConflictingLock?.Id ?? "unknown";
                return $"Error: {access.Message} Use request_unlock or wait for release. lockId={lockId}";
            }
        }

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
        string? lockId = null;
        if (_lockManager is not null)
        {
            var acquire = await _lockManager.AcquireAsync(new LockAcquireRequest(
                OwnerAgentId: _ownerAgentId,
                OwnerAgentName: _ownerAgentName,
                OwnerRole: _ownerRole,
                Targets: [new LockTarget(path, CoordinationLockScope.File)],
                Type: CoordinationLockType.Edit,
                Ttl: TimeSpan.FromMinutes(15),
                Description: $"Implicit edit lock for {Path.GetFileName(path)}"), ct);
            if (!acquire.Acquired)
            {
                var conflict = acquire.ConflictingLock;
                var lockRef = conflict?.Id ?? "unknown";
                return $"Error: {acquire.Message} file is being edited by {conflict?.OwnerAgentName ?? "another agent"}. lockId={lockRef}";
            }

            lockId = acquire.LockId;
        }

        // Permission check
        if (_guard is not null)
        {
            var perm = _guard.ValidateFileWrite(path);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content, ct);
            return $"Written {content.Length} chars to {path}";
        }
        finally
        {
            if (_lockManager is not null && !string.IsNullOrWhiteSpace(lockId))
                await _lockManager.ReleaseAsync(lockId, _ownerAgentId, force: false, ct);
        }
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
