using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Plugins;

public enum PluginLoadStatus
{
    Discovered,
    ManifestInvalid,
    ManifestOnly,
}

public sealed record PluginCatalogEntry
{
    public required string PluginId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required PluginLoadStatus Status { get; init; }
    public string StatusReason { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = [];
}

internal sealed record PluginManifestValidationResult(
    bool IsValid,
    string? PluginId,
    string? Name,
    string? Version,
    string? Error)
{
    public static PluginManifestValidationResult Invalid(string error, string? pluginId = null)
        => new(false, pluginId, null, null, error);
}

/// <summary>
/// Phase 1 plugin catalog: reads plugin.json descriptors from the data root and exposes their
/// tools as manifest-only catalog entries. It deliberately does not load DLLs; that keeps the
/// first slice focused on package shape, validation, capability visibility, and diagnostics.
/// </summary>
public sealed class PluginManifestCatalog : IPuddingToolSource
{
    private static readonly Regex s_pluginIdRegex = new("^[a-z0-9][a-z0-9.-]*[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex s_toolIdRegex = new("^[a-zA-Z0-9_]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PuddingDataPaths _paths;
    private readonly PluginDiagnosticsSink? _diagnostics;
    private readonly object _gate = new();
    private IReadOnlyList<PluginCatalogEntry>? _plugins;
    private IReadOnlyList<IPuddingTool>? _tools;

    public PluginManifestCatalog(PuddingDataPaths paths, PluginDiagnosticsSink? diagnostics = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public string SourceId => "plugins";

    public IReadOnlyList<PluginCatalogEntry> ListPlugins()
    {
        EnsureLoaded();
        return _plugins!;
    }

    public IReadOnlyList<IPuddingTool> ListTools()
    {
        EnsureLoaded();
        return _tools!;
    }

    public void Reload()
    {
        lock (_gate)
        {
            _plugins = null;
            _tools = null;
        }

        _diagnostics?.Record(new PluginDiagnosticEvent
        {
            EventType = "plugin.catalog.reload_requested",
            Status = "requested",
            Message = "Plugin catalog snapshot was cleared and will be rebuilt on the next read.",
        });
    }

    internal static PluginManifestValidationResult ValidateManifestJson(string manifestJson, string pluginDir)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifestDto>(manifestJson, s_jsonOptions);
            if (manifest is null)
                return PluginManifestValidationResult.Invalid("plugin.json is empty.");

            var validationError = ValidateManifest(pluginDir, manifest);
            if (validationError is not null)
                return PluginManifestValidationResult.Invalid(validationError, manifest.Id);

            return new PluginManifestValidationResult(true, manifest.Id!, manifest.Name!, manifest.Version!, null);
        }
        catch (JsonException ex)
        {
            return PluginManifestValidationResult.Invalid($"plugin.json is invalid JSON: {ex.Message}");
        }
    }

    private void EnsureLoaded()
    {
        if (_plugins is not null && _tools is not null)
            return;

        lock (_gate)
        {
            if (_plugins is not null && _tools is not null)
                return;

            var plugins = LoadPlugins();
            _plugins = plugins;
            _tools = plugins
                .Where(p => p.Status == PluginLoadStatus.ManifestOnly)
                .SelectMany(p => p.Tools.Select(d => new ManifestOnlyPluginTool(p.PluginId, d)))
                .Cast<IPuddingTool>()
                .ToList();
        }
    }

    private IReadOnlyList<PluginCatalogEntry> LoadPlugins()
    {
        var root = Path.GetFullPath(_paths.PluginsRoot);
        if (!Directory.Exists(root))
            return [];

        var entries = new List<PluginCatalogEntry>();
        foreach (var pluginDir in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                var invalid = Invalid(Path.GetFileName(pluginDir), manifestPath, "plugin.json not found.");
                RecordManifestEvent("plugin.manifest_invalid", invalid);
                entries.Add(invalid);
                continue;
            }

            entries.Add(LoadManifest(pluginDir, manifestPath));
        }

        return entries;
    }

    private PluginCatalogEntry LoadManifest(string pluginDir, string manifestPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifestDto>(
                File.ReadAllText(manifestPath),
                s_jsonOptions);
            if (manifest is null)
            {
                var invalid = Invalid(Path.GetFileName(pluginDir), manifestPath, "plugin.json is empty.");
                RecordManifestEvent("plugin.manifest_invalid", invalid);
                return invalid;
            }

            var validationError = ValidateManifest(pluginDir, manifest);
            if (validationError is not null)
            {
                var invalid = Invalid(manifest.Id ?? Path.GetFileName(pluginDir), manifestPath, validationError);
                RecordManifestEvent("plugin.manifest_invalid", invalid);
                return invalid;
            }

            var descriptors = manifest.Tools
                .Select(tool => BuildDescriptor(manifest, tool))
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.ToolId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entry = new PluginCatalogEntry
            {
                PluginId = manifest.Id!,
                Name = manifest.Name!,
                Version = manifest.Version!,
                Status = PluginLoadStatus.ManifestOnly,
                StatusReason = "Manifest parsed; DLL loading is not enabled in Phase 1.",
                ManifestPath = manifestPath,
                Tools = descriptors,
            };
            RecordManifestEvent("plugin.manifest_only", entry);
            return entry;
        }
        catch (JsonException ex)
        {
            var invalid = Invalid(Path.GetFileName(pluginDir), manifestPath, $"plugin.json is invalid JSON: {ex.Message}");
            RecordManifestEvent("plugin.manifest_invalid", invalid);
            return invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var invalid = Invalid(Path.GetFileName(pluginDir), manifestPath, $"plugin.json cannot be read: {ex.Message}");
            RecordManifestEvent("plugin.manifest_invalid", invalid);
            return invalid;
        }
    }

    private void RecordManifestEvent(string eventType, PluginCatalogEntry entry)
        => _diagnostics?.Record(new PluginDiagnosticEvent
        {
            EventType = eventType,
            PluginId = entry.PluginId,
            PluginVersion = string.IsNullOrWhiteSpace(entry.Version) ? null : entry.Version,
            Status = entry.Status.ToString(),
            Message = entry.StatusReason,
            Details = new Dictionary<string, string>
            {
                ["manifest_path"] = entry.ManifestPath,
                ["tool_count"] = entry.Tools.Count.ToString(),
            },
        });

    private static string? ValidateManifest(string pluginDir, PluginManifestDto manifest)
    {
        if (!string.Equals(manifest.Schema, "pudding-plugin/v1", StringComparison.OrdinalIgnoreCase))
            return "schema must be pudding-plugin/v1.";
        if (string.IsNullOrWhiteSpace(manifest.Id) || !s_pluginIdRegex.IsMatch(manifest.Id))
            return $"plugin id '{manifest.Id}' is invalid.";
        if (string.IsNullOrWhiteSpace(manifest.Name))
            return "name is required.";
        if (string.IsNullOrWhiteSpace(manifest.Version))
            return "version is required.";
        if (string.IsNullOrWhiteSpace(manifest.Entry?.Assembly))
            return "entry.assembly is required.";
        if (!IsRelativePathInsidePlugin(pluginDir, manifest.Entry.Assembly))
            return $"entry.assembly '{manifest.Entry.Assembly}' must stay inside the plugin root.";
        if (manifest.Tools.Count == 0)
            return "at least one tool is required.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Id) || !s_toolIdRegex.IsMatch(tool.Id))
                return $"tool id '{tool.Id}' is invalid. Tool ids must use letters, numbers, and underscores only.";
            if (!seen.Add(tool.Id))
                return $"duplicate tool id '{tool.Id}'.";
            if (string.IsNullOrWhiteSpace(tool.Name))
                return $"tool '{tool.Id}' name is required.";
            if (string.IsNullOrWhiteSpace(tool.Description))
                return $"tool '{tool.Id}' description is required.";
        }

        return null;
    }

    private static bool IsRelativePathInsidePlugin(string pluginDir, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return false;

        var root = Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static PluginCatalogEntry Invalid(string? pluginId, string manifestPath, string reason) => new()
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId,
        Name = string.IsNullOrWhiteSpace(pluginId) ? "Unknown plugin" : pluginId,
        Version = "",
        Status = PluginLoadStatus.ManifestInvalid,
        StatusReason = reason,
        ManifestPath = manifestPath,
        Tools = [],
    };

    private static ToolDescriptor BuildDescriptor(PluginManifestDto manifest, PluginToolDto tool) => new()
    {
        ToolId = tool.Id!,
        Name = tool.Name!,
        Description = tool.Description!,
        Category = ParseEnum(tool.Category, ToolCategory.General),
        PermissionLevel = ParseEnum(tool.PermissionLevel, ToolPermissionLevel.Medium),
        Safety = ParseSafety(tool.Safety),
        Parameters = new ToolParameterSchema(
            tool.Parameters?.Properties
                .Select(p => new ToolParameter(p.Name ?? "", p.Type ?? "string", p.Description ?? p.Name ?? ""))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList() ?? [],
            tool.Parameters?.Required
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToList() ?? []),
        IsEnabledByDefault = tool.EnabledByDefault ?? false,
        SortOrder = tool.SortOrder ?? 300,
        SourceKind = "Plugin",
        SourceId = manifest.Id,
        RuntimeStatus = "ManifestOnly",
    };

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static ToolSafetyFlags ParseSafety(IReadOnlyList<string>? values)
    {
        var result = ToolSafetyFlags.None;
        foreach (var value in values ?? [])
        {
            if (Enum.TryParse<ToolSafetyFlags>(value, ignoreCase: true, out var parsed))
                result |= parsed;
        }

        return result;
    }

    private sealed record PluginManifestDto
    {
        public string? Schema { get; init; }
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Version { get; init; }
        public PluginEntryDto? Entry { get; init; }
        public IReadOnlyList<PluginToolDto> Tools { get; init; } = [];
    }

    private sealed record PluginEntryDto
    {
        public string? Assembly { get; init; }
        public string? Type { get; init; }
    }

    private sealed record PluginToolDto
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Category { get; init; }
        public string? PermissionLevel { get; init; }
        public IReadOnlyList<string>? Safety { get; init; }
        public bool? EnabledByDefault { get; init; }
        public int? SortOrder { get; init; }
        public PluginToolParametersDto? Parameters { get; init; }
    }

    private sealed record PluginToolParametersDto
    {
        public IReadOnlyList<PluginToolParameterDto> Properties { get; init; } = [];
        public IReadOnlyList<string> Required { get; init; } = [];
    }

    private sealed record PluginToolParameterDto
    {
        public string? Name { get; init; }
        public string? Type { get; init; }
        public string? Description { get; init; }
    }
}

/// <summary>
/// Placeholder executable for Phase 1 manifest-only tools. Returning a clear failure keeps a
/// mistakenly-invoked manifest tool diagnosable while preserving the unified Tool execution path.
/// </summary>
public sealed class ManifestOnlyPluginTool : IPuddingTool
{
    private readonly string _pluginId;

    public ManifestOnlyPluginTool(string pluginId, ToolDescriptor descriptor)
    {
        _pluginId = pluginId;
        Descriptor = descriptor;
    }

    public ToolDescriptor Descriptor { get; }

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        => Task.FromResult(ToolExecutionResult.Fail(
            $"Plugin tool '{Descriptor.ToolId}' from plugin '{_pluginId}' is manifest-only. DLL execution is not enabled in Phase 1."));
}
