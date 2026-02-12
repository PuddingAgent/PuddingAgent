using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;

namespace PuddingRuntime.Services;

public interface IMemoryWriteCoordinator
{
    Task<MemoryWriteResultEnvelope> CoordinateAsync(
        MemoryWriteCommand command,
        CancellationToken ct = default);
}

public sealed class MemoryWriteCoordinator : IMemoryWriteCoordinator
{
    private readonly MemoryWriteCommandValidator _validator;
    private readonly IRuntimeActivitySink? _activitySink;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly IMemoryLibrary? _memoryLibrary;

    public MemoryWriteCoordinator(
        MemoryWriteCommandValidator validator,
        IRuntimeActivitySink? activitySink = null,
        ITelemetryMetricSink? telemetrySink = null,
        IMemoryLibrary? memoryLibrary = null)
    {
        _validator = validator;
        _activitySink = activitySink;
        _telemetrySink = telemetrySink;
        _memoryLibrary = memoryLibrary;
    }

    public async Task<MemoryWriteResultEnvelope> CoordinateAsync(
        MemoryWriteCommand command,
        CancellationToken ct = default)
    {
        var validation = _validator.Validate(command);
        if (!validation.IsValid)
        {
            var rejected = ToRejectedEnvelope(command, validation);
            await RecordDecisionAsync(command, rejected, ct);
            return rejected;
        }

        if (command.Mode == MemoryWriteExecutionModes.Execute)
        {
            var executed = await ExecuteAsync(command, ct);
            await RecordDecisionAsync(command, executed, ct);
            return executed;
        }

        var status = command.Mode == MemoryWriteExecutionModes.ValidateOnly
            ? MemoryWriteResultStatuses.Accepted
            : MemoryWriteResultStatuses.DryRun;

        if (command.Intent == MemoryWriteIntents.ReuseExisting)
            status = MemoryWriteResultStatuses.Reused;

        var envelope = new MemoryWriteResultEnvelope
        {
            CommandId = command.CommandId,
            WorkspaceId = command.WorkspaceId,
            Status = status,
            Mode = command.Mode,
            Intent = command.Intent,
            Decision = command.Intent,
            BookId = command.Target?.BookId,
            ChapterId = command.Target?.ChapterId,
            Metadata = BuildMetadata(command),
        };

        await RecordDecisionAsync(command, envelope, ct);
        return envelope;
    }

    private async Task<MemoryWriteResultEnvelope> ExecuteAsync(
        MemoryWriteCommand command,
        CancellationToken ct)
    {
        if (_memoryLibrary is null || command.Intent != MemoryWriteIntents.AppendNew)
        {
            return new MemoryWriteResultEnvelope
            {
                CommandId = command.CommandId,
                WorkspaceId = command.WorkspaceId,
                Status = MemoryWriteResultStatuses.Rejected,
                Mode = command.Mode,
                Intent = command.Intent,
                Decision = "reject",
                ErrorCodes = [MemoryWriteValidationErrors.UnsupportedIntent],
                Metadata = BuildMetadata(command),
            };
        }

        var payload = command.Payload!;
        var title = string.IsNullOrWhiteSpace(payload.Title) ? "Memory" : payload.Title!;
        var content = payload.Content!;
        var book = await ResolveAppendBookAsync(command, title, content, ct);
        var chapters = await _memoryLibrary.ListChaptersAsync(book.BookId, ct);
        var chapterOrder = chapters.Count > 0 ? chapters.Max(c => c.ChapterOrder) + 1 : 0;
        var chapter = await _memoryLibrary.AddChapterWithSourceAsync(
            book.BookId,
            title,
            content,
            chapterOrder,
            command.Source.SessionId,
            command.Source.HookEventId ?? command.Source.ToolCallId ?? command.Source.SubconsciousJobId,
            command.Source.SourceKind,
            ct);

        return new MemoryWriteResultEnvelope
        {
            CommandId = command.CommandId,
            WorkspaceId = command.WorkspaceId,
            Status = MemoryWriteResultStatuses.Executed,
            Mode = command.Mode,
            Intent = command.Intent,
            Decision = command.Intent,
            BookId = book.BookId,
            ChapterId = chapter.ChapterId,
            Metadata = BuildMetadata(command),
        };
    }

    private async Task<BookRecord> ResolveAppendBookAsync(
        MemoryWriteCommand command,
        string title,
        string content,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.Target?.BookId))
        {
            return await _memoryLibrary!.GetBookReadOnlyAsync(command.Target.BookId, ct)
                ?? throw new InvalidOperationException("Target book not found.");
        }

        var libraries = await _memoryLibrary!.ListLibrariesAsync(command.WorkspaceId, ct);
        if (libraries.Count == 0)
        {
            var created = await _memoryLibrary.CreateLibraryAsync(
                command.WorkspaceId,
                "默认图书馆",
                null,
                ct);
            libraries = [created];
        }

        var libraryId = libraries[0].LibraryId;
        var existingBook = await _memoryLibrary.FindBookByTitleAsync(libraryId, title, ct);
        if (existingBook is not null)
            return existingBook;

        var summary = content.Length > 200 ? content[..200] : content;
        return await _memoryLibrary.CreateBookAsync(libraryId, title, summary, null, ct);
    }

    private static MemoryWriteResultEnvelope ToRejectedEnvelope(
        MemoryWriteCommand command,
        MemoryWriteValidationResult validation) =>
        new()
        {
            CommandId = command.CommandId,
            WorkspaceId = command.WorkspaceId,
            Status = MemoryWriteResultStatuses.Rejected,
            Mode = command.Mode,
            Intent = command.Intent,
            Decision = "reject",
            ErrorCodes = validation.Errors.Select(e => e.Code).Distinct(StringComparer.Ordinal).ToArray(),
            Metadata = BuildMetadata(command),
        };

    private static IReadOnlyDictionary<string, string> BuildMetadata(MemoryWriteCommand command)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_kind"] = command.Source?.SourceKind ?? "missing",
        };

        if (command.Source is null)
            return metadata;

        if (!string.IsNullOrWhiteSpace(command.Source.SessionId))
            metadata["session_id"] = command.Source.SessionId;
        if (!string.IsNullOrWhiteSpace(command.Source.SubconsciousJobId))
            metadata["subconscious_job_id"] = command.Source.SubconsciousJobId;
        if (!string.IsNullOrWhiteSpace(command.Source.PlanId))
            metadata["plan_id"] = command.Source.PlanId;
        if (!string.IsNullOrWhiteSpace(command.Source.OperationId))
            metadata["operation_id"] = command.Source.OperationId;

        return metadata;
    }

    private async Task RecordDecisionAsync(
        MemoryWriteCommand command,
        MemoryWriteResultEnvelope envelope,
        CancellationToken ct)
    {
        var operation = envelope.Status == MemoryWriteResultStatuses.Rejected
            ? "memory_write.reject"
            : command.Mode == MemoryWriteExecutionModes.ValidateOnly
                ? "memory_write.validate"
                : command.Mode == MemoryWriteExecutionModes.Execute
                    ? "memory_write.execute"
                    : "memory_write.dry_run";

        var activityStatus = envelope.Status == MemoryWriteResultStatuses.Rejected
            ? RuntimeActivityStatuses.Failed
            : RuntimeActivityStatuses.Succeeded;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command_id"] = command.CommandId,
            ["workspace_id"] = command.WorkspaceId,
            ["source_kind"] = command.Source?.SourceKind ?? "missing",
            ["intent"] = command.Intent,
            ["mode"] = command.Mode,
            ["status"] = envelope.Status,
            ["dry_run"] = (command.Mode == MemoryWriteExecutionModes.DryRun).ToString().ToLowerInvariant(),
        };

        if (envelope.ErrorCodes.Count > 0)
            metadata["error_code"] = envelope.ErrorCodes[0];

        var trace = RuntimeTraceContext.CreateNew(
            sessionId: command.Source?.SessionId,
            workspaceId: command.WorkspaceId,
            eventId: command.Source?.HookEventId);

        if (_activitySink is not null)
        {
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = trace,
                Component = RuntimeActivityComponents.Memory,
                Operation = operation,
                Status = activityStatus,
                Summary = $"Memory write {envelope.Status}",
                Metadata = metadata,
                ErrorCode = envelope.ErrorCodes.FirstOrDefault(),
            }, ct);
        }

        if (_telemetrySink is not null)
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "pudding.memory.write_coordinator",
                Category = TelemetryMetricCategories.Memory,
                Name = "memory_write.command",
                Status = activityStatus == RuntimeActivityStatuses.Failed
                    ? TelemetryMetricStatuses.Failed
                    : TelemetryMetricStatuses.Succeeded,
                CountValue = 1,
                Unit = "command",
                Severity = activityStatus == RuntimeActivityStatuses.Failed ? "warning" : "info",
                Summary = $"Memory write {envelope.Status}",
                Dimensions = metadata,
                ErrorCode = envelope.ErrorCodes.FirstOrDefault(),
            }, ct);
        }
    }
}
