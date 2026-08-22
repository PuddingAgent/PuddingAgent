using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// ISubAgentRunStore 的文件系统实现。
/// 运行归档以文件为主存储（run.json / events.jsonl / tools.jsonl / output.md），数据库仅做索引。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
public class FileSubAgentRunStore : ISubAgentRunStore
{
    /// <summary>P0-4f-1a step6: subagent.run.* 事件的固定 producer_component（用户拍板三值之一：subagent.runtime）。</summary>
    private const string SubAgentRuntimeProducerComponent = "subagent.runtime";

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<FileSubAgentRunStore> _logger;
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly IConversationEventStore _conversationEventStore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runGates = new(StringComparer.Ordinal);
    private readonly object _projectionScanGate = new();
    private int _projectionScanOffset;

    public FileSubAgentRunStore(
        PuddingDataPaths paths,
        ILogger<FileSubAgentRunStore> logger,
        IDbContextFactory<PlatformDbContext> dbFactory,
        IConversationEventStore conversationEventStore)
    {
        _paths = paths;
        _logger = logger;
        _dbFactory = dbFactory;
        _conversationEventStore = conversationEventStore;
    }

    /// <inheritdoc />
    public async Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default)
    {
        var runId = $"run_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32];
        var archivePath = _paths.SubAgentRunRoot(request.WorkspaceId, request.AgentInstanceId, runId);
        Directory.CreateDirectory(archivePath);

        var now = DateTimeOffset.UtcNow;
        var taskPlanningMetadata = BuildTaskPlanningMetadata(request);
        var manifest = new SubAgentRunManifest
        {
            RunId = runId,
            ParentSessionId = request.ParentSessionId,
            SubSessionId = request.SubSessionId,
            WorkspaceId = request.WorkspaceId,
            AgentInstanceId = request.AgentInstanceId,
            TemplateId = request.TemplateId,
            Task = request.Task,
            Status = "running",
            StartedAt = now,
            TaskPlanning = taskPlanningMetadata,
            InvocationId = request.InvocationId,
            BatchId = request.BatchId,
            OriginToolId = request.OriginToolId,
            Role = request.RoleInPlan,
            ProviderId = request.ProviderId,
            ProfileId = request.ProfileId,
            ModelId = request.ModelId,
            TimeoutSeconds = request.TimeoutSeconds,
            ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
            MaxRounds = request.MaxRounds,
            ParentExecutionIdentity = request.ParentExecutionIdentity,
        };

        // 写 run.json
        var runJson = JsonSerializer.Serialize(manifest, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(Path.Combine(archivePath, "run.json"), runJson, ct);

        // 写 input.json
        var input = new
        {
            task = request.Task,
            parentSessionId = request.ParentSessionId,
            workspaceId = request.WorkspaceId,
            taskPlanning = taskPlanningMetadata,
            invocationId = request.InvocationId,
            batchId = request.BatchId,
            originToolId = request.OriginToolId,
            role = request.RoleInPlan,
            llm = new
            {
                providerId = request.ProviderId,
                profileId = request.ProfileId,
                modelId = request.ModelId,
            },
            limits = new
            {
                timeoutSeconds = request.TimeoutSeconds,
                executionDeadlineUtc = request.ExecutionDeadlineUtc,
                maxRounds = request.MaxRounds,
            },
            parentExecution = request.ParentExecutionIdentity,
        };
        var inputJson = JsonSerializer.Serialize(input, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(Path.Combine(archivePath, "input.json"), inputJson, ct);

        _logger.LogInformation(
            "[FileSubAgentRunStore] Created run runId={RunId} ws={WorkspaceId} agent={AgentInstanceId} path={ArchivePath}",
            runId, request.WorkspaceId, request.AgentInstanceId, archivePath);

        // 同步写 DB 索引
        await WriteDbIndexAsync(runId, request.ParentSessionId, request.SubSessionId,
            request.WorkspaceId, request.AgentInstanceId, request.TemplateId,
            "running", now.ToString("O"), null, archivePath, taskPlanningMetadata, ct);

        await AppendEventAsync(runId, ConversationEventTypes.SubAgentRunCreated, new
        {
            run_id = runId,
            archive_path = archivePath,
            invocation_id = request.InvocationId,
            batch_id = request.BatchId,
            parent_session_id = request.ParentSessionId,
            sub_agent_id = request.SubSessionId,
            parent_turn_id = request.ParentExecutionIdentity?.TurnId,
            parent_run_id = request.ParentExecutionIdentity?.RunId,
            parent_tool_call_id = request.ParentExecutionIdentity?.ToolCallId,
            origin_tool_id = request.OriginToolId,
            role = request.RoleInPlan,
            template = request.TemplateId,
            task_summary = request.Task.Length > 300 ? request.Task[..300] + "..." : request.Task,
            provider_id = request.ProviderId,
            profile_id = request.ProfileId,
            model_id = request.ModelId,
            timeout_seconds = request.TimeoutSeconds,
            execution_deadline_utc = request.ExecutionDeadlineUtc,
            max_rounds = request.MaxRounds,
            status = "created",
        }, ct);

        return new SubAgentRunHandle { RunId = runId, ArchivePath = archivePath };
    }

    /// <inheritdoc />
    public async Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return;

        var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await AppendEventCoreAsync(
                runId,
                runDir,
                Guid.NewGuid().ToString("N"),
                eventType,
                payload,
                ct);
        }
        catch (IOException ex)
        {
            // 运行事件是观测数据：重试耗尽后丢弃事件并标记 archive_degraded，
            // 不得把运行中的子代理直接杀死（run_20260821_230951_dbff4b8075c1 案例）。
            // 终态事件走 CompleteRunAsync，仍保持抛错语义。
            _logger.LogError(ex,
                "[FileSubAgentRunStore] Archive degraded — event dropped runId={RunId} eventType={EventType}",
                runId, eventType);
            await MarkArchiveDegradedAsync(runDir, runId, eventType, ex.Message, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return;

        var line = JsonSerializer.Serialize(entry, PuddingJsonContracts.JsonLines);
        var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await AppendAllTextWithRetryAsync(
                Path.Combine(runDir, "tools.jsonl"),
                line + Environment.NewLine,
                runId,
                "tools.jsonl",
                ct);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "[FileSubAgentRunStore] Archive degraded — tool audit dropped runId={RunId} tool={Tool}",
                runId, entry.ToolName);
            await MarkArchiveDegradedAsync(runDir, runId, $"tool_audit:{entry.ToolName}", ex.Message, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null)
        {
            _logger.LogWarning("[FileSubAgentRunStore] CompleteRunAsync: run dir not found for runId={RunId}", runId);
            return SubAgentRunTerminalWriteResult.NotFound;
        }

        var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
        var runJsonPath = Path.Combine(runDir, "run.json");
        if (!File.Exists(runJsonPath))
        {
            _logger.LogWarning("[FileSubAgentRunStore] CompleteRunAsync: run.json not found for runId={RunId}", runId);
            return SubAgentRunTerminalWriteResult.NotFound;
        }

        // 读 run.json，检查当前 Status — 幂等性保护
        var json = await ReadAllTextSharedAsync(runJsonPath, ct);
        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(json, PuddingJsonContracts.PrettyJson);
        if (manifest is null) return SubAgentRunTerminalWriteResult.NotFound;

        // 如果已经是 terminal 状态，返回 AlreadyTerminal
        if (IsTerminalStatus(manifest.Status))
        {
            _logger.LogWarning(
                "[FileSubAgentRunStore] CompleteRunAsync: run already terminal runId={RunId} currentStatus={CurrentStatus} requestedStatus={RequestedStatus}",
                runId, manifest.Status, completion.Status);
            return SubAgentRunTerminalWriteResult.AlreadyTerminal;
        }

        if (!IsTerminalStatus(completion.Status))
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                completion.Status,
                "Sub-agent run completion status must be terminal.");

        // 终态事件必须先持久化并投影，再推进 run.json。
        // 即使进程在两步之间退出，重试也会使用稳定 eventId 幂等补齐，不会让 UI 永久停在 Running。
        var completedAt = DateTimeOffset.UtcNow;
        completion = await EnrichCompletionFromArchivedEventsAsync(
            runDir,
            manifest,
            completion,
            completedAt,
            ct);
        var terminalEventType = ToTerminalEventType(completion.Status);
        await AppendEventCoreAsync(
            runId,
            runDir,
            $"{runId}:{terminalEventType}",
            terminalEventType,
            new
            {
                parent_session_id = manifest.ParentExecutionIdentity?.ConversationId
                    ?? manifest.ParentSessionId,
                sub_agent_id = manifest.SubSessionId,
                run_id = runId,
                archive_path = runDir,
                invocation_id = manifest.InvocationId,
                batch_id = manifest.BatchId,
                origin_tool_id = manifest.OriginToolId,
                role = manifest.Role,
                status = completion.Status,
                success = string.Equals(completion.Status, "completed", StringComparison.Ordinal),
                reply = completion.Output,
                error = completion.ErrorMessage,
                total_rounds = completion.TotalRounds,
                total_tool_calls = completion.TotalToolCalls,
                total_duration_ms = completion.TotalDurationMs,
                tool_failure_count = completion.ToolFailureCount,
                tool_output_truncated_count = completion.ToolOutputTruncatedCount,
                tool_output_chars = completion.ToolOutputChars,
                tool_failure_summary = completion.ToolFailureSummary,
            },
            ct);

        var updated = manifest with
        {
            Status = completion.Status,
            CompletedAt = completedAt,
            TotalRounds = completion.TotalRounds,
            TotalToolCalls = completion.TotalToolCalls,
            TotalDurationMs = completion.TotalDurationMs,
            ErrorMessage = completion.ErrorMessage,
        };

        var updatedJson = JsonSerializer.Serialize(updated, PuddingJsonContracts.PrettyJson);
        await File.WriteAllTextAsync(runJsonPath, updatedJson, ct);

        // 写 output.md
        if (!string.IsNullOrWhiteSpace(completion.Output))
        {
            await File.WriteAllTextAsync(Path.Combine(runDir, "output.md"), completion.Output, ct);
        }

        // 写 errors.jsonl
        if (!string.IsNullOrWhiteSpace(completion.ErrorMessage))
        {
            var errorLine = JsonSerializer.Serialize(new
            {
                timestamp = completedAt.ToString("O"),
                error = completion.ErrorMessage,
            }, PuddingJsonContracts.JsonLines);
            await AppendAllTextWithRetryAsync(
                Path.Combine(runDir, "errors.jsonl"), errorLine + Environment.NewLine, runId, "errors.jsonl", ct);
        }

        _logger.LogInformation(
            "[FileSubAgentRunStore] Completed run runId={RunId} status={Status} rounds={Rounds} tools={Tools}",
            runId, completion.Status, completion.TotalRounds, completion.TotalToolCalls);

        // 同步更新 DB 索引
        await UpdateDbIndexAsync(runId, completion.Status, completedAt.ToString("O"),
            completion.ErrorMessage, completion.TotalRounds, completion.TotalToolCalls,
            completion.TotalDurationMs, ct);

        return SubAgentRunTerminalWriteResult.Applied;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default)
    {
        var runDir = ResolveRunDir(runId);
        if (runDir is null) return null;

        var runJsonPath = Path.Combine(runDir, "run.json");
        if (!File.Exists(runJsonPath)) return null;

        // 读取必须与写入走同一个 per-run gate：ADR-060 要求归档只在终态被读取，
        // 但历史检查器与诊断 API 仍会读取活动归档，绝不能让读取方把写入方挤成
        // Windows sharing violation（run_20260821_230951_dbff4b8075c1 案例）。
        var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ReadRunArchiveCoreAsync(runDir, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SubAgentRunArchive> ReadRunArchiveCoreAsync(string runDir, CancellationToken ct)
    {
        // 读 run.json
        var json = await ReadAllTextSharedAsync(Path.Combine(runDir, "run.json"), ct);
        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(json, PuddingJsonContracts.PrettyJson);
        if (manifest is null) return null!;

        // 读 events.jsonl（逐行反序列化，单行失败时记录 warning 并跳过）
        var events = new List<object>();
        var eventsPath = Path.Combine(runDir, "events.jsonl");
        if (File.Exists(eventsPath))
        {
            var lines = await ReadAllLinesSharedAsync(eventsPath, ct);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        var obj = JsonSerializer.Deserialize<object>(line, PuddingJsonContracts.JsonLines);
                        if (obj is not null) events.Add(obj);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "[FileSubAgentRunStore] Skipping malformed JSONL line in {Path}", eventsPath);
                    }
                }
            }
        }

        // 读 tools.jsonl（逐行反序列化，单行失败时记录 warning 并跳过）
        var tools = new List<SubAgentToolAuditEntry>();
        var toolsPath = Path.Combine(runDir, "tools.jsonl");
        if (File.Exists(toolsPath))
        {
            var lines = await ReadAllLinesSharedAsync(toolsPath, ct);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<SubAgentToolAuditEntry>(line, PuddingJsonContracts.JsonLines);
                        if (entry is not null) tools.Add(entry);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "[FileSubAgentRunStore] Skipping malformed JSONL line in {Path}", toolsPath);
                    }
                }
            }
        }

        // 读 output.md
        string? output = null;
        var outputPath = Path.Combine(runDir, "output.md");
        if (File.Exists(outputPath))
        {
            output = await ReadAllTextSharedAsync(outputPath, ct);
        }

        // 读 errors.jsonl
        string? errorOutput = null;
        var errorsPath = Path.Combine(runDir, "errors.jsonl");
        if (File.Exists(errorsPath))
        {
            errorOutput = await ReadAllTextSharedAsync(errorsPath, ct);
        }

        return new SubAgentRunArchive
        {
            Manifest = manifest,
            Events = events,
            Tools = tools,
            Output = output,
            ErrorOutput = errorOutput,
            Degraded = await TryReadArchiveDegradedAsync(runDir, ct),
        };
    }

    public async Task<int> RecoverInterruptedRunsAsync(
        DateTimeOffset startedBeforeUtc,
        int maxRuns,
        CancellationToken ct = default)
    {
        if (maxRuns <= 0 || !Directory.Exists(_paths.WorkspacesRoot))
            return 0;

        var recovered = 0;
        foreach (var runJsonPath in Directory.EnumerateFiles(
                     _paths.WorkspacesRoot,
                     "run.json",
                     SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var runDir = Path.GetDirectoryName(runJsonPath);
            if (string.IsNullOrWhiteSpace(runDir))
                continue;

            var runId = Path.GetFileName(runDir);
            if (!runId.StartsWith("run_", StringComparison.Ordinal))
                continue;

            SubAgentRunManifest? manifest;
            try
            {
                var json = await ReadAllTextSharedAsync(runJsonPath, ct);
                manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
                    json,
                    PuddingJsonContracts.PrettyJson);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(
                    ex,
                    "[FileSubAgentRunStore] Skipping invalid run manifest during recovery path={Path}",
                    runJsonPath);
                continue;
            }

            if (manifest is null
                || IsTerminalStatus(manifest.Status)
                || manifest.StartedAt >= startedBeforeUtc)
            {
                continue;
            }

            var completion = await BuildInterruptedCompletionAsync(
                runDir,
                manifest,
                startedBeforeUtc,
                ct);
            var result = await CompleteRunAsync(runId, completion, ct);
            if (result == SubAgentRunTerminalWriteResult.Applied)
                recovered++;

            if (--maxRuns == 0)
                break;
        }

        return recovered;
    }

    public async Task<int> ReplayPendingConversationEventsAsync(
        int maxRuns,
        CancellationToken ct = default)
    {
        if (maxRuns <= 0 || !Directory.Exists(_paths.WorkspacesRoot))
            return 0;

        var runDirectories = Directory.EnumerateFiles(
                _paths.WorkspacesRoot,
                "events.jsonl",
                SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Where(static path =>
                Path.GetFileName(path).StartsWith("run_", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runDirectories.Length == 0)
            return 0;

        var scanCount = Math.Min(maxRuns, runDirectories.Length);
        int scanStart;
        lock (_projectionScanGate)
        {
            scanStart = _projectionScanOffset % runDirectories.Length;
            _projectionScanOffset = (scanStart + scanCount) % runDirectories.Length;
        }

        var projected = 0;
        for (var scanIndex = 0; scanIndex < scanCount; scanIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var runDir = runDirectories[(scanStart + scanIndex) % runDirectories.Length];
            var runId = Path.GetFileName(runDir);
            var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                projected += await ProjectPendingConversationEventsCoreAsync(runId, runDir, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        return projected;
    }

    private static async Task<SubAgentRunCompletion> BuildInterruptedCompletionAsync(
        string runDir,
        SubAgentRunManifest manifest,
        DateTimeOffset interruptedAtUtc,
        CancellationToken ct)
    {
        return await EnrichCompletionFromArchivedEventsAsync(
            runDir,
            manifest,
            new SubAgentRunCompletion
            {
                Status = "interrupted",
                ErrorMessage = "Runtime process restarted before the sub-agent committed a terminal state.",
            },
            interruptedAtUtc,
            ct);
    }

    private static async Task<SubAgentRunCompletion> EnrichCompletionFromArchivedEventsAsync(
        string runDir,
        SubAgentRunManifest manifest,
        SubAgentRunCompletion completion,
        DateTimeOffset completedAtUtc,
        CancellationToken ct)
    {
        var totalRounds = completion.TotalRounds;
        var archivedToolCalls = 0;
        var archivedToolFailureCount = 0;
        var archivedToolOutputTruncatedCount = 0;
        long archivedToolOutputChars = 0;
        var firstToolFailureSummary = completion.ToolFailureSummary;
        var countedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var eventsPath = Path.Combine(runDir, "events.jsonl");
        if (File.Exists(eventsPath))
        {
            foreach (var line in await ReadAllLinesSharedAsync(eventsPath, ct))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var archivedEvent = JsonSerializer.Deserialize<ArchivedRunEvent>(
                        line,
                        PuddingJsonContracts.JsonLines);
                    if (archivedEvent is null
                        || archivedEvent.Payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (archivedEvent.Payload.TryGetProperty("round", out var round)
                        && round.TryGetInt32(out var roundNumber))
                    {
                        totalRounds = Math.Max(totalRounds, roundNumber);
                    }

                    if (archivedEvent.EventType is
                        ConversationEventTypes.SubAgentToolCompleted or
                        ConversationEventTypes.SubAgentToolFailed)
                    {
                        var toolCallKey = archivedEvent.Payload.TryGetProperty("tool_call_id", out var toolCallId)
                            && toolCallId.ValueKind == JsonValueKind.String
                                ? toolCallId.GetString()
                                : archivedEvent.EventId;
                        if (string.IsNullOrWhiteSpace(toolCallKey)
                            || !countedToolCalls.Add(toolCallKey))
                        {
                            continue;
                        }

                        archivedToolCalls++;
                        if (archivedEvent.EventType == ConversationEventTypes.SubAgentToolFailed)
                        {
                            archivedToolFailureCount++;
                            if (string.IsNullOrWhiteSpace(firstToolFailureSummary)
                                && archivedEvent.Payload.TryGetProperty("error", out var error)
                                && error.ValueKind == JsonValueKind.String)
                            {
                                firstToolFailureSummary = error.GetString();
                            }
                        }
                        if (archivedEvent.Payload.TryGetProperty("output_length", out var outputLength)
                            && outputLength.TryGetInt64(out var outputChars))
                        {
                            archivedToolOutputChars += Math.Max(0, outputChars);
                        }
                        if (archivedEvent.Payload.TryGetProperty("output_truncated", out var outputTruncated)
                            && outputTruncated.ValueKind is JsonValueKind.True)
                        {
                            archivedToolOutputTruncatedCount++;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Recovery is best-effort. Projection will log malformed lines separately.
                }
            }
        }

        var elapsedMs = Math.Max(
            0,
            (long)(completedAtUtc - manifest.StartedAt).TotalMilliseconds);
        return completion with
        {
            TotalRounds = totalRounds,
            TotalToolCalls = Math.Max(completion.TotalToolCalls, archivedToolCalls),
            TotalDurationMs = completion.TotalDurationMs > 0
                ? completion.TotalDurationMs
                : elapsedMs,
            ToolFailureCount = Math.Max(completion.ToolFailureCount, archivedToolFailureCount),
            ToolOutputTruncatedCount = Math.Max(
                completion.ToolOutputTruncatedCount,
                archivedToolOutputTruncatedCount),
            ToolOutputChars = Math.Max(completion.ToolOutputChars, archivedToolOutputChars),
            ToolFailureSummary = firstToolFailureSummary,
        };
    }

    private async Task<int> ProjectPendingConversationEventsCoreAsync(
        string runId,
        string runDir,
        CancellationToken ct)
    {
        var runJsonPath = Path.Combine(runDir, "run.json");
        var eventsPath = Path.Combine(runDir, "events.jsonl");
        if (!File.Exists(runJsonPath) || !File.Exists(eventsPath))
            return 0;

        var manifestJson = await ReadAllTextSharedAsync(runJsonPath, ct);
        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
            manifestJson,
            PuddingJsonContracts.PrettyJson);
        if (manifest is null)
            return 0;

        var cursorPath = Path.Combine(runDir, "conversation-projection.cursor");
        var cursor = await ReadProjectionCursorAsync(cursorPath, ct);
        var lines = await ReadAllLinesSharedAsync(eventsPath, ct);
        if (cursor >= lines.LongLength)
            return 0;

        var projected = 0;
        for (var index = cursor; index < lines.LongLength; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                await WriteProjectionCursorAsync(cursorPath, index + 1, ct);
                continue;
            }

            ArchivedRunEvent? archivedEvent;
            try
            {
                archivedEvent = JsonSerializer.Deserialize<ArchivedRunEvent>(
                    lines[index],
                    PuddingJsonContracts.JsonLines);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "[FileSubAgentRunStore] Projection stopped at malformed event runId={RunId} line={Line}",
                    runId,
                    index + 1);
                break;
            }

            if (archivedEvent is null
                || string.IsNullOrWhiteSpace(archivedEvent.EventId)
                || string.IsNullOrWhiteSpace(archivedEvent.EventType))
            {
                break;
            }

            var parent = manifest.ParentExecutionIdentity;
            // ADR-060: Chat observes the parent Conversation. Projecting run
            // facts into the child-only conversation makes the real run invisible
            // to the parent SSE/bootstrap while stale historical parent events
            // remain on screen.
            var conversationId = parent?.ConversationId;
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = manifest.ParentSessionId;
            var payload = archivedEvent.Payload.Clone();
            var draft = new NewConversationEvent(
                EventId: archivedEvent.EventId,
                Type: archivedEvent.EventType,
                SchemaVersion: 1,
                WorkspaceId: manifest.WorkspaceId,
                TurnId: parent?.TurnId,
                CommandId: parent?.CommandId,
                RunId: manifest.RunId,
                MessageId: parent?.MessageId,
                CorrelationId: parent?.ConversationId,
                CausationId: parent?.ToolCallId ?? parent?.RunId,
                ProducerEventId: archivedEvent.EventId,
                Payload: payload,
                TraceId: parent?.TraceId,
                ProducerComponent: SubAgentRuntimeProducerComponent);

            try
            {
                await _conversationEventStore.AppendAsync(
                    conversationId,
                    -1,
                    [draft],
                    new EventWriteCondition(
                        manifest.RunId,
                        0,
                        archivedEvent.EventId,
                        -1),
                    ct);
                await WriteProjectionCursorAsync(cursorPath, index + 1, ct);
                projected++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[FileSubAgentRunStore] Conversation projection deferred runId={RunId} eventId={EventId}",
                    runId,
                    archivedEvent.EventId);
                break;
            }
        }

        return projected;
    }

    private async Task AppendEventCoreAsync(
        string runId,
        string runDir,
        string eventId,
        string eventType,
        object payload,
        CancellationToken ct)
    {
        var payloadNode =
            JsonSerializer.SerializeToNode(payload, PuddingJsonContracts.JsonLines)
            as JsonObject
            ?? new JsonObject();
        payloadNode.TryAdd("run_id", runId);

        var line = JsonSerializer.Serialize(new
        {
            eventId,
            eventType,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            payload = payloadNode,
        }, PuddingJsonContracts.JsonLines);

        await AppendAllTextWithRetryAsync(
            Path.Combine(runDir, "events.jsonl"),
            line + Environment.NewLine,
            runId,
            "events.jsonl",
            ct);
        try
        {
            await ProjectPendingConversationEventsCoreAsync(runId, runDir, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 事件已持久化，投影滞后由 ReplayPendingConversationEventsAsync 补齐；
            // 投影的瞬时 IO 失败不得让调用方误判事件写入失败。
            _logger.LogWarning(ex,
                "[FileSubAgentRunStore] Conversation projection deferred after append runId={RunId} eventId={EventId}",
                runId, eventId);
        }
    }

    /// <summary>
    /// Windows sharing violation 的有限退避重试（约 770ms 上限）。
    /// 检查器/杀毒/备份进程短持文件时，append 必须等到锁释放而不是立刻失败。
    /// </summary>
    private static readonly int[] AppendRetryDelaysMs = [20, 50, 100, 200, 400];

    private async Task AppendAllTextWithRetryAsync(
        string path,
        string contents,
        string runId,
        string fileName,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.AppendAllTextAsync(path, contents, ct);
                return;
            }
            catch (IOException ex) when (attempt < AppendRetryDelaysMs.Length && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "[FileSubAgentRunStore] Append retry {Attempt}/{Max} runId={RunId} file={File}",
                    attempt + 1, AppendRetryDelaysMs.Length, runId, fileName);
                await Task.Delay(AppendRetryDelaysMs[attempt], ct);
            }
        }
    }

    /// <summary>
    /// 以 FileShare.ReadWrite|Delete 读取：读取方不得以只读共享打开归档文件，
    /// 否则读句柄会把并发写入方挤成 sharing violation。
    /// </summary>
    private static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<string[]> ReadAllLinesSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
            lines.Add(line);
        return [.. lines];
    }

    private static async Task<SubAgentArchiveDegradedInfo?> TryReadArchiveDegradedAsync(
        string runDir,
        CancellationToken ct)
    {
        var markerPath = Path.Combine(runDir, "archive-degraded.json");
        if (!File.Exists(markerPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SubAgentArchiveDegradedInfo>(
                await ReadAllTextSharedAsync(markerPath, ct),
                PuddingJsonContracts.PrettyJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 写/更新 archive-degraded.json 标记。标记写入本身也可能失败，
    /// 此时只记录日志——降级标记绝不能反向伤害运行中的子代理。
    /// </summary>
    private async Task MarkArchiveDegradedAsync(
        string runDir,
        string runId,
        string eventType,
        string error,
        CancellationToken ct)
    {
        try
        {
            var previous = await TryReadArchiveDegradedAsync(runDir, ct);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var marker = new SubAgentArchiveDegradedInfo
            {
                FirstFailureAt = string.IsNullOrEmpty(previous?.FirstFailureAt)
                    ? now
                    : previous!.FirstFailureAt,
                LastFailureAt = now,
                DroppedEventCount = (previous?.DroppedEventCount ?? 0) + 1,
                LastEventType = eventType,
                LastError = error,
            };
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "archive-degraded.json"),
                JsonSerializer.Serialize(marker, PuddingJsonContracts.PrettyJson),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[FileSubAgentRunStore] Failed to write archive-degraded marker runId={RunId}", runId);
        }
    }

    private static bool IsTerminalStatus(string status) =>
        status is "completed" or "budget_exhausted" or "failed" or "cancelled" or "timed_out" or "interrupted";

    private static string ToTerminalEventType(string status) =>
        status switch
        {
            "completed" => ConversationEventTypes.SubAgentRunCompleted,
            "budget_exhausted" => ConversationEventTypes.SubAgentRunBudgetExhausted,
            "failed" => ConversationEventTypes.SubAgentRunFailed,
            "cancelled" => ConversationEventTypes.SubAgentRunCancelled,
            "timed_out" => ConversationEventTypes.SubAgentRunTimedOut,
            "interrupted" => ConversationEventTypes.SubAgentRunInterrupted,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static async Task<long> ReadProjectionCursorAsync(
        string cursorPath,
        CancellationToken ct)
    {
        if (!File.Exists(cursorPath))
            return 0;

        var text = await ReadAllTextSharedAsync(cursorPath, ct);
        return long.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var cursor)
            ? Math.Max(0, cursor)
            : 0;
    }

    private static async Task WriteProjectionCursorAsync(
        string cursorPath,
        long cursor,
        CancellationToken ct)
    {
        var tempPath = cursorPath + ".tmp";
        await File.WriteAllTextAsync(
            tempPath,
            cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ct);
        File.Move(tempPath, cursorPath, overwrite: true);
    }

    private sealed record ArchivedRunEvent
    {
        public string EventId { get; init; } = "";
        public string EventType { get; init; } = "";
        public string Timestamp { get; init; } = "";
        public JsonElement Payload { get; init; }
    }

    /// <summary>
    /// 通过 runId 反向查找 run 目录。
    /// 遍历现有 workspace agent runs 目录匹配 runId 子目录名。
    /// </summary>
    private string? ResolveRunDir(string runId)
    {
        // 尝试从 runId 格式解析时间戳部分（避免全盘扫描）
        // runId 格式: run_20260519_HHmmss_xxxxxxxx（共32字符）
        // 直接遍历 workspaces 下的 agents/*/runs/ 目录
        var workspacesRoot = _paths.WorkspacesRoot;
        if (!Directory.Exists(workspacesRoot)) return null;

        foreach (var wsDir in Directory.GetDirectories(workspacesRoot))
        {
            var agentsRoot = Path.Combine(wsDir, "agents");
            if (!Directory.Exists(agentsRoot)) continue;

            foreach (var agentDir in Directory.GetDirectories(agentsRoot))
            {
                var runsRoot = Path.Combine(agentDir, "runs");
                if (!Directory.Exists(runsRoot)) continue;

                var candidate = Path.Combine(runsRoot, runId);
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 同步写 DB 索引 — 创建 run 时调用。
    /// </summary>
    private async Task WriteDbIndexAsync(
        string runId, string parentSessionId, string subSessionId,
        string workspaceId, string agentInstanceId, string templateId,
        string status, string startedAt, string? completedAt, string archivePath,
        IReadOnlyDictionary<string, string> taskPlanningMetadata,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var taskPlanningMetadataJson = taskPlanningMetadata.Count == 0
                ? null
                : JsonSerializer.Serialize(taskPlanningMetadata, PuddingJsonContracts.JsonLines);
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = runId,
                ParentSessionId = parentSessionId,
                SubSessionId = subSessionId,
                WorkspaceId = workspaceId,
                AgentInstanceId = agentInstanceId,
                TemplateId = templateId,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ArchivePath = archivePath,
                TaskPlanningMetadataJson = taskPlanningMetadataJson,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // DB 写入非致命 — 文件系统是主存储，DB 仅索引
            _logger.LogWarning(ex, "[FileSubAgentRunStore] DB index write failed for runId={RunId}", runId);
        }
    }

    private static Dictionary<string, string> BuildTaskPlanningMetadata(SubAgentRunCreateRequest request)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(metadata, "task_plan_id", request.TaskPlanId);
        Add(metadata, "task_node_id", request.TaskNodeId);
        Add(metadata, "parent_task_node_id", request.ParentTaskNodeId);
        Add(metadata, "delegation_depth", request.DelegationDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(metadata, "max_delegation_depth", request.MaxDelegationDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(metadata, "role_in_plan", request.RoleInPlan);
        Add(metadata, "allow_sub_delegation", request.AllowSubDelegation?.ToString().ToLowerInvariant());
        Add(metadata, "allow_agent_creation", request.AllowAgentCreation?.ToString().ToLowerInvariant());
        Add(metadata, "assigned_objective", request.AssignedObjective);
        Add(metadata, "expected_output_contract", request.ExpectedOutputContract);
        Add(metadata, "invocation_id", request.InvocationId);
        Add(metadata, "batch_id", request.BatchId);
        Add(metadata, "origin_tool_id", request.OriginToolId);
        Add(metadata, "provider_id", request.ProviderId);
        Add(metadata, "profile_id", request.ProfileId);
        Add(metadata, "model_id", request.ModelId);
        Add(metadata, "timeout_seconds", request.TimeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(metadata, "max_rounds", request.MaxRounds?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(metadata, "parent_turn_id", request.ParentExecutionIdentity?.TurnId);
        Add(metadata, "parent_command_id", request.ParentExecutionIdentity?.CommandId);
        Add(metadata, "parent_run_id", request.ParentExecutionIdentity?.RunId);
        Add(metadata, "parent_message_id", request.ParentExecutionIdentity?.MessageId);
        Add(metadata, "parent_tool_call_id", request.ParentExecutionIdentity?.ToolCallId);
        return metadata;
    }

    private static void Add(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value.Trim();
    }

    /// <summary>
    /// 同步更新 DB 索引 — 完成 run 时调用。
    /// </summary>
    private async Task UpdateDbIndexAsync(
        string runId, string status, string completedAt,
        string? errorMessage, int totalRounds, int totalToolCalls,
        long totalDurationMs, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.SubAgentRuns.FirstOrDefaultAsync(e => e.RunId == runId, ct);
            if (entity is not null)
            {
                entity.Status = status;
                entity.CompletedAt = completedAt;
                entity.ErrorMessage = errorMessage;
                entity.TotalRounds = totalRounds;
                entity.TotalToolCalls = totalToolCalls;
                entity.TotalDurationMs = totalDurationMs;
                await db.SaveChangesAsync(ct);
            }
        }
                catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileSubAgentRunStore] DB index update failed for runId={RunId}", runId);
        }
    }

    /// <summary>
    /// 删除子代理运行归档：移除数据库索引记录，并清理磁盘归档目录。
    /// 不可逆操作。
    /// </summary>
    public async Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId cannot be null or empty.", nameof(runId));

        var deleted = false;

        // 1. 删除数据库索引
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.SubAgentRuns.FirstOrDefaultAsync(e => e.RunId == runId, ct);
            if (entity is not null)
            {
                db.SubAgentRuns.Remove(entity);
                await db.SaveChangesAsync(ct);
                deleted = true;
                _logger.LogInformation(
                    "[FileSubAgentRunStore] Deleted DB index for runId={RunId}", runId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[FileSubAgentRunStore] DB index delete failed for runId={RunId} (non-fatal)", runId);
        }

        // 2. 删除磁盘归档目录
        var runDir = ResolveRunDir(runId);
        if (runDir is not null && Directory.Exists(runDir))
        {
            try
            {
                Directory.Delete(runDir, recursive: true);
                deleted = true;
                _logger.LogInformation(
                    "[FileSubAgentRunStore] Deleted archive directory for runId={RunId} path={Path}",
                    runId, runDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[FileSubAgentRunStore] Archive directory delete failed for runId={RunId} path={Path} (non-fatal)",
                    runId, runDir);
            }
        }

        return deleted;
    }
}
