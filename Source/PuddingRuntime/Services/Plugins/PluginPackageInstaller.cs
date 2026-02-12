using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services.Plugins;

public sealed record PluginPackageInstallResult(
    string PluginId,
    string Name,
    string Version,
    bool RequiresRestart,
    string Message);

public sealed class PluginPackageValidationException : Exception
{
    public PluginPackageValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Installs user-uploaded plugin ZIP packages into the data-root plugin catalog.
/// The service owns archive validation so controller/UI code cannot accidentally bypass
/// traversal checks, zip-size limits, or manifest contract checks before extraction.
/// </summary>
public sealed class PluginPackageInstaller
{
    private const int MaxEntryCount = 1024;
    private const long MaxArchiveBytes = 64L * 1024 * 1024;
    private const long MaxUncompressedBytes = 128L * 1024 * 1024;
    private const long MaxManifestBytes = 256L * 1024;

    private readonly PuddingDataPaths _paths;
    private readonly PluginDiagnosticsSink? _diagnostics;

    public PluginPackageInstaller(PuddingDataPaths paths, PluginDiagnosticsSink? diagnostics = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public async Task<PluginPackageInstallResult> InstallAsync(
        Stream packageStream,
        string fileName,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await InstallCoreAsync(packageStream, fileName, ct);
            RecordPackageEvent(
                "plugin.package_installed",
                result.PluginId,
                result.Version,
                "succeeded",
                result.Message,
                Stopwatch.GetElapsedTime(started),
                fileName);
            return result;
        }
        catch (PluginPackageValidationException ex)
        {
            RecordPackageEvent(
                "plugin.package_rejected",
                pluginId: null,
                pluginVersion: null,
                status: "failed",
                message: ex.Message,
                duration: Stopwatch.GetElapsedTime(started),
                fileName: fileName);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordPackageEvent(
                "plugin.package_install_failed",
                pluginId: null,
                pluginVersion: null,
                status: "failed",
                message: ex.Message,
                duration: Stopwatch.GetElapsedTime(started),
                fileName: fileName);
            throw;
        }
    }

    private async Task<PluginPackageInstallResult> InstallCoreAsync(
        Stream packageStream,
        string fileName,
        CancellationToken ct)
    {
        if (packageStream.CanSeek && packageStream.Length > MaxArchiveBytes)
            throw new PluginPackageValidationException($"Plugin package exceeds {MaxArchiveBytes / 1024 / 1024} MB.");
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageValidationException("Plugin package must be a .zip file.");

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var layout = ValidateArchiveLayout(archive);
        var targetRoot = Path.Combine(_paths.PluginsRoot, layout.PluginId);
        var validation = PluginManifestCatalog.ValidateManifestJson(layout.ManifestJson, targetRoot);
        if (!validation.IsValid)
            throw new PluginPackageValidationException(validation.Error ?? "plugin.json is invalid.");

        if (!layout.PluginId.Equals(validation.PluginId, StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageValidationException(
                $"Package root plugin id '{layout.PluginId}' does not match manifest id '{validation.PluginId}'.");

        var stagingRoot = Path.Combine(_paths.TempRoot, "plugin-upload", Guid.NewGuid().ToString("N"));
        var backupRoot = $"{targetRoot}.bak-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await ExtractValidatedArchiveAsync(archive, layout.RootPrefix, stagingRoot, ct);
            Directory.CreateDirectory(_paths.PluginsRoot);

            if (Directory.Exists(targetRoot))
                Directory.Move(targetRoot, backupRoot);

            try
            {
                Directory.Move(stagingRoot, targetRoot);
                if (Directory.Exists(backupRoot))
                    Directory.Delete(backupRoot, recursive: true);
            }
            catch
            {
                if (Directory.Exists(targetRoot))
                    Directory.Delete(targetRoot, recursive: true);
                if (Directory.Exists(backupRoot))
                    Directory.Move(backupRoot, targetRoot);
                throw;
            }

            return new PluginPackageInstallResult(
                PluginId: validation.PluginId!,
                Name: validation.Name!,
                Version: validation.Version!,
                RequiresRestart: false,
                Message: "Plugin package installed and manifest-only tools are now visible in the runtime catalog. DLL execution is not enabled in Phase 1.");
        }
        catch
        {
            SafeDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private void RecordPackageEvent(
        string eventType,
        string? pluginId,
        string? pluginVersion,
        string status,
        string message,
        TimeSpan duration,
        string fileName)
        => _diagnostics?.Record(new PluginDiagnosticEvent
        {
            EventType = eventType,
            PluginId = pluginId,
            PluginVersion = pluginVersion,
            Status = status,
            Message = message,
            DurationMs = (long)duration.TotalMilliseconds,
            Details = new Dictionary<string, string>
            {
                ["file_name"] = Path.GetFileName(fileName),
            },
        });

    private static PluginArchiveLayout ValidateArchiveLayout(ZipArchive archive)
    {
        if (archive.Entries.Count == 0)
            throw new PluginPackageValidationException("Plugin package is empty.");
        if (archive.Entries.Count > MaxEntryCount)
            throw new PluginPackageValidationException($"Plugin package contains more than {MaxEntryCount} entries.");

        var normalizedEntries = archive.Entries
            .Select(entry => new NormalizedZipEntry(entry, NormalizeEntryName(entry.FullName)))
            .ToList();

        long totalBytes = 0;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in normalizedEntries)
        {
            ValidateEntryPath(item.NormalizedName);
            if (IsSymlink(item.Entry))
                throw new PluginPackageValidationException($"Zip entry '{item.NormalizedName}' is a symlink and is not allowed.");
            if (!IsDirectoryEntry(item.Entry))
            {
                if (!seenFiles.Add(item.NormalizedName))
                    throw new PluginPackageValidationException($"Zip entry '{item.NormalizedName}' is duplicated.");
                checked { totalBytes += item.Entry.Length; }
                if (totalBytes > MaxUncompressedBytes)
                    throw new PluginPackageValidationException($"Plugin package expands beyond {MaxUncompressedBytes / 1024 / 1024} MB.");
            }
        }

        var manifestEntries = normalizedEntries
            .Where(item => item.NormalizedName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
                           || item.NormalizedName.Count(ch => ch == '/') == 1
                           && item.NormalizedName.EndsWith("/plugin.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (manifestEntries.Count != 1)
            throw new PluginPackageValidationException("Plugin package must contain exactly one plugin.json at root or in one top-level folder.");

        var manifestEntry = manifestEntries[0];
        var rootPrefix = manifestEntry.NormalizedName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : manifestEntry.NormalizedName[..^"plugin.json".Length];

        if (!string.IsNullOrEmpty(rootPrefix)
            && normalizedEntries.Any(item => !IsDirectoryEntry(item.Entry)
                                            && !item.NormalizedName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PluginPackageValidationException("Single-folder plugin packages cannot contain files outside the plugin root folder.");
        }

        var manifestJson = ReadManifest(manifestEntry.Entry);
        var pluginId = ExtractManifestPluginId(manifestJson);
        return new PluginArchiveLayout(rootPrefix, pluginId, manifestJson);
    }

    private static async Task ExtractValidatedArchiveAsync(
        ZipArchive archive,
        string rootPrefix,
        string stagingRoot,
        CancellationToken ct)
    {
        var stagingFullPath = Path.GetFullPath(stagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var entry in archive.Entries)
        {
            var normalizedName = NormalizeEntryName(entry.FullName);
            if (IsDirectoryEntry(entry))
                continue;
            if (!string.IsNullOrEmpty(rootPrefix)
                && !normalizedName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativeName = string.IsNullOrEmpty(rootPrefix)
                ? normalizedName
                : normalizedName[rootPrefix.Length..];
            ValidateEntryPath(relativeName);

            var targetPath = Path.GetFullPath(Path.Combine(stagingFullPath, relativeName));
            if (!targetPath.StartsWith(stagingFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new PluginPackageValidationException($"Zip entry '{normalizedName}' escapes the staging directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var input = entry.Open();
            await using var output = File.Create(targetPath);
            await input.CopyToAsync(output, ct);
        }
    }

    private static string ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxManifestBytes)
            throw new PluginPackageValidationException($"plugin.json exceeds {MaxManifestBytes / 1024} KB.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ExtractManifestPluginId(string manifestJson)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pudding-plugin-manifest-validation");
        var validation = PluginManifestCatalog.ValidateManifestJson(manifestJson, tempRoot);
        if (!validation.IsValid)
            throw new PluginPackageValidationException(validation.Error ?? "plugin.json is invalid.");
        return validation.PluginId!;
    }

    private static string NormalizeEntryName(string name)
        => name.Replace('\\', '/').Trim();

    private static void ValidateEntryPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PluginPackageValidationException("Zip entry path cannot be empty.");
        if (name.StartsWith("/", StringComparison.Ordinal)
            || name.StartsWith("\\", StringComparison.Ordinal)
            || Path.IsPathRooted(name))
        {
            throw new PluginPackageValidationException($"Zip entry '{name}' must be relative.");
        }

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new PluginPackageValidationException("Zip entry path cannot be empty.");
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
            throw new PluginPackageValidationException($"Zip entry '{name}' contains an unsafe path segment.");
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => entry.FullName.EndsWith("/", StringComparison.Ordinal)
           || entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort after a failed install; the original plugin directory is restored separately.
        }
    }

    private sealed record NormalizedZipEntry(ZipArchiveEntry Entry, string NormalizedName);
    private sealed record PluginArchiveLayout(string RootPrefix, string PluginId, string ManifestJson);
}
