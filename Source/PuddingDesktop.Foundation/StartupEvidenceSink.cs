using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingDesktop.Foundation;

/// <summary>
/// Stores frozen startup evidence. A sink must never throw: losing the artifact is a diagnostics
/// problem, but it must not become a startup failure.
/// </summary>
public interface IStartupEvidenceSink
{
    bool TryWrite(StartupEvidence evidence, out string? error);
}

/// <summary>One JSON object per attempt, appended to a single file so repeated runs stay comparable.</summary>
public sealed class StartupEvidenceFileSink : IStartupEvidenceSink
{
    // No BOM: a byte-order mark makes the first line unreadable for strict JSONL parsers
    // (measured: Python's json rejected the artifact while PowerShell tolerated it).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public StartupEvidenceFileSink(string directory, string fileName = "startup-evidence.jsonl")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Directory = Path.GetFullPath(directory);
        FilePath = Path.Combine(Directory, fileName);
    }

    public string Directory { get; }
    public string FilePath { get; }

    public bool TryWrite(StartupEvidence evidence, out string? error)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(FilePath, StartupEvidenceJson.Serialize(evidence) + Environment.NewLine, Utf8NoBom);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }
}

public static class StartupEvidenceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(StartupEvidence evidence) => JsonSerializer.Serialize(evidence, Options);
}
