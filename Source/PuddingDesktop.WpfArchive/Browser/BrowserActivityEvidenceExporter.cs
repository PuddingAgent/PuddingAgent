using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingDesktop.Browser;

// ─── Evidence DTOs ───────────────────────────────────────────────────────────

public sealed record BrowserActivityEvidenceDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset CapturedAt { get; init; }
    public required string BridgeState { get; init; }
    public required string ControlState { get; init; }
    public string? ActiveContextId { get; init; }
    public string? ActivePageId { get; init; }
    public string? AgentTargetPageId { get; init; }
    public required IReadOnlyList<BrowserActivityEvidenceItem> Activities { get; init; }
}

public sealed record BrowserActivityEvidenceItem
{
    public required Guid OperationId { get; init; }
    public required string CommandName { get; init; }
    public required string Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool? Success { get; init; }
    public string? ErrorCode { get; init; }
}

// ─── Exporter Interface ──────────────────────────────────────────────────────

public interface IBrowserActivityEvidenceExporter
{
    Task<string> ExportAsync(
        BrowserActivityEvidenceDocument document,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

// ─── Exporter Implementation ─────────────────────────────────────────────────

/// <summary>
/// Exports sanitized browser activity evidence to a JSON file.
/// Only writes command name, page identity, result, and stable error codes.
/// Never serializes tool parameters, fill/text values, DOM snapshots,
/// URLs with query strings, cookies, headers, or tokens.
/// Uses atomic write: temp file first, then move to final path.
/// </summary>
public sealed class BrowserActivityEvidenceExporter : IBrowserActivityEvidenceExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> ExportAsync(
        BrowserActivityEvidenceDocument document,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        var timestamp = document.CapturedAt.ToString("yyyyMMddTHHmmssZ");
        var fileName = $"browser-activity-{timestamp}.sanitized.json";
        var finalPath = Path.Combine(destinationDirectory, fileName);
        var tempPath = finalPath + ".tmp";

        // Sort activities by time for audit traceability
        var ordered = document with
        {
            Activities = document.Activities
                .OrderBy(a => a.StartedAt)
                .ThenBy(a => a.CommandName, StringComparer.Ordinal)
                .ToList()
        };

        // Write to temp file first
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, ordered, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        // Atomic rename
        File.Move(tempPath, finalPath, overwrite: true);

        return finalPath;
    }
}
