using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Cli;

/// <summary><c>build --wait</c> 的等待结论（含度量来源，避免把估算说成实测）。</summary>
/// <param name="ExitCode">终态映射出的退出码。</param>
/// <param name="Status">终态快照；本进程看不到该 job 时为 null（此时输出 <c>not_in_this_process</c>）。</param>
/// <param name="Note"><c>not_in_this_process</c> / <c>wait-timeout</c>；正常到达终态为 null。</param>
/// <param name="IndexedFileCount">实际写入索引的文件数（builder 自报）。</param>
/// <param name="TotalBytes">写入索引的语料字节数。</param>
/// <param name="ElapsedMs">构建耗时（毫秒）。</param>
/// <param name="MetricsSource"><c>builder</c>（本进程实测）/ <c>job-discovery</c>（本进程没跑构建时的兜底）。</param>
internal sealed record BuildWaitOutcome(
    int ExitCode,
    SupplyJobStatus? Status,
    string? Note,
    int? IndexedFileCount,
    long? TotalBytes,
    long? ElapsedMs,
    string? MetricsSource);

// ── JSON DTO（字段顺序 = 声明顺序；属性名一律 camelCase） ─────────────────────

internal sealed record HolderJson(
    string OwnerId,
    int ProcessId,
    string MachineName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatUtc,
    string? JobId,
    bool IsExpired,
    string? TakeoverReason)
{
    internal static HolderJson? From(SupplyLeaseHolder? holder) =>
        holder is null
            ? null
            : new HolderJson(
                holder.OwnerId,
                holder.ProcessId,
                holder.MachineName,
                holder.StartedAtUtc,
                holder.HeartbeatUtc,
                holder.JobId,
                holder.IsExpired,
                holder.TakeoverReason);
}

internal sealed record ScopeEstimateJson(
    string ScopeKey,
    string RootPath,
    int FileCount,
    long CorpusBytes,
    long PredictedIndexBytes,
    long BudgetBytes,
    bool WithinBudget);

internal sealed record RejectionJson(string Value, string Reason, string Message);

internal sealed record PlanJson(
    string Command,
    string IndexRoot,
    bool IndexRootExplicit,
    string Owner,
    bool Accepted,
    long BudgetBytes,
    double IndexSizeFactor,
    IReadOnlyList<ScopeEstimateJson> Scopes,
    IReadOnlyList<RejectionJson> Rejections,
    int ExitCode);

internal sealed record ScopeOutcomeJson(
    string? Value,
    string? ScopeKey,
    string Outcome,
    string? JobId,
    string? Reason,
    HolderJson? Holder);

internal sealed record JobStatusJson(
    string JobId,
    string ScopeKey,
    string RootPath,
    string State,
    string Phase,
    int DiscoveredFileCount,
    long DiscoveredBytes,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    HolderJson? LeaseHolder)
{
    internal static JobStatusJson From(SupplyJobStatus status) => new(
        status.JobId,
        status.ScopeKey,
        status.RootPath,
        status.State.ToString(),
        status.Phase,
        status.DiscoveredFileCount,
        status.DiscoveredBytes,
        status.StartedAt,
        status.FinishedAt,
        status.Message,
        HolderJson.From(status.LeaseHolder));
}

internal sealed record JobLookupJson(string JobId, string Lookup, string Detail, JobStatusJson? Job);

internal sealed record StatusScopeJson(
    string ScopePath,
    string ScopeKey,
    bool ScopeExists,
    bool HasIndex,
    string IndexDirectory,
    bool IndexDirectoryExists,
    int IndexEntryCount,
    long IndexBytes,
    string LeaseLookup,
    HolderJson? LeaseHolder);

internal sealed record StatusJson(
    string Command,
    string IndexRoot,
    bool IndexRootExplicit,
    string Owner,
    bool IndexRootExists,
    int IndexRootTopLevelEntries,
    JobLookupJson? JobLookup,
    IReadOnlyList<JobStatusJson> Jobs,
    IReadOnlyList<StatusScopeJson> Scopes,
    int ExitCode);

internal sealed record BuildJson(
    string Command,
    string IndexRoot,
    bool IndexRootExplicit,
    string Owner,
    string Outcome,
    string? JobId,
    string? Reason,
    HolderJson? Holder,
    IReadOnlyList<ScopeOutcomeJson> Scopes,
    bool Waited,
    string? State,
    string? Phase,
    string? WaitNote,
    int? IndexedFileCount,
    long? TotalBytes,
    long? ElapsedMs,
    string? MetricsSource,
    string? Message,
    int ExitCode);

internal sealed record CancelJson(
    string Command,
    string JobId,
    string Lookup,
    string Detail,
    string? State,
    int ExitCode);

/// <summary>
/// 输出层：每个命令一个 <c>Render*</c>。<c>--json</c> 时只输出 JSON（可被机器直接解析）；
/// 否则输出 <c>key = value</c> 人类可读行（字段名取规格里的名字，如 <c>IndexedFileCount</c>）。
/// </summary>
internal static class SupplyCliViews
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void RenderPlan(TextWriter stdout, bool json, PlanJson dto)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));
            return;
        }

        Write(stdout, "command", dto.Command);
        Write(stdout, "index-root", dto.IndexRoot);
        Write(stdout, "index-root-source", dto.IndexRootExplicit ? "explicit" : "default");
        Write(stdout, "owner", dto.Owner);
        Write(stdout, "budget-bytes", dto.BudgetBytes);
        Write(stdout, "index-size-factor", dto.IndexSizeFactor);
        Write(stdout, "accepted", dto.Accepted);
        Write(stdout, "scope-count", dto.Scopes.Count);

        for (var i = 0; i < dto.Scopes.Count; i++)
        {
            var scope = dto.Scopes[i];
            Write(stdout, $"scope[{i}].rootPath", scope.RootPath);
            Write(stdout, $"scope[{i}].scopeKey", scope.ScopeKey);
            Write(stdout, $"scope[{i}].fileCount", scope.FileCount);
            Write(stdout, $"scope[{i}].corpusBytes", scope.CorpusBytes);
            Write(stdout, $"scope[{i}].predictedIndexBytes", scope.PredictedIndexBytes);
            Write(stdout, $"scope[{i}].budgetBytes", scope.BudgetBytes);
            Write(stdout, $"scope[{i}].withinBudget", scope.WithinBudget);
        }

        Write(stdout, "rejection-count", dto.Rejections.Count);
        for (var i = 0; i < dto.Rejections.Count; i++)
        {
            Write(stdout, $"rejection[{i}].value", dto.Rejections[i].Value);
            Write(stdout, $"rejection[{i}].reason", dto.Rejections[i].Reason);
            Write(stdout, $"rejection[{i}].message", dto.Rejections[i].Message);
        }

        Write(stdout, "result", dto.Accepted ? "accepted" : "rejected");
        Write(stdout, "exit-code", dto.ExitCode);
    }

    internal static void RenderBuild(TextWriter stdout, bool json, BuildJson dto)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));
            return;
        }

        Write(stdout, "command", dto.Command);
        Write(stdout, "index-root", dto.IndexRoot);
        Write(stdout, "index-root-source", dto.IndexRootExplicit ? "explicit" : "default");
        Write(stdout, "owner", dto.Owner);
        Write(stdout, "outcome", dto.Outcome);
        Write(stdout, "jobId", dto.JobId);
        Write(stdout, "reason", dto.Reason);
        Write(stdout, "scope-count", dto.Scopes.Count);

        for (var i = 0; i < dto.Scopes.Count; i++)
        {
            var scope = dto.Scopes[i];
            Write(stdout, $"scope[{i}].value", scope.Value);
            Write(stdout, $"scope[{i}].scopeKey", scope.ScopeKey);
            Write(stdout, $"scope[{i}].outcome", scope.Outcome);
            Write(stdout, $"scope[{i}].jobId", scope.JobId);
            Write(stdout, $"scope[{i}].reason", scope.Reason);
        }

        Write(stdout, "wait", dto.Waited);
        if (dto.Waited)
        {
            Write(stdout, "wait-note", dto.WaitNote);
            Write(stdout, "state", dto.State);
            Write(stdout, "phase", dto.Phase);
            Write(stdout, "IndexedFileCount", dto.IndexedFileCount);
            Write(stdout, "TotalBytes", dto.TotalBytes);
            Write(stdout, "ElapsedMs", dto.ElapsedMs);
            Write(stdout, "metrics-source", dto.MetricsSource);
            Write(stdout, "job-message", dto.Message);
        }

        Write(stdout, "exit-code", dto.ExitCode);
    }

    internal static void RenderStatus(TextWriter stdout, bool json, StatusJson dto)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));
            return;
        }

        Write(stdout, "command", dto.Command);
        Write(stdout, "index-root", dto.IndexRoot);
        Write(stdout, "index-root-source", dto.IndexRootExplicit ? "explicit" : "default");
        Write(stdout, "index-root-exists", dto.IndexRootExists);
        Write(stdout, "index-root-top-level-entries", dto.IndexRootTopLevelEntries);
        Write(stdout, "owner", dto.Owner);

        Write(stdout, "job-count", dto.Jobs.Count);
        for (var i = 0; i < dto.Jobs.Count; i++)
        {
            var job = dto.Jobs[i];
            Write(stdout, $"job[{i}].jobId", job.JobId);
            Write(stdout, $"job[{i}].state", job.State);
            Write(stdout, $"job[{i}].phase", job.Phase);
            Write(stdout, $"job[{i}].scopeKey", job.ScopeKey);
            Write(stdout, $"job[{i}].rootPath", job.RootPath);
            Write(stdout, $"job[{i}].discoveredFileCount", job.DiscoveredFileCount);
            Write(stdout, $"job[{i}].discoveredBytes", job.DiscoveredBytes);
            Write(stdout, $"job[{i}].startedAt", job.StartedAt);
            Write(stdout, $"job[{i}].finishedAt", job.FinishedAt);
            Write(stdout, $"job[{i}].message", job.Message);
        }

        if (dto.JobLookup is { } lookup)
        {
            Write(stdout, "job-lookup", lookup.Lookup);
            Write(stdout, "job-lookup-detail", lookup.Detail);
            Write(stdout, "job-lookup-state", lookup.Job?.State);
        }

        Write(stdout, "scope-count", dto.Scopes.Count);
        for (var i = 0; i < dto.Scopes.Count; i++)
        {
            var scope = dto.Scopes[i];
            Write(stdout, $"scope[{i}].path", scope.ScopePath);
            Write(stdout, $"scope[{i}].scopeKey", scope.ScopeKey);
            Write(stdout, $"scope[{i}].exists", scope.ScopeExists);
            Write(stdout, $"scope[{i}].hasIndex", scope.HasIndex);
            Write(stdout, $"scope[{i}].indexDirectory", scope.IndexDirectory);
            Write(stdout, $"scope[{i}].indexDirectoryExists", scope.IndexDirectoryExists);
            Write(stdout, $"scope[{i}].indexEntryCount", scope.IndexEntryCount);
            Write(stdout, $"scope[{i}].indexBytes", scope.IndexBytes);
            Write(stdout, $"scope[{i}].lease", scope.LeaseLookup);
            Write(stdout, $"scope[{i}].leaseOwner", scope.LeaseHolder?.OwnerId);
            Write(stdout, $"scope[{i}].leaseJobId", scope.LeaseHolder?.JobId);
        }

        Write(stdout, "exit-code", dto.ExitCode);
    }

    internal static void RenderCancel(TextWriter stdout, bool json, CancelJson dto)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));
            return;
        }

        Write(stdout, "command", dto.Command);
        Write(stdout, "jobId", dto.JobId);
        Write(stdout, "lookup", dto.Lookup);
        Write(stdout, "detail", dto.Detail);
        Write(stdout, "state", dto.State);
        Write(stdout, "exit-code", dto.ExitCode);
    }

    /// <summary>人类可读行：字符串/数值（不变文化）/布尔（小写）/null（<c>none</c>）。</summary>
    private static void Write(TextWriter stdout, string key, object? value)
    {
        var text = value switch
        {
            null => "none",
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        stdout.WriteLine($"{key} = {text}");
    }
}
