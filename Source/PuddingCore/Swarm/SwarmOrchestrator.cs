using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

public sealed class SwarmOrchestrator : ISwarmOrchestrator
{
    private readonly IContractManager _contractManager;
    private readonly IWorkerManager _workerManager;
    private readonly ContractValidator _validator;
    private readonly string _swarmRoot;
    private readonly string _repoRoot;
    private readonly string _finalTestCommand;
    private readonly SwarmSessionStore _sessionStore;

    public SwarmOrchestrator(
        IContractManager contractManager,
        IWorkerManager workerManager,
        string? swarmRoot = null,
        string? finalTestCommand = null)
    {
        _contractManager = contractManager ?? throw new ArgumentNullException(nameof(contractManager));
        _workerManager = workerManager ?? throw new ArgumentNullException(nameof(workerManager));
        _validator = new ContractValidator();
        _swarmRoot = swarmRoot ?? Path.Combine(Directory.GetCurrentDirectory(), ".pudding", "swarm");
        _repoRoot = Path.GetFullPath(Path.Combine(_swarmRoot, "..", ".."));
        _finalTestCommand = finalTestCommand?.Trim() ?? "";
        _sessionStore = new SwarmSessionStore(_swarmRoot);
    }

    public async IAsyncEnumerable<AgentEvent> ProcessSwarmAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var continueMode = userInput.Equals("continue", StringComparison.OrdinalIgnoreCase);

        yield return new ThinkingEvent("Initializing swarm directory...");
        await _contractManager.InitializeSwarmDirectoryAsync(ct);

        yield return new ThinkingEvent(continueMode
            ? "Resuming pending swarm tasks and spawning Leader Agent..."
            : "Analyzing user requirements and spawning Leader Agent...");
        var leaderScope = new WorkerScope(
            AllowedPaths: [ "**/*" ],
            AllowedSymbols: [ "**" ]);

        var leader = await _workerManager.SpawnWorkerAsync(WorkerRole.Leader, userInput, leaderScope, ct);
        yield return new WorkerSpawnedEvent(leader.Id, WorkerRole.Leader, leaderScope);
        yield return new ThinkingEvent("Leader Agent spawned.");

        Contract contract;
        List<SwarmTask> tasks;
        SwarmSessionState session;

        if (continueMode)
        {
            session = await _sessionStore.LoadLatestAsync(ct) ?? new SwarmSessionState();
            if (session.Tasks.Count == 0 || session.Contract.Id == "unknown")
            {
                yield return new ErrorEvent("No resumable swarm session found. Start with /swarm <task> first.");
                yield break;
            }

            contract = session.Contract;
            tasks = session.Tasks;
            yield return new ThinkingEvent($"Continue executor resumed session: {session.SessionId}");
            yield return new ContractDefinedEvent(contract.Id, contract.Symbols);
        }
        else
        {
            yield return new ThinkingEvent("Leader analyzing requirements and defining contracts...");
            contract = await _contractManager.DefineContractAsync(userInput, ct);
            yield return new ContractDefinedEvent(contract.Id, contract.Symbols);

            tasks = BuildTasks(contract);
            session = new SwarmSessionState
            {
                UserInput = userInput,
                Contract = contract,
                Tasks = tasks
            };
            await _sessionStore.SaveAsync(session, ct);
        }

        yield return new ThinkingEvent("Leader spawning Worker Agents for parallel implementation...");

        var workers = new List<WorkerInfo>();

        var runnableTasks = tasks
            .Where(t => t.Status is SwarmTaskStatus.Created
                or SwarmTaskStatus.Assigned
                or SwarmTaskStatus.InProgress
                or SwarmTaskStatus.PendingReview
                or SwarmTaskStatus.Testing
                or SwarmTaskStatus.Blocked
                or SwarmTaskStatus.Failed)
            .Take(5)
            .ToList();

        foreach (var swarmTask in runnableTasks)
        {
            var file = swarmTask.Scope?.AllowedPaths.FirstOrDefault() ?? contract.Files.FirstOrDefault() ?? "**/*";
            var scope = swarmTask.Scope ?? new WorkerScope(
                AllowedPaths: [ file ],
                AllowedSymbols: contract.Symbols.Where(s => file.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList());

            var worker = await _workerManager.SpawnWorkerAsync(WorkerRole.Builder, swarmTask.Description, scope, ct);
            workers.Add(worker);
            swarmTask.AssignedTo = worker.Id;
            swarmTask.Status = SwarmTaskStatus.Assigned;
            await _sessionStore.SaveAsync(session, ct);
            yield return new TaskAssignedEvent(swarmTask.Id, worker.Id, contract.Id);
            yield return new WorkerSpawnedEvent(worker.Id, WorkerRole.Builder, scope);
        }

        if (workers.Count == 0)
        {
            yield return new SwarmCompletedEvent("No runnable tasks. Swarm session already complete.");
            yield break;
        }

        var workerTasks = new List<Task<WorkerExecutionOutcome>>();
        foreach (var worker in workers)
        {
            var task = session.Tasks.FirstOrDefault(t => t.AssignedTo == worker.Id);
            if (task is not null)
            {
                task.Status = SwarmTaskStatus.InProgress;
                await _sessionStore.SaveAsync(session, ct);
            }
            workerTasks.Add(MonitorWorkerProgressAsync(worker, contract, ct));
        }

        yield return new ThinkingEvent($"Waiting for {workers.Count} workers to complete their tasks...");
        var outcomes = await Task.WhenAll(workerTasks);
        var outcomeMap = outcomes.ToDictionary(o => o.WorkerId, StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in outcomes.Where(o => !o.Success))
        {
            var task = session.Tasks.FirstOrDefault(t => t.AssignedTo == outcome.WorkerId);
            if (task is null)
            {
                continue;
            }

            task.Status = SwarmTaskStatus.Failed;
            task.FailReason = outcome.Reason ?? $"Worker failed after {outcome.Attempts} attempt(s).";
            await _sessionStore.SaveAsync(session, ct);
            yield return new TaskFailedEvent(task.Id, outcome.WorkerId, task.FailReason);
        }

        yield return new ThinkingEvent("Validating Worker implementations against contracts...");
        foreach (var worker in workers)
        {
            if (outcomeMap.TryGetValue(worker.Id, out var outcome) && !outcome.Success)
            {
                continue;
            }

            var task = session.Tasks.FirstOrDefault(t => t.AssignedTo == worker.Id);
            if (task is null)
            {
                continue;
            }

            task.Status = SwarmTaskStatus.PendingReview;
            await _sessionStore.SaveAsync(session, ct);

            var validation = _validator.ValidateContract(contract, worker.WorktreePath);
            if (validation.IsValid)
            {
                task.Status = SwarmTaskStatus.Completed;
                task.Result = "Validated by reviewer gate";
                await _sessionStore.SaveAsync(session, ct);
                yield return new ContractValidatedEvent(contract.Id, true);
                yield return new TaskCompletedEvent(task.Id, worker.Id, task.Result);
                yield return new ThinkingEvent($"Contract validation passed for {worker.Role}");
            }
            else
            {
                task.Status = SwarmTaskStatus.Blocked;
                task.FailReason = string.Join("; ", validation.Errors);
                await _sessionStore.SaveAsync(session, ct);
                yield return new ContractValidatedEvent(contract.Id, false);
                yield return new TaskFailedEvent(task.Id, worker.Id, "Blocked by reviewer gate");
                foreach (var error in validation.Errors)
                {
                    yield return new ErrorEvent($"Contract validation failed: {error}");
                }
            }
        }

        yield return new ThinkingEvent("Summarizing merge outcomes...");
        var mergedCount = 0;
        foreach (var worker in workers)
        {
            var task = session.Tasks.FirstOrDefault(t => t.AssignedTo == worker.Id);
            var mergeOk = false;
            if (task is not null && task.Status == SwarmTaskStatus.Completed)
            {
                var merge = await TryMergeWorkerBranchAsync(worker.Id, ct);
                mergeOk = merge.Success;
                if (!merge.Success)
                {
                    task.Status = merge.Kind == MergeFailureKind.Conflict
                        ? SwarmTaskStatus.Blocked
                        : SwarmTaskStatus.Failed;
                    task.FailReason = $"Merge failed ({merge.Kind}): {merge.Error}";
                    await _sessionStore.SaveAsync(session, ct);
                    yield return new TaskFailedEvent(task.Id, worker.Id, task.FailReason);
                }
                else
                {
                    mergedCount++;
                }
            }
            yield return new MergeEvent($"swarm/{worker.Id}", mergeOk);
        }

        if (mergedCount > 0)
        {
            yield return new ThinkingEvent("Running final regression tests...");
            var test = await TryRunFinalTestsAsync(ct);
            if (!test.Success)
            {
                yield return new ErrorEvent($"Final tests failed: {test.Error}");
            }
            else
            {
                yield return new ThinkingEvent("Final regression tests passed.");
            }
        }
        else
        {
            yield return new ThinkingEvent("Skipping final tests because no branch was merged.");
        }

        yield return new ThinkingEvent("Cleaning up swarm resources...");
        foreach (var worker in workers)
        {
            await _workerManager.DismissWorkerAsync(worker.Id, ct);
        }

        var done = session.Tasks.Count(t => t.Status == SwarmTaskStatus.Completed);
        var blocked = session.Tasks.Count(t => t.Status == SwarmTaskStatus.Blocked);
        var failed = session.Tasks.Count(t => t.Status == SwarmTaskStatus.Failed);
        var summary = $"Swarm completed. Contract: {contract.Id}, Workers: {workers.Count}, done={done}, blocked={blocked}, failed={failed}";
        await _sessionStore.SaveAsync(session, ct);
        yield return new SwarmCompletedEvent(summary);
    }

    private static List<SwarmTask> BuildTasks(Contract contract)
    {
        var tasks = new List<SwarmTask>();
        var files = contract.Files.Take(5).ToList();
        if (files.Count == 0)
        {
            files.Add("**/*");
        }

        foreach (var file in files)
        {
            tasks.Add(new SwarmTask
            {
                Id = $"task-{Guid.NewGuid():N}"[..13],
                Title = $"Implement {Path.GetFileName(file)}",
                Description = $"Implement {file} according to contract {contract.Id}. Follow the specification: {contract.Specification}",
                ContractId = contract.Id,
                Scope = new WorkerScope(
                    AllowedPaths: [ file ],
                    AllowedSymbols: contract.Symbols.Where(s => file.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList()),
                Status = SwarmTaskStatus.Created
            });
        }

        return tasks;
    }

    private async Task<WorkerExecutionOutcome> MonitorWorkerProgressAsync(
        WorkerInfo worker,
        Contract contract,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delayMs = 300;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(worker.WorktreePath))
                {
                    throw new InvalidOperationException($"Worktree path not found: {worker.WorktreePath}");
                }

                var git = await RunProcessAsync(
                    "git",
                    "rev-parse --is-inside-work-tree",
                    worker.WorktreePath,
                    ct);
                if (git.ExitCode != 0)
                {
                    throw new InvalidOperationException($"git rev-parse failed: {git.StandardError}");
                }

                var markerDir = Path.Combine(worker.WorktreePath, ".pudding");
                Directory.CreateDirectory(markerDir);
                var markerPath = Path.Combine(markerDir, "worker-execution.log");
                await File.AppendAllTextAsync(
                    markerPath,
                    $"{DateTimeOffset.Now:O} worker={worker.Id} contract={contract.Id} attempt={attempt}{Environment.NewLine}",
                    ct);

                return new WorkerExecutionOutcome(worker.Id, true, attempt, null);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _ = ex;
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
            catch (Exception ex)
            {
                return new WorkerExecutionOutcome(worker.Id, false, attempt, ex.Message);
            }
        }

        return new WorkerExecutionOutcome(worker.Id, false, maxAttempts, "Unknown worker execution failure.");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task<MergeResult> TryMergeWorkerBranchAsync(
        string workerId,
        CancellationToken ct)
    {
        var branch = $"swarm/{workerId}";

        var exists = await RunProcessAsync(
            "git",
            $"branch --list {branch}",
            _repoRoot,
            ct);
        if (exists.ExitCode != 0)
        {
            return new MergeResult(false, MergeFailureKind.Repository, exists.StandardError.Trim());
        }

        if (string.IsNullOrWhiteSpace(exists.StandardOutput))
        {
            return new MergeResult(false, MergeFailureKind.BranchMissing, $"branch not found: {branch}");
        }

        var merge = await RunProcessAsync(
            "git",
            $"merge --no-ff --no-edit {branch}",
            _repoRoot,
            ct);
        if (merge.ExitCode == 0)
        {
            return new MergeResult(true, MergeFailureKind.None, null);
        }

        _ = await RunProcessAsync("git", "merge --abort", _repoRoot, ct);
        var error = string.IsNullOrWhiteSpace(merge.StandardError) ? merge.StandardOutput.Trim() : merge.StandardError.Trim();
        var kind = error.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
            ? MergeFailureKind.Conflict
            : MergeFailureKind.Repository;
        return new MergeResult(false, kind, error);
    }

    private async Task<(bool Success, string? Error)> TryRunFinalTestsAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_finalTestCommand))
        {
            var configured = await RunShellCommandAsync(_finalTestCommand, _repoRoot, ct);
            if (configured.ExitCode == 0)
                return (true, null);

            return (false, string.IsNullOrWhiteSpace(configured.StandardError)
                ? configured.StandardOutput.Trim()
                : configured.StandardError.Trim());
        }

        var slnx = Directory.GetFiles(_repoRoot, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.GetFiles(_repoRoot, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var target = slnx ?? sln;

        if (target is null)
        {
            return (false, "No .slnx/.sln found for dotnet test.");
        }

        var fileName = Path.GetFileName(target);
        var test = await RunProcessAsync(
            "dotnet",
            $"test \"{fileName}\" -c Debug --no-build",
            _repoRoot,
            ct);

        if (test.ExitCode == 0)
        {
            return (true, null);
        }

        return (false, string.IsNullOrWhiteSpace(test.StandardError) ? test.StandardOutput.Trim() : test.StandardError.Trim());
    }

    private static Task<(int ExitCode, string StandardOutput, string StandardError)> RunShellCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RunProcessAsync("cmd.exe", $"/c {command}", workingDirectory, ct);
        }

        return RunProcessAsync("/bin/bash", $"-lc \"{command.Replace("\"", "\\\"")}\"", workingDirectory, ct);
    }

    private sealed record WorkerExecutionOutcome(
        string WorkerId,
        bool Success,
        int Attempts,
        string? Reason);

    private sealed record MergeResult(bool Success, MergeFailureKind Kind, string? Error);

    private enum MergeFailureKind
    {
        None,
        Conflict,
        BranchMissing,
        Repository
    }
}
