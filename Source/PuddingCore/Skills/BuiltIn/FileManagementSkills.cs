using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingCode.Skills.BuiltIn;

/// <summary>
/// File management skills for the desktop spirit — smart directory probing,
/// safe deletion (eco-recycle), atomic rename, and shortcut creation.
/// All delete operations are safe: files go to a staging area, never physically deleted.
/// </summary>
public sealed class FileManagementSkills
{
    /// <summary>Default staging folder for eco-recycled files (30-day retention).</summary>
    private static readonly string DarkMatterPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PuddingCode", "DarkMatter");

    // ──── Smart Probe ────

    /// <summary>Analyzes a directory to identify its "character" — project type, key stats, and summary.</summary>
    [PuddingSkill("Analyze a directory to identify what it is (code repo, photo album, documents, etc.) and return a summary with stats.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> SmartProbe(
        [SkillParam("Directory path to analyze")] string path,
        CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return Task.FromResult($"Directory not found: {path}");

        var di = new DirectoryInfo(path);
        var files = di.GetFiles("*", SearchOption.AllDirectories);
        var dirs = di.GetDirectories("*", SearchOption.AllDirectories);

        var totalSize = files.Sum(f => f.Length);
        var extGroups = files
            .GroupBy(f => f.Extension.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"## 📂 Directory Probe: {di.Name}");
        sb.AppendLine($"- **Path:** {di.FullName}");
        sb.AppendLine($"- **Files:** {files.Length} | **Folders:** {dirs.Length}");
        sb.AppendLine($"- **Total size:** {FormatSize(totalSize)}");
        sb.AppendLine($"- **Created:** {di.CreationTime:yyyy-MM-dd} | **Modified:** {di.LastWriteTime:yyyy-MM-dd}");
        sb.AppendLine();

        // Detect project personality
        var personality = DetectPersonality(di, files);
        sb.AppendLine($"### 🎭 Identity: {personality}");
        sb.AppendLine();

        // Top extensions
        sb.AppendLine("### 📊 Top file types");
        foreach (var g in extGroups)
        {
            var ext = string.IsNullOrEmpty(g.Key) ? "(no ext)" : g.Key;
            sb.AppendLine($"  - `{ext}`: {g.Count()} files ({FormatSize(g.Sum(f => f.Length))})");
        }

        // Recent files
        var recent = files.OrderByDescending(f => f.LastWriteTime).Take(5);
        sb.AppendLine();
        sb.AppendLine("### 🕐 Recently modified");
        foreach (var f in recent)
        {
            sb.AppendLine($"  - `{Path.GetRelativePath(di.FullName, f.FullName)}` ({f.LastWriteTime:MM-dd HH:mm})");
        }

        return Task.FromResult(sb.ToString());
    }

    // ──── Eco-Recycle (Safe Delete) ────

    /// <summary>Safely "deletes" files by moving them to the dark-matter staging area. Never physically deletes.</summary>
    [PuddingSkill("Safely delete files/folders by moving them to the Pudding dark-matter staging area (30-day retention). NEVER physically deletes anything.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> EcoRecycle(
        [SkillParam("File or directory path to recycle")] string path,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);

        var isFile = File.Exists(fullPath);
        var isDir = Directory.Exists(fullPath);

        if (!isFile && !isDir)
            return Task.FromResult($"Not found: {path}");

        // Safety: refuse to recycle critical system paths
        if (IsCriticalPath(fullPath))
            return Task.FromResult($"🚫 Refused: cannot recycle system-critical path '{path}'.");

        // Build staging destination: DarkMatter/YYYY-MM-DD/original_name
        var dateBucket = DateTime.Now.ToString("yyyy-MM-dd");
        var itemName = Path.GetFileName(fullPath);
        var destDir = Path.Combine(DarkMatterPath, dateBucket);
        Directory.CreateDirectory(destDir);

        // Avoid name collision
        var dest = Path.Combine(destDir, itemName);
        if (Path.Exists(dest))
        {
            var stamp = DateTime.Now.ToString("HHmmss");
            var nameNoExt = Path.GetFileNameWithoutExtension(itemName);
            var ext = Path.GetExtension(itemName);
            dest = Path.Combine(destDir, $"{nameNoExt}_{stamp}{ext}");
        }

        if (isFile)
        {
            File.Move(fullPath, dest);
            return Task.FromResult($"♻️ Recycled file → `{Path.GetRelativePath(DarkMatterPath, dest)}`\nRetrieve from: {dest}");
        }

        Directory.Move(fullPath, dest);
        return Task.FromResult($"♻️ Recycled folder → `{Path.GetRelativePath(DarkMatterPath, dest)}`\nRetrieve from: {dest}");
    }

    /// <summary>Lists items currently in the dark-matter staging area.</summary>
    [PuddingSkill("List items in the dark-matter recycle staging area, with dates and sizes.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> ListRecycleBin(CancellationToken ct)
    {
        if (!Directory.Exists(DarkMatterPath))
            return Task.FromResult("🗑️ Dark-matter space is empty.");

        var buckets = Directory.GetDirectories(DarkMatterPath)
            .OrderByDescending(d => d)
            .ToList();

        if (buckets.Count == 0)
            return Task.FromResult("🗑️ Dark-matter space is empty.");

        var sb = new StringBuilder();
        sb.AppendLine("## 🌑 Dark-Matter Space (recycled items)");

        foreach (var bucket in buckets.Take(7))
        {
            var bucketName = Path.GetFileName(bucket);
            var items = Directory.GetFileSystemEntries(bucket);
            sb.AppendLine($"### 📅 {bucketName} ({items.Length} items)");
            foreach (var item in items.Take(10))
            {
                var name = Path.GetFileName(item);
                var isDir = Directory.Exists(item);
                sb.AppendLine($"  - {(isDir ? "📁" : "📄")} `{name}`");
            }
            if (items.Length > 10)
                sb.AppendLine($"  - ... and {items.Length - 10} more");
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>Restores a previously recycled item from dark-matter back to its original location or a specified path.</summary>
    [PuddingSkill("Restore a recycled item from dark-matter staging area to a target path.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> RestoreFromRecycle(
        [SkillParam("Name of the recycled item (e.g. 'my_file.txt' or date bucket like '2025-01-15/my_file.txt')")] string itemName,
        [SkillParam("Destination path to restore to")] string destinationPath,
        CancellationToken ct)
    {
        // Try to find the item in dark matter
        var candidate = Path.Combine(DarkMatterPath, itemName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            // Search across all date buckets
            var found = Directory.Exists(DarkMatterPath)
                ? Directory.GetFileSystemEntries(DarkMatterPath, itemName, SearchOption.AllDirectories).FirstOrDefault()
                : null;

            if (found is null)
                return Task.FromResult($"Item '{itemName}' not found in dark-matter space.");

            candidate = found;
        }

        var destFull = Path.GetFullPath(destinationPath);
        var destDir = Path.GetDirectoryName(destFull);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(candidate))
            File.Move(candidate, destFull, overwrite: false);
        else
            Directory.Move(candidate, destFull);

        return Task.FromResult($"✅ Restored → `{destFull}`");
    }

    // ──── Atomic Rename ────

    /// <summary>Renames a single file or directory.</summary>
    [PuddingSkill("Rename a file or directory. Atomic — either succeeds completely or fails without partial changes.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> Rename(
        [SkillParam("Current file/directory path")] string sourcePath,
        [SkillParam("New name (just the name, not the full path)")] string newName,
        CancellationToken ct)
    {
        var fullSource = Path.GetFullPath(sourcePath);

        if (!File.Exists(fullSource) && !Directory.Exists(fullSource))
            return Task.FromResult($"Not found: {sourcePath}");

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Task.FromResult($"Invalid characters in new name: '{newName}'");

        var parentDir = Path.GetDirectoryName(fullSource) ?? ".";
        var dest = Path.Combine(parentDir, newName);

        if (Path.Exists(dest))
            return Task.FromResult($"Target already exists: {dest}");

        if (File.Exists(fullSource))
            File.Move(fullSource, dest);
        else
            Directory.Move(fullSource, dest);

        return Task.FromResult($"✅ Renamed: `{Path.GetFileName(fullSource)}` → `{newName}`");
    }

    /// <summary>Batch-renames files in a directory using a pattern.</summary>
    [PuddingSkill("Batch-rename files in a directory using search/replace pattern. Preview mode by default.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> BatchRename(
        [SkillParam("Directory containing files to rename")] string directoryPath,
        [SkillParam("Search pattern (regex) to match in file names")] string searchPattern,
        [SkillParam("Replacement string (supports $1, $2 regex groups)")] string replacement,
        [SkillParam("File glob filter (e.g. '*.jpg', '*.*')")] string fileFilter,
        [SkillParam("Actually execute the rename? false = preview only")] bool execute,
        CancellationToken ct)
    {
        if (!Directory.Exists(directoryPath))
            return Task.FromResult($"Directory not found: {directoryPath}");

        Regex regex;
        try
        {
            regex = new Regex(searchPattern, RegexOptions.IgnoreCase);
        }
        catch (RegexParseException ex)
        {
            return Task.FromResult($"Invalid regex pattern: {ex.Message}");
        }

        var files = Directory.GetFiles(directoryPath, fileFilter);
        var renames = new List<(string from, string to)>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var newName = regex.Replace(name, replacement);
            if (newName != name)
                renames.Add((name, newName));
        }

        if (renames.Count == 0)
            return Task.FromResult("No files matched the pattern.");

        var sb = new StringBuilder();

        if (!execute)
        {
            sb.AppendLine($"## 🔄 Batch Rename Preview ({renames.Count} files)");
            foreach (var (from, to) in renames)
                sb.AppendLine($"  `{from}` → `{to}`");
            sb.AppendLine();
            sb.AppendLine("Set `execute=true` to apply these renames.");
            return Task.FromResult(sb.ToString());
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var (from, to) in renames)
        {
            try
            {
                var fullFrom = Path.Combine(directoryPath, from);
                var fullTo = Path.Combine(directoryPath, to);
                if (!Path.Exists(fullTo))
                {
                    File.Move(fullFrom, fullTo);
                    succeeded++;
                }
                else
                {
                    sb.AppendLine($"  ⚠️ Skipped `{from}` → target `{to}` already exists");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ❌ Failed `{from}`: {ex.Message}");
                failed++;
            }
        }

        sb.Insert(0, $"## 🔄 Batch Rename Result: {succeeded} ok, {failed} skipped/failed\n");
        return Task.FromResult(sb.ToString());
    }

    // ──── Shortcut / Symlink ────

    /// <summary>Creates a desktop shortcut (or symlink) to a deep directory.</summary>
    [PuddingSkill("Create a desktop shortcut (portal) pointing to a directory or file. Makes deep paths easily accessible.",
        Group = "FileManagement", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> CreatePortal(
        [SkillParam("Target path (the deep directory or file to link to)")] string targetPath,
        [SkillParam("Shortcut display name (optional, defaults to target folder name)")] string? name,
        CancellationToken ct)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        if (!Path.Exists(fullTarget))
            return Task.FromResult($"Target not found: {targetPath}");

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var linkName = name ?? Path.GetFileName(fullTarget);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsShortcut(fullTarget, desktopPath, linkName);
        }

        // Linux/macOS: create a symbolic link
        var linkPath = Path.Combine(desktopPath, linkName);
        if (Path.Exists(linkPath))
            return Task.FromResult($"A file/link named '{linkName}' already exists on desktop.");

        try
        {
            File.CreateSymbolicLink(linkPath, fullTarget);
            return Task.FromResult($"🌀 Portal created on desktop: `{linkName}` → `{fullTarget}`");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to create symlink: {ex.Message}");
        }
    }

    // ──── Helpers ────

    private static string DetectPersonality(DirectoryInfo dir, FileInfo[] files)
    {
        var extSet = new HashSet<string>(files.Select(f => f.Extension.ToLowerInvariant()));
        var nameSet = new HashSet<string>(files.Select(f => f.Name.ToLowerInvariant()));
        var dirNames = new HashSet<string>(
            dir.GetDirectories().Select(d => d.Name.ToLowerInvariant()));

        // .NET / C# project
        if (extSet.Contains(".csproj") || extSet.Contains(".sln") || extSet.Contains(".fsproj"))
            return "🟣 .NET Project";

        // Node.js
        if (nameSet.Contains("package.json"))
            return "🟢 Node.js Project";

        // Python
        if (nameSet.Contains("requirements.txt") || nameSet.Contains("pyproject.toml") || nameSet.Contains("setup.py"))
            return "🐍 Python Project";

        // Rust
        if (nameSet.Contains("cargo.toml"))
            return "🦀 Rust Project";

        // Go
        if (nameSet.Contains("go.mod"))
            return "🐹 Go Project";

        // Java / Gradle / Maven
        if (nameSet.Contains("pom.xml") || nameSet.Contains("build.gradle") || nameSet.Contains("build.gradle.kts"))
            return "☕ Java/Kotlin Project";

        // C/C++
        if (extSet.Contains(".cpp") || extSet.Contains(".c") || nameSet.Contains("cmakelists.txt") || nameSet.Contains("makefile"))
            return "⚙️ C/C++ Project";

        // Git repo
        if (dirNames.Contains(".git"))
            return "📦 Git Repository";

        // Photo album
        var imageExts = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".raw", ".cr2", ".nef" };
        var imageCount = files.Count(f => imageExts.Contains(f.Extension.ToLowerInvariant()));
        if (imageCount > files.Length * 0.5 && imageCount > 10)
            return $"📸 Photo Album ({imageCount} images)";

        // Video collection
        var videoExts = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv" };
        var videoCount = files.Count(f => videoExts.Contains(f.Extension.ToLowerInvariant()));
        if (videoCount > files.Length * 0.3 && videoCount > 3)
            return $"🎬 Video Collection ({videoCount} videos)";

        // Music
        var musicExts = new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma" };
        var musicCount = files.Count(f => musicExts.Contains(f.Extension.ToLowerInvariant()));
        if (musicCount > files.Length * 0.3 && musicCount > 5)
            return $"🎵 Music Library ({musicCount} tracks)";

        // Documents
        var docExts = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md" };
        var docCount = files.Count(f => docExts.Contains(f.Extension.ToLowerInvariant()));
        if (docCount > files.Length * 0.3 && docCount > 3)
            return $"📚 Document Collection ({docCount} docs)";

        // Downloads
        if (dir.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
            dir.Name.Equals("下载", StringComparison.OrdinalIgnoreCase))
            return "📥 Downloads Folder";

        return $"📁 General Folder ({files.Length} files)";
    }

    private static Task<string> CreateWindowsShortcut(string targetPath, string desktopPath, string linkName)
    {
        // Use PowerShell COM object to create .lnk shortcut
        var lnkPath = Path.Combine(desktopPath, linkName + ".lnk");
        if (File.Exists(lnkPath))
            return Task.FromResult($"Shortcut '{linkName}.lnk' already exists on desktop.");

        try
        {
            var ps = $"""
                $ws = New-Object -ComObject WScript.Shell
                $s = $ws.CreateShortcut('{lnkPath.Replace("'", "''")}')
                $s.TargetPath = '{targetPath.Replace("'", "''")}'
                $s.Save()
                """;

            var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -Command \"{ps}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);

            return File.Exists(lnkPath)
                ? Task.FromResult($"🌀 Portal created on desktop: `{linkName}.lnk` → `{targetPath}`")
                : Task.FromResult("Failed to create shortcut.");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to create shortcut: {ex.Message}");
        }
    }

    private static bool IsCriticalPath(string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var critical = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"
        };

        return critical
            .Where(c => !string.IsNullOrEmpty(c))
            .Any(c => normalized.Equals(Path.GetFullPath(c).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
