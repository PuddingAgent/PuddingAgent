using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>Reasons a calibration run refused to sweep a scope.</summary>
/// <remarks>
/// A refusal is never "nothing to do": it means the verdict "this path is not on disk any more" could not be
/// trusted for the whole scope, so the caller must keep the scope flagged.
/// </remarks>
public static class CodeIndexCalibrationRejections
{
    /// <summary>The scope has no root path at all, so there is nothing to probe.</summary>
    public const string RootPathMissing = "root_path_missing";

    /// <summary>
    /// The scope root does not exist — a drive that is not mounted, a network share that is gone, a directory
    /// that was renamed away.
    /// </summary>
    public const string RootMissing = "root_missing";

    /// <summary>The scope root exists but cannot be read (changed permissions, IO error, detached volume).</summary>
    public const string RootUnreadable = "root_unreadable";
}

/// <summary>
/// One calibration (mark-and-sweep) request for one scope: the indexed file paths of the scope are marked,
/// the file system is asked which of them are still there, and the "vanished" ones are swept.
/// </summary>
/// <param name="WorkspaceId">Workspace that owns the scope.</param>
/// <param name="ScopeId">Scope whose indexed paths are swept (also the store's project id).</param>
/// <param name="RootPath">
/// Root directory the scope is indexed from. It is <b>probed first</b>: when the root is missing or unreadable
/// the sweep is refused outright, because "not on disk" would then be true for every single path.
/// </param>
/// <param name="RecentObservations">
/// Paths the change pipeline observed recently, with the moment they were observed. A path observed within
/// <see cref="CodeIndexCalibrationService.DefaultGraceWindow"/> is never swept, even when it is missing on
/// disk right now: it is (or is about to be) handled by the change pipeline itself, and a file that is only
/// <i>momentarily</i> absent — an atomic replace, an in-flight rename, a build rewriting its own output —
/// must not be mistaken for a historical leftover.
/// </param>
public sealed record CodeIndexCalibrationRequest(
    string WorkspaceId,
    string ScopeId,
    string RootPath,
    IReadOnlyDictionary<string, DateTimeOffset>? RecentObservations = null);

/// <summary>Outcome of one calibration run for one scope.</summary>
/// <param name="WorkspaceId">Workspace the run belongs to.</param>
/// <param name="ScopeId">Scope the run belongs to.</param>
/// <param name="RootUsable"><c>false</c> when the run was refused; nothing was swept in that case.</param>
/// <param name="RejectionReason">One of <see cref="CodeIndexCalibrationRejections"/>, or null when the run was allowed.</param>
/// <param name="ScannedFileCount">Indexed file paths examined before the run ended.</param>
/// <param name="AbsentFileCount">Of those, paths that are not on disk any more.</param>
/// <param name="SweptFileCount">File records really deleted from the store (0 for a path that was not indexed).</param>
/// <param name="ProtectedFileCount">Absent paths left alone because they were observed changing just now.</param>
/// <param name="Truncated"><c>true</c> when the per-run removal ceiling was reached before the listing ended.</param>
/// <param name="StartedAtUtc">When the run started.</param>
/// <param name="CompletedAtUtc">When the run ended.</param>
public sealed record CodeIndexCalibrationResult(
    string WorkspaceId,
    string ScopeId,
    bool RootUsable,
    string? RejectionReason,
    int ScannedFileCount,
    int AbsentFileCount,
    int SweptFileCount,
    int ProtectedFileCount,
    bool Truncated,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// U3-C: the calibration (mark-and-sweep) of one scope's index.
/// <para>
/// What it is for: every other path into the index is <b>change driven</b>, so a stale row is only ever
/// cleared when a change event for that path is observed. Historical leftovers (deleted before the pipeline
/// existed), a whole directory that was deleted or renamed, and events lost while the watcher was not
/// observing therefore keep their rows forever, and retrieval keeps answering with symbols of files that are
/// gone. Nor does a full re-index sweep them: a language indexer walks the syntax trees that exist now, it has
/// no notion of "seen this run".
/// </para>
/// <para>
/// Three rules make this safe to run against a live index:
/// <list type="number">
///   <item><description>
///     <b>The root must be usable.</b> A missing root (unmounted drive, gone network share) or an unreadable
///     one makes "not on disk" true for every path — a naive sweep would empty the whole scope's index. So the
///     root is probed first and the run is refused (zero removals, error log, scope stays flagged) when that
///     probe fails.
///   </description></item>
///   <item><description>
///     <b>Index rows only.</b> The sweep goes through <see cref="ICodeIndexStore.RemoveFilesAsync"/> and
///     nothing else: no file or directory is ever created, moved or deleted by this class.
///   </description></item>
///   <item><description>
///     <b>Recently observed paths are left alone.</b> See
///     <see cref="CodeIndexCalibrationRequest.RecentObservations"/>: the change pipeline owns those paths for
///     now, and the sweep must not race it.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Bounded by construction: removals are applied in transactions of at most
/// <see cref="DefaultSweepBatchSize"/> paths, at most <see cref="DefaultMaxRemovalsPerRun"/> paths are handed
/// to the store in one run, and the run is cancellable per path and per batch. A cancelled run keeps the
/// batches it already committed (each batch is its own transaction, and no file is ever half-removed) and
/// reports nothing: the caller keeps the scope flagged.
/// </para>
/// </summary>
public sealed class CodeIndexCalibrationService
{
    /// <summary>Number of paths handed to the store in one removal transaction.</summary>
    public const int DefaultSweepBatchSize = 256;

    /// <summary>
    /// Upper bound on how many vanished paths one run hands to the store. Reaching it ends the run early with
    /// <see cref="CodeIndexCalibrationResult.Truncated"/> set, so a scope with a huge backlog is swept across
    /// several runs instead of inside one unbounded transaction series.
    /// </summary>
    public const int DefaultMaxRemovalsPerRun = 4096;

    /// <summary>
    /// How long a path observed by the change pipeline stays exempt from the sweep. Chosen to comfortably
    /// exceed the pipeline's own maximum wait (2s) and any in-flight per-file indexing of that path, while
    /// staying far below the interval at which a scope is calibrated.
    /// </summary>
    public static readonly TimeSpan DefaultGraceWindow = TimeSpan.FromMinutes(2);

    private readonly ICodeIndexStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly int _sweepBatchSize;
    private readonly int _maxRemovalsPerRun;
    private readonly TimeSpan _graceWindow;
    private readonly ILogger? _logger;

    /// <summary>Creates the calibrator.</summary>
    /// <param name="store">Store the sweep is applied through (rows only).</param>
    /// <param name="timeProvider">Optional clock; injectable for deterministic tests.</param>
    /// <param name="sweepBatchSize">Removal batch size override.</param>
    /// <param name="maxRemovalsPerRun">Per-run removal ceiling override.</param>
    /// <param name="graceWindow">Grace window override for recently observed paths.</param>
    /// <param name="logger">Optional logger.</param>
    public CodeIndexCalibrationService(
        ICodeIndexStore store,
        TimeProvider? timeProvider = null,
        int sweepBatchSize = DefaultSweepBatchSize,
        int maxRemovalsPerRun = DefaultMaxRemovalsPerRun,
        TimeSpan? graceWindow = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (sweepBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(sweepBatchSize), sweepBatchSize, "Sweep batch size must be positive.");
        if (maxRemovalsPerRun <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRemovalsPerRun), maxRemovalsPerRun, "Per-run removal ceiling must be positive.");

        _graceWindow = graceWindow ?? DefaultGraceWindow;
        if (_graceWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(graceWindow), _graceWindow, "Grace window must not be negative.");

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sweepBatchSize = sweepBatchSize;
        _maxRemovalsPerRun = maxRemovalsPerRun;
        _logger = logger;
    }

    /// <summary>Effective grace window for recently observed paths.</summary>
    public TimeSpan GraceWindow => _graceWindow;

    /// <summary>
    /// Calibrates one scope: marks the indexed paths, probes the file system, sweeps what is gone.
    /// <para>
    /// Never swallows a cancellation: the token is observed per path and per batch, and
    /// <see cref="OperationCanceledException"/> propagates so the caller can tell "nothing decided" from
    /// "decided and done".
    /// </para>
    /// </summary>
    /// <param name="request">Scope, root and recently observed paths.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome of the run, including the refusal reason when it was refused.</returns>
    public async Task<CodeIndexCalibrationResult> CalibrateAsync(
        CodeIndexCalibrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ScopeId);

        var startedAtUtc = _timeProvider.GetUtcNow();

        // Rule 1 (the dangerous one): the sweep is refused outright when the root cannot be probed. Every
        // indexed path would look "vanished" if the root is not there, so on a refusal the answer is
        // "I could not tell", never "remove everything".
        if (!TryProbeRoot(request.RootPath, out var rejectionReason))
        {
            _logger?.LogError(
                "[CodeIndexCalibration] Refusing to sweep scope {ScopeId}: root {RootPath} is unusable ({Reason}); 0 file(s) removed this run.",
                request.ScopeId, request.RootPath, rejectionReason);

            return new CodeIndexCalibrationResult(
                request.WorkspaceId,
                request.ScopeId,
                RootUsable: false,
                rejectionReason,
                ScannedFileCount: 0,
                AbsentFileCount: 0,
                SweptFileCount: 0,
                ProtectedFileCount: 0,
                Truncated: false,
                startedAtUtc,
                _timeProvider.GetUtcNow());
        }

        var indexedFiles = await _store
            .ListFilesAsync(request.WorkspaceId, request.ScopeId, cancellationToken)
            .ConfigureAwait(false);

        var protectedPaths = BuildProtectedPaths(request.RecentObservations, startedAtUtc);

        var scanned = 0;
        var absent = 0;
        var protectedCount = 0;
        var swept = 0;
        var submitted = 0;
        var truncated = false;
        var pending = new List<string>(Math.Min(_sweepBatchSize, 1024));

        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = file.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            scanned++;

            // Still there? A directory standing where a file was indexed also counts as "there": this sweep
            // only removes what is *gone*, and under-deleting is always the safe side.
            if (File.Exists(filePath) || Directory.Exists(filePath))
                continue;

            absent++;

            if (protectedPaths is not null && protectedPaths.Contains(filePath))
            {
                protectedCount++;
                continue;
            }

            if (submitted + pending.Count >= _maxRemovalsPerRun)
            {
                // Bounded per run: stop handing paths to the store, keep the rest for the next run.
                truncated = true;
                break;
            }

            pending.Add(filePath);

            if (pending.Count >= _sweepBatchSize)
            {
                swept += await RemoveBatchAsync(request, pending, cancellationToken).ConfigureAwait(false);
                submitted += pending.Count;
                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            swept += await RemoveBatchAsync(request, pending, cancellationToken).ConfigureAwait(false);
            submitted += pending.Count;
        }

        var completedAtUtc = _timeProvider.GetUtcNow();

        _logger?.LogInformation(
            "[CodeIndexCalibration] Scope {ScopeId}: scanned {Scanned} indexed path(s), {Absent} not on disk, swept {Swept} file record(s) in {Submitted} removal path(s), {Protected} left alone as recently observed{Truncated}.",
            request.ScopeId, scanned, absent, swept, submitted, protectedCount,
            truncated ? " (per-run ceiling reached; the scope stays flagged for a later run)" : string.Empty);

        return new CodeIndexCalibrationResult(
            request.WorkspaceId,
            request.ScopeId,
            RootUsable: true,
            RejectionReason: null,
            scanned,
            absent,
            swept,
            protectedCount,
            truncated,
            startedAtUtc,
            completedAtUtc);
    }

    private async Task<int> RemoveBatchAsync(
        CodeIndexCalibrationRequest request,
        List<string> batch,
        CancellationToken cancellationToken)
    {
        // The store owns the transaction: one batch is all-or-nothing, idempotent, and removes index rows
        // only — file records, their symbols and the graph rows those symbols own.
        var removed = await _store
            .RemoveFilesAsync(request.WorkspaceId, request.ScopeId, batch, cancellationToken)
            .ConfigureAwait(false);

        return removed;
    }

    /// <summary>
    /// Paths observed by the change pipeline inside the grace window — the ones the sweep must not touch.
    /// Matching uses the component's canonical path comparer so a differently-cased dictionary key cannot
    /// smuggle a path past the guard.
    /// </summary>
    private HashSet<string>? BuildProtectedPaths(
        IReadOnlyDictionary<string, DateTimeOffset>? recentObservations,
        DateTimeOffset nowUtc)
    {
        if (recentObservations is null || recentObservations.Count == 0)
            return null;

        var protectedPaths = new HashSet<string>(CodePathIdentity.PathComparer);

        foreach (var (path, observedAtUtc) in recentObservations)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            // A timestamp in the future (clock skew, an injected test clock) means "just now", which is
            // protected, not exempt.
            if (nowUtc - observedAtUtc <= _graceWindow)
                protectedPaths.Add(path);
        }

        return protectedPaths.Count == 0 ? null : protectedPaths;
    }

    /// <summary>
    /// Probes the scope root before anything is marked or swept.
    /// <para>
    /// <see cref="Directory.Exists"/> alone is not enough: an unmounted volume, a disconnected network share
    /// or a root whose permissions changed can still "exist" for the OS while every read fails. One
    /// enumeration step is therefore forced so those failures surface here, where the answer is still "refuse".
    /// </para>
    /// </summary>
    private static bool TryProbeRoot(string? rootPath, out string reason)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            reason = CodeIndexCalibrationRejections.RootPathMissing;
            return false;
        }

        if (!Directory.Exists(rootPath))
        {
            reason = CodeIndexCalibrationRejections.RootMissing;
            return false;
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator();
            entries.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = CodeIndexCalibrationRejections.RootUnreadable;
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
