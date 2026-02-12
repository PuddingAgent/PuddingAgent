using System.Globalization;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using PuddingAssistant.Abstractions;

namespace PuddingAssistant.Core;

/// <summary>
/// Git-based snapshot service. Uses CliWrap to run git commands
/// in the project root directory.
///
/// Snapshot commits use the message prefix "[pudding]" so they
/// can be identified and listed separately from user commits.
/// </summary>
public sealed class GitSnapshotService : IGitSnapshot
{
    private const string Prefix = "[pudding]";
    private readonly string _workDir;

    public GitSnapshotService(string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        _workDir = Path.GetFullPath(projectRoot);
    }

    public bool IsGitRepo => Directory.Exists(Path.Combine(_workDir, ".git"));

    public async Task EnsureRepoAsync(CancellationToken ct = default)
    {
        if (IsGitRepo) return;

        await RunGitAsync(["init"], ct);

        // Create initial commit so reset operations have a base
        await RunGitAsync(["add", "-A"], ct);
        await RunGitAsync(["commit", "--allow-empty", "-m", $"{Prefix} init"], ct);
    }

    public async Task<string?> CreateSnapshotAsync(string label, CancellationToken ct = default)
    {
        if (!IsGitRepo) return null;

        // Stage all changes
        await RunGitAsync(["add", "-A"], ct);

        // Check if there's anything to commit
        var status = await RunGitAsync(["status", "--porcelain"], ct);
        if (string.IsNullOrWhiteSpace(status))
            return null; // nothing to commit

        var message = $"{Prefix} {label}";
        await RunGitAsync(["commit", "-m", message], ct);

        // Return short hash of the new commit
        var hash = await RunGitAsync(["rev-parse", "--short", "HEAD"], ct);
        return hash.Trim();
    }

    public async Task<int> UndoAsync(int count = 1, CancellationToken ct = default)
    {
        if (!IsGitRepo || count <= 0) return 0;

        // Find pudding snapshots to undo
        var snapshots = await ListSnapshotsAsync(count, ct);
        if (snapshots.Count == 0) return 0;

        var undoCount = Math.Min(count, snapshots.Count);

        // Soft reset: moves HEAD back but keeps changes in working tree
        await RunGitAsync(["reset", "--soft", $"HEAD~{undoCount}"], ct);

        // Unstage everything so the user sees clean working tree changes
        await RunGitAsync(["reset", "HEAD"], ct);

        return undoCount;
    }

    public async Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(
        int maxCount = 10, CancellationToken ct = default)
    {
        if (!IsGitRepo) return [];

        // Use git log with a custom format: hash|short|timestamp|message
        var format = "%H|%h|%aI|%s";
        var output = await RunGitAsync(
            ["log", $"--max-count={maxCount * 2}", $"--format={format}", $"--grep={Prefix}"],
            ct);

        if (string.IsNullOrWhiteSpace(output)) return [];

        var entries = new List<SnapshotEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 4) continue;

            var label = parts[3].StartsWith(Prefix)
                ? parts[3][Prefix.Length..].Trim()
                : parts[3];

            if (DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var ts))
            {
                entries.Add(new SnapshotEntry(parts[0], parts[1], label, ts));
            }

            if (entries.Count >= maxCount) break;
        }

        return entries;
    }

    public async Task<bool> RestoreAsync(string commitHash, CancellationToken ct = default)
    {
        if (!IsGitRepo) return false;

        try
        {
            await RunGitAsync(["reset", "--hard", commitHash], ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> RunGitAsync(string[] args, CancellationToken ct)
    {
        var result = await Cli.Wrap("git")
            .WithArguments(args)
            .WithWorkingDirectory(_workDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            var stderr = result.StandardError.Trim();
            // Don't throw for common non-error messages
            if (!stderr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
                !stderr.Contains("Already up to date", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
            }
        }

        return result.StandardOutput;
    }
}
