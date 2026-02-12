using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services.Plugins;

public sealed record PluginDiagnosticEvent
{
    public string Schema { get; init; } = "pudding-plugin-diagnostics";
    public int Version { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string EventType { get; init; }
    public string? PluginId { get; init; }
    public string? PluginVersion { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
    public long? DurationMs { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed record PluginDiagnosticsQuery(
    string? PluginId = null,
    int Limit = 50);

/// <summary>
/// Writes plugin infrastructure evidence to diagnostics JSONL.
/// Plugin package validation and manifest discovery are filesystem-facing trust boundaries, so
/// failures must be reconstructable without asking controllers, UI code, or future DLL loaders to
/// duplicate logging policy. The sink intentionally stores short structured facts rather than raw
/// package contents or manifest bodies.
/// </summary>
public sealed class PluginDiagnosticsSink
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<PluginDiagnosticsSink>? _logger;
    private readonly object _gate = new();

    public PluginDiagnosticsSink(PuddingDataPaths paths, ILogger<PluginDiagnosticsSink>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public void Record(PluginDiagnosticEvent evt)
    {
        try
        {
            var day = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            var root = Path.Combine(_paths.DiagnosticsLogsRoot, "plugins");
            var file = Path.Combine(root, $"{day}.jsonl");
            var line = JsonSerializer.Serialize(evt, s_jsonOptions);

            lock (_gate)
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "[PluginDiagnostics] Failed to write plugin diagnostic event {EventType}", evt.EventType);
        }
    }
}

/// <summary>
/// Reads recent plugin diagnostics from the same JSONL evidence stream written by
/// <see cref="PluginDiagnosticsSink"/>. Keeping read access in Runtime preserves the boundary:
/// plugin infrastructure owns its trace files, while controllers and UI consume a shaped view.
/// </summary>
public sealed class PluginDiagnosticsReader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PuddingDataPaths _paths;

    public PluginDiagnosticsReader(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<PluginDiagnosticEvent> ListRecent(PluginDiagnosticsQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var root = Path.Combine(_paths.DiagnosticsLogsRoot, "plugins");
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*.jsonl")
            .OrderByDescending(Path.GetFileName)
            .SelectMany(ReadEvents)
            .Where(evt => string.IsNullOrWhiteSpace(query.PluginId)
                          || string.Equals(evt.PluginId, query.PluginId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(evt => evt.OccurredAtUtc)
            .Take(limit)
            .ToList();
    }

    private static IEnumerable<PluginDiagnosticEvent> ReadEvents(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            PluginDiagnosticEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<PluginDiagnosticEvent>(line, s_jsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is not null)
                yield return evt;
        }
    }
}
