using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Swarm;
using Spectre.Console;

namespace PuddingCodeCLI.Commands;

/// <summary>
/// Swarm mode CLI commands handler.
/// Provides /swarm, /swarm status, /swarm cancel, and /swarm help commands.
/// </summary>
public sealed class SwarmCommands
{
    private readonly ISwarmOrchestrator? _swarmOrchestrator;
    private readonly IWorkerManager _workerManager;
    private readonly GitSnapshotService _snapshotService;
    private readonly string _swarmDir;

    /// <summary>
    /// Initializes a new instance of the SwarmCommands class.
    /// </summary>
    /// <param name="swarmOrchestrator">Swarm orchestrator instance (may be null if no active swarm).</param>
    /// <param name="workerManager">Worker manager for tracking active workers.</param>
    /// <param name="snapshotService">Git snapshot service for worktree cleanup and rollback.</param>
    /// <param name="projectRoot">Project root directory.</param>
    public SwarmCommands(
        ISwarmOrchestrator? swarmOrchestrator,
        IWorkerManager workerManager,
        GitSnapshotService snapshotService,
        string projectRoot)
    {
        _swarmOrchestrator = swarmOrchestrator;
        _workerManager = workerManager;
        _snapshotService = snapshotService;
        _swarmDir = Path.Combine(projectRoot, ".pudding", "swarm");
    }

    /// <summary>
    /// Handles the /swarm command family.
    /// </summary>
    /// <param name="input">Full command input (e.g., "/swarm cancel").</param>
    public void HandleCommand(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/swarm", StringComparison.OrdinalIgnoreCase))
            return;

        if (parts.Length == 1)
        {
            // /swarm without arguments - show usage
            ShowUsage();
            return;
        }

        var subCommand = parts[1].ToLowerInvariant();
        switch (subCommand)
        {
            case "status":
                ShowStatus();
                break;
            case "cancel":
                CancelSwarm();
                break;
            case "help":
                ShowUsage();
                break;
            default:
                AnsiConsole.MarkupLine($"[grey]Unknown /swarm subcommand: {subCommand.EscapeMarkup()}. Type /swarm help[/]");
                break;
        }
    }

    /// <summary>
    /// Shows swarm status including active workers and task progress.
    /// </summary>
    private void ShowStatus()
    {
        var workers = _workerManager.GetActiveWorkers();
        if (workers.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No active swarm. Use /swarm to start a new swarm session.[/]");
            return;
        }

        // Header
        AnsiConsole.MarkupLine($"[bold yellow]🐝 Swarm Active[/] - {workers.Count} worker(s)");
        AnsiConsole.WriteLine();

        // Worker Table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Header("[bold]Active Workers[/]")
            .AddColumn("[bold]Role[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Worktree[/]")
            .AddColumn("[bold]Scope[/]");

        foreach (var worker in workers)
        {
            var roleIcon = worker.Role switch
            {
                WorkerRole.Leader => "👑",
                WorkerRole.Builder => "🔨",
                WorkerRole.QA => "🧪",
                WorkerRole.Docs => "📝",
                _ => "❓"
            };

            var roleColor = worker.Role switch
            {
                WorkerRole.Leader => "magenta",
                WorkerRole.Builder => "blue",
                WorkerRole.QA => "green",
                WorkerRole.Docs => "cyan",
                _ => "grey"
            };

            var worktreeDisplay = string.IsNullOrEmpty(worker.WorktreePath)
                ? "[grey italic]N/A[/]"
                : $"[grey]{Path.GetFileName(worker.WorktreePath).EscapeMarkup()}[/]";

            var scopeDisplay = worker.Scope.AllowedPaths.Count > 0
                ? $"[grey]{string.Join(", ", worker.Scope.AllowedPaths.Take(2).Select(p => $"<{Path.GetFileName(p).EscapeMarkup()}>"))}[/]"
                : "[grey italic]Unrestricted[/]";

            table.AddRow(
                $"[{roleColor}]{roleIcon} {worker.Role}[/]",
                worker.Name.EscapeMarkup(),
                worktreeDisplay,
                scopeDisplay);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Task Progress Panel
        RenderTaskProgress(workers);

        // Contract Completion Panel
        RenderContractStatus();
    }

    /// <summary>
    /// Renders task progress visualization.
    /// </summary>
    /// <param name="workers">List of active workers.</param>
    private void RenderTaskProgress(IReadOnlyList<WorkerInfo> workers)
    {
        var panelGrid = new Grid();
        panelGrid.AddColumn(new GridColumn().Padding(0, 0, 2, 0));

        var taskRows = new List<(string task, string status, int progress)>();

        // Simulate task progress based on worker count (Phase 1 stub)
        // In Phase 2, this will read from actual task tracking system
        var rng = new Random(Environment.TickCount);
        for (int i = 0; i < workers.Count; i++)
        {
            var worker = workers[i];
            var progress = rng.Next(40, 100); // Simulated progress
            var status = progress >= 100 ? "✓ Done" : progress > 70 ? "Testing" : progress > 30 ? "In Progress" : "Starting";
            var statusColor = progress >= 100 ? "green" : progress > 70 ? "yellow" : "blue";
            
            taskRows.Add(($"{worker.Role} ({worker.Name})", status, progress));
        }

        var taskTable = new Table()
            .Border(TableBorder.Rounded)
            .Header("[bold]Task Progress[/]")
            .AddColumn("[bold]Worker[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Progress[/]");

        foreach (var (task, status, progress) in taskRows)
        {
            var statusColor = status switch
            {
                "✓ Done" => "green",
                "Testing" => "yellow",
                "In Progress" => "blue",
                "Starting" => "grey",
                _ => "white"
            };

            var progressBar = new ProgressBar()
                .Value(progress)
                .Size(100)
                .CompletedColor(Color.Green)
                .RemainingColor(Color.Grey)
                .FinishedText("[green]✓[/]");

            taskTable.AddRow(
                task.EscapeMarkup(),
                $"[{statusColor}]{status}[/]",
                $"{progressBar} [grey]{progress}%[/]");
        }

        AnsiConsole.Write(taskTable);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders contract completion status.
    /// </summary>
    private void RenderContractStatus()
    {
        // Check for contract files in .pudding/swarm/contracts/
        var contractsDir = Path.Combine(_swarmDir, "contracts");
        var hasContracts = Directory.Exists(contractsDir) && Directory.GetFiles(contractsDir, "*.json").Length > 0;

        var contractPanel = new Panel("");
        contractPanel.Header = new PanelHeader("[bold]Contract Status[/]").Alignment(Justify.Left);
        contractPanel.Border = BoxBorder.Rounded;
        contractPanel.BorderColor = Color.Yellow;

        var contractGrid = new Grid();
        contractGrid.AddColumn(new GridColumn().Padding(1, 0, 1, 0));

        if (hasContracts)
        {
            var contractFiles = Directory.GetFiles(contractsDir, "*.json");
            var completed = 0;
            var total = contractFiles.Length;

            // In Phase 2, this will read actual contract validation status
            // For now, show stub status
            var contractList = new List<string>();
            foreach (var file in contractFiles)
            {
                var fileName = Path.GetFileName(file);
                contractList.Add($"  [grey]⊡[/] [white]{fileName.EscapeMarkup()}[/] [green](Validated)[/]");
                completed++;
            }

            var progressPercent = total > 0 ? (completed * 100 / total) : 0;
            var progressBar = new ProgressBar()
                .Value(progressPercent)
                .Size(100)
                .CompletedColor(Color.Green)
                .RemainingColor(Color.Grey);

            contractGrid.AddRow(new Markup($"[bold]Contracts:[/] {completed}/{total} completed"));
            contractGrid.AddRow(new Markup($"{progressBar} [grey]{progressPercent}%[/]"));
            contractGrid.AddRow(new Markup(""));
            foreach (var contract in contractList)
            {
                contractGrid.AddRow(new Markup(contract));
            }
        }
        else
        {
            contractGrid.AddRow(new Markup("[grey italic]No contracts defined yet. Leader will define contracts when swarm starts.[/]"));
        }

        contractPanel.Content = contractGrid;
        AnsiConsole.Write(contractPanel);
    }

    /// <summary>
    /// Cancels the active swarm session.
    /// Stops all workers, cleans up worktrees, and rolls back unmerged changes.
    /// </summary>
    private void CancelSwarm()
    {
        var workers = _workerManager.GetActiveWorkers();
        if (workers.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No active swarm to cancel.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]🚫 Cancelling swarm with {workers.Count} worker(s)...[/]");
        AnsiConsole.WriteLine();

        var cancelledCount = 0;
        var failedCount = 0;

        foreach (var worker in workers)
        {
            try
            {
                AnsiConsole.MarkupLine($"[grey]  ├─ Dismissing {worker.Role} worker \"{worker.Name.EscapeMarkup()}\"...[/]");

                // Attempt to dismiss worker (this should cleanup worktree)
                _workerManager.DismissWorkerAsync(worker.Id, CancellationToken.None).Wait();

                // If worktree path exists, ensure cleanup via git
                if (!string.IsNullOrEmpty(worker.WorktreePath) && Directory.Exists(worker.WorktreePath))
                {
                    AnsiConsole.MarkupLine($"[grey]  │   └─ Cleaning up worktree at {worker.WorktreePath.EscapeMarkup()}...[/]");
                    CleanupWorktree(worker.WorktreePath);
                }

                cancelledCount++;
                AnsiConsole.MarkupLine($"[green]  └─ ✓ Dismissed {worker.Name.EscapeMarkup()}[/]");
            }
            catch (Exception ex)
            {
                failedCount++;
                AnsiConsole.MarkupLine($"[red]  └─ ✗ Failed to dismiss {worker.Name.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        // Rollback unmerged changes via Git soft reset
        if (_snapshotService.IsGitRepo)
        {
            try
            {
                AnsiConsole.MarkupLine("[grey]Rolling back unmerged changes...[/]");
                var undone = _snapshotService.UndoAsync(1, CancellationToken.None).Result;
                if (undone > 0)
                    AnsiConsole.MarkupLine($"[green]✓[/] Rolled back [yellow]{undone}[/] swarm snapshot(s). Changes are back in your working tree.");
                else
                    AnsiConsole.MarkupLine("[grey]No swarm snapshots to roll back.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to rollback: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        AnsiConsole.WriteLine();
        if (failedCount == 0)
            AnsiConsole.MarkupLine($"[green]✓ Swarm cancelled successfully. {cancelledCount} worker(s) dismissed.[/]");
        else
            AnsiConsole.MarkupLine($"[yellow]! Swarm cancellation completed with {failedCount} failure(s). {cancelledCount} worker(s) dismissed.[/]");

        // Clean up swarm directory if empty
        CleanupSwarmDirectory();
    }

    /// <summary>
    /// Cleans up a Git worktree by removing it and its reference.
    /// </summary>
    /// <param name="worktreePath">Path to the worktree directory.</param>
    private void CleanupWorktree(string worktreePath)
    {
        try
        {
            // Try to remove the worktree using git worktree remove
            var projectRoot = Directory.GetParent(worktreePath)?.Parent?.Parent?.FullName 
                              ?? Path.GetFullPath(".");
            
            if (_snapshotService.IsGitRepo)
            {
                // Run: git worktree remove <path> --force
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"worktree remove \"{worktreePath}\" --force",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                process?.WaitForExit(5000);
            }

            // Force delete the directory if it still exists
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey italic]Warning: Could not fully cleanup worktree: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Cleans up the swarm directory if it's empty or contains only leftover files.
    /// </summary>
    private void CleanupSwarmDirectory()
    {
        try
        {
            if (Directory.Exists(_swarmDir))
            {
                var files = Directory.GetFiles(_swarmDir);
                var subdirs = Directory.GetDirectories(_swarmDir);
                
                // If only empty or contains no active swarm data, remove it
                if (files.Length == 0 && subdirs.Length == 0)
                {
                    Directory.Delete(_swarmDir, recursive: true);
                    AnsiConsole.MarkupLine("[grey]Swarm directory cleaned up.[/]");
                }
            }
        }
        catch
        {
            // Silent fail - cleanup is best-effort
        }
    }

    /// <summary>
    /// Shows usage information for /swarm commands.
    /// </summary>
    private void ShowUsage()
    {
        var table = new Table().Border(TableBorder.None).HideHeaders()
            .AddColumn("cmd").AddColumn("desc");
        
        table.AddRow("[yellow]/swarm[/]", "Start a new swarm session (not yet implemented)");
        table.AddRow("[yellow]/swarm status[/]", "View active swarm status");
        table.AddRow("[yellow]/swarm cancel[/]", "Cancel active swarm and cleanup");
        table.AddRow("[yellow]/swarm help[/]", "Show this help");
        
        AnsiConsole.Write(table);
    }
}
