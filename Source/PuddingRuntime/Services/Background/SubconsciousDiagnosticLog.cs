using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;

namespace PuddingRuntime.Services.Background;

public sealed class SubconsciousDiagnosticLogOptions
{
    public long MaxFileSizeBytes { get; init; } = 1_048_576;
    public int RetainedFileCountLimit { get; init; } = 200;
}

public sealed class SubconsciousDiagnosticLog : ISubconsciousDiagnosticLog
{
    private readonly SubconsciousDiagnosticLogOptions _options;
    private readonly object _writeLock = new();

    public SubconsciousDiagnosticLog(
        PuddingDataPaths dataPaths,
        IOptions<SubconsciousDiagnosticLogOptions>? options = null)
    {
        _options = options?.Value ?? new SubconsciousDiagnosticLogOptions();
        LogDirectory = Path.Combine(dataPaths.DiagnosticsLogsRoot, "subconscious");
        Directory.CreateDirectory(LogDirectory);
    }

    public string LogDirectory { get; }

    public void Write(
        string name,
        IReadOnlyDictionary<string, object?> fields)
    {
        try
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
                ["name"] = name,
            };

            foreach (var pair in fields)
                entry[pair.Key] = pair.Value;

            var line = JsonSerializer.Serialize(entry, PuddingJsonContracts.JsonLines);

            lock (_writeLock)
            {
                var filePath = ResolveWritableFilePath();
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
                TrimOldFiles();
            }
        }
        catch
        {
            // Diagnostics must never affect runtime memory maintenance.
        }
    }

    private string ResolveWritableFilePath()
    {
        var datePrefix = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var baseFilePath = Path.Combine(LogDirectory, $"{datePrefix}.jsonl");
        if (!File.Exists(baseFilePath) || new FileInfo(baseFilePath).Length < _options.MaxFileSizeBytes)
            return baseFilePath;

        var maxSeq = 0;
        foreach (var file in Directory.GetFiles(LogDirectory, $"{datePrefix}_*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var underscoreIdx = name.LastIndexOf('_');
            if (underscoreIdx > 0 && int.TryParse(name[(underscoreIdx + 1)..], out var seq) && seq > maxSeq)
                maxSeq = seq;
        }

        return Path.Combine(LogDirectory, $"{datePrefix}_{maxSeq + 1:D3}.jsonl");
    }

    private void TrimOldFiles()
    {
        if (_options.RetainedFileCountLimit <= 0)
            return;

        var allFiles = Directory.GetFiles(LogDirectory, "*.jsonl");
        if (allFiles.Length <= _options.RetainedFileCountLimit)
            return;

        foreach (var file in allFiles
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(allFiles.Length - _options.RetainedFileCountLimit))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort retention cleanup.
            }
        }
    }
}
