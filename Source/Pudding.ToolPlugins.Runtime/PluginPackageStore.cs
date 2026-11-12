using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Runtime;

public sealed record PluginCatalogItem(string Id, string Version, string ContentHash, string Status,
    PluginManifest? Manifest, PluginFailure? Failure);
public sealed record PluginCatalogSnapshot(IReadOnlyList<PluginCatalogItem> Packages, bool Complete);

/// <summary>Metadata and immutable content only. No assembly reflection or plugin activation.</summary>
public sealed class PluginPackageStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private readonly SemaphoreSlim _install = new(1, 1);
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    public const long MaxPackageBytes = 128L * 1024 * 1024;
    public const int MaxFiles = 512;

    public async Task<PluginPackage> ImportAsync(Stream zip, CancellationToken ct)
    {
        await _install.WaitAsync(ct);
        string? staging = null;
        try
        {
            PluginValidation.NoLinks(Root); Directory.CreateDirectory(Root);
            staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // ZipArchive requires seeking. Buffer to a bounded disk file, not unbounded RAM.
            var archivePath = Path.Combine(staging, ".upload.zip");
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                await CopyBoundedAsync(zip, output, MaxArchiveBytes, ct);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                PluginValidation.Require(archive.Entries.Count is > 0 and <= MaxFiles, "plugin.package_limit", "Package must contain 1..512 entries.");
                long total = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.TrimEnd('/'); PluginValidation.RelativePath(name);
                    PluginValidation.Require(!name.StartsWith('.') && seen.Add(name), "plugin.path_rejected", "Duplicate/reserved package path.");
                    PluginValidation.Require(((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000
                        && (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0, "plugin.path_rejected", "Package links are forbidden.");
                    if (entry.FullName.EndsWith('/')) continue;
                    total = checked(total + entry.Length);
                    PluginValidation.Require(total <= MaxPackageBytes, "plugin.package_limit", "Expanded package exceeds 128 MiB.");
                    var destination = PluginValidation.Inside(staging, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await CopyBoundedAsync(input, output, Math.Min(entry.Length, MaxPackageBytes), ct);
                }
            }
            File.Delete(archivePath);
            var manifest = ReadManifest(staging);
            ValidateFiles(staging, manifest);
            var hash = HashDirectory(staging);
            var relative = $"{manifest.Id}/{manifest.Version}/{hash}";
            var target = PluginValidation.Inside(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Verify(new(manifest, hash, target));
            else { Directory.Move(staging, target); staging = null; }
            return new(manifest, hash, target);
        }
        catch (InvalidDataException) { throw new PluginException("plugin.package_invalid", "install", "Invalid ZIP package."); }
        catch (JsonException) { throw new PluginException("plugin.manifest_invalid", "install", "Manifest is not valid v2 JSON."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new PluginException("plugin.package_io_failed", "install", "Package could not be read or published. Check file access and retry."); }
        finally
        {
            // Only a manager-created staging directory under this store can be removed.
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true); }
            finally { _install.Release(); }
        }
    }

    /// <summary>
    /// Registers a plugin from a directory the caller already owns (build output or hand-assembled package).
    /// Content is <b>copied</b>, never moved, and the same limits as ZIP import apply. Registration executes nothing.
    /// </summary>
    public async Task<PluginPackage> RegisterDirectoryAsync(string sourceDirectory, CancellationToken ct)
    {
        await _install.WaitAsync(ct);
        string? staging = null;
        try
        {
            PluginValidation.NoLinks(Root); Directory.CreateDirectory(Root);
            var source = Path.GetFullPath(sourceDirectory);
            if (!Directory.Exists(source))
                throw new PluginException("plugin.package_invalid", "install", "Source directory does not exist.");
            PluginValidation.NoLinks(source);
            staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            long total = 0;
            foreach (var file in Files(source))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                PluginValidation.Require(!relative.StartsWith('.') || relative == "plugin.json",
                    "plugin.path_rejected", "Hidden package paths are reserved.");
                total = checked(total + new FileInfo(file).Length);
                PluginValidation.Require(total <= MaxPackageBytes, "plugin.package_limit", "Expanded package exceeds 128 MiB.");
                var destination = PluginValidation.Inside(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
            }
            var manifest = ReadManifest(staging);
            ValidateFiles(staging, manifest);
            var hash = HashDirectory(staging);
            var target = PluginValidation.Inside(Root, $"{manifest.Id}/{manifest.Version}/{hash}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Verify(new(manifest, hash, target));
            else { Directory.Move(staging, target); staging = null; }
            return new(manifest, hash, target);
        }
        catch (InvalidDataException) { throw new PluginException("plugin.package_invalid", "install", "Invalid plugin directory."); }
        catch (JsonException) { throw new PluginException("plugin.manifest_invalid", "install", "Manifest is not valid v2 JSON."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new PluginException("plugin.package_io_failed", "install", "Directory could not be read or published. Check file access and retry."); }
        finally
        {
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true); }
            finally { _install.Release(); }
        }
    }

    public PluginPackage Get(string id, string version, string hash)
    {
        PluginValidation.Require(hash.Length == 64 && hash.All(Uri.IsHexDigit), "plugin.package_invalid", "Exact content hash is required.");
        var directory = PluginValidation.Inside(Root, $"{id}/{version}/{hash}");
        var manifest = ReadManifest(directory);
        PluginValidation.Require(manifest.Id == id && manifest.Version == version, "plugin.package_invalid", "Package identity differs from directory.");
        return new(manifest, hash, directory);
    }

    public PluginCatalogSnapshot Scan()
    {
        var result = new List<PluginCatalogItem>();
        if (!Directory.Exists(Root)) return new(result, true);
        try
        {
            PluginValidation.NoLinks(Root);
            var visited = 0;
            foreach (var id in Directory.EnumerateDirectories(Root))
            {
                if (Path.GetFileName(id).StartsWith('.')) continue;
                PluginValidation.NoLinks(id);
                foreach (var version in Directory.EnumerateDirectories(id))
                {
                    PluginValidation.NoLinks(version);
                    foreach (var directory in Directory.EnumerateDirectories(version))
                    {
                        if (++visited > 1000) return new(result, false);
                        try
                        {
                            var package = Get(Path.GetFileName(id), Path.GetFileName(version), Path.GetFileName(directory));
                            result.Add(new(package.Manifest.Id, package.Manifest.Version, package.ContentHash, "Validated", package.Manifest, null));
                        }
                        catch (Exception ex) when (ex is PluginException or IOException or UnauthorizedAccessException or JsonException)
                        {
                            var failure = ex is PluginException p ? p.Failure : new PluginFailure("plugin.manifest_invalid", "discover", Guid.NewGuid().ToString("N"), "Cannot read package metadata.");
                            result.Add(new(Path.GetFileName(id), Path.GetFileName(version), Path.GetFileName(directory), "Invalid", null, failure));
                        }
                    }
                }
            }
            return new(result, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginException)
        {
            result.Add(new("", "", "", "Invalid", null, new("plugin.catalog_unavailable", "discover", Guid.NewGuid().ToString("N"), "Catalog scan was interrupted.")));
            return new(result, false);
        }
    }

    public void Verify(PluginPackage package)
    {
        PluginValidation.NoLinks(package.Directory);
        ValidateFiles(package.Directory, ReadManifest(package.Directory));
        PluginValidation.Require(HashDirectory(package.Directory) == package.ContentHash,
            "plugin.content_changed", "Installed package content changed; import a new package.");
    }
    private static PluginManifest ReadManifest(string directory)
    {
        var file = PluginValidation.Inside(directory, "plugin.json");
        PluginValidation.Require(File.Exists(file), "plugin.manifest_missing", "plugin.json is required at package root.");
        PluginValidation.Require(new FileInfo(file).Length <= 256 * 1024, "plugin.package_limit", "Manifest exceeds 256 KiB.");
        var bytes = File.ReadAllBytes(file);
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
        RejectDuplicateKeys(doc.RootElement);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(bytes, PluginJson.Options)
            ?? throw new PluginException("plugin.manifest_invalid", "validate", "Empty manifest.");
        PluginValidation.Manifest(manifest); return manifest;
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject())
            { PluginValidation.Require(names.Add(p.Name), "plugin.manifest_invalid", "Repeated JSON key."); RejectDuplicateKeys(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var v in element.EnumerateArray()) RejectDuplicateKeys(v);
    }
    private static void ValidateFiles(string directory, PluginManifest manifest)
    {
        PluginValidation.Require(File.Exists(PluginValidation.Inside(directory, manifest.Entry.Assembly)), "plugin.entry_invalid", "Entry DLL is missing.");
        foreach (var asset in manifest.Assets) PluginValidation.Require(File.Exists(PluginValidation.Inside(directory, asset.Path)), "plugin.asset_missing", "Declared asset is missing.");
        foreach (var file in Files(directory))
        {
            var name = Path.GetFileName(file);
            PluginValidation.Require(!name.StartsWith("Pudding", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
                "plugin.dependency_rejected", "Packages must not ship Pudding host/contract implementation DLLs; use a distinct plugin assembly name.");
        }
    }
    private static IEnumerable<string> Files(string root)
    {
        var queue = new Queue<string>(); queue.Enqueue(root); var entries = 0;
        while (queue.TryDequeue(out var dir))
        {
            PluginValidation.NoLinks(dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                PluginValidation.Require(++entries <= MaxFiles, "plugin.package_limit", "Package contains too many entries.");
                PluginValidation.NoLinks(path);
                PluginValidation.RelativePath(Path.GetRelativePath(root, path).Replace('\\', '/'));
                if (Directory.Exists(path)) queue.Enqueue(path); else yield return path;
            }
        }
    }
    private static string HashDirectory(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long total = 0;
        foreach (var file in Files(directory).OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            using var stream = File.OpenRead(file); total = checked(total + stream.Length);
            PluginValidation.Require(total <= MaxPackageBytes, "plugin.package_limit", "Package exceeds 128 MiB.");
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0")); hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            PluginValidation.Require(total <= limit, "plugin.package_limit", "Stream exceeds declared budget.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
}
