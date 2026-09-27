using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Configuration;

namespace PuddingDesktop.Configuration;

public sealed record SystemConfigurationLoadResult
{
    public bool Success { get; init; }
    public PuddingSystemConfig? Config { get; init; }
    public List<string> Errors { get; init; } = [];
    public string? FilePath { get; init; }

    public static SystemConfigurationLoadResult Ok(PuddingSystemConfig config, string filePath) =>
        new() { Success = true, Config = config, FilePath = filePath };

    public static SystemConfigurationLoadResult Fail(List<string> errors) =>
        new() { Success = false, Errors = errors };

    public static SystemConfigurationLoadResult Fail(string error) =>
        new() { Success = false, Errors = [error] };
}

public interface ISystemConfigurationService
{
    Task<SystemConfigurationLoadResult> LoadAsync(string dataRoot, CancellationToken cancellationToken);
    Task UpdateDesktopCoreSettingsAsync(
        string dataRoot,
        Func<PuddingDesktopCoreConfig, PuddingDesktopCoreConfig> update,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes &lt;DataRoot&gt;/config/system.json.
/// Desktop-owned settings use PATCH semantics while unknown JSON fields are retained.
/// </summary>
public sealed class SystemConfigurationService : ISystemConfigurationService
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> KnownCoreProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "autoStart",
        "autoRestart",
        "restartMaxAttempts",
        "restartWindowSeconds",
        "restartInitialDelaySeconds",
        "restartMaxDelaySeconds",
        "port",
        "startupTimeoutSeconds",
        "shutdownTimeoutSeconds",
        "controlToken",
    };

    public async Task<SystemConfigurationLoadResult> LoadAsync(
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(dataRoot, "config", "system.json");

        if (!File.Exists(configPath))
            return SystemConfigurationLoadResult.Ok(new PuddingSystemConfig(), configPath);

        try
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            var config = JsonSerializer.Deserialize<PuddingSystemConfig>(json, ReadOptions)
                ?? new PuddingSystemConfig();
            return SystemConfigurationLoadResult.Ok(config, configPath);
        }
        catch (Exception ex)
        {
            return SystemConfigurationLoadResult.Fail($"Failed to load system.json: {ex.Message}");
        }
    }

    public async Task UpdateDesktopCoreSettingsAsync(
        string dataRoot,
        Func<PuddingDesktopCoreConfig, PuddingDesktopCoreConfig> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var configPath = Path.Combine(dataRoot, "config", "system.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var existingJson = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : "{}";

        var root = JsonNode.Parse(
            existingJson,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? new JsonObject();

        var desktop = GetObjectCaseInsensitive(root, "desktop") ?? new JsonObject();
        var existingCoreNode = GetObjectCaseInsensitive(desktop, "core") ?? new JsonObject();
        var existingCore = existingCoreNode.Deserialize<PuddingDesktopCoreConfig>(ReadOptions)
            ?? new PuddingDesktopCoreConfig();
        var updatedCore = update(existingCore)
            ?? throw new InvalidOperationException("Desktop core configuration update returned null.");

        var mergedCore = new JsonObject();
        foreach (var property in existingCoreNode)
        {
            if (!KnownCoreProperties.Contains(property.Key))
                mergedCore[property.Key] = property.Value?.DeepClone();
        }

        var serializedCore = JsonSerializer.SerializeToNode(updatedCore, WriteOptions)!.AsObject();
        foreach (var property in serializedCore)
            mergedCore[property.Key] = property.Value?.DeepClone();

        SetCaseInsensitive(desktop, "core", mergedCore);
        SetCaseInsensitive(root, "desktop", desktop);

        var tempPath = configPath + ".tmp";
        var backupPath = configPath + ".bak";

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, root, WriteOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(configPath))
                File.Replace(tempPath, configPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, configPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static JsonObject? GetObjectCaseInsensitive(JsonObject parent, string propertyName)
    {
        var match = parent.FirstOrDefault(property =>
            string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase));
        return match.Value as JsonObject;
    }

    private static void SetCaseInsensitive(JsonObject parent, string propertyName, JsonNode value)
    {
        var existingName = parent
            .Select(property => property.Key)
            .FirstOrDefault(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase));

        if (existingName is not null && !string.Equals(existingName, propertyName, StringComparison.Ordinal))
            parent.Remove(existingName);

        parent[propertyName] = value;
    }
}
