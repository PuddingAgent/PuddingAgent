using System.Security.Cryptography;
using System.Text.Json;

namespace PuddingDesktop.Guardian;

public static class SafeFiles
{
    public static bool Overlaps(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase)
        || Within(first, second) || Within(second, first);
    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed in deployment paths.");
    }

    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || !Within(Path.Combine(root, relative), root))
            throw new IOException("Artifact path escapes its root.");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        NoLinks(result);
        return result;
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".tmp";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, GuardianJson.Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(staging, path, overwrite: true);
    }
}

public sealed class GuardianStore(GuardianSettings settings)
{
    public string StatePath => Path.Combine(settings.StateDirectory, "state.json");
    public GuardianState Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<GuardianState>(File.ReadAllText(StatePath), GuardianJson.Options)
          ?? throw new InvalidDataException("Guardian state is empty.")
        : new();
    public void Save(GuardianState state) => SafeFiles.AtomicJson(StatePath, state);

    public ArtifactSnapshot Inspect(string directory, string target)
    {
        SafeFiles.NoLinks(directory);
        var files = new List<ArtifactFile>();
        Visit(directory, "");
        if (!files.Any(x => x.Path.Equals(settings.EntryFile(target), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Artifact entry executable is missing.");
        return new(Path.GetFullPath(directory), target, files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray());

        void Visit(string root, string relative)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                SafeFiles.NoLinks(entry);
                var child = Path.Combine(relative, Path.GetFileName(entry));
                // The independent supervisor is never replaced or rolled back with its subjects.
                if (target == "desktop" && child.Equals("guardian", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Visit(entry, child);
                else files.Add(new(child, SafeFiles.Hash(entry)));
            }
        }
    }

    public ArtifactSnapshot Capture(string target)
    {
        var original = Inspect(settings.TargetDirectory(target), target);
        var root = Path.Combine(settings.StateDirectory, "releases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var copy = original with { Directory = root };
        Copy(original, root);
        SafeFiles.AtomicJson(root + ".manifest.json", copy);
        return copy;
    }

    public void Verify(ArtifactSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
            if (!SafeFiles.Hash(SafeFiles.Resolve(snapshot.Directory, file.Path)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact hash mismatch: " + file.Path);
    }

    public void Apply(ArtifactSnapshot snapshot) => Copy(snapshot, settings.TargetDirectory(snapshot.Target));

    public void Prune(GuardianState state)
    {
        if (state.Pending is not null) return;
        var root = Path.GetFullPath(Path.Combine(settings.StateDirectory, "releases"));
        if (!Directory.Exists(root)) return;
        var unused = Directory.EnumerateDirectories(root)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)
                && !string.Equals(path, state.Fallback?.Directory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetCreationTimeUtc).Skip(2).ToArray();
        foreach (var path in unused)
        {
            // Only this store's GUID-named snapshots; never a product, DataRoot or an active transaction.
            if (!SafeFiles.Within(Path.GetFullPath(path), root)) throw new IOException("Snapshot escapes release storage.");
            SafeFiles.NoLinks(path);
            Directory.Delete(path, recursive: true);
            File.Delete(path + ".manifest.json");
        }
    }

    public void Deploy(PendingDeployment pending)
    {
        Apply(pending.Candidate);
        DeleteRemoved(pending.Backup, pending.Candidate);
    }

    public void Restore(PendingDeployment pending)
    {
        Verify(pending.Backup); // Validate the whole rollback set before modifying any live file.
        Apply(pending.Backup);
        DeleteRemoved(pending.Candidate, pending.Backup);
    }

    private void DeleteRemoved(ArtifactSnapshot previous, ArtifactSnapshot current)
    {
        var retained = current.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in previous.Files.Where(x => !retained.Contains(x.Path)))
        {
            var path = SafeFiles.Resolve(settings.TargetDirectory(current.Target), added.Path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void Copy(ArtifactSnapshot snapshot, string destination)
    {
        Verify(snapshot);
        foreach (var file in snapshot.Files)
        {
            var to = SafeFiles.Resolve(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(SafeFiles.Resolve(snapshot.Directory, file.Path), to, overwrite: true);
            if (!SafeFiles.Hash(to).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Copied artifact hash mismatch: " + file.Path);
        }
    }
}
