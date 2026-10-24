using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuddingDesktop.Core;
using PuddingDesktop.Configuration;
using PuddingDesktop.Diagnostics;

namespace PuddingDesktop.Runtime;

public sealed class DiagnosticBundleService(
    Func<DesktopRuntimeSnapshot> snapshotProvider,
    CoreProcessLogBuffer logBuffer,
    string? desktopRunsRoot = null) : IDiagnosticBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<string> CreateAsync(string dataRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            throw new InvalidOperationException("请先配置有效的数据目录。");

        var diagnosticsRoot = Path.Combine(dataRoot, "diagnostics");
        Directory.CreateDirectory(diagnosticsRoot);
        var filePath = Path.Combine(
            diagnosticsRoot,
            $"pudding-diagnostic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

        await using var fileStream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true);

        var snapshot = snapshotProvider();
        await WriteJsonAsync(archive, "runtime.json", new
        {
            capturedAt = DateTimeOffset.UtcNow,
            desktopVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            os = Environment.OSVersion.VersionString,
            framework = Environment.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            dataRoot,
            runtime = snapshot,
        }, cancellationToken);

        await WriteTextAsync(
            archive,
            "core-tail.log",
            RedactLog(logBuffer.GetTail(500)),
            cancellationToken);

        var systemConfigPath = Path.Combine(dataRoot, "config", "system.json");
        if (File.Exists(systemConfigPath))
        {
            var json = await File.ReadAllTextAsync(systemConfigPath, cancellationToken);
            var root = JsonNode.Parse(json);
            var keys = new List<string>();
            CollectKeyPaths(root, prefix: null, keys);
            await WriteTextAsync(
                archive,
                "system-config-keys.txt",
                string.Join(Environment.NewLine, keys.Order(StringComparer.OrdinalIgnoreCase)),
                cancellationToken);
        }

        var runsRoot = desktopRunsRoot ?? Path.Combine(DesktopBootstrapPathProvider.GetDirectoryPath(), "logs", "desktop-runs");
        if (Directory.Exists(runsRoot))
        {
            foreach (var run in Directory.GetDirectories(runsRoot).OrderDescending(StringComparer.Ordinal).Take(3))
            {
                if ((File.GetAttributes(run) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var name in new[] { "state.json", "lifecycle.jsonl", "lifecycle.jsonl.previous", "exit.json", "incident.json", "recovered-exit.json", "observer.json" })
                {
                    var path = Path.Combine(run, name);
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists || info.Length > 300 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var content = await File.ReadAllTextAsync(path, cancellationToken);
                        await WriteTextAsync(archive, $"desktop-runs/{Path.GetFileName(run)}/{name}",
                            DesktopRunJournal.SafeText(content, 300 * 1024), cancellationToken);
                    }
                    catch (IOException) { /* A concurrently rotated file is optional evidence. */ }
                }
            }
        }
        return filePath;
    }

    private static void CollectKeyPaths(JsonNode? node, string? prefix, List<string> keys)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var path = string.IsNullOrEmpty(prefix) ? property.Key : $"{prefix}.{property.Key}";
                keys.Add(path);
                CollectKeyPaths(property.Value, path, keys);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                CollectKeyPaths(array[index], $"{prefix}[]", keys);
        }
    }

    private static string RedactLog(string value)
    {
        var redacted = Regex.Replace(
            value,
            @"(?im)(authorization|x-pudding-desktop-token)\s*[:=]\s*[^\s,;]+",
            "$1: [REDACTED]");
        return Regex.Replace(
            redacted,
            @"(?im)(""?(?:api[_-]?key|control[_-]?token|app[_-]?secret)""?\s*[:=]\s*""?)[^""\s,;]+",
            "$1[REDACTED]");
    }

    private static async Task WriteJsonAsync(
        ZipArchive archive,
        string entryName,
        object value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string entryName,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }
}
