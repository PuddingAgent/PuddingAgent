using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HarnessAgent.Core.Tools;

/// <summary>
/// GitHub CLI (gh) wrapper — issue/PR management via subprocess.
/// </summary>
public sealed class GitHubCli
{
    private readonly string _ghPath;
    private readonly string? _repo;

    public GitHubCli(string? repo = null, string? ghPath = null)
    {
        _repo = repo;
        _ghPath = ghPath ?? "gh";
    }

    // ── Issues ──

    /// <summary>List issues in the current repo.</summary>
    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(
        string state = "open", int limit = 20, string? label = null,
        string? assignee = null, CancellationToken ct = default)
    {
        var args = $"issue list --state {state} --limit {limit} --json number,title,state,labels,assignees,url,createdAt";
        if (label != null) args += $" --label {label}";
        if (assignee != null) args += $" --assignee {assignee}";

        var json = await RunAsync(args, ct);
        return JsonSerializer.Deserialize<List<GitHubIssue>>(json)
            ?? new List<GitHubIssue>();
    }

    /// <summary>Create a new issue.</summary>
    public async Task<GitHubIssue> CreateIssueAsync(
        string title, string? body = null, string? label = null,
        string? assignee = null, CancellationToken ct = default)
    {
        var args = $"issue create --title \"{Escape(title)}\"";
        if (body != null) args += $" --body \"{Escape(body)}\"";
        if (label != null) args += $" --label {label}";
        if (assignee != null) args += $" --assignee {assignee}";

        var output = await RunAsync(args, ct);
        var url = output.Trim();
        var number = int.Parse(url.Split('/').Last());

        return new GitHubIssue
        {
            Number = number,
            Title = title,
            State = "open",
            Url = url,
        };
    }

    /// <summary>View issue details.</summary>
    public async Task<GitHubIssue> ViewIssueAsync(int number, CancellationToken ct = default)
    {
        var json = await RunAsync(
            $"issue view {number} --json number,title,state,body,labels,assignees,url,createdAt", ct);
        return JsonSerializer.Deserialize<GitHubIssue>(json) ?? new();
    }

    /// <summary>Close an issue.</summary>
    public async Task CloseIssueAsync(int number, string? reason = null, CancellationToken ct = default)
    {
        var args = $"issue close {number}";
        if (reason != null) args += $" --reason {reason}";
        await RunAsync(args, ct);
    }

    // ── Pull Requests ──

    /// <summary>List pull requests.</summary>
    public async Task<IReadOnlyList<GitHubPr>> ListPrsAsync(
        string state = "open", int limit = 20, CancellationToken ct = default)
    {
        var json = await RunAsync(
            $"pr list --state {state} --limit {limit} --json number,title,state,author,url,headRefName,baseRefName,createdAt",
            ct);
        return JsonSerializer.Deserialize<List<GitHubPr>>(json)
            ?? new List<GitHubPr>();
    }

    /// <summary>Create a pull request.</summary>
    public async Task<GitHubPr> CreatePrAsync(
        string title, string? body = null, string? baseBranch = null,
        string? headBranch = null, bool draft = false, CancellationToken ct = default)
    {
        var args = $"pr create --title \"{Escape(title)}\"";
        if (body != null) args += $" --body \"{Escape(body)}\"";
        if (baseBranch != null) args += $" --base {baseBranch}";
        if (headBranch != null) args += $" --head {headBranch}";
        if (draft) args += " --draft";

        var url = (await RunAsync(args, ct)).Trim();
        var number = int.Parse(url.Split('/').Last());

        return new GitHubPr { Number = number, Title = title, State = "open", Url = url };
    }

    /// <summary>View PR details.</summary>
    public async Task<GitHubPr> ViewPrAsync(int number, CancellationToken ct = default)
    {
        var json = await RunAsync(
            $"pr view {number} --json number,title,state,body,author,url,headRefName,baseRefName,createdAt,mergedAt,closedAt",
            ct);
        return JsonSerializer.Deserialize<GitHubPr>(json) ?? new();
    }

    /// <summary>Merge a PR.</summary>
    public async Task MergePrAsync(int number, string? method = null, CancellationToken ct = default)
    {
        var args = $"pr merge {number}";
        if (method != null) args += $" --{method}"; // merge, squash, rebase
        await RunAsync(args, ct);
    }

    /// <summary>Close a PR.</summary>
    public async Task ClosePrAsync(int number, CancellationToken ct = default)
        => await RunAsync($"pr close {number}", ct);

    // ── Repo Info ──

    /// <summary>Get current repo info.</summary>
    public async Task<string> GetCurrentRepoAsync(CancellationToken ct = default)
        => (await RunAsync("repo view --json nameWithOwner", ct)).Trim();

    /// <summary>List branches.</summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct = default)
    {
        var output = await RunAsync("branch list --format json", ct);
        var branches = JsonSerializer.Deserialize<List<BranchInfo>>(output);
        return branches?.Select(b => b.Name).ToList() ?? new List<string>();
    }

    // ── Internal ──

    private async Task<string> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ghPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start gh: {args}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"gh failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return stdout;
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"").Replace("\n", "\\n");

    private sealed record BranchInfo { public string Name { get; init; } = ""; }
}

/// <summary>GitHub issue model (subset of gh JSON output).</summary>
public sealed record GitHubIssue
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string? Body { get; init; }
    public string Url { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public IReadOnlyList<GitHubLabel> Labels { get; init; } = new List<GitHubLabel>();
    public IReadOnlyList<GitHubUser> Assignees { get; init; } = new List<GitHubUser>();
}

/// <summary>GitHub PR model.</summary>
public sealed record GitHubPr
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string? Body { get; init; }
    public string Url { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string? MergedAt { get; init; }
    public string? ClosedAt { get; init; }
    public GitHubUser? Author { get; init; }
    public string HeadRefName { get; init; } = "";
    public string BaseRefName { get; init; } = "";
}

public sealed record GitHubLabel { public string Name { get; init; } = ""; }
public sealed record GitHubUser { public string Login { get; init; } = ""; }
